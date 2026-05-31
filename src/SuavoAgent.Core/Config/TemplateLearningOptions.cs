namespace SuavoAgent.Core.Config;

/// <summary>
/// Autonomous workflow template extraction toggles. Bound from
/// <c>Agent:TemplateLearning:*</c>. Disabled by default and capture-only when
/// enabled unless rule generation is explicitly opted in.
/// </summary>
public sealed class TemplateLearningOptions
{
    public bool Enabled { get; set; } = false;
    public string Mode { get; set; } = "capture";
    public bool RuleGeneration { get; set; } = false;
    public bool AutoApproveOnFingerprintMatch { get; set; } = false;
    public double MinRoutineConfidence { get; set; } = 0.6;
    public int MinStepCount { get; set; } = 2;
    public int MaxExpectedVisiblePerScreen { get; set; } = 8;
    public double MatchRatio { get; set; } = 0.8;
    public int LowConfidenceRetirementAfter { get; set; } = 5;
    public string SkillId { get; set; } = "learned";
    public string ProcessNameGlob { get; set; } = "PioneerPharmacy*";
}

/// <summary>
/// Fleet-wide feature flags. Bound from <c>FleetFeatures:*</c>.
/// </summary>
public sealed class FleetFeaturesOptions
{
    public bool SchemaAdaptation { get; set; } = false;
}

/// <summary>
/// Fleet-learning rollout flag. When false (default), the agent still PULLS
/// fleet seeds but does NOT apply the consensus rx_queue_shape — observe-first.
/// Flip per-pilot once the shared-brain warm-start is trusted in the field.
/// Bound from <c>FleetLearning:*</c>.
/// </summary>
public sealed class FleetLearningOptions
{
    public bool Enabled { get; set; } = false;
}
