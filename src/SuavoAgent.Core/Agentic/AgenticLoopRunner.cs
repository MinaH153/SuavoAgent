using System;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// The PURE general agentic orchestrator: preflight → perceive → reason → gate → act →
/// settle+verify → accumulate → stuck-check. Depends ONLY on the four injected ports plus
/// an injected clock + delay, so the entire loop is unit-testable with fakes. Holds no
/// OS/UI/IPC/cloud handles. See docs/architecture/agentic-loop-orchestrator.md.
///
/// Codex-resolved invariants (2026-06-04): denial is deterministic (no implicit re-reason);
/// verify settles before judging (no double-execute); there is NO blind retry (NotMet
/// re-reasons, bounded by IsNoProgress); cloud calls obey a per-run sub-budget; multi-action
/// decisions arrive pre-mapped to Escalate by the reasoner adapter.
/// </summary>
public sealed class AgenticLoopRunner : IAgenticLoopRunner
{
    private readonly IPerceiver _perceiver;
    private readonly IReasoner _reasoner;
    private readonly IActuator _actuator;
    private readonly ISafetyGate _safety;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public AgenticLoopRunner(
        IPerceiver perceiver,
        IReasoner reasoner,
        IActuator actuator,
        ISafetyGate safety,
        Func<DateTimeOffset>? utcNow = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _perceiver = perceiver ?? throw new ArgumentNullException(nameof(perceiver));
        _reasoner = reasoner ?? throw new ArgumentNullException(nameof(reasoner));
        _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _delay = delay ?? Task.Delay;
    }

