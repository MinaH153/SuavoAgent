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
        ICloudReasoning? cloudReasoning = null)
    {
        _rules = rules;
        _localInference = localInference;
        _verifier = verifier;
        _logger = logger;
        _cloudReasoning = cloudReasoning ?? new NullCloudReasoning();
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
        };

        InferenceProposal? proposal = null;
        string tier2Reason = "Local inference disabled";
        var tier2Source = DecisionTier.LocalInference;

        if (_localInference.IsReady)
        {
            try
            {
                proposal = await _localInference.ProposeAsync(request, ct);
                if (proposal == null)
                    tier2Reason = "Local inference returned no proposal";
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
                tier2Reason = "Local inference timed out";
            }
            catch (Exception ex)
            {
                // Defense-in-depth: the interface contract says don't throw, but if
                // an implementation does, we must not crash the caller.
                _logger.LogWarning(ex, "TieredBrain: Tier 2 threw unexpectedly");
                tier2Reason = "Local inference error";
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
                _logger.LogWarning(ex, "TieredBrain: Tier 3 threw unexpectedly");
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
                        Reason = $"Shadow mode — approved proposal not executed",
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
