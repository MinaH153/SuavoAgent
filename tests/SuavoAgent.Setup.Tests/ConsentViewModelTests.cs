using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the Agree button enable/disable logic — the last safety rail before
/// monitoring software is authorized on a workstation. Missing name, state,
/// terms checkbox, or (in CT/DE/NY) the mandatory-notice checkbox must keep
/// the button disabled.
/// </summary>
public sealed class ConsentViewModelTests
{
    [Fact]
    public void Agree_blocked_when_name_empty()
    {
        var vm = NewVm();
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;

        Assert.False(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_blocked_when_state_empty()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.AgreedToTerms = true;

        Assert.False(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_blocked_when_terms_unchecked()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "CA";

        Assert.False(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_enabled_for_non_mandatory_state_with_terms_and_minimum_fields()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;

        Assert.True(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_blocked_in_mandatory_state_when_notice_unchecked()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "NY";
        vm.AgreedToTerms = true;

        Assert.True(vm.RequiresEmployeeNotice);
        Assert.False(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_enabled_in_mandatory_state_when_notice_checked()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "NY";
        vm.AgreedToTerms = true;
        vm.AgreedToNotice = true;

        Assert.True(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_writes_uppercased_trimmed_state_to_context()
    {
        var ctx = NewContext();
        var agreed = false;
        var vm = new ConsentViewModel(ctx, () => agreed = true);

        vm.Name = "  Jane Doe  ";
        vm.Title = "  Owner  ";
        vm.StateCode = "  ca  ";
        vm.AgreedToTerms = true;

        Assert.True(vm.AgreeCommand.CanExecute(null));
        vm.AgreeCommand.Execute(null);

        Assert.True(agreed);
        Assert.NotNull(ctx.Consent);
        Assert.Equal("Jane Doe", ctx.Consent!.AuthorizingName);
        Assert.Equal("Owner", ctx.Consent.AuthorizingTitle);
        Assert.Equal("CA", ctx.Consent.BusinessState);
        Assert.False(ctx.Consent.MandatoryNoticeState);
    }

    [Fact]
    public void Agree_empty_title_falls_back_to_authorized_representative()
    {
        var ctx = NewContext();
        var vm = new ConsentViewModel(ctx, () => { });

        vm.Name = "Jane";
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;
        vm.AgreeCommand.Execute(null);

        Assert.Equal("Authorized Representative", ctx.Consent!.AuthorizingTitle);
    }

    private static ConsentViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.13.6",
        LearningMode: false));
}
