using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public sealed class ServiceCommandMaintenanceTests
{
    [Fact]
    public void ResolveMaintenanceExecutablePath_IsFixedBesideWatchdogWithExactName()
    {
        var expected = Path.Combine(
            ServiceCommand.ResolveInstallDirectory(),
            MaintenanceContract.ExecutableName);

        Assert.Equal(expected, ServiceCommand.ResolveMaintenanceExecutablePath());
    }

    [Fact]
    public void InvokeRepair_LaunchesExactMaintenanceHostWithContractArguments()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        string? launchedFile = null;
        string? launchedArguments = null;
        TimeSpan? launchedTimeout = null;

        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: path => path == executable,
            runForExitCode: (file, arguments, timeout) =>
            {
                launchedFile = file;
                launchedArguments = arguments;
                launchedTimeout = timeout;
                return 0;
            },
            verifyMaintenanceTrust: _ => Trusted());

        var timeout = TimeSpan.FromMinutes(2);
        var result = command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, timeout);

        Assert.True(result);
        Assert.Equal(executable, launchedFile);
        Assert.Equal(
            MaintenanceContract.BuildRepairArguments(MaintenanceReason.ServiceRestartFailed),
            launchedArguments);
        Assert.Equal(timeout, launchedTimeout);
    }

    [Fact]
    public void InvokeRepair_MissingMaintenanceHost_FailsClosedWithoutLaunching()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var launchCalls = 0;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: _ => false,
            runForExitCode: (_, _, _) => { launchCalls++; return 0; },
            verifyMaintenanceTrust: _ => Trusted());

        Assert.False(command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, launchCalls);
    }

    [Theory]
    [InlineData("RenamedMaintenance.exe")]
    [InlineData("maintenance.cmd")]
    public void InvokeRepair_WrongFilename_FailsClosedWithoutLaunching(string filename)
    {
        var installDir = AbsoluteTempDirectory();
        var launchCalls = 0;
        var command = new ServiceCommand(
            Path.Combine(installDir, filename),
            installDir,
            fileExists: _ => true,
            runForExitCode: (_, _, _) => { launchCalls++; return 0; },
            verifyMaintenanceTrust: _ => Trusted());

        Assert.False(command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InvokeRepair_RelocatedExactFilename_FailsClosedWithoutLaunching()
    {
        var installDir = AbsoluteTempDirectory();
        var otherDir = Path.Combine(installDir, "other");
        var launchCalls = 0;
        var command = new ServiceCommand(
            Path.Combine(otherDir, MaintenanceContract.ExecutableName),
            installDir,
            fileExists: _ => true,
            runForExitCode: (_, _, _) => { launchCalls++; return 0; },
            verifyMaintenanceTrust: _ => Trusted());

        Assert.False(command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, launchCalls);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(23)]
    public void InvokeRepair_NonzeroExit_FailsClosed(int exitCode)
    {
        var command = BuildCommand((_, _, _) => exitCode);

        Assert.False(command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void InvokeRepair_Timeout_FailsClosed()
    {
        var command = BuildCommand((_, _, _) => null);

        Assert.False(command.InvokeRepair(MaintenanceReason.ServiceRestartFailed, TimeSpan.FromMilliseconds(1)));
    }

    [Fact]
    public void InvokeRepair_UnspecifiedReason_FailsClosedWithoutLaunching()
    {
        var launchCalls = 0;
        var command = BuildCommand((_, _, _) => { launchCalls++; return 0; });

        Assert.False(command.InvokeRepair(MaintenanceReason.Unspecified, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InvokeRepair_UntrustedMaintenanceHost_FailsClosedWithoutLaunching()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var launchCalls = 0;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: _ => true,
            runForExitCode: (_, _, _) => { launchCalls++; return 0; },
            verifyMaintenanceTrust: _ => new MaintenanceHostTrustResult(
                false,
                MaintenanceTrustSource.None,
                "signed_receipt_missing"));

        Assert.False(command.InvokeRepair(
            MaintenanceReason.ServiceRestartFailed,
            TimeSpan.FromMinutes(1)));
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InvokeUpdateCoordinator_LaunchesExactMaintenanceHostDetachedWithFixedArguments()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var requestPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-update-tests",
            Guid.NewGuid().ToString("N"),
            UpdateActivationContract.ActivationRequestFileName));
        string? launchedFile = null;
        string? launchedArguments = null;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: path => path == executable || path == requestPath,
            runForExitCode: (_, _, _) => throw new InvalidOperationException("repair runner must not run"),
            verifyMaintenanceTrust: _ => Trusted(),
            runDetached: (file, arguments) =>
            {
                launchedFile = file;
                launchedArguments = arguments;
                return true;
            },
            expectedActivationRequestPath: requestPath);

        Assert.True(command.InvokeUpdateCoordinator(requestPath));
        Assert.Equal(executable, launchedFile);
        Assert.Equal(
            $"{UpdateActivationContract.ActivateSwitch} " +
            $"{UpdateActivationContract.RequestPathSwitch} \"{requestPath}\"",
            launchedArguments);
    }

    [Fact]
    public void InvokeUpdateCoordinator_RelocatedRequest_FailsClosedWithoutLaunching()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var expectedRequest = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-update-tests",
            Guid.NewGuid().ToString("N"),
            UpdateActivationContract.ActivationRequestFileName));
        var relocatedRequest = Path.Combine(Path.GetDirectoryName(expectedRequest)!, "other-request.json");
        var launchCalls = 0;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: _ => true,
            runForExitCode: (_, _, _) => 0,
            verifyMaintenanceTrust: _ => Trusted(),
            runDetached: (_, _) => { launchCalls++; return true; },
            expectedActivationRequestPath: expectedRequest);

        Assert.False(command.InvokeUpdateCoordinator(relocatedRequest));
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InvokeUpdateCoordinator_UntrustedMaintenanceHost_FailsClosedWithoutLaunching()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var requestPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-update-tests",
            Guid.NewGuid().ToString("N"),
            UpdateActivationContract.ActivationRequestFileName));
        var launchCalls = 0;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: _ => true,
            runForExitCode: (_, _, _) => 0,
            verifyMaintenanceTrust: _ => new MaintenanceHostTrustResult(
                false,
                MaintenanceTrustSource.None,
                "signed_receipt_missing"),
            runDetached: (_, _) => { launchCalls++; return true; },
            expectedActivationRequestPath: requestPath);

        Assert.False(command.InvokeUpdateCoordinator(requestPath));
        Assert.Equal(0, launchCalls);
    }

    [Fact]
    public void InvokeUpdateCoordinatorResume_LaunchesOnlyFixedSystemClaimPath()
    {
        var installDir = AbsoluteTempDirectory();
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var claimPath = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-maintenance-tests",
            Guid.NewGuid().ToString("N"),
            UpdateActivationContract.ActiveClaimFileName));
        string? launchedArguments = null;
        var command = new ServiceCommand(
            executable,
            installDir,
            fileExists: path => path == executable || path == claimPath,
            runForExitCode: (_, _, _) => 0,
            verifyMaintenanceTrust: _ => Trusted(),
            runDetached: (_, arguments) =>
            {
                launchedArguments = arguments;
                return true;
            },
            expectedActiveClaimPath: claimPath);

        Assert.True(command.InvokeUpdateCoordinatorResume(claimPath));
        Assert.Equal(
            $"{UpdateActivationContract.ResumeSwitch} " +
            $"{UpdateActivationContract.ClaimPathSwitch} \"{claimPath}\"",
            launchedArguments);
        Assert.False(command.InvokeUpdateCoordinatorResume(
            Path.Combine(Path.GetDirectoryName(claimPath)!, "forged-claim.json")));
    }

    private static ServiceCommand BuildCommand(Func<string, string, TimeSpan, int?> runner)
    {
        var installDir = AbsoluteTempDirectory();
        return new ServiceCommand(
            Path.Combine(installDir, MaintenanceContract.ExecutableName),
            installDir,
            fileExists: _ => true,
            runForExitCode: runner,
            verifyMaintenanceTrust: _ => Trusted());
    }

    private static MaintenanceHostTrustResult Trusted() => new(
        true,
        MaintenanceTrustSource.SignedReleaseChecksums,
        "trusted");

    private static string AbsoluteTempDirectory() => Path.GetFullPath(
        Path.Combine(Path.GetTempPath(), "suavo-maintenance-tests", Guid.NewGuid().ToString("N")));
}
