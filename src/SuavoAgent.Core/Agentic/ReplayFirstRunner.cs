using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Outcome of one replay-first attempt. <see cref="ReplayCompleted"/> is the ONLY signal that lets the
/// caller skip the agentic loop; every other shape falls through to the loop unchanged. When a replay
/// was actually attempted (fingerprint matched + every gate passed) <see cref="Replay"/> carries the raw
/// replayer result; otherwise <see cref="SkipReason"/> is a PHI-free operational code (verbs/rule names,
/// never values) explaining why replay-first stood down.
/// </summary>
public sealed record ReplayFirstResult(bool ReplayCompleted, string? SkipReason, SkillReplayResult? Replay)
{
    public static ReplayFirstResult Skip(string reason) => new(false, reason, null);

    public static ReplayFirstResult FellThrough(SkillReplayResult replay)
        => new(false, $"replay_outcome:{replay.Outcome}", replay);

    public static ReplayFirstResult Completed(SkillReplayResult replay) => new(true, null, replay);
}

/// <summary>
/// MOAT Increment 2 — replay-first: the amortization payoff. When a navigate/explore objective arrives
/// for a (pharmacy, taskKey, app) with a HEALTHY banked <see cref="VerifiedSkill"/> whose first step's
/// StateHash matches the live screen, run <see cref="VerifiedSkillReplayer"/> INSTEAD of the agentic
/// loop — zero LLM, zero cloud reasoning. The LLM runs only on miss/drift (the caller falls through).
///
/// <para>Layered safety (every layer fail-closed; ALL must hold before a single replayed action fires):</para>
/// <list type="number">
/// <item><b>Eligibility</b> — success_count ≥ <see cref="MinSuccessCount"/> (one verification could be
/// luck; two is a tube) AND the v1 HARD RULE: every step verb is click-family. A type/press-bearing
/// skill never auto-replays (a banked literal types the OLD value; the "screen changed" postcondition
/// would certify a semantically wrong write) — those stay operator-explicit via <c>replay_skill</c>.
/// <c>Agent:ReplayFirstAllowTypeSteps</c> pre-declares the future opt-in and stays OFF.</item>
/// <item><b>Supervised runs never auto-replay</b> — replay IS actuation. A dry-run dispatch cannot move
/// the screen, so a dry-run replay can never satisfy <see cref="Adapters.PostconditionEvaluator"/>; it
/// would always die PostconditionFailed at step 0 AND falsely decay a healthy skill. When the run is
/// dry-run, or the composite gate answers anything but Allow for the skill's first action (M3 autonomy
/// not earned / gate-forced dry-run / deny), replay-first stands down and the loop — which dry-runs +
/// escalates for operator approval exactly as today — keeps the autonomy/operator boundary. The gate
/// consulted here is the SAME instance the replay itself uses: no parallel policy, zero drift.</item>
/// <item><b>Preflight</b> — the same kill-switch / pause / never-blind-on-live-PMS gate the loop runs,
/// checked BEFORE the precheck perceive (no IPC under a tripped kill-switch). The replayer re-runs it.</item>
/// <item><b>Entry fingerprint</b> — one perceive through the SAME perceiver the loop would use; the
/// frame must assert scrubbed and its ContentHash must equal Steps[0].StateHash exactly. Mismatch is a
/// SILENT skip (no skill decay — an unrelated foreground screen is not the skill's fault).</item>
/// </list>
///
/// <para>Outcome feedback (slime-mold hygiene, identical to the <c>replay_skill</c> handler's set):
/// Completed → record success (re-thicken). StateMismatch / PostconditionFailed / Unparseable /
/// StepRejected → record failure (decay; 3 consecutive retires). Environmental outcomes (GateDenied,
/// PerceiveFailed, Cancelled, NoSteps) record nothing. Every non-Completed outcome falls through to the
/// agentic loop, which perceives FRESH — a partial replay leaves the app mid-flow, and the loop's whole
/// design is "reason from the current screen".</para>
///
/// <para>HIPAA: replay executes only Phase-3B-certified banked strings; perception here and inside the
/// replayer passes the same AssertScrubbed; no new data leaves the box. Skip/fall-through reasons are
/// operational codes only.</para>
/// </summary>
public sealed class ReplayFirstRunner
{
    /// <summary>One verified run could be luck; two is a tube worth trusting.</summary>
    public const int MinSuccessCount = 2;

