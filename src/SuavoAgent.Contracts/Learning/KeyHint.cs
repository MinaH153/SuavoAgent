namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// Closed, PHI-free replacement for the rejected open-map hint bag.
/// A KeyHint either names a UIA key (Enter, Tab, Escape, …) or references a
/// whitelisted placeholder token — never plain text, never an operator label.
///
/// Extractor contract: never persist operator-entered text or text derived
/// from UI labels into a KeyHint. If a workflow step would require arbitrary
/// text input, emit <see cref="TemplateStepKind.Type"/> with
/// <see cref="Placeholder"/> only, and fail extraction if no suitable
/// placeholder applies.
/// </summary>
public sealed record KeyHint(string? KeyName, KeyHintPlaceholder? Placeholder)
{
    /// <summary>
    /// Canonical form used inside <c>WorkflowTemplate.StepsHash</c> so that
    /// byte-identical templates hash equally.
    /// </summary>
    public string CanonicalRepr => $"{KeyName ?? string.Empty}|{(Placeholder?.ToString() ?? string.Empty)}";
}

/// <summary>
/// Whitelist of placeholder values an auto-generated rule may resolve at
/// execution time. Every value here must be sourced from non-PHI agent
/// state or adapter output; never from UI text.
/// </summary>
public enum KeyHintPlaceholder
{
    /// <summary>The Rx number the current skill is processing (already scrubbed upstream).</summary>
    RxNumberEchoed,

    /// <summary>ISO-8601 UTC timestamp, generated at execution time.</summary>
    CurrentDateIsoUtc,

    /// <summary>PMS-adapter-provided user identifier (already scrubbed).</summary>
    AgentUserNameFromAdapter,
}
