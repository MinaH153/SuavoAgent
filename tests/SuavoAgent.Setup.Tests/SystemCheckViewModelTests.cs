using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the System-check "Continue" gate after the minimum-viable-control
/// reframe: the only hard requirement to install is a compatible OS. PioneerRx
/// and SQL are <see cref="CheckState.Deferred"/> — the agent self-heals them
/// after it is online — so their absence must NOT block the install. A box with
/// no PioneerRx (a fresh laptop, or a pharmacy mid-rollout) must still proceed.
/// </summary>
public sealed class SystemCheckViewModelTests
{
    [Fact]
    public void Continue_blocked_before_probes_finish()
    {
        var vm = NewVm();
        // Every check defaults to Pending until RunChecks resolves it.
        Assert.False(vm.IsReady);
        Assert.False(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void Continue_enabled_when_os_ok_even_with_no_pioneerrx()
    {
        var vm = NewVm();
        vm.OsCheck.State = CheckState.Ok;
        vm.DiskCheck.State = CheckState.Ok;
        vm.BitLockerCheck.State = CheckState.Warn;   // BitLocker off — recommended, not required
        vm.PioneerCheck.State = CheckState.Deferred; // no PioneerRx — self-configures later
        vm.SqlCheck.State = CheckState.Deferred;     // no SQL target yet — self-configures later

        Assert.True(vm.IsReady);
        Assert.True(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void Continue_blocked_when_os_unsupported()
    {
        var vm = NewVm();
        vm.OsCheck.State = CheckState.Fail; // the one true hard requirement
        vm.DiskCheck.State = CheckState.Ok;
        vm.BitLockerCheck.State = CheckState.Ok;
        vm.PioneerCheck.State = CheckState.Ok;
        vm.SqlCheck.State = CheckState.Ok;

        Assert.False(vm.IsReady);
        Assert.False(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void Continue_blocked_while_any_probe_still_pending()
    {
        var vm = NewVm();
        vm.OsCheck.State = CheckState.Ok;
        vm.DiskCheck.State = CheckState.Ok;
        vm.BitLockerCheck.State = CheckState.Ok;
        vm.PioneerCheck.State = CheckState.Deferred;
        // SqlCheck deliberately left Pending — readiness must wait for the scan.

        Assert.False(vm.IsReady);
    }

    [Fact]
    public void Continue_enabled_fully_green_box()
    {
        var vm = NewVm();
        vm.OsCheck.State = CheckState.Ok;
        vm.DiskCheck.State = CheckState.Ok;
        vm.BitLockerCheck.State = CheckState.Ok;
        vm.PioneerCheck.State = CheckState.Ok;
        vm.SqlCheck.State = CheckState.Ok;

        Assert.True(vm.IsReady);
    }

    private static SystemCheckViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.15.0",
        LearningMode: false));
}