    // v1 auto-replay surface — click-family ONLY (mirrors HarvestPhiCertifier.ClickFamilyVerbs and the
    // SandboxExploreSafetyGate v1 verb set). Ordinal: verbs are exact catalog names.
    private static readonly HashSet<string> ClickFamilyVerbs = new(StringComparer.Ordinal)
    {
        "click_by_label", "click_by_signature", "launch_sandbox_app",
    };

    // Gated behind Agent:ReplayFirstAllowTypeSteps (declared, OFF in v1) — see AgentOptions.
    private static readonly HashSet<string> TypeFamilyVerbs = new(StringComparer.Ordinal)
    {
        "type_into_field", "press_keys",
    };

    private readonly IPerceiver _perceiver;
    private readonly ISafetyGate _safety;
    private readonly VerifiedSkillReplayer _replayer;

    /// <summary>Wired with the SAME ports the agentic loop for this run would use (perceiver path,
    /// composite/sandbox gate, actuator) — see <see cref="NavigateReplayFactory"/>.</summary>
    public ReplayFirstRunner(
        IPerceiver perceiver,
        IActuator actuator,
        ISafetyGate safety,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _perceiver = perceiver ?? throw new ArgumentNullException(nameof(perceiver));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        if (actuator is null) throw new ArgumentNullException(nameof(actuator));
        _replayer = new VerifiedSkillReplayer(perceiver, actuator, safety, delay);
    }

