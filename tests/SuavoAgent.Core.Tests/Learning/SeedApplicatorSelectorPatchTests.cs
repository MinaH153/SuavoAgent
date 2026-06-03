using System;
using System.Linq;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

/// <summary>
/// M2c receiver-side apply of learned selector patches. The seed envelope is already
/// ECDSA-verified at transport, so these cover the safety checks: idempotency, provenance
/// stamping, and fail-closed rejection of unknown steps / non-identifiable / out-of-range patches
/// (a rejected patch must NEVER reach the active store).
/// </summary>
public class SeedApplicatorSelectorPatchTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");
    private readonly SeedApplicator _applicator;

    public SeedApplicatorSelectorPatchTests() => _applicator = new SeedApplicator(_db);

    public void Dispose() => _db.Dispose();

    private static SeedResponse BuildResponse(SeedSelectorPatch[] patches, string digest = "seed-sp") =>
        new(SeedDigest: digest, SeedVersion: 1, Phase: "model",
            GatesPassed: new[] { "schema" }, UiOverlap: null, Correlations: null,
            QueryShapes: Array.Empty<SeedQueryShape>(),
            StatusMappings: Array.Empty<SeedStatusMapping>(),
            WorkflowHints: null,
            SelectorPatches: patches);

    private static SeedSelectorPatch BuildPatch(
        string id = "p1", string step = "QuickSearchField",
        string targetAid = "ndcSearchBox", double conf = 0.9, int version = 1) =>
        new(id, "skill-x", step, "v1.2", "scrA",
            new SeedElementSignature("Edit", targetAid, "PioneerRxEdit"),
            new[] { new SeedElementSignature("Edit", "altSearchBox", null) },
            conf, version);

    [Fact]
    public void ApplyValidPatch_StoresWithStampedProvenance()
    {
        var res = _applicator.ApplySelectorPatches(BuildResponse(new[] { BuildPatch() }, "seed-xyz"));

        Assert.Equal(1, res.PatchesApplied);
        Assert.Equal(0, res.PatchesSkipped);

        var stored = Assert.Single(_db.GetActiveSelectorPatches());
        Assert.Equal("p1", stored.PatchId);
        Assert.Equal(SelectorStepId.QuickSearchField, stored.StepId);
        Assert.Equal("ndcSearchBox", stored.Target.AutomationId);
        Assert.Equal("altSearchBox", Assert.Single(stored.Fallbacks).AutomationId);
        Assert.Equal("seed-xyz", stored.SeedDigest); // provenance stamped from the response digest
    }

    [Fact]
    public void Reapply_SameSeed_IsIdempotent()
    {
        var resp = BuildResponse(new[] { BuildPatch() });
        _applicator.ApplySelectorPatches(resp);
        var second = _applicator.ApplySelectorPatches(resp);

        Assert.Equal(0, second.PatchesApplied);
        Assert.Single(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void UnknownStepId_SkippedNeverStored()
    {
        var res = _applicator.ApplySelectorPatches(BuildResponse(new[] { BuildPatch(step: "BogusStep") }));

        Assert.Equal(0, res.PatchesApplied);
        Assert.Equal(1, res.PatchesSkipped);
        Assert.Empty(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void EmptyAutomationId_SkippedFailClosed()
    {
        // ElementSignature rejects an empty AutomationId → not cross-install identifiable → skip.
        var res = _applicator.ApplySelectorPatches(BuildResponse(new[] { BuildPatch(targetAid: "") }));

        Assert.Equal(0, res.PatchesApplied);
        Assert.Equal(1, res.PatchesSkipped);
        Assert.Empty(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void OutOfRangeConfidence_Skipped()
    {
        var res = _applicator.ApplySelectorPatches(BuildResponse(new[] { BuildPatch(conf: 1.5) }));

        Assert.Equal(1, res.PatchesSkipped);
        Assert.Empty(_db.GetActiveSelectorPatches());
    }

    [Fact]
    public void NoPatches_NoOp()
    {
        Assert.Equal(0, _applicator.ApplySelectorPatches(
            BuildResponse(Array.Empty<SeedSelectorPatch>())).PatchesApplied);
    }
}
