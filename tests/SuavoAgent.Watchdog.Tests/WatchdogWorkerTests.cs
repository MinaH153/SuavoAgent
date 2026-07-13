using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
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
        public List<MaintenanceReason> RepairCalls { get; } = new();
        public List<string> UpdateResumeCalls { get; } = new();
        public Func<string, bool> StartOutcome { get; set; } = _ => true;
        public Func<string, bool> StopOutcome { get; set; } = _ => true;
        public bool RepairOutcome { get; set; } = true;
        public bool UpdateResumeOutcome { get; set; } = true;

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
        public bool InvokeRepair(MaintenanceReason reason, TimeSpan timeout)
        {
            RepairCalls.Add(reason);
            return RepairOutcome;
        }
        public bool InvokeUpdateCoordinatorResume(string claimPath)
        {
            UpdateResumeCalls.Add(claimPath);
            return UpdateResumeOutcome;
        }
    }

    private static WatchdogWorker MakeWorker(
        FakeCommand cmd,
        string? telemetryPath = null,
        string? repairRequestPath = null)
    {
        var opts = new WatchdogOptions
        {
            WatchedServices = new[] { "SuavoAgent.Core" },
            TelemetryPath = telemetryPath,
            RepairRequestPath = repairRequestPath,
            ExpectedAgentId = "agent-watchdog-test",
            ExpectedMachineFingerprint = "fingerprint-watchdog-test",
            // Tick-only tests never execute the startup ACL repair; keep it injectable for the
            // hosted-service path without coupling these decisions to the local host.
            ReapplyHelperExeGrant = _ => true,
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
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);

        var now = DateTimeOffset.UtcNow;
        worker.TickOnce(now);                          // mark unhealthy
        worker.TickOnce(now.AddMinutes(6));            // attempt 1 → fail
        worker.TickOnce(now.AddMinutes(6).AddSeconds(61)); // attempt 2 → fail
        worker.TickOnce(now.AddMinutes(6).AddSeconds(122)); // attempt 3 → fail
        worker.TickOnce(now.AddMinutes(6).AddSeconds(183)); // escalate

        Assert.Equal(3, cmd.StartCalls.Count);
        Assert.Equal([MaintenanceReason.ServiceRestartFailed], cmd.RepairCalls);
    }

    [Fact]
    public void Tick_AcceptedRestartsButServiceNeverRuns_EscalatesRepair()
    {
        // Crash-loop: the SCM ACCEPTS every start (START_PENDING) but the process dies immediately,
        // so it's always observed Stopped. The old code reset the failure counter on SCM-accept, so
        // the escalation gate was unreachable and it looped forever. Now each accepted-but-not-live
        // restart is counted as a failure on the next tick → repair escalates after 3 cycles.
        var cmd = new FakeCommand { StartOutcome = _ => true }; // SCM always accepts
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(Enumerable.Repeat(ServiceState.Stopped, 12));
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);

        var now = DateTimeOffset.UtcNow;
        worker.TickOnce(now);                               // mark unhealthy
        worker.TickOnce(now.AddMinutes(6));                 // attempt 1 (accepted, pending liveness)
        worker.TickOnce(now.AddMinutes(6).AddSeconds(61));  // count fail 1 → attempt 2
        worker.TickOnce(now.AddMinutes(6).AddSeconds(122)); // count fail 2 → attempt 3
        worker.TickOnce(now.AddMinutes(6).AddSeconds(183)); // count fail 3 → escalate

        Assert.Equal(3, cmd.StartCalls.Count);
        Assert.Equal([MaintenanceReason.ServiceRestartFailed], cmd.RepairCalls);
    }

    [Fact]
    public void Tick_NotInstalled_EscalatesRepairImmediately()
    {
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.NotInstalled });
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);
        worker.TickOnce(DateTimeOffset.UtcNow);
        Assert.Equal([MaintenanceReason.ServiceRestartFailed], cmd.RepairCalls);
        Assert.Empty(cmd.StartCalls);
    }

    [Fact]
    public void Tick_EscalateWhenNativeMaintenanceFails_NoCrashAndRecordsAttempt()
    {
        var cmd = new FakeCommand { RepairOutcome = false };
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>(new[] { ServiceState.NotInstalled });
        var worker = MakeWorker(cmd);
        SeedLedgers(worker);
        var ex = Record.Exception(() => worker.TickOnce(DateTimeOffset.UtcNow));
        Assert.Null(ex);
        Assert.Equal([MaintenanceReason.ServiceRestartFailed], cmd.RepairCalls);
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
    public void Tick_UnsignedLegacyRemoteRepair_IsRejectedAndDeleted()
    {
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = Path.Combine(Path.GetTempPath(), $"watchdog-repair-{Guid.NewGuid():N}.json");
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
        var worker = MakeWorker(cmd, telemetryPath, requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(DateTimeOffset.Parse("2026-05-07T00:00:00Z"));

            Assert.Empty(cmd.RepairCalls);
            Assert.False(File.Exists(requestPath));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var remoteRepair = doc.RootElement.GetProperty("remoteRepair");
            Assert.True(remoteRepair.GetProperty("present").GetBoolean());
            Assert.Equal("not_available", remoteRepair.GetProperty("commandId").GetString());
            Assert.Equal("validation_rejected", remoteRepair.GetProperty("reason").GetString());
            Assert.Equal("request_invalid_json", remoteRepair.GetProperty("outcome").GetString());
            Assert.False(remoteRepair.GetProperty("repairInvoked").GetBoolean());
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_UnsignedUnexpectedReason_IsRejectedWithPhiFreeTelemetry()
    {
        var telemetryPath = Path.Combine(Path.GetTempPath(), $"watchdog-{Guid.NewGuid():N}.json");
        var requestPath = Path.Combine(Path.GetTempPath(), $"watchdog-repair-{Guid.NewGuid():N}.json");
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
        var worker = MakeWorker(cmd, telemetryPath, requestPath);
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(DateTimeOffset.Parse("2026-05-07T00:00:00Z"));

            using var doc = JsonDocument.Parse(File.ReadAllText(telemetryPath));
            var remoteRepair = doc.RootElement.GetProperty("remoteRepair");
            Assert.Equal("not_available", remoteRepair.GetProperty("commandId").GetString());
            Assert.Equal("validation_rejected", remoteRepair.GetProperty("reason").GetString());
            Assert.False(remoteRepair.GetProperty("repairInvoked").GetBoolean());
            Assert.Empty(cmd.RepairCalls);
            Assert.DoesNotContain("patient_john_smith", File.ReadAllText(telemetryPath));
        }
        finally
        {
            try { File.Delete(telemetryPath); } catch { }
            try { File.Delete(requestPath); } catch { }
        }
    }

    [Fact]
    public void Tick_StaleDurableUpdateClaim_RelaunchesResumeOncePerLease()
    {
        var now = DateTimeOffset.Parse("2026-07-10T22:00:00Z");
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-worker-claim-" + Guid.NewGuid().ToString("N")));
        var updateRoot = Path.Combine(root, "updates");
        var maintenanceRoot = Path.Combine(root, "maintenance");
        var stagingId = new string('a', 64);
        var requestPath = UpdateActivationContract.GetCoordinatorRequestPath(
            maintenanceRoot,
            stagingId);
        var payloadDirectory = UpdateActivationContract.GetCoordinatorPayloadDirectory(
            maintenanceRoot,
            stagingId);
        var activeClaimPath = Path.Combine(
            maintenanceRoot,
            UpdateActivationContract.ActiveClaimFileName);
        Directory.CreateDirectory(payloadDirectory);
        File.WriteAllText(requestPath, "{}");
        File.WriteAllText(
            activeClaimPath,
            UpdateActivationContract.Serialize(new UpdateActivationClaimPointer(
                UpdateActivationContract.SchemaVersion,
                new string('b', 64),
                stagingId,
                "2.0.0",
                requestPath,
                payloadDirectory,
                now.AddMinutes(-5).ToString("O"),
                now.AddMinutes(-3).ToString("O"))));

        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>([ServiceState.Running]);
        string? terminatedRoot = null;
        string? terminatedStagingId = null;
        var worker = new WatchdogWorker(
            NullLogger<WatchdogWorker>.Instance,
            cmd,
            new WatchdogOptions
            {
                WatchedServices = ["SuavoAgent.Core"],
                UpdateRoot = updateRoot,
                ActivationRequestPath = Path.Combine(
                    updateRoot,
                    UpdateActivationContract.ActivationRequestFileName),
                ReplayLedgerPath = Path.Combine(updateRoot, "launch-leases.json"),
                MaintenanceRoot = maintenanceRoot,
                ActiveClaimPath = activeClaimPath,
                ActivationCompletionPath = Path.Combine(
                    maintenanceRoot,
                    UpdateActivationContract.CompletionFileName),
                ReapplyHelperExeGrant = _ => true,
                TerminateStaleUpdateRunner = (candidateRoot, candidateStagingId) =>
                {
                    terminatedRoot = candidateRoot;
                    terminatedStagingId = candidateStagingId;
                    return true;
                },
            });
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);
            worker.TickOnce(now.AddSeconds(30));

            Assert.Equal([activeClaimPath], cmd.UpdateResumeCalls);
            Assert.Equal(maintenanceRoot, terminatedRoot);
            Assert.Equal(stagingId, terminatedStagingId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Tick_StaleRunnerTerminationFailure_ReleasesLeaseForNextRetry()
    {
        var now = DateTimeOffset.Parse("2026-07-10T22:00:00Z");
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-worker-kill-retry-" + Guid.NewGuid().ToString("N")));
        var updateRoot = Path.Combine(root, "updates");
        var maintenanceRoot = Path.Combine(root, "maintenance");
        var stagingId = new string('c', 64);
        var requestPath = UpdateActivationContract.GetCoordinatorRequestPath(
            maintenanceRoot,
            stagingId);
        var payloadDirectory = UpdateActivationContract.GetCoordinatorPayloadDirectory(
            maintenanceRoot,
            stagingId);
        var claimPath = Path.Combine(
            maintenanceRoot,
            UpdateActivationContract.ActiveClaimFileName);
        Directory.CreateDirectory(payloadDirectory);
        File.WriteAllText(requestPath, "{}");
        File.WriteAllText(claimPath, UpdateActivationContract.Serialize(
            new UpdateActivationClaimPointer(
                UpdateActivationContract.SchemaVersion,
                new string('d', 64),
                stagingId,
                "2.0.0",
                requestPath,
                payloadDirectory,
                now.AddMinutes(-5).ToString("O"),
                now.AddMinutes(-3).ToString("O"))));
        var terminationCalls = 0;
        var cmd = new FakeCommand();
        cmd.Queries["SuavoAgent.Core"] = new Queue<ServiceState>([ServiceState.Running]);
        var worker = new WatchdogWorker(
            NullLogger<WatchdogWorker>.Instance,
            cmd,
            new WatchdogOptions
            {
                WatchedServices = ["SuavoAgent.Core"],
                UpdateRoot = updateRoot,
                ActivationRequestPath = Path.Combine(
                    updateRoot,
                    UpdateActivationContract.ActivationRequestFileName),
                ReplayLedgerPath = Path.Combine(updateRoot, "launch-leases.json"),
                MaintenanceRoot = maintenanceRoot,
                ActiveClaimPath = claimPath,
                ActivationCompletionPath = Path.Combine(
                    maintenanceRoot,
                    UpdateActivationContract.CompletionFileName),
                ReapplyHelperExeGrant = _ => true,
                TerminateStaleUpdateRunner = (_, _) => ++terminationCalls > 1,
            });
        SeedLedgers(worker);

        try
        {
            worker.TickOnce(now);
            worker.TickOnce(now.AddSeconds(30));

            Assert.Equal(2, terminationCalls);
            Assert.Equal([claimPath], cmd.UpdateResumeCalls);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ── Self-heal on a REAL OS process (not a fake): the hang force-cycle actually kills and relaunches
    //    a live process when its liveness beacon goes stale. Cross-platform (runs on the ubuntu gate). ──
    private sealed class ProcessServiceCommand : IServiceCommand
    {
        private readonly Dictionary<string, System.Diagnostics.Process> _procs = new(StringComparer.OrdinalIgnoreCase);
        public List<string> StopCalls { get; } = new();

        public ServiceState Query(string serviceName)
        {
            if (!_procs.TryGetValue(serviceName, out var p)) return ServiceState.Stopped;
            try { return p.HasExited ? ServiceState.Stopped : ServiceState.Running; }
            catch { return ServiceState.Stopped; }
        }

        public bool Start(string serviceName, TimeSpan timeout)
        {
            // A long-running, killable real process to stand in for the supervised agent process.
            var psi = OperatingSystem.IsWindows()
                ? new System.Diagnostics.ProcessStartInfo("ping", "-n 300 127.0.0.1") { UseShellExecute = false, CreateNoWindow = true }
                : new System.Diagnostics.ProcessStartInfo("sleep", "300") { UseShellExecute = false };
            var proc = System.Diagnostics.Process.Start(psi);
            if (proc is null) return false;
            _procs[serviceName] = proc;
            return true;
        }

        public bool Stop(string serviceName, TimeSpan timeout)
        {
            StopCalls.Add(serviceName);
            if (_procs.TryGetValue(serviceName, out var p))
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); p.WaitForExit(2000); } catch { }
            }
            return true;
        }

        public bool InvokeRepair(MaintenanceReason reason, TimeSpan timeout) => true;

        public int Pid(string serviceName)
        {
            if (!_procs.TryGetValue(serviceName, out var p)) return -1;
            try { return p.Id; } catch { return -1; }
        }

        public void KillAll()
        {
            foreach (var p in _procs.Values)
            {
                try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
                try { p.Dispose(); } catch { }
            }
            _procs.Clear();
        }
    }

    [Fact]
    public void Hang_ForceCyclesARealOsProcess()
    {
        var beaconDir = Path.Combine(Path.GetTempPath(), "wd-realproc-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(beaconDir);
        var cmd = new ProcessServiceCommand();
        try
        {
            var t0 = DateTimeOffset.UtcNow;
            Assert.True(cmd.Start("agent-core", TimeSpan.FromSeconds(5)));
            var pid1 = cmd.Pid("agent-core");
            Assert.True(pid1 > 0);
            Assert.Equal(ServiceState.Running, cmd.Query("agent-core"));

            // The process is alive per the OS, but its liveness beacon is frozen (deadlocked) — write it stale.
            new SuavoAgent.Diagnostics.LivenessBeaconStore(beaconDir).Write("agent-core", t0);

            var worker = new WatchdogWorker(NullLogger<WatchdogWorker>.Instance, cmd, new WatchdogOptions
            {
                WatchedServices = new[] { "agent-core" },
                HangCheckedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "agent-core" },
                HangBeaconDirectory = beaconDir,
                HangStaleThreshold = TimeSpan.FromSeconds(90),
            });
            SeedLedgers(worker);

            worker.TickOnce(t0);                  // begins tracking; beacon fresh ⇒ Live
            worker.TickOnce(t0.AddSeconds(150));  // beacon 150s stale ⇒ HUNG ⇒ stop+start the REAL process

            Assert.Contains("agent-core", cmd.StopCalls);  // the hung process was really stopped
            var pid2 = cmd.Pid("agent-core");
            Assert.True(pid2 > 0);
            Assert.NotEqual(pid1, pid2);                   // a genuinely NEW OS process — self-heal force-cycled it
            Assert.Equal(ServiceState.Running, cmd.Query("agent-core"));
        }
        finally
        {
            cmd.KillAll();
            try { Directory.Delete(beaconDir, true); } catch { }
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
