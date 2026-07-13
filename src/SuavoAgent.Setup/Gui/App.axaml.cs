using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.ViewModels;

namespace SuavoAgent.Setup.Gui;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(
                    exitCode => desktop.Shutdown(exitCode),
                    startInUninstall: Program.IsUninstallUiMode(desktop.Args),
                    startInRepair: Program.IsRepairUiMode(desktop.Args),
                    configureInstalledCohort: Program.IsConnectInstalledMode(desktop.Args)),
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

}
