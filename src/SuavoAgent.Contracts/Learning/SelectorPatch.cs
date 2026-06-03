using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// A learned, signed correction for ONE selector step. The agent's resolver tries the patch's
/// <see cref="Target"/> (then <see cref="Fallbacks"/>) before the hardcoded builtin constant, so
/// a drifted PioneerRx control (e.g. a renamed "Quick Search") can be re-targeted fleet-wide
/// WITHOUT a code change — while the builtin stays the fail-closed safety net.
///
/// The target is a strict <see cref="ElementSignature"/> (AutomationId REQUIRED): only an element
/// the agent can identify cross-installation by GREEN-tier structure can become a distributable
/// patch. Capture (M2a) records the looser <c>ObservedElement</c>; only the identifiable ones graduate.
///
/// Scope/gating: a patch applies only when <see cref="SkillId"/> + <see cref="StepId"/> match and,
/// when present, <see cref="PmsFingerprint"/> + <see cref="ScreenSignatureV1"/> match the live screen —
/// so a fix learned on one PioneerRx version/screen can't mis-fire on another. Provenance:
/// <see cref="SeedDigest"/> (which signed seed delivered it) + monotonic <see cref="Version"/>.
///
/// Compliance: a patch may only change WHERE the agent clicks (PHI-free structure) — never clinical
/// intent. It carries no PHI: every field is an id, an enum, a structural atom, or a number.
/// </summary>
public sealed record SelectorPatch(
    string PatchId,
    string SkillId,
    SelectorStepId StepId,
    string? PmsFingerprint,
    string? ScreenSignatureV1,
    ElementSignature Target,
    IReadOnlyList<ElementSignature> Fallbacks,
    double Confidence,
    string SeedDigest,
    int Version);
