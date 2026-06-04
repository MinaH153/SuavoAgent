using System.Diagnostics;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// Behavioral contracts the single-source PHI merge must hold beyond byte-exactness:
/// dual-DEA (#2), the enforced-vs-shadow denylist split (#3/#5), and ReDoS fail-closed
/// timeouts on the trusted path (#4).
/// </summary>
public sealed class PhiMergeContractTests
{
    // ── fix #2: trusted DEA is shape-only (NO checksum validator) — a checksum-INVALID
    //    token still redacts via ScrubText, exactly as the legacy Core scrubber did. ──
    [Fact]
    public void ScrubText_redacts_checksum_invalid_DEA_shape()
    {
        Assert.Equal("[REDACTED]", PhiScrubber.ScrubText("AB1111111"));
        Assert.Equal("prescriber DEA [REDACTED] wrote",
            PhiScrubber.ScrubText("prescriber DEA AB1111111 wrote"));
    }

    // ── fix #3: EnforcedDenylist == today's Core patterns, including the broad DatePattern. ──
    [Theory]
    [InlineData("Born 1990-01-15")]
    [InlineData("DOB: 01/15/1990")]
    [InlineData("Call 555-123-4567")]
    [InlineData("SSN: 123-45-6789")]
    [InlineData("Address: 1234 Maple Street")]
    public void ContainsPhi_enforced_still_blocks_legacy_shapes(string input)
        => Assert.True(PhiScrubber.ContainsPhi(input));

    // The enforced set does NOT include the Diagnostics-origin staged shapes (e.g. a bare
    // Windows user path) — they are intentionally not enforced yet.
    [Theory]
    [InlineData(@"C:\Users\joshua\AppData\file.log")]
    public void ContainsPhi_enforced_does_not_block_staged_shapes(string input)
        => Assert.False(PhiScrubber.ContainsPhi(input));

    // ── fix #5: the ShadowDenylist DOES detect the staged shapes (would-block, measured). ──
    [Fact]
    public void ShadowDenylistMatch_detects_staged_windows_path()
    {
        Assert.Equal("Windows-user-path",
            PhiScrubber.ShadowDenylistMatch(@"C:\Users\joshua\AppData\file.log"));
        Assert.Null(PhiScrubber.ShadowDenylistMatch("nothing interesting here"));
    }

    // The shadow scanner respects the DEA checksum validator (only "would fire" on a valid
    // token), mirroring the engine's redact decision. (In production these reach the enforced
    // DeaShape first; this exercises the scanner directly.)
    [Fact]
    public void ShadowDenylistMatch_DEA_respects_checksum()
    {
        Assert.Equal("DEA-number-shape", PhiScrubber.ShadowDenylistMatch("xx AB1234563 yy"));
        Assert.Null(PhiScrubber.ShadowDenylistMatch("xx AB1111111 yy")); // invalid checksum
    }

    // ── fix #4: a pathological long no-delimiter input must NOT hang the trusted thread.
    //    The per-rule timeout bounds it (fail-closed); the legacy Core scrubber had none. ──
    [Fact]
    public void ScrubText_does_not_hang_on_pathological_input()
    {
        var pathological = "Address: 1" + new string('x', 40000);
        var sw = Stopwatch.StartNew();
        var result = PhiScrubber.ScrubText(pathological);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"ScrubText took {sw.ElapsedMilliseconds}ms — must be bounded by the per-rule timeout");
        Assert.NotNull(result);
    }

    [Fact]
    public void ContainsPhi_does_not_hang_on_pathological_input()
    {
        var pathological = "Address: 1" + new string('x', 40000);
        var sw = Stopwatch.StartNew();
        PhiScrubber.ContainsPhi(pathological);
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 3000,
            $"ContainsPhi took {sw.ElapsedMilliseconds}ms — must be bounded by the per-rule timeout");
    }
}
