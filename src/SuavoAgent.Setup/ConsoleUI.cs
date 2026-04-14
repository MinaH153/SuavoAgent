namespace SuavoAgent.Setup;

/// <summary>
/// Colored console output helpers for non-technical pharmacy staff.
/// </summary>
internal static class ConsoleUI
{
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

    public static void WriteStep(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {msg}");
        Console.ResetColor();
    }

    public static void WriteOk(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [OK] {msg}");
        Console.ResetColor();
    }

    public static void WriteWarn(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [WARN] {msg}");
        Console.ResetColor();
    }

    public static void WriteFail(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [FAIL] {msg}");
        Console.ResetColor();
    }

    public static void WriteInfo(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {msg}");
        Console.ResetColor();
    }

    public static void WriteProgress(string label, long current, long total)
    {
        if (total <= 0) return;
        var pct = (int)(current * 100 / total);
        var bar = new string('#', pct / 5) + new string('-', 20 - pct / 5);
        Console.Write($"\r  [{bar}] {pct,3}% {label}");
        if (current >= total) Console.WriteLine();
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
