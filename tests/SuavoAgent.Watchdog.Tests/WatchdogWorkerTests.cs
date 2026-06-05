using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public class WatchdogWorkerTests
{
    private sealed class FakeCommand : IServiceCommand
    {
        public Dictionary<string, Queue<ServiceState>> Queries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> StartCalls { get; } = new();
        public List<string> StopCalls { get; } = new();
        public List<string> RepairCalls { get; } = new();
        public Func<string, bool> StartOutcome { get; set; } = _ => true;
        public Func<string, bool> StopOutcome { get; set; } = _ => true;
        public bool RepairOutcome { get; set; } = true;

        public ServiceState Query(string serviceName)
        {
            if (!Queries.TryGetValue(serviceName, out var q) || q.Count == 0) return ServiceState.Unknown;
            // Last observation sticks once the queue drains (mirrors a settled service state).
            return q.Count == 1 ? q.Peek() : q.Dequeue();
        }
        public bool Start(string serviceName, TimeSpan timeout)
        {
            StartCalls.Add(serviceName);
            return StartOutcome(serviceName);
        }
        public bool Stop(string serviceName, TimeSpan timeout)
        {
            StopCalls.Add(serviceName);
            return StopOutcome(serviceName);
        }
        public bool InvokeRepair(string bootstrapPath, TimeSpan timeout)
        {
            RepairCalls.Add(bootstrapPath);
            return RepairOutcome;
        }
    }

    private static WatchdogWorker MakeWorker(
        FakeCommand cmd,
        string? bootstrapPath = null,
        string? telemetryPath = null,
        string? repairRequestPath = null,
        string? restartRequestPath = null)
    {
        var opts = new WatchdogOptions
        {
            WatchedServices = new[] { "SuavoAgent.Core" },
            BootstrapPath = bootstrapPath,
            TelemetryPath = telemetryPath,
            RepairRequestPath = repairRequestPath,
            // Default to a guaranteed-absent path so the post-OTA handler is a no-op in
            // tests that don't exercise it (never resolve to the test host's install dir).
            RestartRequestPath = restartRequestPath
                ?? Path.Combine(Path.GetTempPath(), $"no-such-restart-{Guid.NewGuid():N}.json")
        };
        var worker = new WatchdogWorker(NullLogger<WatchdogWorker>.Instance, cmd, opts);
        // Seed ledger via reflection-free helper: call TickOnce with a "Running" observation
        // to initialize state, then overwrite queue.
        return worker;
    }

    [Fact]
    public void Tick_Running_DoesNothing()
    {
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);
        worker.TickOnce(DateTimeOffset.UtcNow);
        Assert.Empty(cmd.StartCalls);
        Assert.Empty(cmd.RepairCalls);
    }

    [Fact]
    public void Tick_RunningButBeaconStale_ForceCyclesHungService()
    {
        var beaconDir = Path.Combine(Path.GetTempPath(), "wd-hang-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(beaconDir);
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            new SuavoAgent.Diagnostics.LivenessBeaconStore(beaconDir).Write("SuavoAgent.Core", t0); // beacon frozen at t0

            var cmd = new FakeCommand();
            cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running, ServiceState.Running });
            var worker = new WatchdogWorker(NullLogger<WatchdogWorker>.Instance, cmd, new WatchdogOptions
            {
                WatchedServices = new[] { "SuavoAgent.Core" },
                HangBeaconDirectory = beaconDir,
                HangStaleThreshold = TimeSpan.FromSeconds(90),
                RestartRequestPath = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"),
            });
            SeedLedgers(worker);

            worker.TickOnce(t0);                  // begins beacon tracking; beacon fresh ⇒ Live, no cycle
            worker.TickOnce(t0.AddSeconds(150));  // beacon now 150s stale (>90s) ⇒ HUNG ⇒ stop+start

            Assert.Contains("SuavoAgent.Core", cmd.StopCalls);
            Assert.Contains("SuavoAgent.Core", cmd.StartCalls);
        }
        finally { try { Directory.Delete(beaconDir, true); } catch { } }
    }

    [Fact]
    public void Tick_RunningBeaconFresh_DoesNotCycle()
    {
        var beaconDir = Path.Combine(Path.GetTempPath(), "wd-fresh-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(beaconDir);
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            var store = new SuavoAgent.Diagnostics.LivenessBeaconStore(beaconDir);
            var cmd = new FakeCommand();
            cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running, ServiceState.Running });
            var worker = new WatchdogWorker(NullLogger<WatchdogWorker>.Instance, cmd, new WatchdogOptions
            {
                WatchedServices = new[] { "SuavoAgent.Core" },
                HangBeaconDirectory = beaconDir,
                HangStaleThreshold = TimeSpan.FromSeconds(90),
                RestartRequestPath = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"),
            });
            SeedLedgers(worker);

            store.Write("SuavoAgent.Core", t0);
            worker.TickOnce(t0);
            store.Write("SuavoAgent.Core", t0.AddSeconds(150)); // a healthy Core keeps refreshing its beacon
            worker.TickOnce(t0.AddSeconds(160));                // beacon only 10s old ⇒ Live ⇒ no cycle

            Assert.Empty(cmd.StopCalls);
        }
        finally { try { Directory.Delete(beaconDir, true); } catch { } }
    }

    [Fact]
    public void Tick_StalePriorRunBeacon_IsIgnoredDuringStartupGrace_NoCycle()
    {
        // A leftover .beacon from a PRIOR run (timestamp before we started tracking) must NOT bypass the
        // startup grace and force-cycle a slow-starting Core before its first fresh write (Codex P2).
        var beaconDir = Path.Combine(Path.GetTempPath(), "wd-prior-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(beaconDir);
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            new SuavoAgent.Diagnostics.LivenessBeaconStore(beaconDir).Write("SuavoAgent.Core", t0.AddSeconds(-300)); // very old prior-run beacon

            var cmd = new FakeCommand();
            cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running, ServiceState.Running });
            var worker = new WatchdogWorker(NullLogger<WatchdogWorker>.Instance, cmd, new WatchdogOptions
            {
                WatchedServices = new[] { "SuavoAgent.Core" },
                HangBeaconDirectory = beaconDir,
                HangStaleThreshold = TimeSpan.FromSeconds(90),
                RestartRequestPath = Path.Combine(Path.GetTempPath(), $"no-such-{Guid.NewGuid():N}.json"),
            });
            SeedLedgers(worker);

            worker.TickOnce(t0);                  // first RUNNING ⇒ trackingSince=t0; old beacon (< t0) ignored ⇒ BeaconPending
            worker.TickOnce(t0.AddSeconds(30));   // still within the 90s grace ⇒ no cycle

            Assert.Empty(cmd.StopCalls);
        }
        finally { try { Directory.Delete(beaconDir, true); } catch { } }
    }

    [Fact]
    public void Tick_StoppedAfterGrace_InvokesStart()
    {
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Stopped, ServiceState.Stopped });
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);

        var now = DateTimeOffset.UtcNow;
        // First tick: starts unhealthy clock
        worker.TickOnce(now);
        // Second tick 6 min later: should restart
        worker.TickOnce(now.AddMinutes(6));

        Assert.Single(cmd.StartCalls);
        Assert.Equal("SuavoAgent.Core", cmd.StartCalls[0]);
    }

    [Fact]
    public void Tick_ThreeRestartFailures_InvokesRepair()
    {
        var cmd = new FakeCommand { StartOutcome = _ => false };
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(Enumerable.Repeat(ServiceState.Stopped, 10));
        var bootstrap = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(bootstrap, "# stub");
        try
        {
            var worker = MakeWorker(cmd, bootstrap);
            SeedLedgers(worker);

            var now = DateTimeOffset.UtcNow;
            worker.TickOnce(now);                          // mark unhealthy
            worker.TickOnce(now.AddMinutes(6));            // attempt 1 → fail
            worker.TickOnce(now.AddMinutes(6).AddSeconds(61)); // attempt 2 → fail
            worker.TickOnce(now.AddMinutes(6).AddSeconds(122)); // attempt 3 → fail
            worker.TickOnce(now.AddMinutes(6).AddSeconds(183)); // escalate

            Assert.Equal(3, cmd.StartCalls.Count);
            Assert.Single(cmd.RepairCalls);
            Assert.Equal(bootstrap, cmd.RepairCalls[0]);
        }
        finally
        {
            File.Delete(bootstrap);
        }
    }

    [Fact]
    public void Tick_NotInstalled_EscalatesRepairImmediately()
    {
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.NotInstalled });
        var bootstrap = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(bootstrap, "# stub");
        try
        {
            var worker = MakeWorker(cmd, bootstrap);
            SeedLedgers(worker);
            worker.TickOnce(DateTimeOffset.UtcNow);
            Assert.Single(cmd.RepairCalls);
            Assert.Empty(cmd.StartCalls);
        }
        finally
        {
            File.Delete(bootstrap);
        }
    }

    [Fact]
    public void Tick_EscalateWithMissingBootstrap_NoCrash()
    {
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.NotInstalled });
        var worker = MakeWorker(cmd, bootstrapPath: "/nonexistent/path.ps1");
        SeedLedgers(worker);
        var ex = Record.Exception(() => worker.TickOnce(DateTimeOffset.UtcNow));
        Assert.Null(ex);
        Assert.Empty(cmd.RepairCalls); // repair wasn't attempted because path is bad
    }

    [Fact]
    public void Tick_WritesTelemetryEvidenceFile()
    {
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var cmd = new FakeCommand { StartOutcome = _ => false };
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(
            new[] { ServiceState.Stopped, ServiceState.Stopped });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath);
        SeedLedgers(worker);

        var now = DateTimeOffset.UtcNow;
        worker.TickOnce(now);
        worker.TickOnce(now.AddMinutes(6));

        try
        {
            Assert.True(File.Exists(telemetryPath));
            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var root = doc.RootElement;
            Assert.True(root.GetProperty("present").GetBoolean());
            var service = root.GetProperty("services")[0];
            Assert.Equal("SuavoAgent.Core", service.GetProperty("serviceName").GetString());
            Assert.Equal("AttemptRestart", service.GetProperty("action").GetString());
            Assert.False(service.GetProperty("restartAccepted").GetBoolean());
            Assert.Equal(1, service.GetProperty("consecutiveRestartFailures").GetInt32());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
        }
    }

    [Fact]
    public void Tick_QueuedRemoteRepair_InvokesBootstrapRepairAndDeletesRequest()
    {
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = Path.Combine(Path.GetTempPath(), $"watchdog-repair-{Guid.NewGuid():N}.json");
        var bootstrap = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(bootstrap, "# stub");
        File.WriteAllText(requestPath, """
        {
          "schemaVersion": 1,
          "commandId": "cmd-repair-queued-1",
          "reason": "watchdog_critical",
          "requestedAt": "2026-05-06T23:59:00.0000000Z",
          "source": "signed_remote_repair"
        }
        """);

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, bootstrap, telemetryPath, requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(DateTimeOffset.Parse("2026-05-07T00:00:00Z"));

            Assert.Single(cmd.RepairCalls);
            Assert.Equal(bootstrap, cmd.RepairCalls[0]);
            Assert.False(File.Exists(requestPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var remoteRepair = doc.RootElement.GetProperty("remoteRepair");
            Assert.True(remoteRepair.GetProperty("present").GetBoolean());
            Assert.Equal("cmd-repair-queued-1", remoteRepair.GetProperty("commandId").GetString());
            Assert.Equal("watchdog_critical", remoteRepair.GetProperty("reason").GetString());
            Assert.Equal("repair_completed", remoteRepair.GetProperty("outcome").GetString());
            Assert.True(remoteRepair.GetProperty("repairInvoked").GetBoolean());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
            try { File.Delete(bootstrap); } catch { }
        }
    }

    [Fact]
    public void Tick_QueuedRemoteRepair_RedactsUnexpectedReasonInTelemetry()
    {
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = Path.Combine(Path.GetTempPath(), $"watchdog-repair-{Guid.NewGuid():N}.json");
        var bootstrap = Path.Combine(Path.GetTempPath(), $"bootstrap-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(bootstrap, "# stub");
        File.WriteAllText(requestPath, """
        {
          "schemaVersion": 1,
          "commandId": "cmd-repair-queued-2",
          "reason": "patient_john_smith",
          "requestedAt": "2026-05-06T23:59:00.0000000Z"
        }
        """);

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, bootstrap, telemetryPath, requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(DateTimeOffset.Parse("2026-05-07T00:00:00Z"));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var remoteRepair = doc.RootElement.GetProperty("remoteRepair");
            Assert.Equal("remote_command", remoteRepair.GetProperty("reason").GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
            try { File.Delete(bootstrap); } catch { }
        }
    }

    // ── Post-OTA restart handler (#OTA restart-sequencing fix) ──

    private static string WriteRestartRequest(
        int schema, string version, string requestedAt, params string[] services)
    {
        var path = Path.Combine(Path.GetTempPath(), $"watchdog-restart-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            schemaVersion = schema,
            version,
            requestedAt,
            services
        }));
        return path;
    }

    [Fact]
    public void Tick_UpdateRestart_CyclesBrokerAndDeletesFile()
    {
        var now = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = WriteRestartRequest(1, "3.18.3", now.ToString("o"), "SuavoAgent.Broker");

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Broker"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath, restartRequestPath: requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);

            Assert.Equal(new[] { "SuavoAgent.Broker" }, cmd.StopCalls);
            Assert.Contains("SuavoAgent.Broker", cmd.StartCalls);
            Assert.False(File.Exists(requestPath)); // consumed on success

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var ur = doc.RootElement.GetProperty("updateRestart");
            Assert.Equal("restarted", ur.GetProperty("outcome").GetString());
            Assert.Equal("3.18.3", ur.GetProperty("version").GetString());
            Assert.Equal("SuavoAgent.Broker", ur.GetProperty("servicesRestarted")[0].GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_UpdateRestart_StartFails_KeepsFileForRetry()
    {
        var now = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = WriteRestartRequest(1, "3.18.3", now.ToString("o"), "SuavoAgent.Broker");

        // Broker stop succeeds but start is rejected (Core still START_PENDING).
        var cmd = new FakeCommand { StartOutcome = _ => false };
        cmd.Queries["SuavoAgent.Broker"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath, restartRequestPath: requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);

            Assert.Equal(new[] { "SuavoAgent.Broker" }, cmd.StopCalls);
            Assert.Contains("SuavoAgent.Broker", cmd.StartCalls);
            Assert.True(File.Exists(requestPath)); // KEPT for next-tick retry

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            Assert.Equal("pending_retry", doc.RootElement.GetProperty("updateRestart").GetProperty("outcome").GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_UpdateRestart_NonAllowlistedService_Rejected()
    {
        var now = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        // Core is NOT in the allowlist — a forged request must not let anyone bounce Core.
        var requestPath = WriteRestartRequest(1, "3.18.3", now.ToString("o"), "SuavoAgent.Core");

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath, restartRequestPath: requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);

            Assert.Empty(cmd.StopCalls);
            Assert.Empty(cmd.StartCalls);
            Assert.False(File.Exists(requestPath)); // poison request discarded

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            Assert.Equal("rejected_service", doc.RootElement.GetProperty("updateRestart").GetProperty("outcome").GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_UpdateRestart_Expired_Discarded()
    {
        var now = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = WriteRestartRequest(1, "3.18.3", now.AddMinutes(-20).ToString("o"), "SuavoAgent.Broker");

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Broker"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath, restartRequestPath: requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);

            Assert.Empty(cmd.StopCalls);
            Assert.Empty(cmd.StartCalls);
            Assert.False(File.Exists(requestPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            Assert.Equal("expired", doc.RootElement.GetProperty("updateRestart").GetProperty("outcome").GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_UpdateRestart_BadSchema_Rejected()
    {
        var now = DateTimeOffset.Parse("2026-06-02T12:00:00Z");
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = WriteRestartRequest(2, "3.18.3", now.ToString("o"), "SuavoAgent.Broker");

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Broker"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.Running });
        var worker = MakeWorker(cmd, telemetryPath: telemetryPath, restartRequestPath: requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);

            Assert.Empty(cmd.StopCalls);
            Assert.Empty(cmd.StartCalls);
            Assert.False(File.Exists(requestPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            Assert.Equal("rejected_schema", doc.RootElement.GetProperty("updateRestart").GetProperty("outcome").GetString());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    private static void SeedLedgers(WatchdogWorker worker)
    {
        // Trigger lazy ledger initialization by reflection into private dict
        // — the worker normally seeds in ExecuteAsync which we're not calling here.
        var t = typeof(WatchdogWorker);
        var field = t.GetField("_ledgers", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var optsField = t.GetField("_options", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var options = (WatchdogOptions)optsField.GetValue(worker)!;
        var dict = (Dictionary<string, ServiceLedger>)field.GetValue(worker)!;
        foreach (var svc in options.WatchedServices)
        {
            dict[svc] = ServiceLedger.Initial(svc, DateTimeOffset.UtcNow);
        }
    }
}
