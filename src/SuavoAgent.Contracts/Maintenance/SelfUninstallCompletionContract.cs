using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Maintenance;

/// <summary>
/// PHI-free proof of the exact terminal cleanup predicates checked by native
/// maintenance. The digest is over <see cref="SelfUninstallCompletionContract.BuildCleanupEvidenceCanonical"/>,
/// never over caller-supplied opaque bytes.
/// </summary>
public sealed record SelfUninstallCleanupEvidence(
    int SchemaVersion,
    string DataPolicy,
    string MaintenanceCohort,
    string MaintenanceVersion,
    bool ServicesAbsent,
    bool ScheduledUninstallTaskAbsent,
    bool ProtocolRegistrationAbsent,
    bool ArpRegistrationAbsent,
    bool InstallDirectoryAbsent,
    bool RuntimeDirectoryAbsent,
    bool RetainedEvidencePresent,
    bool OperationalCredentialsAbsent,
    int ResidueCount);

/// <summary>
/// Device-signed terminal uninstall receipt. The signature is produced only
/// after cleanup is proved and the exact envelope is durable in retained evidence.
/// </summary>
public sealed record SelfUninstallCompletionTicket(
    int SchemaVersion,
    string CommandId,
    string AgentId,
    string PharmacyId,
    string MachineFingerprint,
    string CommandNonce,
    string ArchiveId,
    string ArchiveDigest,
    string ArchiveReceiptTimestamp,
    string CompletedAtUtc,
    string CleanupEvidenceDigest,
    string DeviceKeyId,
    string Signature);

/// <summary>The exact no-HMAC finalize request body.</summary>
public sealed record SelfUninstallCompletionEnvelope(
    SelfUninstallCompletionTicket Ticket,
    SelfUninstallCleanupEvidence CleanupEvidence);

public sealed record SelfUninstallCompletionValidation(bool IsValid, string Code)
{
    public static SelfUninstallCompletionValidation Valid() => new(true, "valid");
    public static SelfUninstallCompletionValidation Reject(string code) => new(false, code);
}

public static class SelfUninstallCompletionContract
{
    public const int SchemaVersion = 1;
    public const string DataPolicy = "retained_evidence_only";
    public const string MaintenanceCohort = "suavo-native-maintenance";
    public const string PendingFileName = "self-uninstall-completion.pending.json";
    public const string FinalizedFileName = "self-uninstall-completion.finalized.json";
    public const string FinalizeEndpoint = "/api/agent/self-uninstall/finalize";
    public const int MaxEnvelopeBytes = 32 * 1024;
    public const int MaxResponseBytes = 4 * 1024;

    private const string EvidencePrefix = "suavo.self-uninstall-cleanup-evidence.v1";
    private const string TicketPrefix = "suavo.self-uninstall-completion.v1";
    private const string CompletionTimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    public static SelfUninstallCleanupEvidence CreateCleanupEvidence(
        string maintenanceVersion,
        bool servicesAbsent,
        bool scheduledUninstallTaskAbsent,
        bool protocolRegistrationAbsent,
        bool arpRegistrationAbsent,
        bool installDirectoryAbsent,
        bool runtimeDirectoryAbsent,
        bool retainedEvidencePresent,
        bool operationalCredentialsAbsent)
    {
        var predicates = new[]
        {
            servicesAbsent,
            scheduledUninstallTaskAbsent,
            protocolRegistrationAbsent,
            arpRegistrationAbsent,
            installDirectoryAbsent,
            runtimeDirectoryAbsent,
            retainedEvidencePresent,
            operationalCredentialsAbsent,
        };
        return new(
            SchemaVersion,
            DataPolicy,
            MaintenanceCohort,
            maintenanceVersion,
            servicesAbsent,
            scheduledUninstallTaskAbsent,
            protocolRegistrationAbsent,
            arpRegistrationAbsent,
            installDirectoryAbsent,
            runtimeDirectoryAbsent,
            retainedEvidencePresent,
            operationalCredentialsAbsent,
            predicates.Count(value => !value));
    }

