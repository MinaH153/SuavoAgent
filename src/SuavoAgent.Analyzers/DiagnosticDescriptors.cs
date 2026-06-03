using Microsoft.CodeAnalysis;

namespace SuavoAgent.Analyzers;

/// <summary>
/// Centralized definitions of every SUAVO* diagnostic ID emitted by analyzers
/// in this assembly. Diagnostic IDs follow the pattern <c>SUAVOnnnn</c> where
/// <c>nnnn</c> is a unique 4-digit number. Categories track the broader area
/// the rule guards.
/// </summary>
internal static class DiagnosticDescriptors
{
    private const string Category = "SuavoAgent.Compliance";
    private const string HelpLinkBase =
        "https://github.com/MinaH153/SuavoAgent/blob/main/docs/superpowers/specs/2026-05-01-track-3-phi-ci-gate-design.md";

    /// <summary>
    /// SUAVO0001: A property or field marked <c>[PhiDirect]</c> appears on a
    /// type marked <c>[OutboundPayload]</c> (directly or via nested type
    /// references). PHI-Direct fields must never cross the network.
    /// </summary>
    public static readonly DiagnosticDescriptor PhiInOutboundPayload = new(
        id: "SUAVO0001",
        title: "PHI-Direct field on outbound payload",
        messageFormat: "PHI-Direct field '{0}' on outbound payload '{1}' — split required (move to local-only type or remove the [PhiDirect] marker).",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "PHI-Direct fields are forbidden on types marked [OutboundPayload] per Track 3 invariant 1.",
        helpLinkUri: HelpLinkBase + "#3--data-flow");

    /// <summary>
    /// SUAVO0099: The analyzer itself crashed or hit a guard limit. Surfaces
    /// loudly so a HIPAA-existential gate can never silently fail.
    /// </summary>
    public static readonly DiagnosticDescriptor AnalyzerInternalError = new(
        id: "SUAVO0099",
        title: "SuavoAgent analyzer internal error",
        messageFormat: "Analyzer crashed or hit a guard while analyzing '{0}': {1}",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The analyzer encountered an unhandled exception or hit a hard limit (e.g., depth > 50). Build is failed loudly to prevent silent gate bypass.",
        helpLinkUri: HelpLinkBase + "#4--error-handling");
}
