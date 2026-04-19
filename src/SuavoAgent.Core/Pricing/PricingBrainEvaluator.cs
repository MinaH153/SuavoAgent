using System.Globalization;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Outcome of running the TieredBrain against one pricing row. We only care
/// about two flow-control bits — whether to halt the job, and which tier
/// produced the opinion (for audit + pattern mining).
/// </summary>
public sealed record PricingBrainDecision
{
    public required bool ShouldHalt { get; init; }
    public required DecisionTier Tier { get; init; }
    public required string Reason { get; init; }

    public static PricingBrainDecision Continue(DecisionTier tier, string reason) =>
        new() { ShouldHalt = false, Tier = tier, Reason = reason };

    public static PricingBrainDecision Halt(DecisionTier tier, string reason) =>
        new() { ShouldHalt = true, Tier = tier, Reason = reason };
}

/// <summary>
/// Bridges the pricing job into TieredBrain. Builds a RuleContext per NDC
/// result, restricts the brain to non-destructive flow-control actions
/// (Log / Escalate / AskOperator), and translates the BrainDecision into a
/// halt / continue signal for PricingJobRunner.
///
/// Never throws. All exceptions — including cancellation — are caught and
/// surfaced as Continue(OperatorRequired, ...). Pricing jobs must keep
/// running even if the reasoning stack misbehaves. (Cancellation of the
/// outer token still aborts the loop one level up; this class only protects
/// the per-row call.)
/// </summary>
public sealed class PricingBrainEvaluator
{
    private readonly TieredBrain _brain;
    private readonly ILogger<PricingBrainEvaluator> _logger;

    /// <summary>
    /// Only flow-control actions are allowed for pricing. The brain may
    /// observe (Log), request escalation (Escalate), or ask the operator
    /// (AskOperator). Destructive UI actions (Click/Type/PressKey) make no
    /// sense in a Core-side pricing batch and are excluded.
    /// </summary>
    private static readonly IReadOnlySet<RuleActionType> PricingAllowedActions =
        new HashSet<RuleActionType>
        {
            RuleActionType.Log,
            RuleActionType.Escalate,
            RuleActionType.AskOperator,
        };

    /// <summary>Per-row deadline. Pricing jobs are latency-sensitive.</summary>
    private static readonly TimeSpan PerRowBudget = TimeSpan.FromSeconds(3);

    public PricingBrainEvaluator(
        TieredBrain brain,
        ILogger<PricingBrainEvaluator> logger)
    {
        _brain = brain;
        _logger = logger;
    }

    /// <summary>
    /// Ask the brain to evaluate one pricing row and return a halt/continue
    /// signal for PricingJobRunner. Caller passes rolling stats so rules can
    /// trigger on streaks, not just the current row.
    /// </summary>
    public async Task<PricingBrainDecision> EvaluateAsync(
        NdcRow row,
        SupplierPriceResult result,
        PricingRunStats stats,
        CancellationToken ct)
    {
        var ctx = BuildContext(row, result, stats);

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(PerRowBudget);

            var decision = await _brain.DecideAsync(
                ctx,
                allowedTier2Actions: PricingAllowedActions,
                shadowMode: false,
                ct: cts.Token);

            return Translate(decision);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Caller-driven cancellation: let PricingJobRunner's loop see it.
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "PricingBrainEvaluator: per-row budget ({Budget}) exceeded for NDC {Ndc}",
                PerRowBudget, row.NdcNormalized);
            return PricingBrainDecision.Continue(DecisionTier.OperatorRequired, "brain timeout");
        }
        catch (Exception ex)
        {
            // Defense in depth — TieredBrain is contractually non-throwing.
            _logger.LogWarning(ex,
                "PricingBrainEvaluator: unexpected error for NDC {Ndc}; continuing",
                row.NdcNormalized);
            return PricingBrainDecision.Continue(DecisionTier.OperatorRequired, "brain error");
        }
    }

    internal static RuleContext BuildContext(
        NdcRow row,
        SupplierPriceResult result,
        PricingRunStats stats)
    {
        var processed = Math.Max(stats.CompletedItems + stats.FailedItems, 1);
        var failureRatePct = (int)Math.Round(100d * stats.FailedItems / processed);

        var flags = new Dictionary<string, string>
        {
            [PricingBrainFlags.Ndc] = row.NdcNormalized,
            [PricingBrainFlags.Found] = result.Found ? "true" : "false",
            [PricingBrainFlags.Supplier] = result.SupplierName ?? string.Empty,
            [PricingBrainFlags.CostPresent] = result.CostPerUnit.HasValue ? "true" : "false",
            [PricingBrainFlags.ErrorClass] = ClassifyError(result.ErrorMessage),
            [PricingBrainFlags.ConsecutiveFailures] =
                stats.ConsecutiveFailures.ToString(CultureInfo.InvariantCulture),
            [PricingBrainFlags.FailureRatePct] =
                failureRatePct.ToString(CultureInfo.InvariantCulture),
            [PricingBrainFlags.TotalItems] =
                stats.TotalItems.ToString(CultureInfo.InvariantCulture),
        };

        return new RuleContext
        {
            SkillId = PricingSkills.Lookup,
            // ProcessName / WindowTitle / VisibleElements are UI-only fields
            // and intentionally empty for Core-side pricing decisions.
            Flags = flags,
        };
    }

    private static string ClassifyError(string? errorMessage)
    {
        if (string.IsNullOrWhiteSpace(errorMessage)) return string.Empty;
        var lower = errorMessage.ToLowerInvariant();
        if (lower.Contains("timeout") || lower.Contains("timed out")) return "timeout";
        if (lower.Contains("no response")) return "no_response";
        if (lower.Contains("mismatch")) return "pipe_desync";
        if (lower.Contains("deserialize")) return "bad_payload";
        return "other";
    }

    private static PricingBrainDecision Translate(BrainDecision decision)
    {
        var anyHalt = decision.Actions.Any(a =>
            a.Type == RuleActionType.Escalate ||
            a.Type == RuleActionType.AskOperator);

        if (anyHalt)
            return PricingBrainDecision.Halt(decision.Tier, decision.Reason);

        // Precondition block = environment not safe for autonomous pricing.
        if (decision.Outcome == MatchOutcome.Blocked && decision.Tier == DecisionTier.Precondition)
            return PricingBrainDecision.Halt(decision.Tier, decision.Reason);

        return PricingBrainDecision.Continue(decision.Tier, decision.Reason);
    }
}