    public async Task<AgenticLoopResult> RunAsync(AgentObjective objective, AgenticLoopOptions options, CancellationToken ct)
    {
        var budget = new LoopBudget(options.MaxSteps, options.MaxCloudCalls, options.Deadline, _utcNow);
        var memory = ContextAccumulator.Start(objective);

        try
        {
            while (true)
            {
                // 1. instant cancel
                if (ct.IsCancellationRequested)
                    return Terminate(TerminationReason.Cancelled, memory, budget, escalate: false);

                // 2. precedence-1 safety preflight (fail-fast read; authoritative gate is Helper-side at act time)
                var pre = _safety.Preflight(objective);
                if (pre.Decision == SafetyDecision.Deny)
                    return Terminate(TerminationReason.GateDenied, memory, budget, escalate: true, pre.Reason);

                // 3. budget: wall-clock then step
                if (budget.DeadlinePassed())
                    return Terminate(TerminationReason.BudgetExhausted, memory, budget, escalate: true, "deadline");
                if (!budget.TryStep())
                    return Terminate(TerminationReason.BudgetExhausted, memory, budget, escalate: true, "max_steps");

                // 4. perceive (scrubbed snapshot)
                var screen = await _perceiver.PerceiveAsync(ct).ConfigureAwait(false);
                if (screen is null)
                {
                    memory = ContextAccumulator.RecordPerceiveNull(memory);
                    if (memory.PerceiveNullStrikes >= options.PerceiveNullStrikeLimit)
                        return Terminate(TerminationReason.NoProgress, memory, budget, escalate: true, "no_perception");
                    continue;
                }

                // 5. structural egress invariant — an unscrubbed frame NEVER reaches reasoning
                if (!_safety.AssertScrubbed(screen))
                    return Terminate(TerminationReason.GateDenied, memory, budget, escalate: true, "unscrubbed_frame");

                // 6. accumulate observation
                memory = ContextAccumulator.RecordObservation(memory, screen);
                var decisionHash = screen.ContentHash;

                // 7. reason → exactly one NextAction
                var allowReasonCloud = budget.CloudRemaining;
                var reason = await _reasoner
                    .ReasonNextAsync(objective, memory, options.AllowedActions, allowReasonCloud, ct)
                    .ConfigureAwait(false);
                if (reason.UsedCloud)
                    budget.ConsumeCloud();
                if (reason.CloudDenied)
                    return Terminate(TerminationReason.BudgetExhausted, memory, budget, escalate: true, "cloud_budget");

                var action = reason.Action;
                if (action.Kind == NextActionKind.Done)
                    return Terminate(TerminationReason.Done, memory, budget, escalate: false);
                if (action.Kind == NextActionKind.Escalate)
                    return Terminate(TerminationReason.Escalated, memory, budget, escalate: true, action.Rationale);

                // 8. per-action safety gate — deterministic, no implicit re-reason
                var gate = _safety.GateAction(action, objective);
                if (gate.Decision == SafetyDecision.Deny)
                    return Terminate(TerminationReason.GateDenied, memory, budget, escalate: true, gate.Reason);

                var dryRun = options.DryRun || gate.Decision == SafetyDecision.AllowDryRun;

                // 9. act (one action; Helper-side gate is the authoritative kill/pause/disabled enforcement)
                var actCtx = new ActuationContext(objective.PharmacyId, objective.TaskKey, dryRun);
                var outcome = await _actuator.ActAsync(action, actCtx, ct).ConfigureAwait(false);

                // Supervised path: a dry-run dispatch is audited-not-executed, then escalate for operator approval.
                if (gate.Decision == SafetyDecision.AllowDryRun)
                {
                    memory = ContextAccumulator.RecordStep(memory, decisionHash, action, outcome.Status, null, options.MemoryWindow);
                    return Terminate(TerminationReason.GateDenied, memory, budget, escalate: true, "supervised_dry_run");
                }

                // 10. settle then verify (defeats the async-UI repaint race ⇒ no double-execute)
                var after = await SettleAsync(screen, options, ct).ConfigureAwait(false) ?? screen;
                var allowVerifyCloud = budget.CloudRemaining;
                var verify = await _reasoner
                    .VerifyPostconditionAsync(objective, action, screen, after, allowVerifyCloud, ct)
                    .ConfigureAwait(false);
                if (verify.UsedCloud)
                    budget.ConsumeCloud();

                var verdict = verify.Verdict == PostconditionVerdict.Met
                    ? PostconditionVerdict.Met
                    : PostconditionVerdict.NotMet; // Ambiguous ⇒ NotMet, fail-closed

                // 11. record the step
                memory = ContextAccumulator.RecordStep(memory, decisionHash, action, outcome.Status, verdict, options.MemoryWindow);

                // 12. stuck-check — K identical (screen, action) moves ⇒ no progress (replaces blind retry)
                if (ContextAccumulator.IsNoProgress(memory, options.NoProgressWindow))
                    return Terminate(TerminationReason.NoProgress, memory, budget, escalate: true, "no_progress");

                // Met or NotMet, we loop: re-perceive, re-reason. The reasoner returns Done when satisfied.
            }
        }
        catch (OperationCanceledException)
        {
            return Terminate(TerminationReason.Cancelled, memory, budget, escalate: false);
        }
    }

    /// <summary>
    /// Re-perceive in a bounded settle loop: poll until the after-screen content-hash is
    /// stable for SettleStableReads consecutive reads, OR SettleMaxPolls is reached. Never
    /// hangs. Returns the settled frame (or the last polled frame on settle-budget exhaustion).
    /// </summary>
    private async Task<PerceivedScreen?> SettleAsync(PerceivedScreen before, AgenticLoopOptions options, CancellationToken ct)
    {
        PerceivedScreen? lastSeen = null;
        string? stableHash = null;
        var stableCount = 0;

        for (var poll = 0; poll < options.SettleMaxPolls; poll++)
        {
            var frame = await _perceiver.PerceiveAsync(ct).ConfigureAwait(false);
            if (frame is not null)
            {
                lastSeen = frame;
                if (frame.ContentHash == stableHash)
                {
                    stableCount++;
                }
                else
                {
                    stableHash = frame.ContentHash;
                    stableCount = 1;
                }

                if (stableCount >= options.SettleStableReads)
                    return frame;
            }

            if (poll < options.SettleMaxPolls - 1)
                await _delay(options.SettlePollInterval, ct).ConfigureAwait(false);
        }

        return lastSeen;
    }

    private static AgenticLoopResult Terminate(
        TerminationReason reason, WorkingMemory memory, LoopBudget budget, bool escalate, string? detail = null)
        => new(reason, budget.StepsUsed, budget.CloudUsed, escalate, detail, memory);
}
