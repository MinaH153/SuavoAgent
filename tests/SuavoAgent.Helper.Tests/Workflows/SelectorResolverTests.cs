using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests.Workflows;

/// <summary>
/// The resolver's POLICY (learned target → learned fallbacks → builtin) and its safety
/// invariant: with no matching patch, ONLY the builtin is tried — byte-identical to the
/// pre-M2b behavior. (The FlaUI find adapter is Windows-only and verified on-box.)
/// </summary>
public class SelectorResolverTests
{
    private static readonly ElementSignature Target = new("Edit", "ndcSearchBox", null);
    private static readonly ElementSignature Fallback = new("Edit", "altSearchBox", null);

    private static SelectorPatch Patch(
        SelectorStepId step = SelectorStepId.QuickSearchField,
        string? pms = null, string? screen = null, int version = 1, string id = "p1",
        string? approvedByRole = SelectorResolver.PharmacistInChargeRole) =>
        new(id, "skill-x", step, pms, screen, Target, new[] { Fallback }, 0.9, "digest", version,
            approvedByRole);

    [Fact]
    public void QuickSearch_UnapprovedLearnedPatch_IsIgnored_AndOnlyBuiltinRuns()
    {
        var resolver = new SelectorResolver(new[] { Patch(approvedByRole: null) });
        var signatureCalls = 0;
        var builtinCalls = 0;

        var result = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            _ => { signatureCalls++; return true; },
            () => { builtinCalls++; return false; });

        Assert.Equal(0, signatureCalls);
        Assert.Equal(1, builtinCalls);
        Assert.Equal(SelectorOutcome.Failed, result.Outcome);
        Assert.Null(result.PatchId);
    }

    [Fact]
    public void NoPatch_TriesOnlyBuiltin_IdenticalToToday()
    {
        var resolver = new SelectorResolver(patches: null);
        int sigCalls = 0, builtinCalls = 0;

        var res = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            _ => { sigCalls++; return true; },
            () => { builtinCalls++; return true; });

        Assert.Equal(0, sigCalls); // never attempts a learned selector
        Assert.Equal(1, builtinCalls);
        Assert.Equal(SelectorResolvedVia.Builtin, res.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, res.Outcome);
        Assert.Null(res.PatchId);
    }

    [Fact]
    public void NoPatch_BuiltinMiss_Failed()
    {
        var res = new SelectorResolver(Array.Empty<SelectorPatch>())
            .Resolve(SelectorStepId.PricingTab, _ => true, () => false);

        Assert.Equal(SelectorResolvedVia.Builtin, res.ResolvedVia);
        Assert.Equal(SelectorOutcome.Failed, res.Outcome);
    }

    [Fact]
    public void LearnedTarget_Found_DoesNotTouchBuiltin()
    {
        var resolver = new SelectorResolver(new[] { Patch() });
        var builtinCalled = false;

        var res = resolver.Resolve(
            SelectorStepId.QuickSearchField,
            sig => sig == Target,
            () => { builtinCalled = true; return true; });

        Assert.False(builtinCalled);
        Assert.Equal(SelectorResolvedVia.Learned, res.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, res.Outcome);
        Assert.Equal("p1", res.PatchId);
    }

    [Fact]
    public void LearnedTargetMiss_FallbackFound_StillLearned()
    {
        var res = new SelectorResolver(new[] { Patch() })
            .Resolve(SelectorStepId.QuickSearchField, sig => sig == Fallback, () => true);

        Assert.Equal(SelectorResolvedVia.Learned, res.ResolvedVia);
        Assert.Equal(SelectorOutcome.Resolved, res.Outcome);
    }

    [Fact]
    public void LearnedAllMiss_BuiltinFound_FallbackUsed()
    {
        var res = new SelectorResolver(new[] { Patch() })
            .Resolve(SelectorStepId.QuickSearchField, _ => false, () => true);

        Assert.Equal(SelectorResolvedVia.Builtin, res.ResolvedVia);
        Assert.Equal(SelectorOutcome.FallbackUsed, res.Outcome); // the signal that later retires a bad patch
        Assert.Equal("p1", res.PatchId);
    }

    [Fact]
    public void LearnedAndBuiltinMiss_Failed()
    {
        var res = new SelectorResolver(new[] { Patch() })
            .Resolve(SelectorStepId.QuickSearchField, _ => false, () => false);

        Assert.Equal(SelectorOutcome.Failed, res.Outcome);
        Assert.Equal("p1", res.PatchId);
    }

    [Fact]
    public void PatchForDifferentStep_IsIgnored()
    {
        var resolver = new SelectorResolver(new[] { Patch(step: SelectorStepId.PricingTab) });
        Assert.Null(resolver.ActivePatchFor(SelectorStepId.QuickSearchField));
    }

    [Fact]
    public void GateMismatch_PmsOrScreen_ExcludesPatch()
    {
        var pmsGated = new SelectorResolver(
            new[] { Patch(pms: "v1.2") }, pmsFingerprint: "v9.9", screenSignature: null);
        Assert.Null(pmsGated.ActivePatchFor(SelectorStepId.QuickSearchField));

        var screenGated = new SelectorResolver(
            new[] { Patch(screen: "scrA") }, pmsFingerprint: null, screenSignature: "scrB");
        Assert.Null(screenGated.ActivePatchFor(SelectorStepId.QuickSearchField));
    }

    [Fact]
    public void GatedPatch_NullLiveContext_ExcludedFailClosed()
    {
        // A PMS/screen-gated patch must NOT fire when we can't confirm the live context —
        // a gate is a restriction, so unknown context excludes it (fail-closed).
        var pmsGated = new SelectorResolver(
            new[] { Patch(pms: "v1.2") }, pmsFingerprint: null, screenSignature: null);
        Assert.Null(pmsGated.ActivePatchFor(SelectorStepId.QuickSearchField));

        var screenGated = new SelectorResolver(
            new[] { Patch(screen: "scrA") }, pmsFingerprint: null, screenSignature: null);
        Assert.Null(screenGated.ActivePatchFor(SelectorStepId.QuickSearchField));
    }

    [Fact]
    public void GateMatch_OrWildcard_SelectsPatch()
    {
        var matched = new SelectorResolver(
            new[] { Patch(pms: "v1.2", screen: "scrA") }, pmsFingerprint: "v1.2", screenSignature: "scrA");
        Assert.NotNull(matched.ActivePatchFor(SelectorStepId.QuickSearchField));

        // null gates on the patch are wildcards — always eligible.
        var wildcard = new SelectorResolver(new[] { Patch() }, pmsFingerprint: "anything", screenSignature: "anything");
        Assert.NotNull(wildcard.ActivePatchFor(SelectorStepId.QuickSearchField));
    }

    [Fact]
    public void MultiplePatchesSameStep_HighestVersionWins()
    {
        var resolver = new SelectorResolver(new[]
        {
            Patch(version: 1, id: "old"),
            Patch(version: 3, id: "new"),
            Patch(version: 2, id: "mid"),
        });
        Assert.Equal("new", resolver.ActivePatchFor(SelectorStepId.QuickSearchField)!.PatchId);
    }
}
