using SuavoAgent.Contracts.Maintenance;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Maintenance;

public sealed class MaintenanceContractTests
{
    [Theory]
    [InlineData(MaintenanceReason.WatchdogServiceMissing, "watchdog-service-missing")]
    [InlineData(MaintenanceReason.ServiceRestartFailed, "service-restart-failed")]
    [InlineData(MaintenanceReason.HelperLaunchFailed, "helper-launch-failed")]
    [InlineData(MaintenanceReason.RemoteRepairRequested, "remote-repair-requested")]
    [InlineData(MaintenanceReason.SelfUninstallRequested, "self-uninstall-requested")]
    [InlineData(MaintenanceReason.ManualRepairRequested, "manual-repair-requested")]
    public void ReasonRoundTrips(MaintenanceReason expected, string wire)
    {
        Assert.Equal(wire, MaintenanceContract.ToWireValue(expected));
        Assert.True(MaintenanceContract.TryParseReason(wire, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void UnknownReasonFailsClosed()
    {
        Assert.False(MaintenanceContract.TryParseReason("unexpected", out var reason));
        Assert.Equal(MaintenanceReason.Unspecified, reason);
    }

    [Fact]
    public void RepairArgumentsContainNoShellOrScriptDependency()
    {
        var args = MaintenanceContract.BuildRepairArguments(MaintenanceReason.ServiceRestartFailed);

        Assert.Equal("--repair-services --reason service-restart-failed", args);
        Assert.DoesNotContain("powershell", args, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".ps1", args, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Signed_receipt_names_are_fixed_and_adjacent_safe()
    {
        Assert.Equal("SuavoSetup.exe", MaintenanceContract.SignedSetupArtifactName);
        Assert.Equal("checksums.sha256", MaintenanceContract.ReleaseChecksumsFileName);
        Assert.Equal("checksums.sha256.sig", MaintenanceContract.ReleaseChecksumsSignatureFileName);
        Assert.Equal("current-update-manifest.txt", MaintenanceContract.CurrentOtaManifestFileName);
        Assert.Equal("current-update-manifest.sig", MaintenanceContract.CurrentOtaManifestSignatureFileName);

        Assert.All(
            new[]
            {
                MaintenanceContract.ReleaseChecksumsFileName,
                MaintenanceContract.ReleaseChecksumsSignatureFileName,
                MaintenanceContract.CurrentOtaManifestFileName,
                MaintenanceContract.CurrentOtaManifestSignatureFileName,
            },
            name => Assert.Equal(name, Path.GetFileName(name)));
    }
}