    /// <summary>
    /// Pure, fail-closed eligibility for AUTO-replay. False with a PHI-free <paramref name="reason"/>
    /// code on: thin success count, any step whose verb cannot be resolved, any verb outside the
    /// click-family (type-family only when <paramref name="allowTypeSteps"/> — OFF in v1). Legacy
    /// sig-only rows resolve their verb from the signature prefix; anything unresolvable refuses.
    /// </summary>
    public static bool IsEligible(
        IReadOnlyList<VerifiedStep> steps, int successCount, bool allowTypeSteps, out string? reason)
    {
        if (steps is null || steps.Count == 0)
        {
            reason = "no_steps";
            return false;
        }
        if (successCount < MinSuccessCount)
        {
            reason = "success_count_below_threshold";
            return false;
        }

        foreach (var step in steps)
        {
            var verb = ResolveVerb(step);
            if (verb is null)
            {
                reason = "unresolvable_step_verb";
                return false;
            }
            if (ClickFamilyVerbs.Contains(verb))
                continue;
            if (TypeFamilyVerbs.Contains(verb))
            {
                if (allowTypeSteps)
                    continue;
                reason = $"type_bearing_step:{verb}";
                return false;
            }
            reason = $"non_click_step:{verb}";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// The replay-first protocol: eligibility → supervised stand-down → preflight → one precheck
    /// perceive + entry-fingerprint compare → gate pre-check on the first action → deterministic replay
    /// → outcome recording. Returns Completed ONLY when every banked step executed and verified; the
    /// caller acks success and skips the loop. Everything else returns a skip/fell-through result and
    /// the caller runs the agentic loop exactly as if replay-first did not exist.
    /// <paramref name="recordOutcome"/> is (skillId, success) — invoked once for Completed (true) and
    /// once for skill-fault outcomes (false); never for environmental outcomes or pre-replay skips.
    /// </summary>
    public async Task<ReplayFirstResult> TryReplayAsync(
        VerifiedSkill skill,
        int successCount,
        AgentObjective objective,
        AgenticLoopOptions options,
        bool allowTypeSteps,
        Action<string, bool> recordOutcome,
        CancellationToken ct)
    {
        if (skill is null) throw new ArgumentNullException(nameof(skill));
        if (objective is null) throw new ArgumentNullException(nameof(objective));
        if (options is null) throw new ArgumentNullException(nameof(options));
        if (recordOutcome is null) throw new ArgumentNullException(nameof(recordOutcome));

        // 1. Eligibility — pure, no IO. Fail-closed before a single IPC round-trip.
        if (!IsEligible(skill.Steps, successCount, allowTypeSteps, out var why))
            return ReplayFirstResult.Skip(why!);

        // 2. Supervised runs never auto-replay (see class doc #2): a dry-run replay can never Complete
        //    (postconditions need a real screen change) and would falsely decay the skill. The loop's
        //    own supervised dry-run + escalate path IS the operator boundary — fall through to it.
        if (options.DryRun)
            return ReplayFirstResult.Skip("run_is_dry_run");

        // 3. Precedence-1 preflight — same first gate the loop applies, BEFORE any perceive IPC.
        var pre = _safety.Preflight(objective);
        if (pre.Decision != SafetyDecision.Allow)
            return ReplayFirstResult.Skip($"preflight_denied:{pre.Reason ?? pre.Decision.ToString()}");

        // 4. One perceive via the loop's own perceiver path; entry-state fingerprint must match EXACTLY.
        //    Mismatch = the screen is somewhere else — not the skill's fault: skip SILENTLY (no decay).
        var screen = await _perceiver.PerceiveAsync(ct).ConfigureAwait(false);
        if (screen is null)
            return ReplayFirstResult.Skip("precheck_perceive_failed");
        if (!_safety.AssertScrubbed(screen))
            return ReplayFirstResult.Skip("precheck_unscrubbed_frame");
        if (!string.Equals(screen.ContentHash, skill.Steps[0].StateHash, StringComparison.Ordinal))
            return ReplayFirstResult.Skip("state_fingerprint_mismatch");

        // 5. Autonomy/operator boundary pre-check via the SAME gate instance the replay will use: the
        //    first action must be allowed to run REAL. Every eligible verb is destructive (click-family
        //    ⊆ NavigateSafety.DestructiveVerbs), and all steps share (task, pharmacy), so the composite
        //    gate's verdict for step 0 is the verdict for every step. AllowDryRun (autonomy not earned /
        //    gate-forced dry-run) ⇒ stand down to the loop, which dry-runs + escalates as today.
        var firstAction = VerifiedSkillReplayer.ReconstructAction(skill.Steps[0]);
        if (firstAction is null)
        {
            // The replayer would refuse this skill at step 0 (Unparseable) — same fault class, recorded
            // identically, without dispatching anything.
            recordOutcome(skill.SkillId, false);
            return ReplayFirstResult.Skip("step0_unreconstructable");
        }
        var gate = _safety.GateAction(firstAction, objective);
        if (gate.Decision == SafetyDecision.Deny)
            return ReplayFirstResult.Skip($"gate_denied:{gate.Reason ?? "denied"}");
        if (gate.Decision == SafetyDecision.AllowDryRun)
            return ReplayFirstResult.Skip($"supervised_dry_run:{gate.Reason ?? "not_earned"}");

        // 6. Deterministic cash-in. The replayer re-runs preflight + AssertScrubbed + the per-action
        //    gate on EVERY step and stops fail-closed on any drift.
        var result = await _replayer.ReplayAsync(skill, objective, options, ct).ConfigureAwait(false);

        if (result.Outcome == SkillReplayOutcome.Completed)
        {
            recordOutcome(skill.SkillId, true);
            return ReplayFirstResult.Completed(result);
        }

        // Skill-fault outcomes decay the skill (same set as the replay_skill handler); environmental
        // outcomes (GateDenied / PerceiveFailed / Cancelled / NoSteps) are not the skill's fault.
        if (result.Outcome is SkillReplayOutcome.StateMismatch
            or SkillReplayOutcome.PostconditionFailed
            or SkillReplayOutcome.Unparseable
            or SkillReplayOutcome.StepRejected)
        {
            recordOutcome(skill.SkillId, false);
        }

        return ReplayFirstResult.FellThrough(result);
    }

    /// <summary>Structured verb when banked; else the signature's verb prefix (legacy sig-only rows).
    /// Null (⇒ ineligible) on anything that does not parse — fail-closed.</summary>
    private static string? ResolveVerb(VerifiedStep step)
    {
        if (!string.IsNullOrEmpty(step.Verb))
            return step.Verb;

        var sig = step.ActionSignature;
        if (string.IsNullOrEmpty(sig))
            return null;

        var paren = sig.IndexOf('(');
        return paren > 0 ? sig[..paren] : null;
    }
}
