using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>M2b selector_patches store — round-trip, retire, and INSERT-OR-REPLACE re-activation.</summary>
public class SelectorPatchStoreTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_sp_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public SelectorPatchStoreTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static SelectorPatch Patch(string id = "p1", int version = 1, string? pms = "v1.2", string? screen = "scrA") =>
        new(id, "skill-x", SelectorStepId.QuickSearchField, pms, screen,
            new ElementSignature("Edit", "ndcSearchBox", "PioneerRxEdit"),
            new[] { new ElementSignature("Edit", "altSearchBox", null) },
            0.85, "seed-digest", version);

    [Fact]
    public void Upsert_GetActive_RoundTrips()
    {
        _db.UpsertSelectorPatch(Patch(), "2026-06-03T00:00:00Z");

        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Equal("p1", got.PatchId);
        Assert.Equal("skill-x", got.SkillId);
        Assert.Equal(SelectorStepId.QuickSearchField, got.StepId);
        Assert.Equal("v1.2", got.PmsFingerprint);
        Assert.Equal("scrA", got.ScreenSignatureV1);
        Assert.Equal("Edit", got.Target.ControlType);
        Assert.Equal("ndcSearchBox", got.Target.AutomationId);
        Assert.Equal("PioneerRxEdit", got.Target.ClassName);
        Assert.Equal("altSearchBox", Assert.Single(got.Fallbacks).AutomationId);
        Assert.Equal(0.85, got.Confidence, 3);
        Assert.Equal("seed-digest", got.SeedDigest);
        Assert.Equal(1, got.Version);
    }

    [Fact]
    public void Retire_ExcludesFromActive()
    {
        _db.UpsertSelectorPatch(Patch(), "t1");
        _db.RetireSelectorPatch("p1", "t2", "confidence_drop");
        Assert.Empty(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void Upsert_SameId_Replaces_AndReactivates()
    {
        _db.UpsertSelectorPatch(Patch(version: 1), "t1");
        _db.RetireSelectorPatch("p1", "t2", "superseded");
        Assert.Empty(_db.GetActiveSelectorPatches());

        _db.UpsertSelectorPatch(Patch(version: 2), "t3"); // re-apply same id → fresh + active
        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Equal(2, got.Version);
    }

    [Fact]
    public void NullGatesAndEmptyFallbacks_RoundTrip()
    {
        _db.UpsertSelectorPatch(
            new SelectorPatch("p2", "skill-x", SelectorStepId.PricingTab, null, null,
                new ElementSignature("TabItem", "pricingTab", null),
                Array.Empty<ElementSignature>(), 0.5, "d", 1),
            "t1");

        var got = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Null(got.PmsFingerprint);
        Assert.Null(got.ScreenSignatureV1);
        Assert.Empty(got.Fallbacks);
    }

    [Fact]
    public void MultipleActivePatches_OrderedNewestVersionFirst()
    {
        _db.UpsertSelectorPatch(Patch(id: "a", version: 1), "t1");
        _db.UpsertSelectorPatch(Patch(id: "b", version: 5), "t2");
        var active = _db.GetActiveSelectorPatches();
        Assert.Equal(2, active.Count);
        Assert.Equal("b", active[0].PatchId); // ORDER BY version DESC
    }
}
