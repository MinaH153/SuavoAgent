using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using SuavoAgent.Setup.Gui;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

// Mesh PR 3: catches Bug 24's class (Avalonia InvalidCastException at
// MainWindow.axaml DynamicResource type mismatch). Without this smoke
// check, a package bump in Directory.Build.props + global.json that
// re-targets Avalonia or changes the resource binding contract ships
// green and only blows up at runtime on the installer host.
//
// [AvaloniaFact] runs on the Avalonia UI thread with the headless
// platform pre-initialized via AvaloniaTestAppBuilder.
public class AvaloniaInitSmokeTest
{
    [AvaloniaFact]
    public void App_AvaloniaXamlLoader_Initializes_Without_Exception()
    {
        // AvaloniaTestApplication initialization has already constructed
        // an App instance and called Initialize() (AvaloniaXamlLoader.Load).
        // If App.axaml or its merged Styles/SuavoResources.axaml fail to
        // resolve, this assertion's prerequisite never runs.
        Assert.NotNull(Application.Current);
    }

    [AvaloniaFact]
    public void MainWindow_InitializeComponent_Does_Not_Throw()
    {
        // Bug 24's actual failure site. `new MainWindow()` calls the
        // generated InitializeComponent() which runs AvaloniaXamlLoader.Load
        // against MainWindow.axaml — this is where the DynamicResource
        // type mismatch (SuavoRadius* as x:Double bound to CornerRadius,
        // SuavoWarning as Color bound to IBrush) blew up at runtime.
        var window = new MainWindow();
        Assert.NotNull(window);
    }

    [AvaloniaFact]
    public void MainWindow_DefaultDataContext_Is_MainWindowViewModel()
    {
        // The XAML declares <Window.DataContext><vm:MainWindowViewModel/>.
        // If MainWindowViewModel's parameterless constructor throws during
        // XAML load (e.g., a downstream view's resource resolution), this
        // catches the regression at PR time.
        var window = new MainWindow();
        Assert.NotNull(window.DataContext);
        Assert.IsType<MainWindowViewModel>(window.DataContext);
    }

    [AvaloniaFact]
    public void MainWindow_Background_Resolves_DynamicResource()
    {
        // MainWindow.axaml binds Background to {DynamicResource
        // SuavoBackgroundBrush}. If the merged Styles dictionary changes
        // type or moves the key, the resource fails to resolve at load
        // time and the Background is null (Bug 24's class for any future
        // brush-typed resource).
        var window = new MainWindow();
        Assert.NotNull(window.Background);
    }

    [AvaloniaFact]
    public void MainWindow_Show_Completes_Without_Exception()
    {
        // Exercising Show() on the headless platform forces the layout
        // pass + nested user control construction. Catches a deeper class
        // of XAML errors that survive the constructor but throw on layout.
        var window = new MainWindow();
        window.Show();
        Assert.True(window.IsVisible);
        window.Close();
    }
}
