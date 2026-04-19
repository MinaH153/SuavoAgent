namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Rolling statistics handed to the PricingBrainEvaluator after each NDC
/// lookup. Lets rules and LLM tiers reason about overall job health (failure
/// streaks, global failure rate) not just the current row.
/// </summary>
public sealed record PricingRunStats
{
    /// <summary>Total rows the job plans to process (pending + already done).</summary>
    public required int TotalItems { get; init; }

    /// <summary>Rows that produced a usable result so far.</summary>
    public required int CompletedItems { get; init; }

    /// <summary>Rows that ended in Fail (no supplier/cost) so far.</summary>
    public required int FailedItems { get; init; }

    /// <summary>Consecutive failing rows leading up to the current lookup.</summary>
    public required int ConsecutiveFailures { get; init; }
}

/// <summary>
/// Stable string keys the evaluator puts in RuleContext.Flags so both YAML
/// rules and LLM prompts can reference the same names. Keep these in sync
/// with any bundled pricing rule files.
/// </summary>
public static class PricingBrainFlags
{
    public const string Ndc = "ndc";
    public const string Found = "found";
    public const string Supplier = "supplier";
    public const string CostPresent = "cost_present";
    public const string ErrorClass = "error_class";
    public const string ConsecutiveFailures = "consecutive_failures";
    public const string FailureRatePct = "failure_rate_pct";
    public const string TotalItems = "total_items";

    // Derived boolean flags. Tier-1 RuleEngine only supports exact string
    // equality on StateFlags (no >= operators), so the evaluator bakes the
    // thresholds into these yes/no flags that YAML rules match with "true".
    /// <summary>"true" once consecutive_failures ≥ 3.</summary>
    public const string StreakWarning = "streak_warning";
    /// <summary>"true" once consecutive_failures ≥ 10 — stop autonomously.</summary>
    public const string StreakSevere = "streak_severe";
    /// <summary>"true" when failure_rate_pct ≥ 50 AND ≥ 10 rows processed.</summary>
    public const string FailureRateHigh = "failure_rate_high";
}

public static class PricingBrainThresholds
{
    public const int StreakWarning = 3;
    public const int StreakSevere = 10;
    public const int FailureRateHighPct = 50;
    public const int FailureRateMinSample = 10;
}

/// <summary>
/// Skill ids used by the pricing subsystem when consulting TieredBrain.
/// </summary>
public static class PricingSkills
{
    /// <summary>Post-lookup evaluation of a single NDC result.</summary>
    public const string Lookup = "pricing-lookup";
}