    public static string BuildCleanupEvidenceCanonical(SelfUninstallCleanupEvidence evidence) =>
        $"{EvidencePrefix}|{evidence.SchemaVersion}|{evidence.DataPolicy}|" +
        $"{evidence.MaintenanceCohort}|{evidence.MaintenanceVersion}|" +
        $"{BooleanToken(evidence.ServicesAbsent)}|" +
        $"{BooleanToken(evidence.ScheduledUninstallTaskAbsent)}|" +
        $"{BooleanToken(evidence.ProtocolRegistrationAbsent)}|" +
        $"{BooleanToken(evidence.ArpRegistrationAbsent)}|" +
        $"{BooleanToken(evidence.InstallDirectoryAbsent)}|" +
        $"{BooleanToken(evidence.RuntimeDirectoryAbsent)}|" +
        $"{BooleanToken(evidence.RetainedEvidencePresent)}|" +
        $"{BooleanToken(evidence.OperationalCredentialsAbsent)}|{evidence.ResidueCount}";

    public static string ComputeCleanupEvidenceDigest(SelfUninstallCleanupEvidence evidence) =>
        LowerSha256(BuildCleanupEvidenceCanonical(evidence));

    public static string BuildTicketCanonical(SelfUninstallCompletionTicket ticket) =>
        $"{TicketPrefix}|{ticket.SchemaVersion}|{ticket.CommandId}|{ticket.AgentId}|" +
        $"{ticket.PharmacyId}|{ticket.MachineFingerprint}|{ticket.CommandNonce}|" +
        $"{ticket.ArchiveId}|{ticket.ArchiveDigest}|{ticket.ArchiveReceiptTimestamp}|" +
        $"{ticket.CleanupEvidenceDigest}|{ticket.CompletedAtUtc}|{ticket.DeviceKeyId}";

    public static string ComputeReceiptDigest(SelfUninstallCompletionTicket ticket) =>
        LowerSha256($"{BuildTicketCanonical(ticket)}|{ticket.Signature}");

    public static SelfUninstallCompletionEnvelope CreateSignedEnvelope(
        SelfUninstallRequest request,
        string pharmacyId,
        SelfUninstallCleanupEvidence evidence,
        string deviceKeyId,
        Func<ReadOnlyMemory<byte>, byte[]> sign,
        DateTimeOffset completedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(sign);
        var completed = completedAtUtc.UtcDateTime.ToString(
            CompletionTimestampFormat,
            CultureInfo.InvariantCulture);
        var unsigned = new SelfUninstallCompletionTicket(
            SchemaVersion,
            request.CommandId,
            request.AgentId,
            pharmacyId,
            request.MachineFingerprint,
            request.Nonce,
            request.ArchiveReceipt.ArchiveId,
            request.ArchiveDigest,
            request.ArchiveReceipt.Timestamp,
            completed,
            ComputeCleanupEvidenceDigest(evidence),
            deviceKeyId,
            string.Empty);
        var signature = sign(Encoding.UTF8.GetBytes(BuildTicketCanonical(unsigned)));
        if (signature.Length != 64)
            throw new CryptographicException(
                "Device completion signature must be a 64-byte P1363 value.");
        return new(unsigned with { Signature = Base64UrlEncode(signature) }, evidence);
    }

