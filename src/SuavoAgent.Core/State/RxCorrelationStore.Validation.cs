using System.Globalization;
using System.Security.Cryptography;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    private static void ValidateDocument(StoreDocument document)
    {
        if (document.SchemaVersion != SchemaVersion || document.Entries is null)
            throw new InvalidDataException("Rx correlation store schema is unsupported.");
        if (document.Entries.Count > DefaultMaxEntries)
            throw new InvalidDataException("Rx correlation store entry count is invalid.");

        var keys = new HashSet<string>(StringComparer.Ordinal);
        var commands = new HashSet<string>(StringComparer.Ordinal);
        var writebacks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in document.Entries)
        {
            var key = new RxCorrelationKey(entry.PharmacyId, entry.AgentId, entry.RxHash, entry.EvidenceId);
            ValidateKey(key);
            ValidateIdentity(entry.MachineFingerprint, nameof(entry.MachineFingerprint));
            if (!Enum.IsDefined(entry.State) || entry.Writebacks is null ||
                entry.FillNumber is < 0 or > 999 || entry.ProtectionVersion is not (1 or 2 or 3))
                throw new InvalidDataException("Rx correlation fill/protection metadata is invalid.");
            if (entry.AttemptCount is < 0 or > MaxPatientFetchAttempts ||
                entry.LastFailureCategory is { Length: > 64 } ||
                entry.LastFailureCategory?.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '_' and not '-') == true)
                throw new InvalidDataException("Rx correlation retry metadata is invalid.");
            if (entry.LookupMaterialPurged && !string.IsNullOrEmpty(entry.ProtectedRx))
                throw new InvalidDataException("Purged Rx lookup material is still present.");
            try
            {
                ValidateSourceBinding(entry.SourceKind, entry.SourceBinding);
            }
            catch (ArgumentException ex)
            {
                throw new InvalidDataException("Rx correlation source binding is invalid.", ex);
            }
            if (entry.ProtectionVersion < 3 &&
                (entry.SourceKind != RxCorrelationSourceKinds.PioneerRxBuiltIn || entry.SourceBinding is not null))
                throw new InvalidDataException("Legacy Rx ciphertext cannot assert a learned source binding.");
            var ttlBasis = entry.LastUpdatedAtUtc ?? entry.CreatedAtUtc;
            if (entry.CreatedAtUtc == default || entry.ExpiresAtUtc <= ttlBasis ||
                entry.ExpiresAtUtc - ttlBasis > DefaultObservationTtl + TimeSpan.FromMinutes(5))
                throw new InvalidDataException("Rx correlation TTL is invalid.");
            if (!keys.Add(
                    $"{entry.PharmacyId}|{entry.AgentId}|{entry.RxHash}|{entry.EvidenceId}|{entry.SourceKind}|{entry.SourceBinding}"))
                throw new InvalidDataException("Rx correlation store contains duplicate evidence keys.");

            ValidatePatientRetrievalState(entry, commands);
            foreach (var claim in entry.Writebacks)
                ValidateWritebackClaim(entry, claim, commands, writebacks);

            foreach (var transitionClaims in entry.Writebacks.GroupBy(
                         claim => claim.Transition,
                         StringComparer.Ordinal))
            {
                var ordered = transitionClaims
                    .OrderBy(claim => claim.RegisteredAtUtc)
                    .ToArray();
                if (ordered.Count(claim => claim.State == StoredWritebackState.Registered) > 1 ||
                    ordered[..^1].Any(claim =>
                        claim.State != StoredWritebackState.ReceiptVerified ||
                        claim.ResultCode is null ||
                        claim.ResultCode.Value.IsCloudSuccess()))
                    throw new InvalidDataException(
                        "Rx correlation contains an invalid writeback successor chain.");
            }

            var completeCloudSuccess = entry.Writebacks.Any(claim =>
                claim.Transition == "complete" &&
                claim.State == StoredWritebackState.ReceiptVerified &&
                claim.ResultCode is { } resultCode &&
                resultCode.IsCloudSuccess());
            if (string.IsNullOrEmpty(entry.ProtectedRx))
            {
                var legacyTerminalTombstone =
                    entry.ProtectionVersion == 1 &&
                    entry.State is StoredCorrelationState.CallbackAccepted or StoredCorrelationState.Completed &&
                    entry.Writebacks.Count == 0;
                var quarantinedTombstone = entry.State == StoredCorrelationState.Quarantined;
                var boundedRetentionTombstone =
                    entry.LookupMaterialPurged &&
                    entry.State is StoredCorrelationState.CallbackAccepted or StoredCorrelationState.Completed;
                if ((!completeCloudSuccess && !legacyTerminalTombstone && !quarantinedTombstone &&
                     !boundedRetentionTombstone) ||
                    entry.Writebacks.Any(claim => claim.State == StoredWritebackState.Registered))
                    throw new InvalidDataException("Protected lookup material was purged before terminal completion.");
            }
            else
            {
                if (completeCloudSuccess)
                    throw new InvalidDataException("Terminal completion retained protected lookup material.");
                ValidateCiphertext(entry.ProtectedRx);
            }
        }
    }

    private static void ValidatePatientRetrievalState(
        StoredCorrelation entry,
        HashSet<string> commands)
    {
        if (entry.State == StoredCorrelationState.Observed)
        {
            if (entry.CommandId is not null || entry.CandidateId is not null ||
                entry.StagingId is not null || entry.TransitionId is not null ||
                entry.CallbackExpiresAtUtc is not null ||
                (entry.LastUpdatedAtUtc is not null && entry.Writebacks.Count == 0))
                throw new InvalidDataException("Observed Rx correlation state is inconsistent.");
            return;
        }

        ValidateUuid(entry.CommandId, nameof(entry.CommandId));
        ValidateUuid(entry.CandidateId, nameof(entry.CandidateId));
        if (entry.LastUpdatedAtUtc is null || !commands.Add(entry.CommandId!))
            throw new InvalidDataException("Command-bound Rx correlation metadata is inconsistent.");
        if (entry.State == StoredCorrelationState.Quarantined)
        {
            if (!string.IsNullOrEmpty(entry.ProtectedRx) || entry.NextAttemptAtUtc is not null ||
                string.IsNullOrWhiteSpace(entry.LastFailureCategory) ||
                entry.FailureAckedAtUtc is { } failureAckedAt &&
                entry.LastUpdatedAtUtc is { } lastUpdatedAt &&
                failureAckedAt < lastUpdatedAt)
                throw new InvalidDataException("Quarantined Rx correlation state is inconsistent.");
            return;
        }
        if (entry.FailureAckedAtUtc is not null)
            throw new InvalidDataException("Non-quarantined Rx correlation has a failure acknowledgement.");
        if (entry.State == StoredCorrelationState.AwaitingCallback)
        {
            if (string.IsNullOrEmpty(entry.ProtectedRx) || entry.StagingId is not null ||
                entry.TransitionId is not null || entry.CallbackExpiresAtUtc is not null)
                throw new InvalidDataException("Pending Rx correlation state is inconsistent.");
            return;
        }

        ValidateUuid(entry.StagingId, nameof(entry.StagingId));
        ValidateUuid(entry.TransitionId, nameof(entry.TransitionId));
        if (entry.CallbackExpiresAtUtc is null)
            throw new InvalidDataException("Terminal Rx correlation receipt state is inconsistent.");
    }

    private static void ValidateWritebackClaim(
        StoredCorrelation entry,
        StoredWritebackClaim claim,
        HashSet<string> commands,
        HashSet<string> writebacks)
    {
        ValidateUuid(claim.CommandId, nameof(claim.CommandId));
        ValidateUuid(claim.WritebackId, nameof(claim.WritebackId));
        ValidateUuid(claim.CandidateId, nameof(claim.CandidateId));
        ValidateUuid(claim.OrderId, nameof(claim.OrderId));
        ValidateUuid(claim.InboxItemId, nameof(claim.InboxItemId));
        if (!Enum.IsDefined(claim.State) ||
            claim.ResultCode is { } result && !Enum.IsDefined(result) ||
            claim.Transition is not ("pickup" or "complete") ||
            !DeliveryOffsetTimestamp.IsMatch(claim.TransitionAt) ||
            !DateTimeOffset.TryParse(
                claim.TransitionAt,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out _) ||
            claim.RegisteredAtUtc == default ||
            entry.CandidateId is null ||
            !string.Equals(claim.CandidateId, entry.CandidateId, StringComparison.Ordinal))
            throw new InvalidDataException("Delivery writeback claim is invalid.");
        if (!commands.Add(claim.CommandId) || !writebacks.Add(claim.WritebackId))
            throw new InvalidDataException("Rx correlation store contains duplicate writeback identities.");
        if (claim.State == StoredWritebackState.Registered)
        {
            if (claim.ResultCode is not null || claim.ReceiptVerifiedAtUtc is not null ||
                claim.ExpiredAtUtc is not null)
                throw new InvalidDataException("Pending delivery writeback claim is inconsistent.");
        }
        else if (claim.State == StoredWritebackState.ReceiptVerified)
        {
            if (claim.ResultCode is null || claim.ReceiptVerifiedAtUtc is null ||
                claim.ReceiptVerifiedAtUtc < claim.RegisteredAtUtc || claim.ExpiredAtUtc is not null)
                throw new InvalidDataException("Verified delivery writeback claim is inconsistent.");
        }
        else if (claim.ResultCode is not null || claim.ReceiptVerifiedAtUtc is not null ||
                 claim.ExpiredAtUtc is null || claim.ExpiredAtUtc < claim.RegisteredAtUtc)
        {
            throw new InvalidDataException("Expired delivery writeback claim is inconsistent.");
        }
    }

    private static void ValidateCiphertext(string protectedRx)
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(protectedRx); }
        catch (FormatException ex)
        {
            throw new InvalidDataException("Rx correlation ciphertext is invalid.", ex);
        }
        try
        {
            if (bytes.Length is <= 0 or > MaxProtectedRxBytes)
                throw new InvalidDataException("Rx correlation ciphertext size is invalid.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
