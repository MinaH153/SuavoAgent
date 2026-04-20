namespace SuavoAgent.Core.Learning;

/// <summary>
/// Tunable thresholds for <see cref="WorkflowTemplateExtractor"/>. Exposed so
/// pilot pharmacies can adjust without a rebuild. Defaults picked to match the
/// v3.12 spec — keep conservative until we have a second pharmacy's worth of
/// field data.
/// </summary>
public sealed record WorkflowTemplateThresholds
{
    /// <summary>Only routines at or above this confidence produce/refresh a template.</summary>
    public double MinRoutineConfidence { get; init; } = 0.6;

    /// <summary>Minimum step count for a template to be emitted.</summary>
    public int MinStepCount { get; init; } = 2;

    /// <summary>Cap on ExpectedVisible signatures per step (prevents huge hashes).</summary>
    public int MaxExpectedVisiblePerScreen { get; init; } = 8;

    /// <summary>K-of-M ratio for per-step MinElementsRequired (0..1).</summary>
    public double MatchRatio { get; init; } = 0.8;

    /// <summary>After this many consecutive low-conf extractor runs, the template is retired.</summary>
    public int LowConfidenceRetirementAfter { get; init; } = 5;

    /// <summary>
    /// treeHash the extractor reads to build ExpectedAfter for writeback steps.
    /// In production this comes from the Helper's post-action UIA capture; in
    /// tests we inject a known tree_hash. Null = no ExpectedAfter available →
    /// writeback templates fail closed.
    /// </summary>
    public string? WritebackPostStateTreeHash { get; init; }

    public static readonly WorkflowTemplateThresholds Default = new();
}