    public static SelfUninstallCompletionValidation Validate(
        SelfUninstallCompletionEnvelope envelope,
        SelfUninstallRequest request,
        string expectedPharmacyId,
        string expectedDeviceKeyId,
        string devicePublicKeySpki)
    {
        if (envelope is null || envelope.Ticket is null || envelope.CleanupEvidence is null)
            return SelfUninstallCompletionValidation.Reject("completion_envelope_missing");
        var evidenceValidation = ValidateCleanupEvidence(envelope.CleanupEvidence);
        if (!evidenceValidation.IsValid) return evidenceValidation;

        var ticket = envelope.Ticket;
        if (ticket.SchemaVersion != SchemaVersion)
            return SelfUninstallCompletionValidation.Reject("completion_schema_mismatch");
        if (!Exact(ticket.CommandId, request.CommandId) ||
            !Exact(ticket.AgentId, request.AgentId) ||
            !Exact(ticket.PharmacyId, expectedPharmacyId) ||
            !Exact(ticket.MachineFingerprint, request.MachineFingerprint) ||
            !Exact(ticket.CommandNonce, request.Nonce) ||
            !Exact(ticket.ArchiveId, request.ArchiveReceipt.ArchiveId) ||
            !Exact(ticket.ArchiveDigest, request.ArchiveDigest) ||
            !Exact(ticket.ArchiveReceiptTimestamp, request.ArchiveReceipt.Timestamp) ||
            !Exact(ticket.DeviceKeyId, expectedDeviceKeyId))
            return SelfUninstallCompletionValidation.Reject("completion_request_binding_mismatch");
        if (!SelfUninstallContract.IsCanonicalUuid(ticket.CommandId) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.AgentId) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.PharmacyId) ||
            !IsSafeToken(ticket.MachineFingerprint, 160) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.CommandNonce) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.ArchiveId) ||
            !IsCanonicalText(ticket.ArchiveReceiptTimestamp, 80) ||
            !IsLowerHex64(ticket.ArchiveDigest) ||
            !IsLowerHex64(ticket.DeviceKeyId))
            return SelfUninstallCompletionValidation.Reject("completion_identity_invalid");
        if (!DateTimeOffset.TryParseExact(
                ticket.CompletedAtUtc,
                CompletionTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            return SelfUninstallCompletionValidation.Reject("completion_timestamp_invalid");

        var digest = ComputeCleanupEvidenceDigest(envelope.CleanupEvidence);
        if (!IsLowerHex64(ticket.CleanupEvidenceDigest) ||
            !FixedTimeAsciiEquals(ticket.CleanupEvidenceDigest, digest))
            return SelfUninstallCompletionValidation.Reject("cleanup_evidence_digest_mismatch");
        if (!VerifyDeviceSignature(
                devicePublicKeySpki,
                BuildTicketCanonical(ticket),
                ticket.Signature))
            return SelfUninstallCompletionValidation.Reject("completion_signature_invalid");
        return SelfUninstallCompletionValidation.Valid();
    }

    public static SelfUninstallCompletionValidation ValidateForReplay(
        SelfUninstallCompletionEnvelope envelope)
    {
        if (envelope is null || envelope.Ticket is null || envelope.CleanupEvidence is null)
            return SelfUninstallCompletionValidation.Reject("completion_envelope_missing");
        var evidenceValidation = ValidateCleanupEvidence(envelope.CleanupEvidence);
        if (!evidenceValidation.IsValid) return evidenceValidation;
        var ticket = envelope.Ticket;
        if (ticket.SchemaVersion != SchemaVersion ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.CommandId) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.AgentId) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.PharmacyId) ||
            !IsSafeToken(ticket.MachineFingerprint, 160) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.CommandNonce) ||
            !SelfUninstallContract.IsCanonicalUuid(ticket.ArchiveId) ||
            !IsLowerHex64(ticket.ArchiveDigest) ||
            !IsCanonicalText(ticket.ArchiveReceiptTimestamp, 80) ||
            !IsLowerHex64(ticket.CleanupEvidenceDigest) ||
            !IsLowerHex64(ticket.DeviceKeyId) ||
            !TryBase64UrlDecode(ticket.Signature, out var signature) ||
            signature.Length != 64 ||
            !DateTimeOffset.TryParseExact(
                ticket.CompletedAtUtc,
                CompletionTimestampFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out _))
            return SelfUninstallCompletionValidation.Reject("completion_shape_invalid");
        var digest = ComputeCleanupEvidenceDigest(envelope.CleanupEvidence);
        return FixedTimeAsciiEquals(ticket.CleanupEvidenceDigest, digest)
            ? SelfUninstallCompletionValidation.Valid()
            : SelfUninstallCompletionValidation.Reject("cleanup_evidence_digest_mismatch");
    }

    public static SelfUninstallCompletionValidation ValidateReplaySignature(
        SelfUninstallCompletionEnvelope envelope,
        string devicePublicKeySpki)
    {
        var structural = ValidateForReplay(envelope);
        if (!structural.IsValid) return structural;
        return VerifyDeviceSignature(
            devicePublicKeySpki,
            BuildTicketCanonical(envelope.Ticket),
            envelope.Ticket.Signature)
            ? SelfUninstallCompletionValidation.Valid()
            : SelfUninstallCompletionValidation.Reject("completion_signature_invalid");
    }

    public static SelfUninstallCompletionValidation ValidateCleanupEvidence(
        SelfUninstallCleanupEvidence evidence)
    {
        if (evidence.SchemaVersion != SchemaVersion ||
            !Exact(evidence.DataPolicy, DataPolicy) ||
            !Exact(evidence.MaintenanceCohort, MaintenanceCohort) ||
            !IsSafeToken(evidence.MaintenanceVersion, 80))
            return SelfUninstallCompletionValidation.Reject("cleanup_evidence_identity_invalid");
        var falseCount = new[]
        {
            evidence.ServicesAbsent,
            evidence.ScheduledUninstallTaskAbsent,
            evidence.ProtocolRegistrationAbsent,
            evidence.ArpRegistrationAbsent,
            evidence.InstallDirectoryAbsent,
            evidence.RuntimeDirectoryAbsent,
            evidence.RetainedEvidencePresent,
            evidence.OperationalCredentialsAbsent,
        }.Count(value => !value);
        if (evidence.ResidueCount != falseCount || evidence.ResidueCount != 0)
            return SelfUninstallCompletionValidation.Reject("cleanup_not_terminal");
        return SelfUninstallCompletionValidation.Valid();
    }

    public static string Serialize(SelfUninstallCompletionEnvelope envelope) =>
        JsonSerializer.Serialize(envelope, JsonOptions);

    public static bool TryDeserialize(
        string json,
        out SelfUninstallCompletionEnvelope? envelope,
        out string rejectionCode)
    {
        envelope = null;
        rejectionCode = "completion_invalid_json";
        if (string.IsNullOrWhiteSpace(json))
        {
            rejectionCode = "completion_empty";
            return false;
        }
        if (Encoding.UTF8.GetByteCount(json) > MaxEnvelopeBytes)
        {
            rejectionCode = "completion_too_large";
            return false;
        }
        try
        {
            envelope = JsonSerializer.Deserialize<SelfUninstallCompletionEnvelope>(json, JsonOptions);
            if (envelope is null)
            {
                rejectionCode = "completion_null";
                return false;
            }
            rejectionCode = "valid";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool IsExactFinalizedResponse(
        string json,
        SelfUninstallCompletionEnvelope expectedEnvelope,
        out string receiptDigest)
    {
        receiptDigest = string.Empty;
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaxResponseBytes)
            return false;
        try
        {
            var response = JsonSerializer.Deserialize<FinalizeResponse>(json, JsonOptions);
            if (response is null ||
                !Exact(response.Status, "finalized") ||
                !Exact(response.CommandId, expectedEnvelope.Ticket.CommandId) ||
                !IsLowerHex64(response.ReceiptDigest) ||
                !FixedTimeAsciiEquals(
                    response.ReceiptDigest,
                    ComputeReceiptDigest(expectedEnvelope.Ticket)))
                return false;
            receiptDigest = response.ReceiptDigest;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool VerifyDeviceSignature(
        string publicKeySpki,
        string canonical,
        string signature)
    {
        if (!TryBase64UrlDecode(signature, out var signatureBytes) ||
            signatureBytes.Length != 64)
            return false;
        try
        {
            var publicKey = Convert.FromBase64String(publicKeySpki);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            return consumed == publicKey.Length &&
                   verifier.KeySize == 256 &&
                   verifier.VerifyData(
                       Encoding.UTF8.GetBytes(canonical),
                       signatureBytes,
                       HashAlgorithmName.SHA256,
                       DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            return false;
        }
    }

    private static string LowerSha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string BooleanToken(bool value) => value ? "true" : "false";

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool TryBase64UrlDecode(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value) || value.Contains('=') ||
            value.Any(character => character is not (>= 'A' and <= 'Z') and
                                         not (>= 'a' and <= 'z') and
                                         not (>= '0' and <= '9') and
                                         not '-' and not '_'))
            return false;
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += (padded.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        try
        {
            bytes = Convert.FromBase64String(padded);
            return Base64UrlEncode(bytes).Equals(value, StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(character => character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static bool IsSafeToken(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':');

    private static bool IsCanonicalText(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= maximumLength &&
        value.All(character => character != '|' && !char.IsControl(character));

    private static bool Exact(string? left, string? right) =>
        string.Equals(left, right, StringComparison.Ordinal);

    private static bool FixedTimeAsciiEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private sealed record FinalizeResponse(
        string Status,
        string CommandId,
        string ReceiptDigest);
}
