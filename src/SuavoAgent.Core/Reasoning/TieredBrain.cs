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
    /// <summary>Cloud Claude proposed and verifier approved.</summary>
    CloudInference,
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
    private readonly ICloudReasoning _cloudReasoning;
    private readonly ActionVerifier _verifier;
    private readonly ILogger<TieredBrain> _logger;

    /// <summary>Wall-clock budget threaded onto every Tier-2 InferenceRequest. From
    /// ReasoningOptions.InferenceTimeoutSeconds; defaults to the contract's 3 s when unset.</summary>
    private readonly TimeSpan _inferenceTimeout;

    /// <summary>
    /// Tier-2 proposals below this confidence escalate to Tier-3 instead of
    /// going straight to the verifier. Keeps Tier-2 autonomy for clear cases
    /// while letting the cloud (with Claude-grade reasoning) second-guess the
    /// borderline ones. 0.5 is deliberately soft — the verifier still has
    /// final say per action class.
    /// </summary>
    private const double CloudEscalationConfidence = 0.5;

    public TieredBrain(
        RuleEngine rules,
        ILocalInference localInference,
        ActionVerifier verifier,
        ILogger<TieredBrain> logger,
        ICloudReasoning? cloudReasoning = null,
        TimeSpan? inferenceTimeout = null)
    {
        _rules = rules;
        _localInference = localInference;
        _verifier = verifier;
        _logger = logger;
        _cloudReasoning = cloudReasoning ?? new NullCloudReasoning();
        _inferenceTimeout = inferenceTimeout ?? TimeSpan.FromSeconds(3);
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
            _logger.LogDebug("core.reasoning.tier1_rule_matched");
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
            _logger.LogInformation("core.reasoning.precondition_blocked");
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
        // Note: IsReady here now means "configured and verified" (Codex M-1),
        // not "already loaded in RAM". Lazy-load happens inside ProposeAsync.
        var request = new InferenceRequest
        {
            Context = ctx,
            EscalationReason = ruleResult.Reason,
            // Safe-by-default — no destructive actions unless caller opts in.
            // Callers that want Tier-2 to propose Click/Type/PressKey must pass
            // an explicit allowedTier2Actions (Codex C-3).
            AllowedActions = allowedTier2Actions ?? InferenceRequest.SafeDefault,
            Timeout = _inferenceTimeout,
        };

        InferenceProposal? proposal = null;
        string tier2Reason = "local_model_unavailable";
        var tier2Source = DecisionTier.LocalInference;

        if (_localInference.IsReady)
        {
            try
            {
                proposal = await _localInference.ProposeAsync(request, ct);
                if (proposal == null)
                    tier2Reason = "local_model_no_proposal";
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Caller canceled — propagate instead of masking as an escalation
                // (Codex M-2). Upstream workers need to see cancellation so they
                // can tear down cleanly.
                throw;
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("TieredBrain: Tier 2 timed out");
                tier2Reason = "local_model_timeout";
            }
            catch (Exception ex)
            {
                // Defense-in-depth: the interface contract says don't throw, but if
                // an implementation does, we must not crash the caller.
                _logger.LogSafeWarning(ex);
                tier2Reason = "local_model_error";
            }
        }
        else
        {
            _logger.LogDebug("TieredBrain: Tier 2 not ready — considering Tier 3");
        }

        // --- Tier 3: cloud reasoning (Claude) ----------------------------------
        // Escalate to the cloud whenever Tier-2 couldn't confidently decide:
        //   • Tier-2 disabled / not ready
        //   • Tier-2 returned null (timeout, grammar failure, model error)
        //   • Tier-2 returned a low-confidence proposal
        // When Tier-3 also declines, we fall back to Tier-2's proposal (if any)
        // so the verifier can still route it to operator approval.
        var shouldTryCloud = _cloudReasoning.IsEnabled
            && (proposal == null || proposal.Confidence < CloudEscalationConfidence);

        if (shouldTryCloud)
        {
            try
            {
                if (proposal is not null)
                    tier2Reason = "local_model_low_confidence";
                var cloudProposal = await _cloudReasoning.ProposeAsync(request, tier2Reason, ct);
                if (cloudProposal != null)
                {
                    proposal = cloudProposal;
                    tier2Source = DecisionTier.CloudInference;
                    _logger.LogInformation(
                        "TieredBrain: Tier 3 cloud proposed {Action} (confidence {Conf:F2})",
                        cloudProposal.Action.Type, cloudProposal.Confidence);
                }
                else
                {
                    _logger.LogDebug("TieredBrain: Tier 3 declined — no cloud proposal");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // ICloudReasoning.ProposeAsync is contractually fail-closed;
                // this catch is defense-in-depth only.
                _logger.LogSafeWarning(ex);
            }
        }

        if (proposal == null)
        {
            return new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = tier2Reason,
            };
        }

        // --- Verifier (mandatory for every Tier 2/3 proposal) ------------------
        var verification = _verifier.Verify(proposal, request);
        if (tier2Source == DecisionTier.CloudInference &&
            ActionVerifier.Destructive.Contains(proposal.Action.Type) &&
            verification.Outcome != VerificationOutcome.Rejected)
        {
            // A cloud model may recommend a write, but it never earns actuation
            // authority from model confidence or a global Tier-2 setting. The
            // operator/autonomy ledger remains the only promotion boundary.
            verification = new VerificationResult
            {
                Outcome = VerificationOutcome.OperatorApprovalRequired,
                Reason = "cloud_destructive_action_requires_approval",
            };
        }

        switch (verification.Outcome)
        {
            case VerificationOutcome.Approved:
                if (shadowMode)
                {
                    _logger.LogInformation(
                        "TieredBrain: [SHADOW] {Tier} would have executed {Action}",
                        tier2Source, proposal.Action.Type);
                    return new BrainDecision
                    {
                        Outcome = MatchOutcome.NoMatch,
                        Tier = tier2Source,
                        Proposal = proposal,
                        Verification = verification,
                        Reason = "model_proposal_shadowed",
                    };
                }

                _logger.LogInformation(
                    "TieredBrain: {Tier} approved {Action} (confidence {Conf:F2})",
                    tier2Source, proposal.Action.Type, proposal.Confidence);
                return new BrainDecision
                {
                    Outcome = MatchOutcome.Matched,
                    Tier = tier2Source,
                    Actions = new[] { proposal.Action },
                    Proposal = proposal,
                    Verification = verification,
                    Reason = verification.Reason,
                };

            case VerificationOutcome.OperatorApprovalRequired:
                _logger.LogInformation("core.reasoning.operator_approval_required");
                return new BrainDecision
                {
                    Outcome = MatchOutcome.Blocked,
                    Tier = DecisionTier.OperatorRequired,
                    Proposal = proposal,
                    Verification = verification,
                    Reason = verification.Reason,
                };

            case VerificationOutcome.Rejected:
            default:
                _logger.LogWarning("core.reasoning.proposal_rejected");
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
