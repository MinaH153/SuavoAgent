using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Cloud;

internal sealed record PomActivationDeviceReceipt(
    int SchemaVersion,
    string CommandId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string PomId,
    string SessionId,
    string ApprovedModelDigest,
    string ApprovedTemplateDigest,
    string ApprovedBy,
    string ResultCode,
    long Counter,
    string CompletedAt,
    string CommandPayloadDigest);

internal sealed record RxSourceDeviceReceipt(
    int SchemaVersion,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string BatchDigest,
    string SourceKind,
    string SourceBindingId,
    string Pms,
    string SchemaSignature,
    string? PomId,
    string? SessionId,
    string? ModelDigest,
    string? TemplateDigest,
    long Counter,
    string CapturedAt);

internal sealed record SeedApplicationDeviceReceipt(
    int SchemaVersion,
    string CommandId,
    string AgentId,
    string PharmacyId,
    string DeviceKeyId,
    string SeedDigest,
    long SeedVersion,
    string Phase,
    string SourceManifestDigest,
    string SessionId,
    string AppliedAt,
    int CorrelationsApplied,
    int CorrelationsSkipped,
    long Counter);

internal sealed record AutonomyEvidenceDeviceReceipt(
    int SchemaVersion,
    string ReceiptId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string DeviceKeyId,
    string TaskType,
    string TaskKey,
    string AppId,
    string AppVersion,
    string SelectorDigest,
    string TemplateDigest,
    string ModelDigest,
    string ExecutorMode,
    string ScopeDigest,
    bool Supervised,
    int WorkItemCount,
    string SemanticResult,
    bool PostconditionSatisfied,
    string PostconditionDigest,
    bool Clean,
    int LocalStreak,
    int LocalTotalRuns,
    long Counter,
    string CompletedAt);

internal sealed record SignedDeviceReceipt<T>(
    T Receipt,
    string KeyId,
    string Signature,
    string CanonicalDigest);

internal sealed record DeviceProvisioningProofPayload(
    string DeviceCode,
    string ProvisioningId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string KeyId,
    string Challenge,
    string? SqlServerCertificateSha256);

internal sealed record SignedDeviceProvisioningProof(
    string DeviceCode,
    string ProvisioningId,
    string AgentId,
    string PharmacyId,
    string Fingerprint,
    string KeyId,
    string Challenge,
    string? SqlServerCertificateSha256,
    string Signature,
    string CanonicalDigest);

internal sealed record SignedDeviceProbationHealth(
    DeviceProbationHealthFields Health,
    string Signature,
    string CanonicalDigest);

internal interface IDeviceAuthoritySigner : IDisposable
{
    string KeyId { get; }
    SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(PomActivationDeviceReceipt receipt);
    SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(RxSourceDeviceReceipt receipt);
    SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
        SeedApplicationDeviceReceipt receipt);
    SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
        AutonomyEvidenceDeviceReceipt receipt);
    SignedDeviceProvisioningProof SignProvisioningProof(
        DeviceProvisioningProofPayload proof);
    SignedDeviceProbationHealth SignProbationHealth(
        DeviceProbationHealthFields health);
}

internal sealed class DeviceAuthoritySigner : IDeviceAuthoritySigner
{
    private readonly IDeviceAttestationKey _key;

    internal DeviceAuthoritySigner(
        AgentOptions options,
        IDeviceAttestationKeyProvider? provider = null)
    {
        if (string.IsNullOrWhiteSpace(options.MachineFingerprint))
            throw new InvalidOperationException("Device authority fingerprint is unavailable.");
        provider ??= DeviceAttestationKeyProvider.CreateProduction();
        _key = string.IsNullOrWhiteSpace(options.DeviceAttestationKeyId) &&
               string.IsNullOrWhiteSpace(options.DeviceAttestationKeyName)
            ? provider.OpenExisting(options.MachineFingerprint)
            : !string.IsNullOrWhiteSpace(options.DeviceAttestationKeyId) &&
              !string.IsNullOrWhiteSpace(options.DeviceAttestationKeyName)
                ? provider.OpenVersion(
                options.MachineFingerprint,
                options.DeviceAttestationKeyName,
                options.DeviceAttestationKeyId)
                : throw new InvalidOperationException(
                    "Device authority key version metadata is incomplete.");
    }

