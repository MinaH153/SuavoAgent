using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Maintenance;

public sealed record SelfUninstallBrokerAcceptance(
    int SchemaVersion,
    string CommandId,
    string CommandNonce,
    string AgentId,
    string MachineFingerprint,
    string RequestDigest,
    string AcceptedAtUtc,
    string AuthorityExpiresAtUtc,
    string MaintenanceKeyId,
    string MaintenancePublicKeySpki,
    string Signature);

/// <summary>
/// Durable LocalSystem Broker receipt proving the exact cloud-signed uninstall
/// entered the privileged maintenance outbox before its live authority expired.
/// Once valid, delayed/restarted maintenance validates this receipt instead of
/// pretending the original five-minute lease must remain live forever.
/// </summary>
public static class SelfUninstallAcceptanceContract
{
    public const int SchemaVersion = 1;
    public const string FileSuffix = ".broker-accepted.json";
    public const int MaxReceiptBytes = 16 * 1024;
    private const string Domain = "suavo.self-uninstall-broker-accepted.v1";
    private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
        WriteIndented = false,
    };

    public static string PathForClaim(string claimPath) =>
        Path.GetFullPath(claimPath) + FileSuffix;

    public static string Serialize(SelfUninstallBrokerAcceptance receipt) =>
        JsonSerializer.Serialize(receipt, JsonOptions);

    public static bool TryDeserialize(
        string json,
        out SelfUninstallBrokerAcceptance? receipt)
    {
        receipt = null;
        if (string.IsNullOrWhiteSpace(json) ||
            Encoding.UTF8.GetByteCount(json) > MaxReceiptBytes)
            return false;
        try
        {
            receipt = JsonSerializer.Deserialize<SelfUninstallBrokerAcceptance>(
                json, JsonOptions);
            return receipt is not null;
        }
        catch (JsonException) { return false; }
    }

    public static string BuildCanonical(SelfUninstallBrokerAcceptance receipt) =>
        string.Join('|',
            Domain,
            receipt.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            receipt.CommandId,
            receipt.CommandNonce,
            receipt.AgentId,
            receipt.MachineFingerprint,
            receipt.RequestDigest,
            receipt.AcceptedAtUtc,
            receipt.AuthorityExpiresAtUtc,
            receipt.MaintenanceKeyId,
            receipt.MaintenancePublicKeySpki);

    public static SelfUninstallValidationResult Validate(
        SelfUninstallBrokerAcceptance receipt,
        SelfUninstallRequest request,
        string exactRequestJson,
        string expectedAgentId,
        string expectedFingerprint,
        string expectedMaintenanceKeyId,
        IReadOnlyDictionary<string, string> trustedCommandKeys)
    {
        if (receipt.SchemaVersion != SchemaVersion ||
            !string.Equals(receipt.CommandId, request.CommandId, StringComparison.Ordinal) ||
            !string.Equals(receipt.CommandNonce, request.Nonce, StringComparison.Ordinal) ||
            !string.Equals(receipt.AgentId, expectedAgentId, StringComparison.Ordinal) ||
            !string.Equals(receipt.AgentId, request.AgentId, StringComparison.Ordinal) ||
            !string.Equals(receipt.MachineFingerprint, expectedFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.MachineFingerprint, request.MachineFingerprint, StringComparison.Ordinal) ||
            !string.Equals(receipt.MaintenanceKeyId, expectedMaintenanceKeyId, StringComparison.Ordinal) ||
            !string.Equals(
                receipt.RequestDigest,
                RemoteCommandTrust.ComputeSha256Hex(exactRequestJson),
                StringComparison.Ordinal))
            return SelfUninstallValidationResult.Reject("broker_acceptance_binding_invalid");
        if (!SelfUninstallContract.TryReadCommandAuthorityData(
                request.DataJson, out _, out var expiresAt) ||
            !string.Equals(receipt.AuthorityExpiresAtUtc, expiresAt, StringComparison.Ordinal) ||
            !DateTimeOffset.TryParse(receipt.AcceptedAtUtc, out var acceptedAt) ||
            !DateTimeOffset.TryParse(expiresAt, out var authorityExpiry) ||
            acceptedAt >= authorityExpiry)
            return SelfUninstallValidationResult.Reject("broker_acceptance_time_invalid");
        var requestValidation = SelfUninstallContract.Validate(
            request,
            expectedAgentId,
            expectedFingerprint,
            trustedCommandKeys,
            acceptedAt);
        if (!requestValidation.IsValid) return requestValidation;
        try
        {
            var spki = Convert.FromBase64String(receipt.MaintenancePublicKeySpki);
            if (!string.Equals(
                    Convert.ToHexString(SHA256.HashData(spki)).ToLowerInvariant(),
                    receipt.MaintenanceKeyId,
                    StringComparison.Ordinal))
                return SelfUninstallValidationResult.Reject("broker_acceptance_key_invalid");
            var signature = Base64UrlDecode(receipt.Signature);
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(spki, out var consumed);
            if (consumed != spki.Length || signature.Length != 64 ||
                !verifier.VerifyData(
                    Encoding.UTF8.GetBytes(BuildCanonical(receipt with { Signature = string.Empty })),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                return SelfUninstallValidationResult.Reject("broker_acceptance_signature_invalid");
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return SelfUninstallValidationResult.Reject("broker_acceptance_signature_invalid");
        }
        return SelfUninstallValidationResult.Valid();
    }

    public static string FormatTimestamp(DateTimeOffset value) =>
        value.UtcDateTime.ToString(TimestampFormat, CultureInfo.InvariantCulture);

    public static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var standard = value.Replace('-', '+').Replace('_', '/');
        standard += (standard.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        return Convert.FromBase64String(standard);
    }
}
