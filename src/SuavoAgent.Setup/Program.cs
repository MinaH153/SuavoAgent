using System.Runtime.InteropServices;
using Avalonia;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Setup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Diagnostic Mesh: Wire.AttachUnhandledHooks MUST be the literal
        // first executable statement of Main (spec §7 PR 4 wire-ordering
        // invariant; verified by WireOrderingTests). Bug 24's CLR fast-
        // fail surface lives in this entry point's BuildAvaloniaApp call.
        Wire.AttachUnhandledHooks(WireComponent.Setup, new WireOptions
        {
            LocalCrashLogPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "logs", "setup-crash.log"),
            LocalJournalPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent", "diagnostics", "events.jsonl"),
            Dsn = Environment.GetEnvironmentVariable("SUAVO_SENTRY_DSN"),
            EnableSentry = true,
        });

        if (IsConsoleMode(args))
        {
            AttachParentConsole();
            return ConsoleInstaller.RunAsync(args).GetAwaiter().GetResult();
        }

        // Wrap BuildAvaloniaApp + Lifetime in try/catch so XAML compile
        // failures during AppBuilder.Configure (Bug 24's class) reach Wire
        // before the CLR fast-fails on the unhandled exception.
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Wire.ReportException(WireComponent.Setup, ex, stage: "AvaloniaConfigure");
            throw;
        }
    }

    // Public so Avalonia's previewer and designer tooling can discover it.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<Gui.App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static bool IsConsoleMode(string[] args) =>
        args.Any(a =>
            string.Equals(a, "--console", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(a, "--silent", StringComparison.OrdinalIgnoreCase));

    // Reattach to the parent process's console when launched from PowerShell / cmd.
    // Lets fleet-deploy scripts still see phase output from a WinExe binary.
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    private static void AttachParentConsole()
    {
        try { AttachConsole(ATTACH_PARENT_PROCESS); }
        catch { /* No parent console available — GUI mode will still work. */ }
    }
}
