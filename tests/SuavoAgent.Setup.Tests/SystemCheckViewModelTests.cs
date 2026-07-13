using System.Threading.Tasks;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class SystemCheckViewModelTests
{
    [Fact]
    public void ContinueIsBlockedBeforeAllRequiredProofsFinish()
    {
        var vm = NewVm();

        Assert.False(vm.IsReady);
        Assert.False(vm.ContinueCommand.CanExecute(null));
    }

    [Fact]
    public void ContinueIsEnabledOnlyForFullyGreenPharmacyWorkstation()
    {
        var vm = NewVm();
        MarkGreen(vm);

        Assert.True(vm.IsReady);
        Assert.True(vm.ContinueCommand.CanExecute(null));
    }

    [Theory]
    [InlineData("os")]
    [InlineData("disk")]
    [InlineData("runtime")]
    [InlineData("bitlocker")]
    [InlineData("device-key")]
    [InlineData("pioneerrx")]
    [InlineData("sql")]
    public void EveryRequiredControlFailsClosed(string control)
    {
        var vm = NewVm();
        MarkGreen(vm);
        Check(vm, control).State = CheckState.Fail;

        Assert.False(vm.IsReady);
        Assert.False(vm.ContinueCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(CheckState.Warn)]
    [InlineData(CheckState.Deferred)]
    public void RequiredWarningOrDeferredStateIsNotInstallAuthorization(
        CheckState state)
    {
        var vm = NewVm();
        MarkGreen(vm);
        vm.BitLockerCheck.State = state;

        Assert.False(vm.IsReady);
    }

    [Fact]
    public void ProbeFailureExplainsPioneerAndSqlBlockInsteadOfClaimingSelfHealing()
    {
        var vm = new SystemCheckViewModel(
            NewContext(),
            () => { },
            probeIsWindows10: () => true,
            probePioneer: () => throw new InvalidOperationException("boom"),
            probeSql: _ => null,
            probeRuntime: _ => Task.FromResult((CheckState.Ok, "VC++ runtime present")),
            probeDisk: () => (CheckState.Ok, "10 GB free"),
            probeEncryptedStorage: () => (CheckState.Ok, "BitLocker on"),
            probeDeviceKey: () => (CheckState.Ok, "TPM key enrolled"));

        vm.Apply(vm.Probe());

        Assert.Equal(CheckState.Fail, vm.PioneerCheck.State);
        Assert.Equal(CheckState.Fail, vm.SqlCheck.State);
        Assert.Contains("PioneerRx", vm.PioneerCheck.Detail);
        Assert.False(vm.IsReady);
    }

    [Fact]
    public void UnsupportedOsBlocksEvenWhenEveryOtherInjectedProbeIsHealthy()
    {
        var vm = new SystemCheckViewModel(
            NewContext(),
            () => { },
            probeIsWindows10: () => false,
            probePioneer: () => null,
            probeSql: _ => null,
            probeRuntime: _ => Task.FromResult((CheckState.Ok, "VC++ runtime present")),
            probeDisk: () => (CheckState.Ok, "10 GB free"),
            probeEncryptedStorage: () => (CheckState.Ok, "BitLocker on"),
            probeDeviceKey: () => (CheckState.Ok, "TPM key enrolled"));

        vm.Apply(vm.Probe());

        Assert.Equal(CheckState.Fail, vm.OsCheck.State);
        Assert.False(vm.IsReady);
    }

    private static CheckItem Check(SystemCheckViewModel vm, string control) => control switch
    {
        "os" => vm.OsCheck,
        "disk" => vm.DiskCheck,
        "runtime" => vm.RuntimeCheck,
        "bitlocker" => vm.BitLockerCheck,
        "device-key" => vm.DeviceKeyCheck,
        "pioneerrx" => vm.PioneerCheck,
        "sql" => vm.SqlCheck,
        _ => throw new ArgumentOutOfRangeException(nameof(control)),
    };

    private static void MarkGreen(SystemCheckViewModel vm)
    {
        vm.OsCheck.State = CheckState.Ok;
        vm.DiskCheck.State = CheckState.Ok;
        vm.RuntimeCheck.State = CheckState.Ok;
        vm.BitLockerCheck.State = CheckState.Ok;
        vm.DeviceKeyCheck.State = CheckState.Ok;
        vm.PioneerCheck.State = CheckState.Ok;
        vm.SqlCheck.State = CheckState.Ok;
    }

    private static SystemCheckViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.15.0",
        LearningMode: false,
        AgentId: "11111111-1111-1111-1111-111111111111",
        DeviceCode: "ABCD-2345",
        DeviceKeyId: new string('a', 64),
        DeviceKeyName: "SuavoAgent.DeviceAuthority.v1.test.slot.pending",
        DeviceFingerprint: "test-fingerprint",
        DeviceChallenge: new string('A', 43)));
}
