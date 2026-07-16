using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Security;

public sealed record DeviceProvisioningProofFields(
    string DeviceCode,
    string ProvisioningId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string KeyId,
    string Challenge,
    string? SqlServerCertificateSha256 = null);

public sealed record DeviceProbationHealthFields(
    string DeviceCode,
    string ProvisioningId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string Version,
    string KeyId,
    string Challenge,
    bool HelperAttached,
    bool IpcConnected,
    bool ActuationReady,
    bool SqlConnected,
    bool SchemaCanaryGreen,
    string PmsCode,
    string? SqlServerCertificateSha256 = null,
    string ObservedAtUtc = "",
    long ChallengeCounter = 0);

public static class DeviceProvisioningProofCanonical
{
    public const string Domain = "suavo.device-provisioning.v2";

    public static string Serialize(DeviceProvisioningProofFields proof)
    {
        ArgumentNullException.ThrowIfNull(proof);
        var fields = new[]
        {
            proof.DeviceCode,
            proof.ProvisioningId,
            proof.AgentId,
            proof.PharmacyId,
            proof.Fingerprint,
            proof.KeyId,
            proof.Challenge,
        };
        if (fields.Any(value => string.IsNullOrWhiteSpace(value) ||
                                value.Any(character => character is '\r' or '\n')))
            throw new InvalidOperationException(
                "Provisioning proof fields must be non-empty single-line values.");
        return Domain + "\n" + string.Join('\n', fields) + "\n" +
               $"sqlServerCertificateSha256={CertificateBinding(proof.SqlServerCertificateSha256)}";
    }

    public static string Digest(DeviceProvisioningProofFields proof) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Serialize(proof)))).ToLowerInvariant();

    public static bool IsP1363Signature(string value)
    {
        try
        {
            var normalized = value.Replace('-', '+').Replace('_', '/');
            normalized = normalized.PadRight(
                normalized.Length + (4 - normalized.Length % 4) % 4,
                '=');
            return Convert.FromBase64String(normalized).Length == 64;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    internal static string CertificateBinding(string? value)
    {
        if (value is null) return "none";
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9') and
                not (>= 'a' and <= 'f')))
            throw new InvalidOperationException(
                "SQL Server certificate binding must be null or lowercase SHA-256.");
        return value;
    }
}

public static class DeviceProbationHealthCanonical
{
    public const string Domain = "suavo.device-probation-health.v3";

    public static string Serialize(DeviceProbationHealthFields health)
    {
        ArgumentNullException.ThrowIfNull(health);
        var identity = new[]
        {
            health.DeviceCode,
            health.ProvisioningId,
            health.AgentId,
            health.PharmacyId,
            health.Fingerprint,
            health.Version,
            health.KeyId,
            health.Challenge,
            health.PmsCode,
        };
        if (identity.Any(value => string.IsNullOrWhiteSpace(value) ||
                                  value.Any(character => character is '\r' or '\n')))
            throw new InvalidOperationException(
                "Probation health fields must be non-empty single-line values.");
        if (!DateTimeOffset.TryParseExact(
                health.ObservedAtUtc,
                "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal |
                System.Globalization.DateTimeStyles.AdjustToUniversal,
                out _) ||
            health.ChallengeCounter != 1)
            throw new InvalidOperationException(
                "Probation health must bind the one-time server challenge counter and an exact UTC observation time.");
        static string Bool(bool value) => value ? "true" : "false";
        return Domain + "\n" +
               string.Join('\n', identity.Take(8)) + "\n" +
               $"sqlServerCertificateSha256={DeviceProvisioningProofCanonical.CertificateBinding(health.SqlServerCertificateSha256)}\n" +
               $"observedAtUtc={health.ObservedAtUtc}\n" +
               $"challengeCounter={health.ChallengeCounter}\n" +
               $"helperAttached={Bool(health.HelperAttached)}\n" +
               $"ipcConnected={Bool(health.IpcConnected)}\n" +
               $"actuationReady={Bool(health.ActuationReady)}\n" +
               $"sqlConnected={Bool(health.SqlConnected)}\n" +
               $"schemaCanaryGreen={Bool(health.SchemaCanaryGreen)}\n" +
               $"pmsCode={health.PmsCode}";
    }

    public static string Digest(DeviceProbationHealthFields health) =>
        Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Serialize(health)))).ToLowerInvariant();
}