    public string KeyId => _key.Enrollment.KeyId;

    public SignedDeviceReceipt<PomActivationDeviceReceipt> Sign(
        PomActivationDeviceReceipt receipt) => SignCore(
            receipt,
            "suavo.pom-activation.v1\n");

    public SignedDeviceReceipt<RxSourceDeviceReceipt> Sign(
        RxSourceDeviceReceipt receipt) => SignCore(
            receipt,
            "suavo.rx-source.v1\n");

    public SignedDeviceReceipt<SeedApplicationDeviceReceipt> Sign(
        SeedApplicationDeviceReceipt receipt) => SignCore(
            receipt,
            "suavo.seed-application.v1\n");

    public SignedDeviceReceipt<AutonomyEvidenceDeviceReceipt> Sign(
        AutonomyEvidenceDeviceReceipt receipt) => SignCore(
            receipt,
            "suavo.autonomy-evidence.v1\n");

    public SignedDeviceProvisioningProof SignProvisioningProof(
        DeviceProvisioningProofPayload proof)
    {
        if (!string.Equals(proof.KeyId, KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Provisioning proof key id does not match the opened device key.");
        var canonical = DeviceAuthorityCanonical.ProvisioningProof(proof);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return new(
            proof.DeviceCode,
            proof.ProvisioningId,
            proof.AgentId,
            proof.PharmacyId,
            proof.Fingerprint,
            proof.KeyId,
            proof.Challenge,
            proof.SqlServerCertificateSha256,
            Base64Url(_key.Sign(bytes)),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public SignedDeviceProbationHealth SignProbationHealth(
        DeviceProbationHealthFields health)
    {
        if (!string.Equals(health.KeyId, KeyId, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Probation health key id does not match the opened device key.");
        var canonical = DeviceProbationHealthCanonical.Serialize(health);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        return new(
            health,
            Base64Url(_key.Sign(bytes)),
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private SignedDeviceReceipt<T> SignCore<T>(T receipt, string domain)
    {
        var canonical = domain + DeviceAuthorityCanonical.Serialize(receipt);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var signature = Base64Url(_key.Sign(bytes));
        return new(
            receipt,
            _key.Enrollment.KeyId,
            signature,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    public void Dispose() => _key.Dispose();

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

internal static class DeviceAuthorityCanonical
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    internal static string Serialize<T>(T value)
    {
        var element = JsonSerializer.SerializeToElement(value, WebJson);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions
        {
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            Indented = false,
        }))
        {
            WriteSorted(writer, element);
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static string HashUnsignedSync(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Rx sync payload must be an object.");
        var normalized = new Dictionary<string, object?>
        {
            ["snapshotType"] = payload.GetProperty("snapshotType").Clone(),
            ["data"] = payload.GetProperty("data").Clone(),
            ["sqlConnected"] = payload.TryGetProperty("sqlConnected", out var sql) && sql.GetBoolean(),
            ["uiaConnected"] = payload.TryGetProperty("uiaConnected", out var uia) && uia.GetBoolean(),
        };
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Serialize(normalized)))).ToLowerInvariant();
    }

    internal static string HashPomCommand(JsonElement data)
    {
        return Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Serialize(data)))).ToLowerInvariant();
    }

    internal static string ProvisioningProof(DeviceProvisioningProofPayload proof)
        => DeviceProvisioningProofCanonical.Serialize(new(
            proof.DeviceCode,
            proof.ProvisioningId,
            proof.AgentId,
            proof.PharmacyId,
            proof.Fingerprint,
            proof.KeyId,
            proof.Challenge,
            proof.SqlServerCertificateSha256));

    private static void WriteSorted(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject()
                             .OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteSorted(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteSorted(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }
}
