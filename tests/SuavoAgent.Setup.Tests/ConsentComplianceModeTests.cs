using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the compliance-mode branching on ConsentViewModel:
/// HIPAA mode shows the disclosure card + employee-notice gate;
/// non-HIPAA ("none") hides the card and skips the notice requirement.
/// </summary>
public class ConsentComplianceModeTests
{
    // ── ShowHipaaDisclosure ───────────────────────────────────────────────────

    [Fact]
    public void Default_mode_is_hipaa_disclosure_visible()
    {
        var vm = NewVm();
        Assert.True(vm.ShowHipaaDisclosure);
    }

    [Fact]
    public void None_mode_hides_disclosure()
    {
        var vm = NewVm();
        vm.ComplianceMode = "none";
        Assert.False(vm.ShowHipaaDisclosure);
    }

    [Fact]
    public void Hipaa_mode_shows_disclosure()
    {
        var vm = NewVm();
        vm.ComplianceMode = "none";
        vm.ComplianceMode = "hipaa";
        Assert.True(vm.ShowHipaaDisclosure);
    }

    // ── RequiresEmployeeNotice gated on HIPAA ─────────────────────────────────

    [Fact]
    public void RequiresEmployeeNotice_false_in_none_mode_even_for_mandatory_state()
    {
        var vm = NewVm();
        vm.ComplianceMode = "none";
        vm.StateCode = "NY";
        Assert.False(vm.RequiresEmployeeNotice);
    }

    [Fact]
    public void RequiresEmployeeNotice_true_in_hipaa_mode_for_every_state()
    {
        var vm = NewVm();
        // default is hipaa
        vm.StateCode = "CA";
        Assert.True(vm.RequiresEmployeeNotice);
    }

    // ── CanAgree in none mode doesn't require employee notice ─────────────────

    [Fact]
    public void Agree_enabled_in_none_mode_without_notice_for_mandatory_state()
    {
        var vm = NewVm();
        vm.ComplianceMode = "none";
        vm.Name = "Jane";
        vm.StateCode = "NY";
        vm.AgreedToTerms = true;
        // Should be enabled without the employee notice (not required in "none" mode)
        Assert.True(vm.AgreeCommand.CanExecute(null));
    }

    [Fact]
    public void Agree_in_none_mode_persists_compliance_mode()
    {
        var ctx = NewContext();
        var vm = new ConsentViewModel(ctx, () => { });
        vm.ComplianceMode = "none";
        vm.Name = "Jane";
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;
        vm.AgreedToNotice = true;
        vm.AgreeCommand.Execute(null);
        Assert.Equal("none", ctx.Consent!.ComplianceMode);
    }

    [Fact]
    public void Agree_in_hipaa_mode_persists_hipaa_compliance_mode()
    {
        var ctx = NewContext();
        var vm = new ConsentViewModel(ctx, () => { });
        // default = hipaa
        vm.Name = "Jane";
        vm.StateCode = "CA";
        vm.AgreedToTerms = true;
        vm.AgreedToNotice = true;
        vm.AgreeCommand.Execute(null);
        Assert.Equal("hipaa", ctx.Consent!.ComplianceMode);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static ConsentViewModel NewVm() => new(NewContext(), () => { });

    private static InstallContext NewContext() => new(new SetupConfig(
        PharmacyId: "PH-test",
        ApiKey: "test-key",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.13.6",
        LearningMode: false));
}
