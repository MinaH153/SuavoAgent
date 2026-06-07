using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

/// <summary>
/// The launch exe for a Windows 11 packaged app (notepad.exe / calc.exe) is a stub whose own
/// process never owns the window. Resolution must therefore search for the REAL process name(s).
/// These assertions pin the alias map that drives that search; they run on the cross-platform CI
/// gate (no Win32 surface), unlike the live foreground behaviour which is proven on a real box.
/// </summary>
public sealed class PackagedAppAliasesTests
{
    [Fact]
    public void Notepad_ResolvesToBareName_WhichMatchesCaseInsensitively()
    {
        // The Win11 Store Notepad's process is still "Notepad"; GetProcessesByName is
        // case-insensitive, so the bare launcher name is sufficient (no separate alias needed).
        var names = PackagedAppAliases.CandidateProcessNames("notepad.exe");
        Assert.Equal(new[] { "notepad" }, names);
    }

    [Fact]
    public void Calc_IncludesBothPackagedCandidates()
    {
        var names = PackagedAppAliases.CandidateProcessNames("calc.exe");
        Assert.Equal("calc", names[0]);
        Assert.Contains("Calculator", names);
        Assert.Contains("CalculatorApp", names);
    }

    [Fact]
    public void UnknownApp_FallsBackToBareNameOnly()
    {
        var names = PackagedAppAliases.CandidateProcessNames("mspaint.exe");
        Assert.Equal(new[] { "mspaint" }, names);
    }

    [Fact]
    public void StripsExtension_AndIsCaseInsensitive()
    {
        // Case is preserved (GetProcessesByName is case-insensitive, so it doesn't matter); only the
        // ".exe" suffix and any stray path are stripped.
        Assert.Equal("NOTEPAD", PackagedAppAliases.CandidateProcessNames("NOTEPAD.EXE")[0]);
        Assert.Equal("Notepad", PackagedAppAliases.CandidateProcessNames("Notepad")[0]); // no .exe → bare name preserved
        Assert.Contains("Calculator", PackagedAppAliases.CandidateProcessNames("CALC.EXE")); // alias lookup is case-insensitive
    }

    [Fact]
    public void StripsStrayPathComponent()
    {
        // The allowlist forbids paths, but resolution must still degrade safely if one slips through.
        var names = PackagedAppAliases.CandidateProcessNames(@"C:\Windows\System32\notepad.exe");
        Assert.Equal("notepad", names[0]);
    }

    [Fact]
    public void DeduplicatesCandidates()
    {
        var names = PackagedAppAliases.CandidateProcessNames("calc.exe");
        Assert.Equal(names.Count, System.Linq.Enumerable.Distinct(names, System.StringComparer.OrdinalIgnoreCase).Count());
    }
}
