using FlaUI.Core.AutomationElements;
using FlaUI.Core.Conditions;
using FlaUI.Core.Definitions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Resolves a workflow step's target element, trying a learned <see cref="SelectorPatch"/>
/// (target then fallbacks) before the hardcoded builtin condition. With no matching patch it
/// uses the builtin alone — byte-identical to the pre-M2b behavior — so the builtin constant
/// stays the fail-closed safety net and a job with no patches is unchanged.
///
/// Even a wrong learned selector degrades gracefully: the per-element builtin fallback fires
/// the moment a learned target isn't found, so a bad patch never blinds the step — it just
/// reports <see cref="SelectorOutcome.FallbackUsed"/>, the signal that retires it later.
///
/// The resolution POLICY (<see cref="Resolve"/> / <see cref="ActivePatchFor"/>) is pure and
/// unit-tested; <see cref="FindFirst"/> is the thin FlaUI adapter (Windows-only, on-box verified).
/// </summary>
public sealed class SelectorResolver
{
    private readonly IReadOnlyList<SelectorPatch> _patches;
    private readonly string? _pmsFingerprint;
    private readonly string? _screenSignature;

    public SelectorResolver(
        IReadOnlyList<SelectorPatch>? patches,
        string? pmsFingerprint = null,
        string? screenSignature = null)
    {
        _patches = patches ?? Array.Empty<SelectorPatch>();
        _pmsFingerprint = pmsFingerprint;
        _screenSignature = screenSignature;
    }

    public bool HasPatches => _patches.Count > 0;

    public readonly record struct Resolution(
        SelectorResolvedVia ResolvedVia,
        SelectorOutcome Outcome,
        string? PatchId);

    /// <summary>
    /// The highest-version active patch whose step matches and whose (optional) PMS/screen gates
    /// are satisfied by the live context. A null gate on the patch is a wildcard; a null context
    /// can't exclude a gated patch (we don't know the context, so we don't block on it).
    /// </summary>
    public SelectorPatch? ActivePatchFor(SelectorStepId step)
    {
        SelectorPatch? best = null;
        foreach (var p in _patches)
        {
            if (p.StepId != step) continue;
            if (p.PmsFingerprint is not null && _pmsFingerprint is not null
                && !string.Equals(p.PmsFingerprint, _pmsFingerprint, StringComparison.Ordinal)) continue;
            if (p.ScreenSignatureV1 is not null && _screenSignature is not null
                && !string.Equals(p.ScreenSignatureV1, _screenSignature, StringComparison.Ordinal)) continue;
            if (best is null || p.Version > best.Version) best = p;
        }
        return best;
    }

    /// <summary>
    /// Pure resolution policy: learned target → learned fallbacks → builtin. The find delegates
    /// are injected so this is testable without a live UIA tree.
    /// </summary>
    public Resolution Resolve(
        SelectorStepId step,
        Func<ElementSignature, bool> tryFindBySignature,
        Func<bool> tryFindBuiltin)
    {
        var patch = ActivePatchFor(step);
        if (patch is not null)
        {
            if (tryFindBySignature(patch.Target))
                return new Resolution(SelectorResolvedVia.Learned, SelectorOutcome.Resolved, patch.PatchId);
            foreach (var fb in patch.Fallbacks)
                if (tryFindBySignature(fb))
                    return new Resolution(SelectorResolvedVia.Learned, SelectorOutcome.Resolved, patch.PatchId);

            // Learned selectors all missed — the builtin keeps the step working.
            return tryFindBuiltin()
                ? new Resolution(SelectorResolvedVia.Builtin, SelectorOutcome.FallbackUsed, patch.PatchId)
                : new Resolution(SelectorResolvedVia.Builtin, SelectorOutcome.Failed, patch.PatchId);
        }

        return tryFindBuiltin()
            ? new Resolution(SelectorResolvedVia.Builtin, SelectorOutcome.Resolved, null)
            : new Resolution(SelectorResolvedVia.Builtin, SelectorOutcome.Failed, null);
    }

    /// <summary>
    /// FlaUI adapter: wires the live finds into <see cref="Resolve"/> and returns the found
    /// element (or null) plus how it resolved. With no patch this is exactly
    /// <c>scope.FindFirstDescendant(builtin)</c>.
    /// </summary>
    public (AutomationElement? Element, Resolution Resolution) FindFirst(
        AutomationElement scope, ConditionFactory cf, SelectorStepId step, ConditionBase builtin)
    {
        AutomationElement? found = null;
        var resolution = Resolve(
            step,
            sig =>
            {
                var cond = ToCondition(cf, sig);
                if (cond is null) return false;
                found = TryFind(scope, cond);
                return found is not null;
            },
            () =>
            {
                found = TryFind(scope, builtin);
                return found is not null;
            });
        return (found, resolution);
    }

    private static AutomationElement? TryFind(AutomationElement scope, ConditionBase cond)
    {
        try { return scope.FindFirstDescendant(cond); }
        catch { return null; }
    }

    /// <summary>
    /// GREEN-tier <see cref="ElementSignature"/> → a UIA condition (ControlType + AutomationId
    /// [+ ClassName]). Returns null when the ControlType isn't a known UIA enum — a patch we
    /// can't safely apply, so the resolver skips it and falls through to the builtin.
    /// </summary>
    internal static ConditionBase? ToCondition(ConditionFactory cf, ElementSignature sig)
    {
        if (!Enum.TryParse<ControlType>(sig.ControlType, ignoreCase: false, out var ct)) return null;
        ConditionBase cond = new AndCondition(cf.ByControlType(ct), cf.ByAutomationId(sig.AutomationId));
        if (sig.ClassName is not null)
            cond = new AndCondition(cond, cf.ByClassName(sig.ClassName));
        return cond;
    }
}
