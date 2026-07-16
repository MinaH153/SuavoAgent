using System.Collections.Concurrent;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.State;

internal sealed partial class RxCorrelationStore
{
    private void MakeRoom(StoreDocument document)
    {
        if (document.Entries.Count < _maxEntries) return;

        var evictable = document.Entries
            .Where(e =>
                (string.IsNullOrEmpty(e.ProtectedRx) &&
                 e.Writebacks.All(claim => claim.State == StoredWritebackState.ReceiptVerified)) ||
                (e.State == StoredCorrelationState.Observed && e.Writebacks.Count == 0))
            .OrderBy(e => e.State == StoredCorrelationState.Completed ? 0 : 1)
            .ThenBy(e => e.LastUpdatedAtUtc ?? e.CreatedAtUtc)
            .FirstOrDefault();
        if (evictable is null)
            throw new InvalidOperationException("Rx correlation store is full of in-flight approvals.");
        document.Entries.Remove(evictable);
    }

    private static PendingApprovedPatientFetch ToPending(StoredCorrelation entry)
    {
        if (entry.CommandId is null || entry.CandidateId is null)
            throw new InvalidDataException("Rx correlation command metadata is incomplete.");
        return new PendingApprovedPatientFetch(
            new RxCorrelationKey(entry.PharmacyId, entry.AgentId, entry.RxHash, entry.EvidenceId),
            entry.MachineFingerprint,
            entry.CandidateId,
            entry.CommandId,
            entry.State switch
            {
                StoredCorrelationState.AwaitingCallback => RxCorrelationCommandState.AwaitingCallback,
                StoredCorrelationState.CallbackAccepted => RxCorrelationCommandState.CallbackAccepted,
                StoredCorrelationState.Completed => RxCorrelationCommandState.Completed,
                _ => throw new InvalidDataException("Rx correlation is not command-bound."),
            },
            entry.SourceKind,
            entry.SourceBinding,
            entry.AttemptCount,
            entry.NextAttemptAtUtc,
            entry.AuthorizationExpiresAtUtc,
            entry.CallbackExpiresAtUtc);
    }

    private static StoredCorrelation? FindExact(StoreDocument document, PendingApprovedPatientFetch pending) =>
        document.Entries.SingleOrDefault(e =>
            Matches(e, pending.Key) &&
            string.Equals(e.MachineFingerprint, pending.MachineFingerprint, StringComparison.Ordinal) &&
            string.Equals(e.CandidateId, pending.CandidateId, StringComparison.Ordinal) &&
            string.Equals(e.CommandId, pending.CommandId, StringComparison.Ordinal) &&
            string.Equals(e.SourceKind, pending.SourceKind, StringComparison.Ordinal) &&
            string.Equals(e.SourceBinding, pending.SourceBinding, StringComparison.Ordinal));

    private static bool Matches(StoredCorrelation entry, RxCorrelationKey key) =>
        string.Equals(entry.PharmacyId, key.PharmacyId, StringComparison.Ordinal) &&
        string.Equals(entry.AgentId, key.AgentId, StringComparison.Ordinal) &&
        string.Equals(entry.RxHash, key.RxHash, StringComparison.Ordinal) &&
        string.Equals(entry.EvidenceId, key.EvidenceId, StringComparison.Ordinal);

    private static void Quarantine(StoredCorrelation entry, DateTimeOffset now)
    {
        entry.State = StoredCorrelationState.Quarantined;
        entry.ProtectedRx = null;
        entry.NextAttemptAtUtc = null;
        entry.FailureAckedAtUtc = null;
        entry.LastUpdatedAtUtc = now;
        entry.ExpiresAtUtc = now + DefaultObservationTtl;
    }

    private void EnsureProductionBoundary(bool fileMustExist)
    {
        if (!_requireProductionBoundary) return;
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The production Rx correlation store requires Windows.");
        ProductionAclBoundary.ValidatePath(
            _filePath,
            "rx-correlations.v1.json",
            fileMustExist);
    }

