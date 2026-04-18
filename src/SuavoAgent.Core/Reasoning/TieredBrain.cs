using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Outcome of a full tiered-brain decision. Richer than EvaluationResult
/// because it tracks which tier produced the answer — essential for the
/// pattern miner (Week 4) that promotes Tier 2/3 decisions to Tier 1 rules.
/// </summary>
public sealed record BrainDecision
{
    public required MatchOutcome Outcome { get; init; }

    /// <summary>Which tier actually decided.</summary>
    public required DecisionTier Tier { get; init; }

    /// <summary>Actions to execute. Empty unless Outcome == Matched.</summary>
    public IReadOnlyList<RuleActionSpec> Actions { get; init; } = Array.Empty<RuleActionSpec>();

    /// <summary>Rule that matched (Tier 1) or null.</summary>
    public Rule? MatchedRule { get; init; }

    /// <summary>LLM proposal (Tier 2) or null.</summary>
    public InferenceProposal? Proposal { get; init; }

    /// <summary>Verifier output for Tier 2 proposals, or null for Tier 1 decisions.</summary>
    public VerificationResult? Verification { get; init; }

    /// <summary>Human-readable explanation for logs + audit.</summary>
    public required string Reason { get; init; }

    public DateTimeOffset DecidedAt { get; init; } = DateTimeOffset.UtcNow;
}

public enum DecisionTier
{
    /// <summary>Deterministic rule matched.</summary>
    Rules,
    /// <summary>Local LLM proposed and verifier approved.</summary>
    LocalInference,
    /// <summary>No tier could decide — operator must act.</summary>
    OperatorRequired,
    /// <summary>Tier 1 blocked by precondition before anything else ran.</summary>
    Precondition,
}

/// <summary>
/// The full tiered-brain orchestrator. Chains Tier 1 (RuleEngine) → Tier 2
/// (ILocalInference + ActionVerifier) → operator escalation.
///
/// Tier 3 (CloudClaude) is added in Week 4 when the cloud reason endpoint lands.
/// For now, anything Tier 2 can't handle goes to the operator approval queue.
/// </summary>
public sealed class TieredBrain
{
    private readonly RuleEngine _rules;
    private readonly ILocalInference _localInference;
    private readonly ActionVerifier _verifier;
    private readonly ILogger<TieredBrain> _logger;

    public TieredBrain(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ILogger<TieredBrain> logger)
    {
        _rules = rules;
        _localInference = localInference;
        _verifier = verifier;
        _logger = logger;
    }

    /// <summary>
    /// Makes a decision for the given context. Never throws — inference and
    /// verification errors are reported via BrainDecision so the caller has
    /// a single consistent surface.
    /// </summary>
    public async Task<BrainDecision> DecideAsync(
        RuleContext ctx,
        IReadOnlySet<RuleActionType>? allowedTier2Actions = null,
        bool shadowMode = false,
        CancellationToken ct = default)
    {
        // --- Tier 1: rules ------------------------------------------------------
        var ruleResult = _rules.Evaluate(ctx, shadowMode);
        if (ruleResult.Outcome == MatchOutcome.Matched)
        {
            _logger.LogDebug("TieredBrain: Tier 1 matched rule {Id}", ruleResult.MatchedRule!.Id);
            return new BrainDecision
            {
                Outcome = MatchOutcome.Matched,
                Tier = DecisionTier.Rules,
                MatchedRule = ruleResult.MatchedRule,
                Actions = ruleResult.Actions,
                Reason = ruleResult.Reason,
            };
        }
        if (ruleResult.Outcome == MatchOutcome.Blocked)
        {
            // Blocked by a precondition (autonomousOk=false gate) — short-circuit.
            _logger.LogInformation("TieredBrain: precondition blocked — {Reason}", ruleResult.Reason);
            return new BrainDecision
            {
                Outcome = MatchOutcome.Blocked,
                Tier = DecisionTier.Precondition,
                MatchedRule = ruleResult.MatchedRule,
                Actions = ruleResult.Actions,
                Reason = ruleResult.Reason,
            };
        }

        // --- Tier 2: local inference -------------------------------------------
        if (!_localInference.IsReady)
        {
            _logger.LogInformation("TieredBrain: Tier 2 unavailable — escalating to operator");
            return new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = "Local inference not ready; operator must act",
            };
        }

        var request = new InferenceRequest
        {
            Context = ctx,
            EscalationReason = ruleResult.Reason,
            AllowedActions = allowedTier2Actions ??
                new HashSet<RuleActionType>(Enum.GetValues<RuleActionType>()),
        };

        InferenceProposal? proposal;
        try
        {
            proposal = await _localInference.ProposeAsync(request, ct);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("TieredBrain: Tier 2 timed out");
            return new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = "Local inference timed out",
            };
        }
        catch (Exception ex)
        {
            // Defense-in-depth: the interface contract says don't throw, but if
            // an implementation does, we must not crash the caller.
            _logger.LogWarning(ex, "TieredBrain: Tier 2 threw unexpectedly");
            return new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = "Local inference error",
            };
        }

        if (proposal == null)
        {
            return new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = "Local inference returned no proposal",
            };
        }

        // --- Verifier (mandatory for every Tier 2 proposal) --------------------
        var verification = _verifier.Verify(proposal, request);

        switch (verification.Outcome)
        {
            case VerificationOutcome.Approved:
                if (shadowMode)
                {
                    _logger.LogInformation(
                        "TieredBrain: [SHADOW] Tier 2 would have executed {Action}",
                        proposal.Action.Type);
                    return new BrainDecision
                    {
                        Outcome = MatchOutcome.NoMatch,
                        Tier = DecisionTier.LocalInference,
                        Proposal = proposal,
                        Verification = verification,
                        Reason = $"Shadow mode — approved proposal not executed",
                    };
                }

                _logger.LogInformation(
                    "TieredBrain: Tier 2 approved {Action} (confidence {Conf:F2})",
                    proposal.Action.Type, proposal.Confidence);
                return new BrainDecision
                {
                    Outcome = MatchOutcome.Matched,
                    Tier = DecisionTier.LocalInference,
                    Actions = new[] { proposal.Action },
                    Proposal = proposal,
                    Verification = verification,
                    Reason = verification.Reason,
                };

            case VerificationOutcome.OperatorApprovalRequired:
                _logger.LogInformation(
                    "TieredBrain: Tier 2 → operator approval: {Reason}", verification.Reason);
                return new BrainDecision
                {
                    Outcome = MatchOutcome.Blocked,
                    Tier = DecisionTier.OperatorRequired,
                    Actions = new[] { proposal.Action },
                    Proposal = proposal,
                    Verification = verification,
                    Reason = verification.Reason,
                };

            case VerificationOutcome.Rejected:
            default:
                _logger.LogWarning(
                    "TieredBrain: Tier 2 proposal rejected — {Reason}", verification.Reason);
                return new BrainDecision
                {
                    Outcome = MatchOutcome.NoMatch,
                    Tier = DecisionTier.OperatorRequired,
                    Proposal = proposal,
                    Verification = verification,
                    Reason = verification.Reason,
                };
        }
    }
}
