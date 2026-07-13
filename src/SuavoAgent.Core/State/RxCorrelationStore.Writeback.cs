using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    private static readonly Regex DeliveryOffsetTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public WritebackCorrelationRegistrationResult RegisterDeliveryWriteback(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint)
    {
        ValidateDeliveryWritebackCommand(command);
        ValidateIdentity(currentAgentId, nameof(currentAgentId));
        ValidateIdentity(currentMachineFingerprint, nameof(currentMachineFingerprint));
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: false);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var replay = document.Entries
                .SelectMany(entry => entry.Writebacks.Select(claim => (entry, claim)))
                .SingleOrDefault(pair =>
                    string.Equals(pair.claim.CommandId, command.CommandId, StringComparison.Ordinal));
            if (replay.entry is not null)
            {
                var exactReplay = ExactWritebackClaim(
                    replay.entry,
                    replay.claim,
                    command,
                    currentAgentId,
                    currentMachineFingerprint);
                if (!exactReplay)
                    return new WritebackCorrelationRegistrationResult(
                        WritebackCorrelationRegistrationCode.CommandReplayConflict);
                return new WritebackCorrelationRegistrationResult(
                    replay.claim.State == StoredWritebackState.Expired
                        ? WritebackCorrelationRegistrationCode.RawLookupUnavailable
                        : WritebackCorrelationRegistrationCode.Idempotent);
            }

            var key = new RxCorrelationKey(
                command.PharmacyId, currentAgentId, command.RxHash, command.EvidenceId);
            var entry = document.Entries.SingleOrDefault(item => Matches(item, key));
            if (entry is null)
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.CorrelationNotFound);
            if (!string.Equals(entry.AgentId, currentAgentId, StringComparison.Ordinal) ||
                !string.Equals(entry.MachineFingerprint, currentMachineFingerprint, StringComparison.Ordinal))
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.IdentityMismatch);
            if (entry.CandidateId is null ||
                !string.Equals(entry.CandidateId, command.CandidateId, StringComparison.Ordinal))
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.CandidateMismatch);
            if (entry.SourceKind != RxCorrelationSourceKinds.PioneerRxBuiltIn ||
                entry.SourceBinding is not null)
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.SourceUnsupported);
            if (entry.State != StoredCorrelationState.Completed)
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.PatientRetrievalIncomplete);
            if (string.IsNullOrEmpty(entry.ProtectedRx))
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.RawLookupUnavailable);
            if (entry.Writebacks.Any(claim =>
                    string.Equals(claim.Transition, command.Transition, StringComparison.Ordinal) &&
                    (claim.State != StoredWritebackState.ReceiptVerified ||
                     claim.ResultCode is null ||
                     claim.ResultCode.Value.IsCloudSuccess())))
                return new WritebackCorrelationRegistrationResult(
                    WritebackCorrelationRegistrationCode.TransitionConflict);

            entry.Writebacks.Add(StoredWritebackClaim.FromCommand(command, now));
            entry.ExpiresAtUtc = now + _ttl;
            entry.LastUpdatedAtUtc = now;
            Write(document);
            return new WritebackCorrelationRegistrationResult(
                WritebackCorrelationRegistrationCode.Registered);
        }
    }

    public bool TryRevealDeliveryWriteback(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint,
        out SensitiveRxBuffer? rawRxNumber,
        out int fillNumber)
    {
        ValidateDeliveryWritebackCommand(command);
        ValidateIdentity(currentAgentId, nameof(currentAgentId));
        ValidateIdentity(currentMachineFingerprint, nameof(currentMachineFingerprint));
        rawRxNumber = null;
        fillNumber = 0;
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now, out var pruned);
            if (pruned) Write(document);
            var key = new RxCorrelationKey(
                command.PharmacyId, currentAgentId, command.RxHash, command.EvidenceId);
            var entry = document.Entries.SingleOrDefault(item =>
                Matches(item, key) &&
                string.Equals(item.MachineFingerprint, currentMachineFingerprint, StringComparison.Ordinal));
            var claim = entry?.Writebacks.SingleOrDefault(item =>
                string.Equals(item.CommandId, command.CommandId, StringComparison.Ordinal));
            if (entry is null || claim is null ||
                entry.State != StoredCorrelationState.Completed ||
                !ExactWritebackClaim(entry, claim, command, currentAgentId, currentMachineFingerprint) ||
                claim.State != StoredWritebackState.Registered ||
                string.IsNullOrEmpty(entry.ProtectedRx))
                return false;

            rawRxNumber = RevealSensitive(entry);
            fillNumber = entry.FillNumber;
            return true;
        }
    }

    public void MarkDeliveryWritebackReceiptVerified(
        AgentDeliveryWritebackCommand command,
        string currentAgentId,
        string currentMachineFingerprint,
        DeliveryWritebackResultCode resultCode)
    {
        ValidateDeliveryWritebackCommand(command);
        ValidateIdentity(currentAgentId, nameof(currentAgentId));
        ValidateIdentity(currentMachineFingerprint, nameof(currentMachineFingerprint));
        if (!Enum.IsDefined(resultCode))
            throw new ArgumentOutOfRangeException(nameof(resultCode));
        lock (_gate)
        {
            EnsureProductionBoundary(fileMustExist: true);
            var now = _timeProvider.GetUtcNow();
            var document = ReadAndPrune(now);
            var key = new RxCorrelationKey(
                command.PharmacyId, currentAgentId, command.RxHash, command.EvidenceId);
            var entry = document.Entries.SingleOrDefault(item =>
                Matches(item, key) &&
                string.Equals(item.MachineFingerprint, currentMachineFingerprint, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Delivery writeback correlation no longer exists.");
            var claim = entry.Writebacks.SingleOrDefault(item =>
                string.Equals(item.CommandId, command.CommandId, StringComparison.Ordinal))
                ?? throw new InvalidOperationException("Delivery writeback claim no longer exists.");
            if (!ExactWritebackClaim(entry, claim, command, currentAgentId, currentMachineFingerprint))
                throw new InvalidDataException("Delivery writeback receipt identity conflicts with local correlation.");
            if (claim.State == StoredWritebackState.ReceiptVerified)
            {
                if (claim.ResultCode != resultCode)
                    throw new InvalidDataException("Delivery writeback result conflicts with local correlation.");
                return;
            }
            if (command.Transition == "complete" &&
                entry.Writebacks.Any(other =>
                    !ReferenceEquals(other, claim) && other.State == StoredWritebackState.Registered))
                throw new InvalidOperationException(
                    "Cannot purge protected lookup material while another writeback is pending.");

            claim.State = StoredWritebackState.ReceiptVerified;
            claim.ResultCode = resultCode;
            claim.ReceiptVerifiedAtUtc = now;
            claim.ExpiredAtUtc = null;
            // A needs-attention completion is explicitly recoverable: retain
            // DPAPI-bound lookup material for a signed successor. Purge only
            // after PioneerRx actually reached (or already had) the target.
            if (command.Transition == "complete" && resultCode.IsCloudSuccess())
                entry.ProtectedRx = null;
            entry.LastUpdatedAtUtc = now;
            entry.ExpiresAtUtc = now + _ttl;
            Write(document);
        }
    }

    private static bool ExactWritebackClaim(
        StoredCorrelation entry,
        StoredWritebackClaim claim,
        AgentDeliveryWritebackCommand command,
        string agentId,
        string fingerprint) =>
        string.Equals(entry.PharmacyId, command.PharmacyId, StringComparison.Ordinal) &&
        string.Equals(entry.AgentId, agentId, StringComparison.Ordinal) &&
        string.Equals(entry.MachineFingerprint, fingerprint, StringComparison.Ordinal) &&
        string.Equals(entry.CandidateId, command.CandidateId, StringComparison.Ordinal) &&
        string.Equals(entry.RxHash, command.RxHash, StringComparison.Ordinal) &&
        string.Equals(entry.EvidenceId, command.EvidenceId, StringComparison.Ordinal) &&
        string.Equals(claim.CommandId, command.CommandId, StringComparison.Ordinal) &&
        string.Equals(claim.WritebackId, command.WritebackId, StringComparison.Ordinal) &&
        string.Equals(claim.CandidateId, command.CandidateId, StringComparison.Ordinal) &&
        string.Equals(claim.OrderId, command.OrderId, StringComparison.Ordinal) &&
        string.Equals(claim.InboxItemId, command.InboxItemId, StringComparison.Ordinal) &&
        string.Equals(claim.PmsReferenceId, command.PmsReferenceId, StringComparison.Ordinal) &&
        string.Equals(claim.ProofRecordId, command.ProofRecordId, StringComparison.Ordinal) &&
        string.Equals(claim.ProofDigest, command.ProofDigest, StringComparison.Ordinal) &&
        string.Equals(claim.Transition, command.Transition, StringComparison.Ordinal) &&
        string.Equals(claim.TransitionAt, command.TransitionAt, StringComparison.Ordinal);

    private static void ValidateDeliveryWritebackCommand(AgentDeliveryWritebackCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.SchemaVersion != 2)
            throw new ArgumentException("Unsupported delivery writeback schema version.");
        ValidateUuid(command.WritebackId, nameof(command.WritebackId));
        ValidateUuid(command.CandidateId, nameof(command.CandidateId));
        ValidateUuid(command.PharmacyId, nameof(command.PharmacyId));
        ValidateUuid(command.OrderId, nameof(command.OrderId));
        ValidateUuid(command.InboxItemId, nameof(command.InboxItemId));
        ValidateUuid(command.PmsReferenceId, nameof(command.PmsReferenceId));
        ValidateUuid(command.CommandId, nameof(command.CommandId));
        ValidateHash(command.RxHash);
        ValidateEvidence(command.EvidenceId, command.RxHash);
        if (command.Transition is not ("pickup" or "complete") ||
            !DeliveryOffsetTimestamp.IsMatch(command.TransitionAt) ||
            !DateTimeOffset.TryParse(
                command.TransitionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _))
            throw new ArgumentException("Delivery writeback transition is invalid.");
        if (command.Transition == "complete")
        {
            ValidateUuid(command.ProofRecordId ?? "", nameof(command.ProofRecordId));
            ValidateHash(command.ProofDigest ?? "");
        }
        else if (command.ProofRecordId is not null || command.ProofDigest is not null)
        {
            throw new ArgumentException("Pickup writeback cannot carry completion proof.");
        }
    }

    [JsonConverter(typeof(JsonStringEnumConverter<StoredWritebackState>))]
    private enum StoredWritebackState
    {
        Registered,
        ReceiptVerified,
        Expired,
    }

    private sealed class StoredWritebackClaim
    {
        public string CommandId { get; set; } = "";
        public string WritebackId { get; set; } = "";
        public string CandidateId { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string InboxItemId { get; set; } = "";
        public string PmsReferenceId { get; set; } = "";
        public string? ProofRecordId { get; set; }
        public string? ProofDigest { get; set; }
        public string Transition { get; set; } = "";
        public string TransitionAt { get; set; } = "";
        public StoredWritebackState State { get; set; }
        public DeliveryWritebackResultCode? ResultCode { get; set; }
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public DateTimeOffset? ReceiptVerifiedAtUtc { get; set; }
        public DateTimeOffset? ExpiredAtUtc { get; set; }

        internal static StoredWritebackClaim FromCommand(
            AgentDeliveryWritebackCommand command,
            DateTimeOffset now) => new()
        {
            CommandId = command.CommandId,
            WritebackId = command.WritebackId,
            CandidateId = command.CandidateId,
            OrderId = command.OrderId,
            InboxItemId = command.InboxItemId,
            PmsReferenceId = command.PmsReferenceId,
            ProofRecordId = command.ProofRecordId,
            ProofDigest = command.ProofDigest,
            Transition = command.Transition,
            TransitionAt = command.TransitionAt,
            State = StoredWritebackState.Registered,
            RegisteredAtUtc = now,
        };
    }
}
