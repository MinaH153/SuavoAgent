using System;

namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Cross-installation UIA match atom — the unit of the v3.12 structural
/// fingerprint. All three fields come from the GREEN tier of
/// <see cref="UiaPropertyScrubber"/>, so no PHI can leak through this type.
///
/// Matching rules (see <see cref="MatchesStructurally"/>):
///   - ControlType: exact, case-sensitive.
///   - AutomationId: case-insensitive (ordinal).
///   - ClassName: null-tolerant (an unspecified class on either side matches
///     a specified class on the other); when both specified they must agree.
///
/// Never extend with Name / NameHash / Value / Text / Selection / HelpText /
/// ItemStatus — the GREEN/YELLOW/RED boundary from UiaPropertyScrubber must
/// hold end-to-end.
/// </summary>
public sealed record ElementSignature
{
    public string ControlType { get; }
    public string AutomationId { get; }
    public string? ClassName { get; }

    public ElementSignature(string ControlType, string AutomationId, string? ClassName)
    {
        if (string.IsNullOrWhiteSpace(ControlType))
            throw new ArgumentException(
                "ControlType must be a non-empty UIA enum string", nameof(ControlType));
        if (string.IsNullOrWhiteSpace(AutomationId))
            throw new ArgumentException(
                "AutomationId is required; anonymous elements cannot be part of a cross-installation signature",
                nameof(AutomationId));

        this.ControlType = ControlType;
        this.AutomationId = AutomationId;
        this.ClassName = string.IsNullOrWhiteSpace(ClassName) ? null : ClassName;
    }

    /// <summary>
    /// The line format baked into <c>WorkflowTemplate.StepsHash</c> and
    /// <c>WorkflowTemplate.ScreenSignatureV1</c>. Changing this format is a
    /// breaking change for every existing template and must bump the
    /// canonical version.
    /// </summary>
    public string CanonicalRepr => $"{ControlType}|{AutomationId}|{ClassName ?? string.Empty}";

    /// <summary>
    /// Returns true when this signature structurally matches <paramref name="other"/>
    /// under the rules above. Symmetric.
    /// </summary>
    public bool MatchesStructurally(ElementSignature other)
    {
        if (other is null) return false;
        if (!string.Equals(ControlType, other.ControlType, StringComparison.Ordinal))
            return false;
        if (!string.Equals(AutomationId, other.AutomationId, StringComparison.OrdinalIgnoreCase))
            return false;
        if (ClassName is not null && other.ClassName is not null
            && !string.Equals(ClassName, other.ClassName, StringComparison.Ordinal))
            return false;
        return true;
    }
}
