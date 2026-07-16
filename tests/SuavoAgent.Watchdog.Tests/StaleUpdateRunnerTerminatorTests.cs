using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Watchdog;
using Xunit;

namespace SuavoAgent.Watchdog.Tests;

public sealed class StaleUpdateRunnerTerminatorTests
{
    [Fact]
    public void IsExactRunnerImage_AcceptsOnlyClaimBoundSystemRunner()
    {
        var root = Path.GetFullPath(Path.Combine(
            Path.GetTempPath(),
            "suavo-maintenance-" + Guid.NewGuid().ToString("N")));
        var stagingId = new string('a', 64);
        var expected = UpdateActivationContract.GetMaintenanceRunnerPath(root, stagingId);

        Assert.True(StaleUpdateRunnerTerminator.IsExactRunnerImage(expected, expected));
        Assert.False(StaleUpdateRunnerTerminator.IsExactRunnerImage(
            Path.Combine(root, MaintenanceContract.ExecutableName),
            expected));
        Assert.False(StaleUpdateRunnerTerminator.IsExactRunnerImage(
            UpdateActivationContract.GetMaintenanceRunnerPath(root, new string('b', 64)),
            expected));
        Assert.False(StaleUpdateRunnerTerminator.IsExactRunnerImage(
            MaintenanceContract.ExecutableName,
            expected));
    }
}