    private static byte[] ReadBounded(Stream stream, int maxBytes)
    {
        using var memory = new MemoryStream(capacity: (int)Math.Min(stream.Length, maxBytes));
        var buffer = new byte[8192];
        var total = 0;
        while (true)
        {
            var read = stream.Read(buffer, 0, Math.Min(buffer.Length, maxBytes + 1 - total));
            if (read == 0) break;
            total += read;
            if (total > maxBytes) throw new InvalidDataException("Rx correlation store is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static byte[] BuildEntropy(
        RxCorrelationKey key,
        string fingerprint,
        int fillNumber,
        int protectionVersion,
        string sourceKind,
        string? sourceBinding)
    {
        var context = protectionVersion switch
        {
            1 => $"SuavoAgent.RxCorrelation.v1|{key.PharmacyId}|{key.AgentId}|{fingerprint}|{key.RxHash}|{key.EvidenceId}",
            2 => $"SuavoAgent.RxCorrelation.v2|{key.PharmacyId}|{key.AgentId}|{fingerprint}|{key.RxHash}|{key.EvidenceId}|{fillNumber}",
            3 => $"SuavoAgent.RxCorrelation.v3|{key.PharmacyId}|{key.AgentId}|{fingerprint}|{key.RxHash}|{key.EvidenceId}|{fillNumber}|{sourceKind}|{sourceBinding}",
            _ => throw new InvalidDataException("Rx correlation protection version is unsupported."),
        };
        return SHA256.HashData(Encoding.UTF8.GetBytes(context));
    }

    private static bool FixedTimeUtf8Equals(string left, string right)
    {
        var a = Encoding.UTF8.GetBytes(left);
        var b = Encoding.UTF8.GetBytes(right);
        try
        {
            return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(a);
            CryptographicOperations.ZeroMemory(b);
        }
    }

    // Strings are immutable, so this only clears a transient byte projection. Callers keep raw Rx
    // values scoped to the shortest possible method and never persist or log them.
    private static void ZeroStringBytes(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        CryptographicOperations.ZeroMemory(bytes);
    }

    private static void ValidateObservation(RxCorrelationObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ValidateKey(observation.Key);
        ValidateIdentity(observation.MachineFingerprint, nameof(observation.MachineFingerprint));
        ValidateRawRx(observation.RawRxNumber);
        if (observation.FillNumber is < 0 or > 999)
            throw new ArgumentOutOfRangeException(nameof(observation.FillNumber));
        ValidateSourceBinding(observation.SourceKind, observation.SourceBinding);
    }

    private static void ValidateCommand(ApprovedPatientFetchCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        ValidateUuid(command.CandidateId, nameof(command.CandidateId));
        ValidateUuid(command.CommandId, nameof(command.CommandId));
        ValidateUuid(command.PharmacyId, nameof(command.PharmacyId));
        ValidateHash(command.RxHash);
        ValidateEvidence(command.EvidenceId, command.RxHash);
        ValidateSourceBinding(command.SourceKind, command.SourceBinding);
    }

    private static void ValidateKey(RxCorrelationKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        ValidateUuid(key.PharmacyId, nameof(key.PharmacyId));
        ValidateIdentity(key.AgentId, nameof(key.AgentId));
        ValidateHash(key.RxHash);
        ValidateEvidence(key.EvidenceId, key.RxHash);
    }

    private static void ValidateUuid(string? value, string name)
    {
        if (value is null || value.Length != 36 ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(parsed.ToString("D"), value, StringComparison.Ordinal))
            throw new ArgumentException("Expected a canonical lowercase UUID.", name);
    }

    private static void ValidateIdentity(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxIdentityLength ||
            value.Any(c => char.IsControl(c)))
            throw new ArgumentException("Identity token is invalid.", name);
    }

    private static void ValidateHash(string? value)
    {
        if (value is null || value.Length != 64 || value.Any(c => !IsLowerHex(c)))
            throw new ArgumentException("Rx hash must be 64 lowercase hexadecimal characters.", nameof(value));
    }

    private static void ValidateEvidence(string? value, string rxHash)
    {
        var prefix = "rxh-" + rxHash[..16] + "-";
        if (value is null || !value.StartsWith(prefix, StringComparison.Ordinal) ||
            value.Length < prefix.Length + 10 || value.Length > prefix.Length + 13 ||
            value[prefix.Length..].Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Evidence id is not bound to the Rx hash.", nameof(value));
    }

    private static void ValidateRawRx(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 ||
            value.Any(c => char.IsControl(c)))
            throw new ArgumentException("Raw Rx lookup key is invalid.", nameof(value));
    }

    private static void ValidateRawRx(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty || value.Length > 64)
            throw new ArgumentException("Raw Rx lookup key is invalid.", nameof(value));
        var hasNonWhitespace = false;
        foreach (var ch in value)
        {
            if (char.IsControl(ch))
                throw new ArgumentException("Raw Rx lookup key is invalid.", nameof(value));
            if (!char.IsWhiteSpace(ch)) hasNonWhitespace = true;
        }
        if (!hasNonWhitespace)
            throw new ArgumentException("Raw Rx lookup key is invalid.", nameof(value));
    }

