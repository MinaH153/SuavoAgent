using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

/// <summary>
/// The Broker (LocalSystem) re-registers a missing Watchdog service via bootstrap --repair —
/// breaking the install-recovery deadlock (Core is LocalService and can't register; the repair
/// command needs the very Watchdog that's gone). Acts ONLY when the service is missing but its
/// binary + bootstrap.ps1 are present (so repair can succeed); no-op on a healthy install.
/// </summary>
public class WatchdogServiceGuardTests
{
    private sealed class FakeProbe : IWatchdogServiceProbe
    {
        public bool Installed;
        public bool RepairResult = true;
        public int RepairCalls;
        public string? LastBootstrapPath;

        public bool IsWatchdogServiceInstalled() => Installed;

        public bool InvokeBootstrapRepair(string bootstrapPath, TimeSpan timeout)
        {
            RepairCalls++;
            LastBootstrapPath = bootstrapPath;
            return RepairResult;
        }
    }

    // ---- Pure decision (cross-platform) ----

    [Theory]
    // enabled, windows, installed, binary, bootstrap -> expected
    [InlineData(false, true, false, true, true, WatchdogGuardAction.SkipDisabled)]
    [InlineData(true, false, false, true, true, WatchdogGuardAction.SkipNonWindows)]
    [InlineData(true, true, true, true, true, WatchdogGuardAction.SkipAlreadyInstalled)]
    [InlineData(true, true, false, false, true, WatchdogGuardAction.SkipBinaryMissing)]
    [InlineData(true, true, false, true, false, WatchdogGuardAction.SkipBootstrapMissing)]
    [InlineData(true, true, false, true, true, WatchdogGuardAction.Repair)]
    public void Decide_CoversEveryBranch(
        bool enabled, bool isWindows, bool installed, bool binary, bool bootstrap, WatchdogGuardAction expected)
    {
        Assert.Equal(expected, WatchdogServiceGuard.Decide(enabled, isWindows, installed, binary, bootstrap));
    }

    // ---- EnsureWatchdogRegistered side-effect (fake probe + injected windows/file existence) ----

    private static WatchdogServiceGuard Build(
        FakeProbe probe, bool enabled = true, bool windows = true,
        bool binaryExists = true, bool bootstrapExists = true)
        => new(
            probe,
            NullLogger.Instance,
            enabled,
            watchdogBinaryPath: @"C:\install\SuavoAgent.Watchdog.exe",
            bootstrapPath: @"C:\data\bootstrap.ps1",
            fileExists: path => path.EndsWith("Watchdog.exe") ? binaryExists : bootstrapExists,
            isWindows: () => windows);

    [Fact]
    public void Repairs_WhenServiceMissing_AndBinaryAndBootstrapPresent()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe);

        Assert.True(guard.EnsureWatchdogRegistered());
        Assert.Equal(1, probe.RepairCalls);
        Assert.Equal(@"C:\data\bootstrap.ps1", probe.LastBootstrapPath);
    }

    [Fact]
    public void NoOp_WhenServiceAlreadyInstalled()
    {
        var probe = new FakeProbe { Installed = true };
        var guard = Build(probe);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.RepairCalls);
    }

    [Fact]
    public void NoRepair_WhenWatchdogBinaryMissing()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, binaryExists: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.RepairCalls);
    }

    [Fact]
    public void NoRepair_WhenBootstrapMissing()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, bootstrapExists: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.RepairCalls);
    }

    [Fact]
    public void NoRepair_AndNoProbe_WhenDisabled()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, enabled: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.RepairCalls);
    }

    [Fact]
    public void NoRepair_OnNonWindows()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, windows: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.RepairCalls);
    }

    [Fact]
    public void ReturnsFalse_WhenRepairInvocationFails()
    {
        var probe = new FakeProbe { Installed = false, RepairResult = false };
        var guard = Build(probe);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(1, probe.RepairCalls);
    }
}
