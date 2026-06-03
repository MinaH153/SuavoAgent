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
                // Drawing off keeps the test under the 5s budget; the bugs
                // we care about (XAML compile, resource type binding) fire
                // before any pixel is rasterized.
                UseHeadlessDrawing = false,
            });
}
