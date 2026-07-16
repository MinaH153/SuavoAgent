using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SuavoAgent.Setup.Gui.Views;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Loads every shipped installer surface through Avalonia's compiled-XAML
/// runtime. A broken resource key, converter, template, or compiled binding in
/// any maintenance/onboarding screen must fail CI before the signed installer
/// reaches a workstation.
/// </summary>
public sealed class AllInstallerViewsSmokeTests
{
    [AvaloniaFact]
    public void EveryShippedInstallerView_LoadsItsCompiledXaml()
    {
        UserControl[] views =
        [
            new WelcomeView(),
            new DeviceCodePairingView(),
            new SystemCheckView(),
            new ConsentView(),
            new DestinationView(),
            new ProgressView(),
            new SuccessView(),
            new ErrorView(),
            new RepairConfirmView(),
            new UninstallConfirmView(),
            new UninstallSuccessView(),
        ];

        Assert.All(views, view =>
        {
            Assert.NotNull(view);
            Assert.NotNull(view.Content);
        });
    }
}
