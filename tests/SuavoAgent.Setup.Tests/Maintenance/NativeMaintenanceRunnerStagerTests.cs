using System.Diagnostics;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class NativeMaintenanceRunnerStagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-runner-stager-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Trusted_installed_host_is_copied_outside_live_directory_and_launched_without_shell()
    {
        var live = Path.Combine(_root, "live");
        var maintenanceRoot = Path.Combine(_root, "maintenance");
        Directory.CreateDirectory(live);
        var source = Path.Combine(live, MaintenanceContract.ExecutableName);
        File.WriteAllText(source, "trusted-host");
        File.WriteAllText(
            Path.Combine(live, MaintenanceContract.ReleaseChecksumsFileName),
            "receipt");
        File.WriteAllText(
            Path.Combine(live, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            "signature");
        ProcessStartInfo? launched = null;
        var stager = new NativeMaintenanceRunnerStager(
            lockdown: _ => { },
            verifyTrust: path => File.Exists(path)
                ? new MaintenanceHostTrustResult(true, MaintenanceTrustSource.SignedReleaseChecksums, "trusted")
                : new MaintenanceHostTrustResult(false, MaintenanceTrustSource.None, "missing"),
            launch: info => { launched = info; return true; });
        var stagingId = new string('a', 64);

        var result = stager.Stage(source, maintenanceRoot, stagingId);
        Assert.True(result.Succeeded, result.Code);
        Assert.NotEqual(Path.GetDirectoryName(source), Path.GetDirectoryName(result.RunnerPath));
        Assert.Equal("trusted-host", File.ReadAllText(result.RunnerPath!));
        Assert.True(stager.LaunchRunner(result.RunnerPath!, Path.Combine(maintenanceRoot, "request.json")));

        Assert.NotNull(launched);
        Assert.False(launched!.UseShellExecute);
        Assert.True(launched.CreateNoWindow);
        Assert.Equal(UpdateActivationContract.RunnerSwitch, launched.ArgumentList[0]);
        Assert.Equal(UpdateActivationContract.RequestPathSwitch, launched.ArgumentList[1]);
        Assert.Equal(Path.Combine(maintenanceRoot, "request.json"), launched.ArgumentList[2]);
    }

    [Fact]
    public void Wrong_filename_or_untrusted_host_never_stages()
    {
        Directory.CreateDirectory(_root);
        var wrong = Path.Combine(_root, "not-maintenance.exe");
        File.WriteAllText(wrong, "host");
        var stager = new NativeMaintenanceRunnerStager(
            lockdown: _ => { },
            verifyTrust: _ => new MaintenanceHostTrustResult(
                false,
                MaintenanceTrustSource.None,
                "untrusted"),
            launch: _ => throw new InvalidOperationException());

        var result = stager.Stage(wrong, Path.Combine(_root, "root"), new string('b', 64));

        Assert.False(result.Succeeded);
        Assert.Equal("runner_source_invalid", result.Code);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }
}
