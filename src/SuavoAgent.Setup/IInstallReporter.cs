namespace SuavoAgent.Setup;

/// <summary>
/// Phase-level reporter used by install services (discovery, download, service
/// registration). The console path writes to stdout via
/// <see cref="DefaultConsoleReporter"/>; the GUI path installs a reporter that
/// funnels events into the progress view-model.
/// </summary>
internal interface IInstallReporter
{
    void Step(string message);
    void Ok(string message);
    void Warn(string message);
    void Fail(string message);
    void Info(string message);
    void Progress(string label, long current, long total);
}

internal sealed class DefaultConsoleReporter : IInstallReporter
{
    public void Step(string message)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n[{DateTime.Now:HH:mm:ss}] {message}");
        Console.ResetColor();
    }

    public void Ok(string message)
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  [OK] {message}");
        Console.ResetColor();
    }

    public void Warn(string message)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"  [WARN] {message}");
        Console.ResetColor();
    }

    public void Fail(string message)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"  [FAIL] {message}");
        Console.ResetColor();
    }

    public void Info(string message)
    {
        Console.ForegroundColor = ConsoleColor.Gray;
        Console.WriteLine($"  {message}");
        Console.ResetColor();
    }

    public void Progress(string label, long current, long total)
    {
        if (total <= 0) return;
        var pct = (int)(current * 100 / total);
        var bar = new string('#', pct / 5) + new string('-', 20 - pct / 5);
        Console.Write($"\r  [{bar}] {pct,3}% {label}");
        if (current >= total) Console.WriteLine();
    }
}
