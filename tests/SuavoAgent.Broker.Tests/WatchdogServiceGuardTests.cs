using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Broker;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using Xunit;

namespace SuavoAgent.Broker.Tests;

/// <summary>
/// The LocalSystem Broker repairs a missing Watchdog through the signed native maintenance host
/// staged beside it. Healthy installs remain no-op and no caller can substitute a script path.
/// </summary>
public class WatchdogServiceGuardTests
{
    private sealed class FakeProbe : IWatchdogServiceProbe
    {
        public bool Installed;
        public bool StartAccepted = true;
        public int StartCalls;
        public MaintenanceReason? LastReason;
        public string MaintenanceExecutablePath { get; } =
            Path.Combine("test-install", MaintenanceContract.ExecutableName);

        public bool IsWatchdogServiceInstalled() => Installed;

        public bool TryStartMaintenanceRepair(MaintenanceReason reason)
        {
            StartCalls++;
            LastReason = reason;
            return StartAccepted;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }

    [Theory]
    // enabled, windows, installed, watchdog binary, maintenance host -> expected
    [InlineData(false, true, false, true, true, WatchdogGuardAction.SkipDisabled)]
    [InlineData(true, false, false, true, true, WatchdogGuardAction.SkipNonWindows)]
    [InlineData(true, true, true, true, true, WatchdogGuardAction.SkipAlreadyInstalled)]
    [InlineData(true, true, false, false, true, WatchdogGuardAction.SkipBinaryMissing)]
    [InlineData(true, true, false, true, false, WatchdogGuardAction.SkipMaintenanceMissing)]
    [InlineData(true, true, false, true, true, WatchdogGuardAction.Repair)]
    public void Decide_CoversEveryBranch(
        bool enabled,
        bool isWindows,
        bool installed,
        bool binary,
        bool maintenance,
        WatchdogGuardAction expected)
    {
        Assert.Equal(
            expected,
            WatchdogServiceGuard.Decide(enabled, isWindows, installed, binary, maintenance));
    }

    private static WatchdogServiceGuard Build(
        FakeProbe probe,
        bool enabled = true,
        bool windows = true,
        bool binaryExists = true,
        bool maintenanceExists = true,
        ILogger? logger = null)
    {
        var watchdogPath = Path.Combine("test-install", "SuavoAgent.Watchdog.exe");
        return new WatchdogServiceGuard(
            probe,
            logger ?? NullLogger.Instance,
            enabled,
            watchdogBinaryPath: watchdogPath,
            fileExists: path => path == watchdogPath ? binaryExists : maintenanceExists,
            isWindows: () => windows);
    }

    [Fact]
    public void StartsRepair_WhenServiceMissing_AndRequiredBinariesPresent()
    {
        var probe = new FakeProbe { Installed = false };
        var logger = new RecordingLogger();
        var guard = Build(probe, logger: logger);

        Assert.True(guard.EnsureWatchdogRegistered());
        Assert.Equal(1, probe.StartCalls);
        Assert.Equal(MaintenanceReason.WatchdogServiceMissing, probe.LastReason);
        Assert.Contains(
            logger.Messages,
            message => message.Contains("repair launch accepted", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            logger.Messages,
            message => message.Contains("completed", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoOp_WhenServiceAlreadyInstalled()
    {
        var probe = new FakeProbe { Installed = true };
        var guard = Build(probe);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.StartCalls);
    }

    [Fact]
    public void NoRepair_WhenWatchdogBinaryMissing()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, binaryExists: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.StartCalls);
    }

    [Fact]
    public void NoRepair_WhenMaintenanceHostMissing()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, maintenanceExists: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.StartCalls);
    }

