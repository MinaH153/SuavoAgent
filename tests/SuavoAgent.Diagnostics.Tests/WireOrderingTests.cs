using System.Reflection;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// Wire-ordering invariant tests. Spec §7 PR 4: <c>Wire.AttachUnhandledHooks</c>
/// MUST be the literal first executable statement in every long-running
/// entry point's Program.cs (Core / Broker / Helper / Watchdog / Setup).
///
/// Approach: source-text scan, not Roslyn AST. Each test asserts that the
/// Wire call appears BEFORE any "competitor" statement (AppDomain handler
/// registration, Serilog config, IsConsoleMode check). Source-text scan
/// catches re-ordering regressions at PR time without dragging the
/// Microsoft.CodeAnalysis surface into the test project.
public class WireOrderingTests
{
    private static readonly string RepoRoot = ResolveRepoRoot();

    private static string ResolveRepoRoot()
    {
        // Walk up from the test assembly's bin dir to find a directory
        // containing SuavoAgent.sln. Robust to layout changes.
        var dir = new DirectoryInfo(Path.GetDirectoryName(typeof(WireOrderingTests).Assembly.Location)!);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SuavoAgent.sln")))
        {
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Theory]
    [InlineData("Core",     "src/SuavoAgent.Core/Program.cs",     "WireComponent.Core")]
    [InlineData("Broker",   "src/SuavoAgent.Broker/Program.cs",   "WireComponent.Broker")]
    [InlineData("Helper",   "src/SuavoAgent.Helper/Program.cs",   "WireComponent.Helper")]
    [InlineData("Watchdog", "src/SuavoAgent.Watchdog/Program.cs", "WireComponent.Watchdog")]
    [InlineData("Setup",    "src/SuavoAgent.Setup/Program.cs",    "WireComponent.Setup")]
    public void EntryPoint_invokes_Wire_AttachUnhandledHooks(string component, string relPath, string expectedComponent)
    {
        var path = Path.Combine(RepoRoot, relPath);
        Assert.True(File.Exists(path), $"{component} entry-point source not found: {path}");
        var source = File.ReadAllText(path);
        Assert.Contains("Wire.AttachUnhandledHooks(" + expectedComponent, source);
    }

    [Theory]
    [InlineData("src/SuavoAgent.Core/Program.cs",     "WireComponent.Core",     "AppDomain.CurrentDomain.UnhandledException")]
    [InlineData("src/SuavoAgent.Broker/Program.cs",   "WireComponent.Broker",   "AppDomain.CurrentDomain.UnhandledException")]
    [InlineData("src/SuavoAgent.Watchdog/Program.cs", "WireComponent.Watchdog", "AppDomain.CurrentDomain.UnhandledException")]
    public void Wire_appears_before_legacy_AppDomain_handler(string relPath, string expectedComponent, string competitor)
    {
        var (wirePos, competitorPos) = LocatePair(relPath, expectedComponent, competitor);
        Assert.True(wirePos < competitorPos,
            $"Wire.AttachUnhandledHooks({expectedComponent}) at position {wirePos} must appear BEFORE '{competitor}' at position {competitorPos} in {relPath}");
    }

    [Theory]
    [InlineData("src/SuavoAgent.Core/Program.cs",     "WireComponent.Core",     "Log.Logger = new LoggerConfiguration")]
    [InlineData("src/SuavoAgent.Broker/Program.cs",   "WireComponent.Broker",   "Log.Logger = new LoggerConfiguration")]
    [InlineData("src/SuavoAgent.Helper/Program.cs",   "WireComponent.Helper",   "Log.Logger = new LoggerConfiguration")]
    [InlineData("src/SuavoAgent.Watchdog/Program.cs", "WireComponent.Watchdog", "Log.Logger = new LoggerConfiguration")]
    public void Wire_appears_before_Serilog_config(string relPath, string expectedComponent, string competitor)
    {
        var (wirePos, competitorPos) = LocatePair(relPath, expectedComponent, competitor);
        Assert.True(wirePos < competitorPos,
            $"Wire must come before Serilog config in {relPath} (Wire at {wirePos}, Serilog at {competitorPos})");
    }

    [Fact]
    public void Setup_Wire_is_first_statement_in_Main()
    {
        var path = Path.Combine(RepoRoot, "src/SuavoAgent.Setup/Program.cs");
        var source = File.ReadAllText(path);

        var mainSig = source.IndexOf("Main(string[] args)", StringComparison.Ordinal);
        Assert.True(mainSig > 0, "Could not locate Setup.Main signature");
        var openBrace = source.IndexOf('{', mainSig);
        Assert.True(openBrace > mainSig, "Could not locate Setup.Main opening brace");

        var wirePos = source.IndexOf("Wire.AttachUnhandledHooks(WireComponent.Setup", StringComparison.Ordinal);
        Assert.True(wirePos > openBrace, "Wire call not found inside Setup.Main");

        // Assert the inter-region (between Main's `{` and the Wire call) contains
        // only whitespace + comments — no other executable statement.
        var between = source.Substring(openBrace + 1, wirePos - openBrace - 1);
        var nonTrivialLines = between.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("//") && !l.StartsWith("/*") && !l.StartsWith("*"))
            .ToArray();
        Assert.Empty(nonTrivialLines);
    }

    [Fact]
    public void Setup_BuildAvaloniaApp_is_wrapped_in_try_catch_that_calls_Wire_ReportException()
    {
        var path = Path.Combine(RepoRoot, "src/SuavoAgent.Setup/Program.cs");
        var source = File.ReadAllText(path);
        Assert.Contains("try", source);
        Assert.Contains("BuildAvaloniaApp().StartWithClassicDesktopLifetime", source);
        Assert.Contains("Wire.ReportException(WireComponent.Setup", source);
        // The Wire.ReportException must appear AFTER the try block opening so
        // it's clearly inside the catch.
        var startCallPos = source.IndexOf("StartWithClassicDesktopLifetime", StringComparison.Ordinal);
        var reportPos = source.IndexOf("Wire.ReportException(WireComponent.Setup", StringComparison.Ordinal);
        Assert.True(reportPos > startCallPos,
            "Wire.ReportException must follow the BuildAvaloniaApp call (be in the catch block)");
    }

    private static (int wirePos, int competitorPos) LocatePair(string relPath, string expectedComponent, string competitor)
    {
        var path = Path.Combine(RepoRoot, relPath);
        Assert.True(File.Exists(path), $"Entry-point source not found: {path}");
        var source = File.ReadAllText(path);

        var wirePos = source.IndexOf($"Wire.AttachUnhandledHooks({expectedComponent}", StringComparison.Ordinal);
        var competitorPos = source.IndexOf(competitor, StringComparison.Ordinal);

        Assert.True(wirePos >= 0, $"Wire call for {expectedComponent} not found in {relPath}");
        Assert.True(competitorPos >= 0, $"Competitor '{competitor}' not found in {relPath}");
        return (wirePos, competitorPos);
    }
}
