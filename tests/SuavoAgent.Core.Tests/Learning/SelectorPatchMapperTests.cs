using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// The shared fail-closed validator used by BOTH the fleet seed path and the operator
/// `update_selector` command. A malformed or non-identifiable patch must always map to null
/// (caller skips, never stores). Provenance is stamped from the caller (seed digest or operator).
/// </summary>
public class SelectorPatchMapperTests
{
    private static SeedSelectorPatch Wire(
        string step = "QuickSearchField",
        string targetCt = "Edit",
        string targetAid = "ndcBox",
        double conf = 0.9,
        string patchId = "p1",
        string skillId = "pharmacy_rx") =>
        new(patchId, skillId, step, "v1.2", "scrA",
            new SeedElementSignature(targetCt, targetAid, "Cls"),
            new[] { new SeedElementSignature("Edit", "altBox", null) },
            conf, 1);

    [Fact]
    public void Valid_MapsWithProvenanceStamped()
    {
        var p = SelectorPatchMapper.TryMap(Wire(), "operator:cmd-9");
        Assert.NotNull(p);
        Assert.Equal("p1", p!.PatchId);
        Assert.Equal(SelectorStepId.QuickSearchField, p.StepId);
        Assert.Equal("ndcBox", p.Target.AutomationId);
        Assert.Equal("altBox", Assert.Single(p.Fallbacks).AutomationId);
        Assert.Equal("operator:cmd-9", p.SeedDigest); // provenance from the operator command
    }

    [Theory]
    [InlineData("BogusStep", "Edit", "x", 0.9)] // unknown step
    [InlineData("QuickSearchField", "", "x", 0.9)] // empty ControlType (ctor rejects)
    [InlineData("QuickSearchField", "Edit", "", 0.9)] // empty AutomationId (ctor rejects)
    [InlineData("QuickSearchField", "Edit", "x", 1.5)] // confidence > 1
    [InlineData("QuickSearchField", "Edit", "x", -0.1)] // confidence < 0
    public void Malformed_ReturnsNull_FailClosed(string step, string ct, string aid, double conf)
    {
        Assert.Null(SelectorPatchMapper.TryMap(Wire(step, ct, aid, conf), "operator:c"));
    }

    [Fact]
    public void EmptyIds_ReturnNull()
    {
        Assert.Null(SelectorPatchMapper.TryMap(Wire(patchId: ""), "p"));
        Assert.Null(SelectorPatchMapper.TryMap(Wire(skillId: " "), "p"));
    }
}
