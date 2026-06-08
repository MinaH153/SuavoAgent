using System;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic.Adapters;

namespace SuavoAgent.Core.Agentic;

/// <summary>Why a skill replay stopped. Every non-Completed outcome is a fail-closed STOP — replay never
/// blind-continues past a state it doesn't recognise or an action it can't reconstruct exactly.</summary>
public enum SkillReplayOutcome
{
    Completed,          // every banked step executed AND verified — the ratchet cashed in
    NoSteps,            // empty skill
    GateDenied,         // preflight (kill/pause/live-PMS) or a per-action gate denied
    PerceiveFailed,     // null perception — can't confirm where we are
    StateMismatch,      // the live screen isn't the state this step expects (drift) — abort, never guess
    Unparseable,        // a banked signature couldn't be reconstructed EXACTLY (fail-closed)
    StepRejected,       // the actuator rejected/failed the action
    PostconditionFailed,// the action ran but didn't move the screen as the verified path required
    Cancelled,
}

public sealed record SkillReplayResult(SkillReplayOutcome Outcome, int StepsCompleted, string? Detail);

/// <summary>
/// Cashes in the amortize ratchet: DETERMINISTIC replay of a banked <see cref="VerifiedSkill"/> — zero
/// reasoning, no LLM, no exploration. Walks the stored (state → action) chain and executes each step ONLY
/// while the live screen matches the state that step was verified at; on any drift / unrecognised state /
/// inexact-signature it STOPS (fail-closed) rather than acting blind. Reuses the SAME ports + verifier as
/// the agentic loop, so safety + scrubbing + actuation gating are identical — replay just removes the
/// thinking. Used for KNOWN tasks (a thick verified tube); the loop/explorer remain for the frontier.
/// </summary>
public sealed class VerifiedSkillReplayer
{
    private readonly IPerceiver _perceiver;
    private readonly IActuator _actuator;
    private readonly ISafetyGate _safety;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public VerifiedSkillReplayer(
        IPerceiver perceiver,
        IActuator actuator,
        ISafetyGate safety,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _perceiver = perceiver ?? throw new ArgumentNullException(nameof(perceiver));
        _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _delay = delay ?? Task.Delay;
    }

    public async Task<SkillReplayResult> ReplayAsync(
        VerifiedSkill skill, AgentObjective objective, AgenticLoopOptions options, CancellationToken ct)
    {
        if (skill is null || skill.Steps.Count == 0)
            return new SkillReplayResult(SkillReplayOutcome.NoSteps, 0, null);

        // Precedence-1 preflight (kill-switch / pause / never-blind-on-live-PMS) — same gate as the loop.
        var pre = _safety.Preflight(objective);
        if (pre.Decision == SafetyDecision.Deny)
            return new SkillReplayResult(SkillReplayOutcome.GateDenied, 0, pre.Reason);

        var completed = 0;
        for (var i = 0; i < skill.Steps.Count; i++)
        {
            if (ct.IsCancellationRequested)
                return new SkillReplayResult(SkillReplayOutcome.Cancelled, completed, null);

            var step = skill.Steps[i];

            // 1. Perceive and CONFIRM we are at the state this step was verified at. Any mismatch = drift
            //    (the PMS UI changed, or we started from the wrong place) → STOP. Never act on a screen we
            //    don't recognise — that is the whole point of deterministic replay being safe.
            var screen = await _perceiver.PerceiveAsync(ct).ConfigureAwait(false);
            if (screen is null)
                return new SkillReplayResult(SkillReplayOutcome.PerceiveFailed, completed, $"step {i}: no_perception");
            if (!_safety.AssertScrubbed(screen))
                return new SkillReplayResult(SkillReplayOutcome.GateDenied, completed, "unscrubbed_frame");
            if (!string.Equals(screen.ContentHash, step.StateHash, StringComparison.Ordinal))
                return new SkillReplayResult(SkillReplayOutcome.StateMismatch, completed,
                    $"step {i}: expected state {step.StateHash}, saw {screen.ContentHash}");

            // 2. Reconstruct the action from the banked signature — fail-closed unless it round-trips EXACTLY
            //    (a misparse reconstructs a different signature and is rejected, never executed).
            //    Codex 2026-06-08 residuals, both BOUNDED by sandbox-confinement + the step-1 state-match:
            //    (Q2) Signature() is unescaped, so a value literally containing ",key=" is theoretically
            //    ambiguous; the round-trip guard catches every realistic case, and the worst a contrived one
            //    could do is ONE wrong click in an allowlisted $0 sandbox process — which the NEXT step's
            //    state-match immediately aborts. The airtight fix is structured params (StepRecord
            //    enrichment), deferred. (Q1) TOCTOU between this perceive and the click is the same gap the
            //    agentic loop has; the next state-match catches a wrong landing.
            var action = ActionSignatureParser.TryParse(step.ActionSignature);
            if (action is null || !string.Equals(action.Signature(), step.ActionSignature, StringComparison.Ordinal))
                return new SkillReplayResult(SkillReplayOutcome.Unparseable, completed,
                    $"step {i}: {step.ActionSignature}");

            // 3. Per-action safety gate (deterministic; same gate as the loop — e.g. sandbox allowlist).
            var gate = _safety.GateAction(action, objective);
            if (gate.Decision == SafetyDecision.Deny)
                return new SkillReplayResult(SkillReplayOutcome.GateDenied, completed, gate.Reason);
            var dryRun = options.DryRun || gate.Decision == SafetyDecision.AllowDryRun;

            // 4. Execute exactly one action.
            var ctx = new ActuationContext(objective.PharmacyId, objective.TaskKey, dryRun);
            var outcome = await _actuator.ActAsync(action, ctx, ct).ConfigureAwait(false);
            if (outcome.Status != ActStatus.Success)
                return new SkillReplayResult(SkillReplayOutcome.StepRejected, completed,
                    $"step {i}: {outcome.RejectionCode ?? outcome.Status.ToString()}");

            // 5. Settle, then confirm the action actually moved the screen (it was Met when banked). A step
            //    that produced no change means reality diverged from the verified path → STOP fail-closed.
            var after = await SettleAsync(screen, options, ct).ConfigureAwait(false) ?? screen;
            if (PostconditionEvaluator.Evaluate(action, screen, after) != PostconditionVerdict.Met)
                return new SkillReplayResult(SkillReplayOutcome.PostconditionFailed, completed,
                    $"step {i}: postcondition not met");

            completed++;
        }

        return new SkillReplayResult(SkillReplayOutcome.Completed, completed, null);
    }

    /// <summary>Bounded settle: poll until the screen hash is stable for SettleStableReads reads or
    /// SettleMaxPolls is hit. Mirrors AgenticLoopRunner.SettleAsync so replay sees the same repaint race.</summary>
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
                if (frame.ContentHash == stableHash) stableCount++;
                else { stableHash = frame.ContentHash; stableCount = 1; }
                if (stableCount >= options.SettleStableReads) return frame;
            }
            if (poll < options.SettleMaxPolls - 1)
                await _delay(options.SettlePollInterval, ct).ConfigureAwait(false);
        }
        return lastSeen;
    }
}