    private static void ValidateSourceBinding(string? sourceKind, string? sourceBinding)
    {
        if (sourceKind == RxCorrelationSourceKinds.PioneerRxBuiltIn)
        {
            if (sourceBinding is not null)
                throw new ArgumentException("Built-in Rx source cannot carry a learned binding.", nameof(sourceBinding));
            return;
        }
        if (sourceKind == RxCorrelationSourceKinds.LearnedApproved &&
            sourceBinding is { Length: 64 } && sourceBinding.All(IsLowerHex))
            return;
        throw new ArgumentException("Rx correlation source binding is invalid.", nameof(sourceKind));
    }

    private static bool IsLowerHex(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private sealed class StoreDocument
    {
        public int SchemaVersion { get; set; } = RxCorrelationStore.SchemaVersion;
        public List<StoredCorrelation> Entries { get; set; } = [];
    }

    [JsonConverter(typeof(JsonStringEnumConverter<StoredCorrelationState>))]
    private enum StoredCorrelationState
    {
        Observed,
        AwaitingCallback,
        CallbackAccepted,
        Completed,
        Quarantined,
    }

    private sealed class StoredCorrelation
    {
        public string PharmacyId { get; set; } = "";
        public string AgentId { get; set; } = "";
        public string MachineFingerprint { get; set; } = "";
        public string RxHash { get; set; } = "";
        public string EvidenceId { get; set; } = "";
        public int FillNumber { get; set; }
        public int ProtectionVersion { get; set; } = 1;
        public string SourceKind { get; set; } = RxCorrelationSourceKinds.PioneerRxBuiltIn;
        public string? SourceBinding { get; set; }
        public int AttemptCount { get; set; }
        public DateTimeOffset? NextAttemptAtUtc { get; set; }
        public DateTimeOffset? AuthorizationExpiresAtUtc { get; set; }
        public string? LastFailureCategory { get; set; }
        public bool LookupMaterialPurged { get; set; }
        public string? ProtectedRx { get; set; }
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
        public StoredCorrelationState State { get; set; }
        public string? CandidateId { get; set; }
        public string? CommandId { get; set; }
        public string? StagingId { get; set; }
        public string? TransitionId { get; set; }
        public DateTimeOffset? CallbackExpiresAtUtc { get; set; }
        public DateTimeOffset? LastUpdatedAtUtc { get; set; }
        public DateTimeOffset? FailureAckedAtUtc { get; set; }
        public List<StoredWritebackClaim> Writebacks { get; set; } = [];
    }

}
