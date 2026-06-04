using System;
using SuavoAgent.Diagnostics;
using SuavoAgent.Diagnostics.Phi;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Catalog-derived sentinel strip-set (blueprint fix #6). The residual-PHI check
/// (<c>IsDefinitelyPhi</c>) must strip a scrubber's OWN output labels before testing, or it
/// would re-flag a correctly-scrubbed value. The label set is single-sourced in
/// <see cref="PhiRuleCatalog.SentinelLabels"/>; this pins every label is present and that a
/// pure-sentinel string is never treated as residual PHI.
/// </summary>
public sealed class PhiSentinelLabelTests
{
    [Theory]
    [InlineData("PIONEERRX_ID")]
    [InlineData("MEMBER_ID")]
    [InlineData("NPI")]
    [InlineData("NDC")]
    [InlineData("DEA")]
    [InlineData("SSN")]
    [InlineData("RX_NUM")]
    [InlineData("USER")]
    [InlineData("DOB")]
    [InlineData("PATIENT")]
    [InlineData("SCRUB_TIMEOUT")]
    [InlineData("SCRUB_FAILED")]
    [InlineData("PHONE")]
    [InlineData("MRN")]
    [InlineData("ADDRESS")]
    [InlineData("NAME")]
    [InlineData("CODE")]
    public void Sentinel_label_is_in_the_catalog_strip_set(string label)
        => Assert.Contains(label, PhiRuleCatalog.SentinelLabels);

    [Fact]
    public void IsDefinitelyPhi_is_false_for_a_pure_sentinel_string()
    {
        var scrubber = new PhiScrubber(new RulesetV1(), TimeSpan.FromMilliseconds(200));
        Assert.False(scrubber.IsDefinitelyPhi(
            "[NAME] [CODE] [PHONE] [MRN] [ADDRESS] [SSN] [DEA] [NDC] [PIONEERRX_ID]"));
    }
}
