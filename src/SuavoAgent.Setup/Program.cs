using System.Runtime.InteropServices;
using Avalonia;

namespace SuavoAgent.Setup;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (IsConsoleMode(args))
        {
            AttachParentConsole();
            return ConsoleInstaller.RunAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
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
