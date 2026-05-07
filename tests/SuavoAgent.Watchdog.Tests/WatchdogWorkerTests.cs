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
        public List<string> RepairCalls { get; } = new();
        public Func<string, bool> StartOutcome { get; set; } = _ => true;
        public bool RepairOutcome { get; set; } = true;

        public ServiceState Query(string serviceName)
        {
            if (!Queries.TryGetValue(serviceName, out var q) || q.Count == 0) return ServiceState.Unknown;
            return q.Dequeue();
        }
        public bool Start(string serviceName, TimeSpan timeout)
        {
            StartCalls.Add(serviceName);
            return StartOutcome(serviceName);
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
        string? repairRequestPath = null)
    {
        var opts = new WatchdogOptions
        {
            WatchedServices = new[] { "SuavoAgent.Core" },
            BootstrapPath = bootstrapPath,
            TelemetryPath = telemetryPath,
            RepairRequestPath = repairRequestPath
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
