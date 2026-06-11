using Avalonia;
using Avalonia.Headless;
using SuavoAgent.Setup.Gui;

// Tells Avalonia.Headless.XUnit how to build the test AppBuilder. Headless
// platform: no real OS windows opened, but the XAML loader + resource
// resolution + control construction all run for real, which is exactly the
// surface where Bug 24 (Avalonia InvalidCastException on a DynamicResource
// type mismatch) fired.
[assembly: AvaloniaTestApplication(typeof(SuavoAgent.Setup.Tests.AvaloniaTestAppBuilder))]

namespace SuavoAgent.Setup.Tests;

public static class AvaloniaTestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions
            {
                // Drawing ON: MainWindow.axaml now loads the Suavo logo as an
                // <Image>/Window.Icon, and Bitmap decode at InitializeComponent
                // needs IPlatformRenderInterface — with drawing off the smoke
                // tests throw "Unable to locate 'IPlatformRenderInterface'".
                // The headless software backend rasterizes in-memory + no-op,
                // so the 4 construct-only tests stay well under the time budget.
                UseHeadlessDrawing = true,
            });
}
