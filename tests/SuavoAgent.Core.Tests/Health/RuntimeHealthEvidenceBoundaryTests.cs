using System.Text.Json;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

public sealed class RuntimeHealthEvidenceBoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-runtime-health-boundary-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void HealthWriters_AtomicallyRoundTripEveryStructuralField()
    {
        var at = DateTimeOffset.Parse("2026-07-12T12:00:00Z");
        var config = RuntimeHealthEvidence.ConfigSyncHealthPath(_root);
        var cloud = RuntimeHealthEvidence.CloudAuthHealthPath(_root);
        var update = RuntimeHealthEvidence.UpdateHealthPath(_root);

        RuntimeHealthEvidence.WriteConfigSyncHealth(
            config, "failed", at, null, 3, "http_401", 7);
        RuntimeHealthEvidence.WriteCloudAuthHealth(
            cloud, "recovered", at, at.AddMinutes(-1), 1, null,
            recoveryAttempted: true, recoveryOutcome: "rotated", restartRequested: true);
        RuntimeHealthEvidence.WriteUpdateHealth(
            update, "applying", "v4.0.0", at, null, 2, "activation_pending", "canary");

        var configPayload = RuntimeHealthEvidence.ReadConfigSyncHealth(config);
        var cloudPayload = RuntimeHealthEvidence.ReadCloudAuthHealth(cloud);
        var updatePayload = RuntimeHealthEvidence.ReadUpdateHealth(update);
        Assert.Equal("failed", configPayload.Status);
        Assert.Equal(3, configPayload.ConsecutiveFailures);
        Assert.Equal(7, configPayload.LastAppliedOverrideCount);
        Assert.Equal("recovered", cloudPayload.Status);
        Assert.True(cloudPayload.RecoveryAttempted);
        Assert.True(cloudPayload.RestartRequested);
        Assert.Equal("rotated", cloudPayload.RecoveryOutcome);
        Assert.Equal("applying", updatePayload.Status);
        Assert.Equal("v4.0.0", updatePayload.TargetVersion);
        Assert.Equal("canary", updatePayload.Channel);
        Assert.False(File.Exists(config + ".tmp"));
        Assert.False(File.Exists(cloud + ".tmp"));
        Assert.False(File.Exists(update + ".tmp"));
    }

    [Theory]
    [InlineData("config")]
    [InlineData("cloud")]
    [InlineData("update")]
    public void MalformedHealthFile_SurfacesUnreadableWithoutEchoingContent(string kind)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, kind + ".json");
        File.WriteAllText(path, "patient Jane Doe { malformed");

        var payload = kind switch
        {
            "config" => JsonSerializer.Serialize(RuntimeHealthEvidence.ReadConfigSyncHealth(path)),
            "cloud" => JsonSerializer.Serialize(RuntimeHealthEvidence.ReadCloudAuthHealth(path)),
            _ => JsonSerializer.Serialize(RuntimeHealthEvidence.ReadUpdateHealth(path)),
        };

        Assert.Contains("unreadable", payload);
        Assert.Contains("health_file_unreadable", payload);
        Assert.DoesNotContain("Jane Doe", payload);
    }

    [Fact]
    public void HealthReaders_InvalidNumericTypesFailClosedAsUnreadable()
    {
        Directory.CreateDirectory(_root);
        var config = Path.Combine(_root, "wrong-types-config.json");
        var cloud = Path.Combine(_root, "wrong-types-cloud.json");
        var update = Path.Combine(_root, "wrong-types-update.json");
        const string wrongTypes = """
            {"status":17,"lastAttemptAt":false,"lastSuccessAt":{},"consecutiveFailures":"3","lastErrorKind":[],"lastAppliedOverrideCount":null,"recoveryAttempted":"true","recoveryOutcome":1,"restartRequested":1,"targetVersion":{},"channel":false}
            """;
        File.WriteAllText(config, wrongTypes);
        File.WriteAllText(cloud, wrongTypes);
        File.WriteAllText(update, wrongTypes);

        var configPayload = RuntimeHealthEvidence.ReadConfigSyncHealth(config);
        var cloudPayload = RuntimeHealthEvidence.ReadCloudAuthHealth(cloud);
        var updatePayload = RuntimeHealthEvidence.ReadUpdateHealth(update);

        Assert.Equal("unreadable", configPayload.Status);
        Assert.Equal(0, configPayload.ConsecutiveFailures);
        Assert.Null(configPayload.LastAttemptAt);
        Assert.Equal("unreadable", cloudPayload.Status);
        Assert.False(cloudPayload.RecoveryAttempted);
        Assert.False(cloudPayload.RestartRequested);
        Assert.Equal("unreadable", updatePayload.Status);
        Assert.Null(updatePayload.TargetVersion);
        Assert.Null(updatePayload.Channel);
    }

    [Fact]
    public void InstallEvidence_DistinguishesMissingAndPartialCohorts()
    {
        var install = Path.Combine(_root, "install");
        Directory.CreateDirectory(install);

        var missing = RuntimeHealthEvidence.ReadInstallHealth(install);
        File.WriteAllBytes(Path.Combine(install, "SuavoAgent.Core.exe"), [1, 2, 3]);
        var partial = RuntimeHealthEvidence.ReadInstallHealth(install);

        Assert.False(missing.Present);
        Assert.Equal("missing", missing.Status);
        Assert.Null(missing.ReceiptSha256);
        Assert.True(partial.Present);
        Assert.Equal("partial", partial.Status);
        Assert.Null(partial.ReceiptSha256);
        Assert.Single(partial.Binaries, binary => binary.Exists);
    }

    [Theory]
    [InlineData(false, null, "ok")]
    [InlineData(true, "provisioning-1", "not_ready")]
    [InlineData(false, "provisioning-1", "not_ready")]
    public void ActivationReadiness_RequiresDeviceProofOnlyForProvisionedIdentity(
        bool disableSql,
        string? provisioningId,
        string expectedStatus)
    {
        var path = RuntimeHealthEvidence.ActivationReadinessPath(_root);

        RuntimeHealthEvidence.WriteActivationReadiness(
            path,
            "4.0.0",
            "agent-1",
            provisioningId,
            DateTimeOffset.Parse("2026-07-12T12:00:00Z"),
            helperAttached: true,
            ipcConnected: true,
            actuationReady: true,
            sqlConnected: !disableSql,
            schemaCanaryGreen: true,
            pmsCode: "pms_operational",
            deviceProof: null);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal(expectedStatus, root.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("deviceProof").ValueKind);
        Assert.Equal(JsonValueKind.Null, root.GetProperty("probationHealthProof").ValueKind);
    }

    [Fact]
    public void ActivationReadiness_ProjectsSignedProvisioningAndProbationProofs()
    {
        var path = RuntimeHealthEvidence.ActivationReadinessPath(_root);
        var provisioningProof = new SignedDeviceProvisioningProof(
            "device-1", "provisioning-1", "agent-1", "pharmacy-1", "fingerprint-1",
            "key-1", "challenge-1", SqlServerCertificateSha256: null,
            "signature-1", new string('a', 64));
        var probationHealth = new DeviceProbationHealthFields(
            "device-1", "provisioning-1", "agent-1", "pharmacy-1", "fingerprint-1",
            "4.0.0", "key-1", "challenge-2", false, false, false, true, true,
            "pms_schema_canary", SqlServerCertificateSha256: null,
            ObservedAtUtc: "2026-07-13T08:00:00.0000000Z", ChallengeCounter: 1);
        var probationProof = new SignedDeviceProbationHealth(
            probationHealth, "signature-2", new string('b', 64));

        RuntimeHealthEvidence.WriteActivationReadiness(
            path, "4.0.0", "agent-1", "provisioning-1",
            DateTimeOffset.Parse("2026-07-12T12:00:00Z"),
            helperAttached: false, ipcConnected: false, actuationReady: false,
            sqlConnected: true, schemaCanaryGreen: true, pmsCode: "pms_schema_canary",
            provisioningProof, probationProof);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        Assert.Equal("ok", root.GetProperty("status").GetString());
        Assert.Equal("device-1",
            root.GetProperty("deviceProof").GetProperty("deviceCode").GetString());
        Assert.Equal("challenge-2",
            root.GetProperty("probationHealthProof").GetProperty("challenge").GetString());
        Assert.True(root.GetProperty("probationHealthProof")
            .GetProperty("schemaCanaryGreen").GetBoolean());
        Assert.Equal(1, root.GetProperty("probationHealthProof")
            .GetProperty("challengeCounter").GetInt64());
        Assert.Equal(new string('b', 64),
            root.GetProperty("probationHealthProof").GetProperty("canonicalDigest").GetString());
    }

    [Fact]
    public void Collect_CanonicalConfigPathWinsOverLegacyAlias()
    {
        RuntimeHealthEvidence.WriteConfigSyncHealth(
            RuntimeHealthEvidence.LegacyConfigSyncHealthPath(_root),
            "legacy", DateTimeOffset.UtcNow, null, 0, null, 0);
        RuntimeHealthEvidence.WriteConfigSyncHealth(
            RuntimeHealthEvidence.ConfigSyncHealthPath(_root),
            "canonical", DateTimeOffset.UtcNow, null, 0, null, 0);

        var collected = RuntimeHealthEvidence.Collect(_root, Path.Combine(_root, "missing-install"));

        Assert.Equal("canonical", collected.ConfigSync.Status);
        Assert.Equal(3, collected.CrashLogs.Count);
        Assert.All(collected.CrashLogs, crash => Assert.False(crash.Exists));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
