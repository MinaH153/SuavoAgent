using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests;

[Collection("IpcRejectionStats")]
public sealed class HealthSnapshotTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(),
        $"suavo_health_{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public HealthSnapshotTests()
    {
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void Take_Includes_Ipc_Rejection_Diagnostics()
    {
        var before = IpcRejectionStats.Count;
        IpcRejectionStats.Record("health_snapshot_test");

        using var services = new ServiceCollection()
            .BuildServiceProvider();
        var options = new AgentOptions
        {
            AgentId = "agent-health-test",
            Version = "test",
            PharmacyId = "pharmacy-health-test",
        };

        var snapshot = new HealthSnapshot(
            options,
            _db,
            services,
            DateTimeOffset.UtcNow.AddSeconds(-5)).Take();

        var helper = snapshot.GetProperty("helper");
        Assert.False(helper.GetProperty("attached").GetBoolean());
        Assert.True(helper.GetProperty("ipcRejectionCount").GetInt64() >= before + 1);
        Assert.Equal(
            "health_snapshot_test",
            helper.GetProperty("lastIpcRejectReason").GetString());
        Assert.True(
            DateTimeOffset.TryParse(
                helper.GetProperty("lastIpcRejectAt").GetString(),
                out _));
    }

    [Fact]
    public void Take_Includes_MachineFingerprint_ConfigSync_And_CrashLogMetadataWithoutBody()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_runtime_health_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(root, "logs"));
        var crashPath = Path.Combine(root, "logs", "startup-crash.log");
        File.WriteAllText(crashPath, "System.Exception: patient Jane Doe should never leave this file");
        var configHealthPath = RuntimeHealthEvidence.ConfigSyncHealthPath(root);
        File.WriteAllText(configHealthPath, """
            {
              "status": "failed",
              "lastAttemptAt": "2026-04-29T12:00:00.0000000+00:00",
              "lastSuccessAt": null,
              "consecutiveFailures": 3,
              "lastErrorKind": "http_401",
              "lastAppliedOverrideCount": 2
            }
            """);
        File.WriteAllText(RuntimeHealthEvidence.CloudAuthHealthPath(root), """
            {
              "status": "failed",
              "lastAttemptAt": "2026-05-01T09:15:00.0000000+00:00",
              "lastSuccessAt": null,
              "consecutiveFailures": 2,
              "lastErrorKind": "http_401_Agent_not_found",
              "recoveryAttempted": true,
              "recoveryOutcome": "http_404_Not_Found",
              "restartRequested": false
            }
            """);

        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var options = new AgentOptions
            {
                AgentId = "agent-health-test",
                Version = "test",
                PharmacyId = "pharmacy-health-test",
                MachineFingerprint = "machine-fp-test",
            };

            var snapshot = new HealthSnapshot(
                options,
                _db,
                services,
                DateTimeOffset.UtcNow.AddSeconds(-5),
                root).Take();

            Assert.Equal("machine-fp-test", snapshot.GetProperty("machineFingerprint").GetString());
            var runtime = snapshot.GetProperty("runtimeHealth");
            var configSync = runtime.GetProperty("configSync");
            Assert.True(configSync.GetProperty("present").GetBoolean());
            Assert.Equal("failed", configSync.GetProperty("status").GetString());
            Assert.Equal(3, configSync.GetProperty("consecutiveFailures").GetInt32());
            var cloudAuth = runtime.GetProperty("cloudAuth");
            Assert.True(cloudAuth.GetProperty("present").GetBoolean());
            Assert.Equal("failed", cloudAuth.GetProperty("status").GetString());
            Assert.Equal("http_401_Agent_not_found", cloudAuth.GetProperty("lastErrorKind").GetString());
            Assert.True(cloudAuth.GetProperty("recoveryAttempted").GetBoolean());
            Assert.Equal("http_404_Not_Found", cloudAuth.GetProperty("recoveryOutcome").GetString());
            Assert.False(cloudAuth.GetProperty("restartRequested").GetBoolean());
            var update = runtime.GetProperty("update");
            Assert.False(update.GetProperty("present").GetBoolean());
            Assert.Equal("missing", update.GetProperty("status").GetString());

            var crash = runtime.GetProperty("crashLogs").EnumerateArray()
                .Single(log => log.GetProperty("component").GetString() == "core");
            Assert.True(crash.GetProperty("exists").GetBoolean());
            Assert.True(crash.GetProperty("bytes").GetInt64() > 0);
            Assert.True(crash.GetProperty("sha256Prefix").GetString()?.Length == 16);

            var serialized = snapshot.GetRawText();
            Assert.DoesNotContain("Jane Doe", serialized);
            Assert.DoesNotContain("System.Exception", serialized);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeHealthEvidence_Collect_ReadsLegacyConfigSyncHealthFilename()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_runtime_health_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "config sync health.json"), """
            {
              "status": "ok",
              "lastAttemptAt": "2026-05-06T21:11:59.0000000+00:00",
              "lastSuccessAt": "2026-05-06T21:11:59.0000000+00:00",
              "consecutiveFailures": 0,
              "lastErrorKind": null,
              "lastAppliedOverrideCount": 0
            }
            """);

        try
        {
            var payload = RuntimeHealthEvidence.Collect(root);

            Assert.True(payload.ConfigSync.Present);
            Assert.Equal("ok", payload.ConfigSync.Status);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void RuntimeHealthEvidence_Collect_IncludesDigestBackedInstallReceipt()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_runtime_health_{Guid.NewGuid():N}");
        var installDir = Path.Combine(Path.GetTempPath(), $"suavo_install_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(installDir);
        foreach (var binary in new[]
        {
            "SuavoAgent.Core.exe",
            "SuavoAgent.Broker.exe",
            "SuavoAgent.Helper.exe",
            "SuavoAgent.Watchdog.exe",
            "SuavoSetup.exe",
        })
        {
            File.WriteAllText(Path.Combine(installDir, binary), $"binary:{binary}");
        }

        try
        {
            var payload = RuntimeHealthEvidence.Collect(root, installDir);

            Assert.True(payload.Install.Present);
            Assert.Equal("ok", payload.Install.Status);
            Assert.Equal(5, payload.Install.Binaries.Count);
            Assert.Matches("^[a-f0-9]{64}$", payload.Install.ReceiptSha256);
            Assert.All(payload.Install.Binaries, binary =>
            {
                Assert.True(binary.Exists);
                Assert.True(binary.Bytes > 0);
                Assert.Matches("^[a-f0-9]{64}$", binary.Sha256);
                Assert.Matches("^[a-f0-9]{16}$", binary.Sha256Prefix);
                Assert.DoesNotContain(Path.DirectorySeparatorChar.ToString(), binary.Name);
            });
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
            try { Directory.Delete(installDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Take_Includes_UpdateHealthEvidence_WithoutErrorBody()
    {
        var root = Path.Combine(Path.GetTempPath(), $"suavo_runtime_health_{Guid.NewGuid():N}");
        RuntimeHealthEvidence.WriteUpdateHealth(
            RuntimeHealthEvidence.UpdateHealthPath(root),
            status: "failed",
            targetVersion: "v3.14.4",
            lastAttemptAt: DateTimeOffset.Parse("2026-05-06T12:00:00+00:00"),
            lastSuccessAt: null,
            consecutiveFailures: 1,
            lastErrorKind: "apply_returned_without_exit",
            channel: "stable");

        try
        {
            using var services = new ServiceCollection().BuildServiceProvider();
            var options = new AgentOptions
            {
                AgentId = "agent-health-test",
                Version = "test",
                PharmacyId = "pharmacy-health-test",
            };

            var snapshot = new HealthSnapshot(
                options,
                _db,
                services,
                DateTimeOffset.UtcNow.AddSeconds(-5),
                root).Take();

            var update = snapshot
                .GetProperty("runtimeHealth")
                .GetProperty("update");
            Assert.True(update.GetProperty("present").GetBoolean());
            Assert.Equal("failed", update.GetProperty("status").GetString());
            Assert.Equal("v3.14.4", update.GetProperty("targetVersion").GetString());
            Assert.Equal("apply_returned_without_exit", update.GetProperty("lastErrorKind").GetString());

            var serialized = snapshot.GetRawText();
            Assert.DoesNotContain("System.Exception", serialized);
            Assert.DoesNotContain("Jane Doe", serialized);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
