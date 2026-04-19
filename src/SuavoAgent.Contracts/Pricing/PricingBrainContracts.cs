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
}

/// <summary>
/// Skill ids used by the pricing subsystem when consulting TieredBrain.
/// </summary>
public static class PricingSkills
{
    /// <summary>Post-lookup evaluation of a single NDC result.</summary>
    public const string Lookup = "pricing-lookup";
}
