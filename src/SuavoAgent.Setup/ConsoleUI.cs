namespace SuavoAgent.Setup;

/// <summary>
/// Phase-level logging surface used by every install step. Routes through
/// <see cref="IInstallReporter"/> so the same phase code drives either the
/// headless maintenance console path (default) or the Avalonia progress view
/// when the GUI installs a custom reporter via <see cref="SetReporter"/>.
/// The <see cref="Banner"/>, <see cref="CompletionSummary"/>, and
/// <see cref="WaitForExit"/> / <see cref="FatalError"/> helpers remain
/// console-only; the GUI has its own welcome/success surfaces.
/// </summary>
internal static class ConsoleUI
{
    private static IInstallReporter _reporter = new DefaultConsoleReporter();

    public static void SetReporter(IInstallReporter reporter) => _reporter = reporter;

    // Every step line also lands in %ProgramData%\SuavoAgent\logs\setup.log — the GUI
    // has no console, and a failed GUI install used to leave zero on-box evidence.
    public static void WriteStep(string msg) => Report("STEP", msg, _reporter.Step);
    public static void WriteOk(string msg) => Report("OK", msg, _reporter.Ok);
    public static void WriteWarn(string msg) => Report("WARN", msg, _reporter.Warn);
    public static void WriteFail(string msg) => Report("FAIL", msg, _reporter.Fail);
    public static void WriteInfo(string msg) => Report("INFO", msg, _reporter.Info);

    public static void WriteProgress(string label, long current, long total)
    {
        // Progress labels render in both the native GUI and a parent console.
        // Treat them as untrusted even though current callers use fixed asset names.
        _reporter.Progress(SetupLog.SanitizeForLog(label), current, total);
    }

    private static void Report(string level, string message, Action<string> report)
    {
        // One privacy boundary for both destinations. Previously SetupLog scrubbed
        // the file copy but the GUI Details panel and parent console received the
        // original string, so an exception or tool stderr could still expose PHI.
        var safe = SetupLog.SanitizeForLog(message);
        SetupLog.Append(level, safe);
        report(safe);
    }

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
        Console.WriteLine("  Need help? Contact Suavo support at support@suavollc.com");
        Console.ResetColor();
        WaitForExit();
    }
}
