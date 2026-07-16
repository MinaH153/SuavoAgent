using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class ServiceInstallerProcessOwnershipTests
{
    [Theory]
    [InlineData("SuavoAgent.Core")]
    [InlineData("SuavoAgent.Broker")]
    [InlineData("SuavoAgent.Watchdog")]
    [InlineData("SuavoAgent.Helper")]
    public void Exact_msi_owned_runtime_process_is_classified(string processName) =>
        Assert.True(ServiceInstaller.IsOwnedInstalledCohortProcess(
            processName,
            $@"C:\Program Files\Suavo\Agent\{processName}.exe",
            @"C:\Program Files\Suavo\Agent"));

    [Theory]
    [InlineData(
        "SuavoAgent.Broker",
        @"C:\Users\queen\operator-tools\SuavoAgent.Broker.exe")]
    [InlineData(
        "SuavoAgent.Broker-copy",
        @"C:\Program Files\Suavo\Agent\SuavoAgent.Broker-copy.exe")]
    [InlineData(
        "SuavoAgent.Maintenance",
        @"C:\Program Files\Suavo\Agent\SuavoAgent.Maintenance.exe")]
    [InlineData(
        "SuavoAgent.Helper",
        @"C:\Program Files\Suavo\Agent\..\Agent\SuavoAgent.Helper.exe")]
    [InlineData(
        "SuavoAgent.Broker",
        @"C:\Users\queen\suavo-publish\Broker\SuavoAgent.Broker.exe")]
    public void Same_named_or_non_installed_process_is_never_terminated_by_cohort_quiesce(
        string processName,
        string executablePath) =>
        Assert.False(ServiceInstaller.IsOwnedInstalledCohortProcess(
            processName,
            executablePath,
            @"C:\Program Files\Suavo\Agent"));
}
