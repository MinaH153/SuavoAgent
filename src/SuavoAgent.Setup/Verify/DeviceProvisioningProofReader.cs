using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Verify;

internal sealed record DeviceProvisioningExpectation(
    string DeviceCode,
    string ProvisioningId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string KeyId,
    string Challenge,
    string? SqlServerCertificateSha256);

internal sealed record LocalDeviceProvisioningProof(
    DeviceProvisioningProofFields Fields,
    string Signature,
    string CanonicalDigest);

internal static class DeviceProvisioningProofReader
{
    internal static bool TryRead(
        JsonElement readinessRoot,
        DeviceProvisioningExpectation expected,
        out LocalDeviceProvisioningProof? proof)
    {
        proof = null;
        if (!readinessRoot.TryGetProperty("deviceProof", out var value) ||
            value.ValueKind != JsonValueKind.Object)
            return false;
        var names = value.EnumerateObject().Select(property => property.Name).ToArray();
        var exact = new[]
        {
            "deviceCode", "provisioningId", "agentId", "pharmacyId",
            "fingerprint", "keyId", "challenge", "sqlServerCertificateSha256",
            "signature", "canonicalDigest",
        };
        if (names.Length != exact.Length || exact.Any(name => !names.Contains(name, StringComparer.Ordinal)))
            return false;

        string? Read(string name) =>
            value.TryGetProperty(name, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;
        var fields = new DeviceProvisioningProofFields(
            Read("deviceCode") ?? "",
            Read("provisioningId") ?? "",
            Read("agentId") ?? "",
            Read("pharmacyId") ?? "",
            Read("fingerprint") ?? "",
            Read("keyId") ?? "",
            Read("challenge") ?? "",
            value.TryGetProperty("sqlServerCertificateSha256", out var certificate) &&
            certificate.ValueKind == JsonValueKind.String
                ? certificate.GetString()
                : null);
        var signature = Read("signature") ?? "";
        var digest = Read("canonicalDigest") ?? "";
        if (!string.Equals(fields.DeviceCode, expected.DeviceCode, StringComparison.Ordinal) ||
            !string.Equals(fields.ProvisioningId, expected.ProvisioningId, StringComparison.Ordinal) ||
            !string.Equals(fields.AgentId, expected.AgentId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fields.PharmacyId, expected.PharmacyId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(fields.Fingerprint, expected.Fingerprint, StringComparison.Ordinal) ||
            !string.Equals(fields.KeyId, expected.KeyId, StringComparison.Ordinal) ||
            !string.Equals(fields.Challenge, expected.Challenge, StringComparison.Ordinal) ||
            !string.Equals(
                fields.SqlServerCertificateSha256,
                expected.SqlServerCertificateSha256,
                StringComparison.Ordinal) ||
            !DeviceProvisioningProofCanonical.IsP1363Signature(signature))
            return false;
        var calculated = DeviceProvisioningProofCanonical.Digest(fields);
        if (digest.Length != calculated.Length ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(digest),
                Encoding.ASCII.GetBytes(calculated)))
            return false;
        proof = new(fields, signature, digest);
        return true;
    }
}