    [Fact]
    public void NoRepair_AndNoProbe_WhenDisabled()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, enabled: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.StartCalls);
    }

    [Fact]
    public void NoRepair_OnNonWindows()
    {
        var probe = new FakeProbe { Installed = false };
        var guard = Build(probe, windows: false);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(0, probe.StartCalls);
    }

    [Fact]
    public void ReturnsFalse_WhenRepairLaunchFails()
    {
        var probe = new FakeProbe { Installed = false, StartAccepted = false };
        var guard = Build(probe);

        Assert.False(guard.EnsureWatchdogRegistered());
        Assert.Equal(1, probe.StartCalls);
    }

    [Fact]
    public void MaintenancePath_IsExactlyAdjacentToBroker()
    {
        var installDir = Path.Combine(Path.GetTempPath(), "suavo-broker-path-test");
        var brokerPath = Path.Combine(installDir, "SuavoAgent.Broker.exe");

        var actual = ScWatchdogServiceProbe.ResolveMaintenanceExecutablePath(brokerPath);

        Assert.Equal(Path.Combine(installDir, MaintenanceContract.ExecutableName), actual);
    }

    [Fact]
    public void MaintenancePathValidation_RejectsRelocatedOrRelativeExecutables()
    {
        var installDir = Path.Combine(Path.GetTempPath(), "suavo-broker-path-validation");
        var expected = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var relocated = Path.Combine(
            Path.GetTempPath(),
            "other",
            MaintenanceContract.ExecutableName);

        Assert.True(ScWatchdogServiceProbe.IsExpectedMaintenanceExecutable(expected, installDir));
        Assert.False(ScWatchdogServiceProbe.IsExpectedMaintenanceExecutable(relocated, installDir));
        Assert.False(ScWatchdogServiceProbe.IsExpectedMaintenanceExecutable(
            MaintenanceContract.ExecutableName,
            installDir));
    }

    [Fact]
    public void MaintenanceRepairStartInfo_UsesClosedSetNativeArguments()
    {
        var maintenancePath = Path.Combine(
            Path.GetTempPath(),
            "test-install",
            MaintenanceContract.ExecutableName);

        var startInfo = ScWatchdogServiceProbe.BuildMaintenanceRepairStartInfo(
            maintenancePath,
            MaintenanceReason.WatchdogServiceMissing);

        Assert.Equal(maintenancePath, startInfo.FileName);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(
            new[]
            {
                MaintenanceContract.RepairServicesSwitch,
                MaintenanceContract.ReasonSwitch,
                "watchdog-service-missing",
            },
            startInfo.ArgumentList);
    }

    [Fact]
    public void TryStartMaintenanceRepair_DoesNotWaitForExitOrReadChildOutput()
    {
        var installDir = Path.Combine(
            Path.GetTempPath(),
            "suavo-broker-detached-repair-test");
        var maintenancePath = Path.Combine(
            installDir,
            MaintenanceContract.ExecutableName);
        ProcessStartInfo? captured = null;

        // An unassociated Process throws if WaitForExit, HasExited, StandardOutput, or
        // StandardError is touched. Returning true therefore proves the probe only accepted
        // process creation and disposed the wrapper without observing child completion/output.
        var probe = new ScWatchdogServiceProbe(
            maintenancePath,
            installDir,
            fileExists: path => path == maintenancePath,
            startProcess: startInfo =>
            {
                captured = startInfo;
                return new Process();
            },
            verifyMaintenanceTrust: _ => Trusted());

        Assert.True(probe.TryStartMaintenanceRepair(
            MaintenanceReason.WatchdogServiceMissing));
        Assert.NotNull(captured);
        Assert.False(captured!.RedirectStandardInput);
        Assert.False(captured.RedirectStandardOutput);
        Assert.False(captured.RedirectStandardError);
    }

    [Fact]
    public void TryStartMaintenanceRepair_RejectsUntrustedHostBeforeProcessCreation()
    {
        var installDir = Path.Combine(Path.GetTempPath(), "suavo-broker-untrusted-repair-test");
        var maintenancePath = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var starts = 0;
        var probe = new ScWatchdogServiceProbe(
            maintenancePath,
            installDir,
            fileExists: path => path == maintenancePath,
            startProcess: _ =>
            {
                starts++;
                return new Process();
            },
            verifyMaintenanceTrust: _ => new MaintenanceHostTrustResult(
                false,
                MaintenanceTrustSource.None,
                "ota_signature_invalid"));

        Assert.False(probe.TryStartMaintenanceRepair(
            MaintenanceReason.WatchdogServiceMissing));
        Assert.Equal(0, starts);
    }

    private static MaintenanceHostTrustResult Trusted() => new(
        true,
        MaintenanceTrustSource.SignedReleaseChecksums,
        "trusted");
}
