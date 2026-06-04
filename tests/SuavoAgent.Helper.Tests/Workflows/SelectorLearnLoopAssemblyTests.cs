using System;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Workflows;

/// <summary>
/// Vigorous ASSEMBLED test of the M2 learn-from-mistake loop's agent half: a signed seed's selector
/// patch, applied by Core's <see cref="SeedApplicator"/> into the live store, is actually USED by the
/// Helper's <see cref="SelectorResolver"/> to resolve to the LEARNED selector — and the hardcoded
/// builtin still backstops every step (no patch, or a learned-miss).
///
/// The pieces are unit-tested individually; this proves the cross-project
/// apply → store → resolve chain assembles, catching the kind of dead-on-arrival wire bug the
/// per-piece unit tests cannot (see feedback-exercise-loop-on-real-agent-mocked-tests-miss-doa).
/// </summary>
public class SelectorLearnLoopAssemblyTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");

    public void Dispose() => _db.Dispose();

    private const string Target = "learnedNdcBox";
    private const string Fallback = "learnedAltBox";
    // The patch is gated to a specific PMS version + screen — a learned fix only fires where the
    // live context is confirmed to match (fail-closed). The resolver must be given that context.
    private const string Pms = "v1.2";
    private const string Screen = "scrA";

    private static SeedResponse Seed(params SeedSelectorPatch[] patches) =>
        new(SeedDigest: "seed-asm", SeedVersion: 1, Phase: "model",
            GatesPassed: new[] { "schema" }, UiOverlap: null, Correlations: null,
            QueryShapes: Array.Empty<SeedQueryShape>(),
            StatusMappings: Array.Empty<SeedStatusMapping>(),
            WorkflowHints: null, SelectorPatches: patches);

    private static SeedSelectorPatch Patch() =>
        new("p-asm", "skill-x", "QuickSearchField", Pms, Screen,
            new SeedElementSignature("Edit", Target, "PioneerRxEdit"),
            new[] { new SeedElementSignature("Edit", Fallback, null) },
            0.9, 1);

    // The resolver as the job builds it once a patch is applied — WITH the live PMS/screen context
    // the gate requires.
    private SelectorResolver ResolverInContext() =>
        new(_db.GetActiveSelectorPatches(), pmsFingerprint: Pms, screenSignature: Screen);

    [Fact]
    public void AppliedSelectorPatch_IsUsedByResolver_ToResolveLearned()
    {
        // APPLY (Core): a signed seed's patch lands in the active store.
        var applied = new SeedApplicator(_db).ApplySelectorPatches(Seed(Patch()));
        Assert.Equal(1, applied.PatchesApplied);

        var resolver = ResolverInContext();
        Assert.True(resolver.HasPatches);

        // RESOLVE: the learned target exists on screen → Learned/Resolved, carrying the patch id.
        var r = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            tryFindBySignature: sig => sig.AutomationId == Target,
            tryFindBuiltin: () => true);

        Assert.Equal(SelectorResolvedVia.Learned, r.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, r.Outcome);
        Assert.Equal("p-asm", r.PatchId);
    }

    [Fact]
    public void GatedPatch_OnMismatchedContext_FailsClosedToBuiltin()
    {
        // SAFETY: a version/screen-gated fix must NOT fire where the live context isn't confirmed to
        // match — even though the patch is applied + in the store. This is the fail-closed property.
        new SeedApplicator(_db).ApplySelectorPatches(Seed(Patch()));
        var resolver = new SelectorResolver(_db.GetActiveSelectorPatches()); // NO live context

        var r = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            tryFindBySignature: sig => sig.AutomationId == Target, // would match if it ran...
            tryFindBuiltin: () => true);

        Assert.Equal(SelectorResolvedVia.Builtin, r.ResolvedVia); // ...but the gate excludes it
        Assert.Equal(SelectorOutcome.Resolved, r.Outcome);
        Assert.Null(r.PatchId);
    }

    [Fact]
    public void NoPatch_ResolvesViaBuiltin_Unchanged()
    {
        var resolver = ResolverInContext(); // empty store
        Assert.False(resolver.HasPatches);

        var r = resolver.Resolve(SelectorStepId.QuickSearchField, _ => false, () => true);

        Assert.Equal(SelectorResolvedVia.Builtin, r.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, r.Outcome);
        Assert.Null(r.PatchId);
    }

    [Fact]
    public void LearnedTargetMissing_BuiltinBackstops_AndFlagsFallbackForRetirement()
    {
        new SeedApplicator(_db).ApplySelectorPatches(Seed(Patch()));
        var resolver = ResolverInContext();

        // Learned target + fallback both absent on screen; the builtin keeps the step working.
        var r = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            tryFindBySignature: _ => false,
            tryFindBuiltin: () => true);

        Assert.Equal(SelectorResolvedVia.Builtin, r.ResolvedVia);
        Assert.Equal(SelectorOutcome.FallbackUsed, r.Outcome); // the signal that retires a bad patch
        Assert.Equal("p-asm", r.PatchId);
    }

    [Fact]
    public void LearnedTargetMissing_LearnedFallbackSignatureUsed()
    {
        new SeedApplicator(_db).ApplySelectorPatches(Seed(Patch()));
        var resolver = ResolverInContext();

        // Target missing but the learned FALLBACK signature is present → still Learned/Resolved.
        var r = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            tryFindBySignature: sig => sig.AutomationId == Fallback,
            tryFindBuiltin: () => true);

        Assert.Equal(SelectorResolvedVia.Learned, r.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, r.Outcome);
    }

    [Fact]
    public void RejectedPatch_NeverReachesResolver()
    {
        // A patch with an unknown step is skipped at apply → the resolver sees no patch → builtin.
        var bogus = new SeedSelectorPatch("p-bogus", "skill-x", "BogusStep", "v1.2", "scrA",
            new SeedElementSignature("Edit", Target, null), Array.Empty<SeedElementSignature>(), 0.9, 1);
        new SeedApplicator(_db).ApplySelectorPatches(Seed(bogus));

        var resolver = new SelectorResolver(_db.GetActiveSelectorPatches());
        Assert.False(resolver.HasPatches);
    }
}
