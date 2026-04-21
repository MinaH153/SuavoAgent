namespace SuavoAgent.Verbs;

/// <summary>
/// Risk tier drives approval gate behavior per
/// <c>docs/self-healing/action-grammar-v1.md §Risk tiers</c>.
/// LOW: auto-approve allowed within autonomy ladder.
/// MED: operator approval always required, time-boxed consent.
/// HIGH: operator approval + MFA + 2-person approval for first 30 days.
/// UNKNOWN: structurally rejected until classified by Joshua + SO.
/// </summary>
public enum VerbRiskTier
{
    Unknown,
    Low,
    Medium,
    High
}
