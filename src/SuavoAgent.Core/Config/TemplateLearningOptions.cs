namespace SuavoAgent.Core.Config;

/// <summary>
/// v3.12 autonomous workflow template extraction toggles. Bound from
/// <c>appsettings.json</c> section <c>Learning:Template:*</c>. Disabled by
/// default — opted in per pilot pharmacy via operator command.
/// </summary>
public sealed class TemplateLearningOptions
{
    public bool Enabled { get; set; } = false;
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
