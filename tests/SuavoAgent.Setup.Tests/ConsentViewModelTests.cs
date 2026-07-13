using System.Linq;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the Agree button enable/disable logic — the last safety rail before
/// monitoring software is authorized on a workstation. Missing name, state,
/// terms checkbox, or employee-notice checkbox must keep
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
    public void Agree_blocked_for_every_hipaa_state_when_notice_unchecked()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;

        Assert.True(vm.RequiresEmployeeNotice);
        Assert.False(vm.AgreeCommand.CanExecute(null));
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
        vm.AgreedToNotice = true;

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
        vm.AgreedToNotice = true;
        vm.AgreeCommand.Execute(null);

        Assert.Equal("Authorized Representative", ctx.Consent!.AuthorizingTitle);
    }

    [Fact]
    public void StateCode_drives_notice_logic()
    {
        var vm = NewVm();
        vm.StateCode = "NY";

        Assert.Equal("NY", vm.StateCode);
        Assert.True(vm.RequiresEmployeeNotice); // NY = mandatory-notice state
        Assert.Contains("NY", vm.NoticeBannerText);
    }

    [Fact]
    public void MissingHint_names_whats_missing_and_clears_when_complete()
    {
        var vm = NewVm();
        Assert.Contains("your full name", vm.MissingHint);
        Assert.Contains("your state", vm.MissingHint);
        Assert.Contains("authorization checkbox", vm.MissingHint);

        vm.Name = "Jane";
        Assert.DoesNotContain("your full name", vm.MissingHint);

        vm.StateCode = "CA";
        vm.AgreedToTerms = true;
        Assert.Contains("employee-notice", vm.MissingHint);
        Assert.False(vm.AgreeCommand.CanExecute(null));

        vm.AgreedToNotice = true;
        Assert.Equal(string.Empty, vm.MissingHint); // complete -> hint disappears
        Assert.True(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void MissingHint_includes_employee_notice_for_mandatory_states()
    {
        var vm = NewVm();
        vm.Name = "Jane";
        vm.StateCode = "CT";
        vm.AgreedToTerms = true;

        Assert.Contains("employee-notice", vm.MissingHint);
        Assert.False(vm.AgreeCommand.CanExecute(null));

        vm.AgreedToNotice = true;
        Assert.Equal(string.Empty, vm.MissingHint);
        Assert.True(vm.AgreeCommand.CanExecute(null));
    }

    private static ConsentViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.13.6",
        LearningMode: false));
}
