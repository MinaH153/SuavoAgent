using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Canary;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class RxDetectionWorkerTests
{
    // Bug 12 — on a host with no PioneerRx installed (Queen, dev workstations,
    // any non-Windows runner) RunCycleAsync must short-circuit instead of
    // burning a 30s SqlConnection.OpenAsync timeout + a warning log every
    // ~6 minutes. The detector returns false on non-Windows, so this test
    // exercises the no-PMS path on every CI architecture without a Windows
    // fixture.
    [Fact]
    public async Task RunCycle_NoPmsHost_SkipsSqlConnectWithoutAttempt()
    {
        if (OperatingSystem.IsWindows())
        {
            // On Windows we can't guarantee PioneerRx is absent without a
            // clean-room VM. The cross-platform invariant carries the
            // contract; skip when running on a host that might have the PMS.
            return;
        }

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var options = Options.Create(new AgentOptions
        {
            SqlServer = "127.0.0.1,9", // intentionally unreachable
            SqlDatabase = "PioneerPharmacySystem",
            LearningMode = true,
        });

        var worker = new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            options, _stateDb, sp);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        // RunCycleAsync is internal; reachable via InternalsVisibleTo.
        // Use a short cancel budget so a missing short-circuit would fail
        // loud — a real SqlConnection.OpenAsync attempt takes ~30s.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await worker.RunCycleAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            // The cycle's 60s "skipping detection" Task.Delay will trip the
            // cancel after the short-circuit; that's expected and proves we
            // never blocked on an actual SQL connect.
        }
        sw.Stop();

        Assert.False(worker.IsSqlConnected);
        Assert.True(worker.LoggedNoPmsOnce,
            "expected no-PMS short-circuit to log once");
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(15),
            $"RunCycleAsync took {sw.Elapsed} — short-circuit must skip the 30s SQL connect timeout");
    }

    [Fact]
    public async Task RunCycle_NoPmsHost_LogsOnceAcrossMultipleCycles()
    {
        if (OperatingSystem.IsWindows()) return;

        var services = new ServiceCollection();
        var sp = services.BuildServiceProvider();
        var options = Options.Create(new AgentOptions { LearningMode = true });

        var worker = new RxDetectionWorker(
            NullLogger<RxDetectionWorker>.Instance,
            NullLoggerFactory.Instance,
            options, _stateDb, sp);

        for (var i = 0; i < 3; i++)
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try { await worker.RunCycleAsync(cts.Token); }
            catch (OperationCanceledException) { }
        }

        Assert.True(worker.LoggedNoPmsOnce);
    }
}
