using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// The single fail-closed validator that maps a wire <see cref="SeedSelectorPatch"/> to the
/// strict domain <see cref="SelectorPatch"/>. Shared by BOTH apply paths — the fleet seed pull
/// (<see cref="SeedApplicator.ApplySelectorPatches"/>) and the operator direct-correction command
/// (<c>update_selector</c>) — so the safety rules can never drift between them.
///
/// Returns null (caller skips, never stores) on anything malformed or unsafe: empty ids,
/// out-of-range confidence, an unknown step, a null target, or an <see cref="ElementSignature"/>
/// the domain ctor rejects (empty ControlType/AutomationId = not cross-install identifiable).
/// <paramref name="provenance"/> stamps the patch's origin — a seed digest for fleet patches,
/// <c>operator:&lt;commandId&gt;</c> for a direct correction.
/// </summary>
public static class SelectorPatchMapper
{
    public static SelectorPatch? TryMap(SeedSelectorPatch p, string provenance)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(p.PatchId) || string.IsNullOrWhiteSpace(p.SkillId)) return null;
            if (p.Confidence < 0 || p.Confidence > 1) return null;
            if (!System.Enum.TryParse<SelectorStepId>(p.StepId, ignoreCase: false, out var step)) return null;
            if (p.Target is null) return null;

            var target = new ElementSignature(p.Target.ControlType, p.Target.AutomationId, p.Target.ClassName);
            var fallbacks = (p.Fallbacks ?? System.Array.Empty<SeedElementSignature>())
                .Select(f => new ElementSignature(f.ControlType, f.AutomationId, f.ClassName))
                .ToList();

            return new SelectorPatch(
                p.PatchId, p.SkillId, step, p.PmsFingerprint, p.ScreenSignature,
                target, fallbacks, p.Confidence, provenance, p.Version,
                string.IsNullOrWhiteSpace(p.ApprovedByRole) ? null : p.ApprovedByRole.Trim());
        }
        catch
        {
            return null;
        }
    }
}
