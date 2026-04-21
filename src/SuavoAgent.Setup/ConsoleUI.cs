namespace SuavoAgent.Setup;

/// <summary>
/// Phase-level logging surface used by every install step. Routes through
/// <see cref="IInstallReporter"/> so the same phase code drives either the
/// PowerShell-style console path (default) or the Avalonia progress view
/// when the GUI installs a custom reporter via <see cref="SetReporter"/>.
/// The <see cref="Banner"/>, <see cref="CompletionSummary"/>, and
/// <see cref="WaitForExit"/> / <see cref="FatalError"/> helpers remain
/// console-only; the GUI has its own welcome/success surfaces.
/// </summary>
internal static class ConsoleUI
{
    private static IInstallReporter _reporter = new DefaultConsoleReporter();

    public static void SetReporter(IInstallReporter reporter) => _reporter = reporter;

    public static void WriteStep(string msg) => _reporter.Step(msg);
    public static void WriteOk(string msg) => _reporter.Ok(msg);
    public static void WriteWarn(string msg) => _reporter.Warn(msg);
    public static void WriteFail(string msg) => _reporter.Fail(msg);
    public static void WriteInfo(string msg) => _reporter.Info(msg);
    public static void WriteProgress(string label, long current, long total) => _reporter.Progress(label, current, total);

    public static void Banner(string pharmacyId, string releaseTag)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine();
        Console.WriteLine("  ╔═══════════════════════════════════════╗");
        Console.WriteLine("  ║   SuavoAgent — One-Click Installer    ║");
        Console.WriteLine("  ╚═══════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine($"  Pharmacy: {pharmacyId}");
        Console.WriteLine($"  Release:  {releaseTag}");
        Console.WriteLine();
    }

    public static void CompletionSummary(string installDir, string dataDir, string agentId,
        string sqlServer, string sqlDatabase, string? sqlUser)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine();
        Console.WriteLine("  ╔═══════════════════════════════════════════╗");
        Console.WriteLine("  ║   SuavoAgent — Installation Complete      ║");
        Console.WriteLine("  ╚═══════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"  Install:  {installDir}");
        Console.WriteLine($"  Data:     {dataDir}");
        Console.WriteLine($"  Logs:     {dataDir}\\logs\\");
        Console.WriteLine($"  Agent ID: {agentId}");
        Console.WriteLine();
        Console.WriteLine($"  SQL:      {sqlServer} / {sqlDatabase}");
        Console.WriteLine($"  Auth:     {(sqlUser != null ? $"SQL ({sqlUser})" : "Windows")}");
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  Agent will query for delivery-ready Rxs every 5 minutes.");
        Console.ResetColor();
    }

    public static void WaitForExit()
    {
        Console.WriteLine();
        Console.WriteLine("  Press any key to close this window...");
        try { Console.ReadKey(true); } catch { /* non-interactive */ }
    }

    public static void FatalError(string message)
    {
        Console.WriteLine();
        WriteFail(message);
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine("  Need help? Contact Suavo support:");
        Console.WriteLine("  Email: support@suavollc.com");
        Console.WriteLine("  Phone: (555) 123-4567");
        Console.ResetColor();
        WaitForExit();
    }
}
