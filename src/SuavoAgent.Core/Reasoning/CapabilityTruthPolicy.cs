namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Deterministic answers for capability questions that free-form local inference
/// cannot verify. Chat carries no live tool receipt, so it must not manufacture
/// hardware details, visible-app state, or computer-control authority.
/// </summary>
internal static class CapabilityTruthPolicy
{
    private static readonly (string Needle, string Label)[] KnownApps =
    [
        ("pioneerrx", "PioneerRx"),
        ("pioneer rx", "PioneerRx"),
        ("calculator", "Calculator"),
        ("chrome", "Chrome"),
        ("excel", "Excel"),
    ];

    internal static string? TryReply(string prompt)
    {
        var normalized = prompt.Trim().ToLowerInvariant();
        if (normalized.Length == 0) return null;

        if ((normalized.Contains("spec", StringComparison.Ordinal) ||
             normalized.Contains("hardware", StringComparison.Ordinal)) &&
            (normalized.Contains("computer", StringComparison.Ordinal) ||
             normalized.Contains("pc", StringComparison.Ordinal)))
        {
            return "I haven't verified this computer's hardware specifications.";
        }

        var capabilityQuestion =
            normalized.Contains("access", StringComparison.Ordinal) ||
            normalized.Contains("open ", StringComparison.Ordinal) ||
            normalized.Contains("launch ", StringComparison.Ordinal) ||
            normalized.Contains("do you see", StringComparison.Ordinal) ||
            normalized.Contains("can you see", StringComparison.Ordinal) ||
            normalized.Contains("control", StringComparison.Ordinal);
        if (!capabilityQuestion) return null;

        var app = KnownApps.FirstOrDefault(candidate =>
            normalized.Contains(candidate.Needle, StringComparison.Ordinal));
        return string.IsNullOrEmpty(app.Label)
            ? "I haven't verified that capability or computer-control authority for this request."
            : $"I haven't verified access to {app.Label} or computer-control authority for this request.";
    }
}
