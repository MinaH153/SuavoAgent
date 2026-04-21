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
            var ctx = TryLoadContext(desktop.Args ?? Array.Empty<string>());
            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(ctx, exitCode => desktop.Shutdown(exitCode)),
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static InstallContext? TryLoadContext(string[] args)
    {
        try
        {
            var config = SetupConfig.Load(args);
            return config == null ? null : new InstallContext(config);
        }
        catch
        {
            // SetupConfig.Validate threw — surface the no-config error view.
            return null;
        }
    }
}
