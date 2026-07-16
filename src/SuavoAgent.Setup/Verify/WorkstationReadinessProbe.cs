using System.Text.Json;
using SuavoAgent.Core.Config;

namespace SuavoAgent.Setup.Verify;

internal sealed record WorkstationActivationTarget(
    string Version,
    string AgentId,
    DeviceProvisioningExpectation? PendingProof);

/// <summary>
/// Requires fresh positive evidence from the exact target Core that its Helper
/// is interactive, both IPC directions work, PioneerRx SQL/schema checks pass,
/// and the PMS situation is operational. Missing/transitional evidence remains
/// Warn so the bounded activation milestone can retry; it never means Ready.
/// </summary>
public sealed class WorkstationReadinessProbe
{
    private static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(2);
    private readonly Func<string?> _readHealthJson;
    private readonly Func<WorkstationActivationTarget?> _readExpectedTarget;
    private readonly Func<DateTimeOffset> _utcNow;

    internal WorkstationReadinessProbe(
        Func<string?>? readHealthJson = null,
        Func<WorkstationActivationTarget?>? readExpectedTarget = null,
        Func<DateTimeOffset>? utcNow = null)
    {
        _readHealthJson = readHealthJson ?? ReadDefaultHealth;
        _readExpectedTarget = readExpectedTarget ?? ReadDefaultTarget;
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
    }

    public GateResult Check()
    {
        var json = _readHealthJson();
        if (string.IsNullOrWhiteSpace(json))
            return new GateResult("Workstation", GateState.Warn, "Waiting for interactive workstation proof");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var version = ReadString(root, "version");
            var agentId = ReadString(root, "agentId");
            var provisioningId = ReadString(root, "provisioningId");
            var checkedAtRaw = ReadString(root, "checkedAt");
            var expected = _readExpectedTarget();
            if (expected is null)
            {
                return new GateResult("Workstation", GateState.Warn, "Target identity is not available yet");
            }
            if (!string.Equals(NormalizeVersion(version), NormalizeVersion(expected.Version), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(agentId, expected.AgentId, StringComparison.OrdinalIgnoreCase))
            {
                return new GateResult("Workstation", GateState.Warn, "Waiting for proof from the target release");
            }
            if (expected.PendingProof is { } pending &&
                !string.Equals(
                    provisioningId,
                    pending.ProvisioningId,
                    StringComparison.OrdinalIgnoreCase))
            {
                return new GateResult("Workstation", GateState.Warn, "Waiting for proof from this install transaction");
            }
            if (expected.PendingProof is null && !string.IsNullOrWhiteSpace(provisioningId))
                return new GateResult("Workstation", GateState.Warn, "Waiting for active workstation proof");
            if (!DateTimeOffset.TryParse(checkedAtRaw, out var checkedAt) ||
                checkedAt > _utcNow().AddMinutes(1) ||
                _utcNow() - checkedAt > MaxAge)
            {
                return new GateResult("Workstation", GateState.Warn, "Workstation proof is stale");
            }

            var hasDeviceProof = expected.PendingProof is { } expectedProof
                ? DeviceProvisioningProofReader.TryRead(root, expectedProof, out _)
                : root.TryGetProperty("deviceProof", out var activeProof) &&
                  activeProof.ValueKind == JsonValueKind.Null;
            var ready = hasDeviceProof &&
                        string.Equals(ReadString(root, "status"), "ok", StringComparison.Ordinal) &&
                        ReadBool(root, "helperAttached") &&
                        ReadBool(root, "ipcConnected") &&
                        ReadBool(root, "actuationReady") &&
                        ReadBool(root, "sqlConnected") &&
                        ReadBool(root, "schemaCanaryGreen") &&
                        string.Equals(ReadString(root, "pmsCode"), "pms_operational", StringComparison.Ordinal);
            return ready
                ? new GateResult("Workstation", GateState.Ok, "Helper, IPC, PioneerRx, and schema checks are ready")
                : new GateResult("Workstation", GateState.Warn, "Waiting for Helper and PioneerRx readiness");
        }
        catch
        {
            return new GateResult("Workstation", GateState.Warn, "Workstation proof is unreadable");
        }
    }

    private static string? ReadDefaultHealth()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "activation-readiness.json");
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch { return null; }
    }

    private static WorkstationActivationTarget? ReadDefaultTarget()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path) || !OperatingSystem.IsWindows()) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("Agent", out var agent))
                return null;
            var store = new DpapiCredentialStore();
            var version = ReadString(agent, "Version");
            var agentId = ReadString(agent, "AgentId");
            var pharmacyId = ReadString(agent, "PharmacyId");
            var fingerprint = ReadString(agent, "MachineFingerprint");
            var sqlServerCertificateSha256 = ReadString(
                agent,
                "SqlServerCertificateSha256");
            var provisioningId = store.Get(CredentialKeys.PendingProvisioningId);
            var deviceCode = store.Get(CredentialKeys.PendingDeviceCode);
            var keyId = store.Get(CredentialKeys.PendingDeviceKeyId);
            var challenge = store.Get(CredentialKeys.PendingDeviceChallenge);
            var pendingValues = new[]
                {
                    provisioningId, deviceCode, keyId, challenge,
                };
            if (new[] { version, agentId, pharmacyId, fingerprint }.Any(string.IsNullOrWhiteSpace))
                return null;
            if (pendingValues.All(string.IsNullOrWhiteSpace))
                return new(version!, agentId!, PendingProof: null);
            if (pendingValues.Any(string.IsNullOrWhiteSpace)) return null;

            var pendingPromoted =
                string.Equals(
                    store.Get(CredentialKeys.AuthKey),
                    store.Get(CredentialKeys.PendingAuthKey),
                    StringComparison.Ordinal) &&
                string.Equals(
                    store.Get(CredentialKeys.AgentId),
                    store.Get(CredentialKeys.PendingAgentId),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    store.Get(CredentialKeys.PharmacyId),
                    store.Get(CredentialKeys.PendingPharmacyId),
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    store.Get(CredentialKeys.DeviceKeyId),
                    store.Get(CredentialKeys.PendingDeviceKeyId),
                    StringComparison.Ordinal);
            if (pendingPromoted)
                return new(version!, agentId!, PendingProof: null);
            return new(
                version!,
                agentId!,
                new(
                    deviceCode!,
                    provisioningId!,
                    agentId!,
                    pharmacyId!,
                    fingerprint!,
                    keyId!,
                    challenge!,
                    sqlServerCertificateSha256));
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool ReadBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        value.GetBoolean();

    private static string NormalizeVersion(string? value) =>
        value?.Trim().TrimStart('v', 'V') ?? string.Empty;
}
