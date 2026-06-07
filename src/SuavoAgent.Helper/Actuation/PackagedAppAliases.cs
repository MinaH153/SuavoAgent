namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Maps a launch executable (e.g. <c>notepad.exe</c>) to the set of process names whose window
/// we should look for after launching it.
///
/// WHY: the Windows 11 Notepad and Calculator are packaged (Store / WinUI) apps. The
/// <c>notepad.exe</c> / <c>calc.exe</c> entries under System32 are thin LAUNCHER STUBS that hand
/// off to a differently-named process (<c>Notepad.exe</c>, <c>CalculatorApp.exe</c>) and then exit.
/// The launcher's <see cref="System.Diagnostics.Process.MainWindowHandle"/> therefore stays
/// <c>IntPtr.Zero</c>, so any code that foregrounds "the process we started" silently does nothing —
/// and the next keystroke lands in whatever window was already focused. (Observed live: a
/// <c>type_into_field</c> leaked "Hello from SuavoAgent" into a PowerShell prompt instead of the
/// launched Notepad.) Resolving by the REAL process name fixes the happy path; the driver's
/// fail-closed foreground check is the safety net for everything this map misses.
///
/// This type is intentionally free of any Win32 surface so it is unit-testable on the Linux CI gate.
/// </summary>
public static class PackagedAppAliases
{
    // launcher-exe (no extension, case-insensitive) → ADDITIONAL packaged process name(s) that own
    // the window when they differ from the launcher name. Notepad needs no entry: the Win11 Store
    // Notepad's process is still "Notepad", which the bare launcher name matches (GetProcessesByName
    // is case-insensitive). Calculator genuinely differs — calc.exe hands off to Calculator /
    // CalculatorApp — so it must be listed.
    private static readonly IReadOnlyDictionary<string, string[]> Map =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["calc"] = new[] { "Calculator", "CalculatorApp" },
        };

    /// <summary>
    /// Candidate process names to search for after launching <paramref name="processExe"/>, most
    /// specific first: the bare launcher name (covers classic Win32 apps an operator might add),
    /// followed by any known packaged-app aliases. De-duplicated, case-insensitive.
    /// </summary>
    public static IReadOnlyList<string> CandidateProcessNames(string processExe)
    {
        var bare = StripExe(processExe);
        var ordered = new List<string>();
        if (!string.IsNullOrWhiteSpace(bare)) ordered.Add(bare);
        if (Map.TryGetValue(bare, out var aliases))
            ordered.AddRange(aliases);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<string>(ordered.Count);
        foreach (var n in ordered)
            if (seen.Add(n)) result.Add(n);
        return result;
    }

    private static string StripExe(string? processExe)
    {
        if (string.IsNullOrWhiteSpace(processExe)) return string.Empty;
        var name = processExe.Trim();
        // Defend against a stray path component even though the allowlist forbids one.
        var slash = name.LastIndexOfAny(new[] { '\\', '/' });
        if (slash >= 0 && slash < name.Length - 1) name = name[(slash + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? name[..^4]
            : name;
    }
}
