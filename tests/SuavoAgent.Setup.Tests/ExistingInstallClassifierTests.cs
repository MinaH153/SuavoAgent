using Avalonia.Headless.XUnit;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;
using SuavoAgent.Setup.Gui.Views;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class ExistingInstallClassifierTests
{
    [Fact]
    public void OnlyCompleteAbsenceAllowsFreshPairing()
    {
        Assert.Equal(
            ExistingInstallDisposition.NotInstalled,
            ExistingInstallClassifier.Classify(
                installPresent: false,
                arpPresent: false,
                servicePresent: false,
                cohortValid: false,
                configuredIdentityValid: false));

        Assert.Equal(
            ExistingInstallDisposition.RecoveryRequired,
            ExistingInstallClassifier.Classify(
                installPresent: false,
                arpPresent: true,
                servicePresent: false,
                cohortValid: false,
                configuredIdentityValid: false));
        Assert.Equal(
            ExistingInstallDisposition.RecoveryRequired,
            ExistingInstallClassifier.Classify(
                installPresent: false,
                arpPresent: false,
                servicePresent: false,
                cohortValid: false,
                configuredIdentityValid: false,
                dataPresent: true));
        Assert.Equal(
            ExistingInstallDisposition.RecoveryRequired,
            ExistingInstallClassifier.Classify(
                installPresent: true,
                arpPresent: false,
                servicePresent: false,
                cohortValid: false,
                configuredIdentityValid: false));
    }

    [Fact]
    public void ValidCohortIsClassifiedByConfiguredIdentity()
    {
        Assert.Equal(
            ExistingInstallDisposition.InstalledUnconfigured,
            ExistingInstallClassifier.Classify(true, true, true, true, false));
        Assert.Equal(
            ExistingInstallDisposition.InstalledConfigured,
            ExistingInstallClassifier.Classify(true, true, true, true, true));
    }

    [Fact]
    public void PriorOrPendingAuthorityNeverFallsThroughToFreshPairing()
    {
        Assert.Equal(
            ExistingInstallDisposition.RecoveryRequired,
            ExistingInstallClassifier.Classify(
                installPresent: true,
                arpPresent: true,
                servicePresent: true,
                cohortValid: true,
                configuredIdentityValid: false,
                authorityStatePresent: true));
        Assert.Equal(
            ExistingInstallDisposition.InstalledRecoveryPending,
            ExistingInstallClassifier.Classify(
                installPresent: true,
                arpPresent: true,
                servicePresent: true,
                cohortValid: true,
                configuredIdentityValid: true,
                authorityStatePresent: true,
                configurationRecoveryPending: true));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void UnconfiguredCohortRequiresCompleteMsiLifecycleBeforePairing(
        bool arpPresent,
        bool serviceCohortComplete)
    {
        Assert.Equal(
            ExistingInstallDisposition.RecoveryRequired,
            ExistingInstallClassifier.Classify(
                installPresent: true,
                arpPresent: arpPresent,
                servicePresent: true,
                cohortValid: true,
                configuredIdentityValid: false,
                serviceCohortComplete: serviceCohortComplete));
    }

    [AvaloniaFact]
    public void ConfiguredInstallNeverStartsDevicePairing()
    {
        var viewModel = new MainWindowViewModel(
            _ => { },
            classifyExistingInstall: () =>
                ExistingInstallDisposition.InstalledConfigured);

        Assert.IsType<RepairConfirmView>(viewModel.CurrentView);
        Assert.Equal("Repair services · Confirm", viewModel.StepLabel);
    }

    [AvaloniaFact]
    public void AmbiguousFootprintFailsClosedBeforeDevicePairing()
    {
        var viewModel = new MainWindowViewModel(
            _ => { },
            classifyExistingInstall: () =>
                ExistingInstallDisposition.RecoveryRequired);

        Assert.IsType<ErrorView>(viewModel.CurrentView);
        Assert.Equal("Existing installation needs recovery", viewModel.StepLabel);
    }
}
