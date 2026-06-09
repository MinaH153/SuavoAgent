using System;
using System.IO;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

public class SelfUninstallTests
{
    private const string Install = @"C:\Program Files\Suavo\Agent";
    private const string Marker = @"C:\Windows\Temp\suavo_uninstall_done.txt";

    [Fact]
    public void BuildCleanerScript_ListsServices_WatchdogFirst()
    {
        var s = SelfUninstall.BuildCleanerScript(Install, Marker);
        var wd = s.IndexOf("SuavoAgent.Watchdog", StringComparison.Ordinal);
        var br = s.IndexOf("SuavoAgent.Broker", StringComparison.Ordinal);
        var co = s.IndexOf("SuavoAgent.Core", StringComparison.Ordinal);
        Assert.True(wd >= 0 && br >= 0 && co >= 0, "all three services must be named");
        Assert.True(wd < br && br < co,
            "watchdog MUST be first so it can't resurrect Core/Broker mid-wipe");
    }

    [Fact]
    public void BuildCleanerScript_DisablesThenStopsThenDeletes()
    {
        var s = SelfUninstall.BuildCleanerScript(Install, Marker);
        var disable = s.IndexOf("sc.exe config", StringComparison.Ordinal);
        var stop = s.IndexOf("sc.exe stop", StringComparison.Ordinal);
        var delete = s.IndexOf("sc.exe delete", StringComparison.Ordinal);
        Assert.True(disable >= 0 && stop >= 0 && delete >= 0);
        Assert.True(disable < stop && stop < delete,
            "must disable (no auto-restart) before stopping, and delete only after stopping");
    }

    [Fact]
    public void BuildCleanerScript_KillsOrphanProcessesAndWipesBothDirs()
    {
        var s = SelfUninstall.BuildCleanerScript(Install, Marker);
        Assert.Contains("SuavoAgent.*", s);              // process match incl. the Helper
        Assert.Contains("Stop-Process", s);
        Assert.Contains("Join-Path $env:ProgramData 'SuavoAgent'", s);
        Assert.Contains(Install, s);                     // install dir baked in
        Assert.Contains("Remove-Item", s);
    }

    [Fact]
    public void BuildCleanerScript_WritesMarkerAndSelfDeletes()
    {
        var s = SelfUninstall.BuildCleanerScript(Install, Marker);
        Assert.Contains(Marker, s);
        Assert.Contains("schtasks /delete /tn " + SelfUninstall.TaskName, s);
        Assert.Contains("Remove-Item $MyInvocation.MyCommand.Path", s);
    }

    [Fact]
    public void BuildCleanerScript_EscapesSingleQuotesInPaths()
    {
        // PowerShell single-quoted literals escape a quote by doubling it; without this an install
        // path containing a quote would break the literal (or allow injection).
        var s = SelfUninstall.BuildCleanerScript(@"C:\O'Brien\Agent", Marker);
        Assert.Contains(@"C:\O''Brien\Agent", s);
    }

    [Fact]
    public void BuildCleanerScript_ToleratesEmptyInstallDir()
    {
        // Dev box / unresolved install dir: the script must still be valid and guard Test-Path.
        var s = SelfUninstall.BuildCleanerScript(string.Empty, Marker);
        Assert.Contains("Test-Path", s);
        Assert.Contains("$install = ''", s);
    }

    [Fact]
    public void SentinelPath_IsUnderSuavoAgentDataDir()
    {
        Assert.EndsWith(Path.Combine("SuavoAgent", "uninstall.request"), SelfUninstall.SentinelPath());
    }
}
