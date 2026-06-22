using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
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

            // 2. Reconstruct the action. AIRTIGHT path (Codex Q2 retired): if the skill banked structured
            //    params (Verb + ParamsJson, delimiter-safe JSON), rebuild the EXACT action from them — no
            //    parsing of the unescaped signature, so a value containing "," or "=" replays correctly.
            //    LEGACY fallback: sig-only rows banked before this change reconstruct via the round-trip
            //    parser. Either way the result is rejected fail-closed unless its signature round-trips
            //    EXACTLY to the banked one. (Q1 TOCTOU between this perceive and the click is the same gap
            //    the agentic loop has; the next step's state-match catches a wrong landing.)
            var action = ReconstructAction(step);
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
            //
            //    QA learning-hardening — KNOWN GAP (deferred to harvest-resumption): PostconditionEvaluator
            //    is v1 change-detection — it asserts the screen CHANGED, not that it changed to the EXPECTED
            //    state. For a MIDDLE step a wrong landing is still caught next iteration by the state-match at
            //    the top of the loop (screen.ContentHash == step[i+1].StateHash). The TERMINAL step has no
            //    next step, so a wrong click that produces SOME change reads as Met → Completed → the skill
            //    re-thickens on a wrong action (worst case: navigation only — auto-replay is click-family and
            //    ReplayFirstAllowTypeSteps is OFF, so no wrong VALUE is written). The real fix is to bank a
            //    FinalStateHash (the post-state of the last action) on VerifiedSkill and assert
            //    after.ContentHash == skill.FinalStateHash on the terminal step (legacy skills without it fall
            //    back to this change-detection). That requires the harvester to CAPTURE the final post-state +
            //    a skill-schema/serialization change, so it belongs with the (currently hard-stopped) Phase-3B
            //    harvest work where the harvest→bank→replay round-trip can be exercised end-to-end.
            var after = await SettleAsync(screen, options, ct).ConfigureAwait(false) ?? screen;
            if (PostconditionEvaluator.Evaluate(action, screen, after) != PostconditionVerdict.Met)
                return new SkillReplayResult(SkillReplayOutcome.PostconditionFailed, completed,
                    $"step {i}: postcondition not met");

            completed++;
        }

        return new SkillReplayResult(SkillReplayOutcome.Completed, completed, null);
    }

    /// <summary>Rebuild the executable action for a step: structured (Verb + ParamsJson) when present
    /// (airtight), else parse the legacy signature. Returns null on any failure (caller rejects).
    /// Internal: <see cref="ReplayFirstRunner"/> reuses the EXACT same reconstruction for its
    /// autonomy-gate pre-check, so pre-check and replay can never disagree about the action.</summary>
    internal static NextAction? ReconstructAction(VerifiedStep step)
    {
        if (!string.IsNullOrEmpty(step.Verb))
        {
            try
            {
                var raw = string.IsNullOrEmpty(step.ParamsJson)
                    ? new Dictionary<string, string>()
                    : JsonSerializer.Deserialize<Dictionary<string, string>>(step.ParamsJson) ?? new Dictionary<string, string>();
                var p = raw.ToDictionary(kv => kv.Key, kv => (object?)kv.Value);
                return NextAction.Act(step.Verb, p);
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return ActionSignatureParser.TryParse(step.ActionSignature); // legacy sig-only banked rows
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
