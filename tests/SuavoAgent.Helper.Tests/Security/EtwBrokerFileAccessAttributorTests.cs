using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Helper.Security;
using Xunit;

namespace SuavoAgent.Helper.Tests.Security;

/// <summary>
/// The Helper-side ETW-handoff attributor. Locks the two read-side safety properties: the NAME-FORMAT fix
/// (a full-path/".exe" resolver output must reduce to the bare name the corroborator's denylist compares
/// against — proven END-TO-END through the REAL HoneytokenCorroborator, since a format mismatch would make
/// the oracle DEAF to its one true positive), and FAIL-OPEN (any miss → (null,null) → reversible Degrade).
/// </summary>
public sealed class EtwBrokerFileAccessAttributorTests
{
    private static readonly string Canonical = HoneytokenConstants.ComputeId();

    private static EtwBrokerFileAccessAttributor Build(
        IReadOnlyList<HoneytokenAttributionEntry> entries,
        Func<DateTimeOffset> now,
        TimeSpan? recency = null)
        => new("doc.json", "nonce", now: now, recencyWindow: recency,
               reader: (_, _, _, _) => entries);

    private static HoneytokenAttributionEntry Toucher(string? name, string? exe, DateTimeOffset at)
        => new(Canonical, 4242, name, exe, at);

    [Fact]
    public void RealPowershellFullPath_NormalizesToDenylistName_AND_Apoptoses_EndToEnd()
    {
        // The resolver hands over a FULL PATH with .exe — the exact format that would silently miss the
        // name-only denylist without normalization. Prove: attributor → 'powershell' → real corroborator → Apoptosis.
        var now = DateTimeOffset.UnixEpoch;
        var attr = Build(
            [Toucher(@"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe",
                     @"C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe", now)],
            () => now);

        var toucher = attr.Attribute("decoy");
        Assert.Equal("powershell", toucher.ProcessName);

        var corroborator = new HoneytokenCorroborator(@"C:\Program Files\SuavoAgent");
        var verdict = corroborator.Corroborate(toucher.ProcessName, toucher.ExePath, priorTouchCount: 0);
        Assert.Equal(CorroborationLevel.Apoptosis, verdict.Level);
    }

    [Fact]
    public void BenignNonShell_NormalizesAndDegrades_NeverLatches()
    {
        var now = DateTimeOffset.UnixEpoch;
        var attr = Build([Toucher(@"C:\Windows\explorer.exe", @"C:\Windows\explorer.exe", now)], () => now);
        var toucher = attr.Attribute("decoy");
        Assert.Equal("explorer", toucher.ProcessName);

        var corroborator = new HoneytokenCorroborator(@"C:\Program Files\SuavoAgent");
        // Even on a repeat, a non-shell name is reversible Degrade (the #189 never-brick guarantee).
        Assert.Equal(CorroborationLevel.Degrade,
            corroborator.Corroborate(toucher.ProcessName, toucher.ExePath, priorTouchCount: 9).Level);
    }

    [Fact]
    public void RecencyMatch_WithinWindow_ReturnsToucher()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var attr = Build([Toucher("powershell", "x", now.AddSeconds(-2))], () => now, recency: TimeSpan.FromSeconds(3));
        Assert.Equal("powershell", attr.Attribute("decoy").ProcessName);
    }

    [Fact]
    public void RecencyMatch_OutsideWindow_ReturnsNull_FailOpen()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(100);
        // Observed 5s ago, window 3s → no match → (null,null) → corroborator Degrades (safe), never apoptosis.
        var attr = Build([Toucher("powershell", "x", now.AddSeconds(-5))], () => now, recency: TimeSpan.FromSeconds(3));
        var t = attr.Attribute("decoy");
        Assert.Null(t.ProcessName);
        Assert.Null(t.ExePath);
    }

    [Fact]
    public void EmptyHandoff_ReturnsNull_FailOpen()
    {
        var now = DateTimeOffset.UnixEpoch;
        var attr = Build(Array.Empty<HoneytokenAttributionEntry>(), () => now);
        Assert.Null(attr.Attribute("decoy").ProcessName);
    }

    [Fact]
    public void PicksNewestEntry_WithinWindow()
    {
        var now = DateTimeOffset.UnixEpoch.AddSeconds(100);
        var attr = Build(
            [
                Toucher("cmd", "a", now.AddSeconds(-2)),
                Toucher("powershell", "b", now.AddSeconds(-1)), // newest
            ],
            () => now, recency: TimeSpan.FromSeconds(3));
        Assert.Equal("powershell", attr.Attribute("decoy").ProcessName);
    }

    [Fact]
    public void Cache_NotRefetchedWithinTtl_RefetchedAfter()
    {
        var clock = DateTimeOffset.UnixEpoch;
        var calls = 0;
        var attr = new EtwBrokerFileAccessAttributor(
            "doc.json", "nonce",
            now: () => clock,
            recencyWindow: TimeSpan.FromSeconds(3),
            cacheTtl: TimeSpan.FromMilliseconds(500),
            reader: (_, _, _, _) => { calls++; return [Toucher("powershell", "x", clock)]; });

        attr.Attribute("decoy");            // read #1
        attr.Attribute("decoy");            // within TTL → cached, no read
        Assert.Equal(1, calls);
        clock = clock.AddSeconds(1);        // past TTL
        attr.Attribute("decoy");            // read #2
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData(@"C:\Windows\System32\powershell.exe", "powershell")]
    [InlineData("powershell.exe", "powershell")]
    [InlineData("POWERSHELL", "powershell")]
    [InlineData(@"C:\Program Files\SuavoAgent\SuavoAgent.Helper.exe", "suavoagent.helper")]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    public void NormalizeName_ReducesToBareLowercaseName(string? raw, string? expected)
    {
        Assert.Equal(expected, EtwBrokerFileAccessAttributor.NormalizeName(raw));
    }
}
