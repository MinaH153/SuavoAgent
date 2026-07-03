using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Core.Agentic.Replication;

/// <summary>Why a gated template execution stopped. Every non-Completed outcome is a fail-closed STOP —
/// the executor never blind-continues past a screen it doesn't recognise or a step it can't gate.</summary>
public enum GatedReplayOutcome
{
    /// <summary>Every step verified/actuated + every ExpectedAfter met — the operator-approved task ran.</summary>
    Completed,
    /// <summary>Empty template.</summary>
    NoSteps,
    /// <summary>v1 refusal: a Type / PressKey / IsWrite step is present. Click-family only — actuate nothing.</summary>
    TypeStepsUnsupportedV1,
    /// <summary>The live entry screen does not structurally match Steps[0].ExpectedVisible — actuate nothing.</summary>
    EntryMismatch,
    /// <summary>A later step's live screen no longer matches its ExpectedVisible (drift mid-run) — STOP.</summary>
    ScreenDrift,
    /// <summary>Null / unusable perception — can't confirm where we are.</summary>
    PerceiveFailed,
    /// <summary>Preflight (kill/pause/never-blind), an unscrubbed frame, or a per-action gate refused. NEVER
    /// actuated a denied OR dry-run step live — dry-run is a STOP here, not a supervised dispatch.</summary>
    GateDenied,
    /// <summary>The actuator rejected / failed the click.</summary>
    StepRejected,
    /// <summary>The click ran but ExpectedAfter did not appear — the change-detection gap, closed for this path.</summary>
    PostconditionFailed,
    /// <summary>Cooperative cancellation (abort_navigation) or takeover observed between steps.</summary>
    Cancelled,
}

public sealed record GatedReplayResult(GatedReplayOutcome Outcome, int StepsCompleted, int? FailedOrdinal, string? Detail);

/// <summary>
/// FSD "autopilot engage": deterministically executes an operator-APPROVED learned
/// <see cref="WorkflowTemplate"/> by replaying its steps through the SAME fail-closed
/// <see cref="IPerceiver"/> + <see cref="ISafetyGate"/> + <see cref="IActuator"/> seam the NL loop uses
/// (built by <see cref="ReplayFactory.CreateExecutor"/>). No reasoner: the template IS the plan.
///
/// This is the SAFETY-CRITICAL executing path (distinct from <see cref="TemplateReplayer"/>, the internal
/// harvest-replicate replayer). Differences that make it safe to run on a live app on an operator's command:
///   • v1 CLICK-FAMILY ONLY — refuses the WHOLE template up front if any step is Type / PressKey / IsWrite.
///   • Preflight (kill/pause/never-blind) before start AND before every step.
///   • ELEMENT-CENTRIC verify (<see cref="ElementSignature.MatchesStructurally"/> honouring MinElementsRequired —
///     the SAME predicate as the observe→assist keystone) at entry, before each step (drift), and on ExpectedAfter.
///   • A step reaches the actuator ONLY after <see cref="ISafetyGate.AssertScrubbed"/> + a hard
///     <see cref="SafetyDecision.Allow"/> from <see cref="ISafetyGate.GateAction"/>. Deny OR AllowDryRun ⇒ STOP
///     (never a live actuation, never a silent dry-run downgrade).
///   • Honours the CancellationToken between every step. No auto-banking — the template stays source of truth.
/// </summary>
public sealed class GatedTemplateExecutor
{
    private readonly IPerceiver _perceiver;
    private readonly IActuator _actuator;
    private readonly ISafetyGate _safety;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public GatedTemplateExecutor(
        IPerceiver perceiver, IActuator actuator, ISafetyGate safety,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _perceiver = perceiver ?? throw new ArgumentNullException(nameof(perceiver));
        _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _delay = delay ?? Task.Delay;
    }

    /// <summary>Executes an APPROVED template. Caller (HeartbeatWorker) owns the approval-status gate;
    /// this method owns the per-step safety gates. <paramref name="baseContext"/>.DryRun should be false for
    /// an operator-commanded run — but a step still only actuates on a hard Allow from GateAction.</summary>
    public async Task<GatedReplayResult> ExecuteAsync(
        WorkflowTemplate template,
        AgentObjective objective,
        ActuationContext baseContext,
        TemplateReplayOptions options,
        CancellationToken ct)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        var steps = template.Steps.OrderBy(s => s.Ordinal).ToList();
        if (steps.Count == 0)
            return new GatedReplayResult(GatedReplayOutcome.NoSteps, 0, null, null);

        // 1. REFUSE UNSAFE TEMPLATES UP FRONT — before any perceive/actuate. v1 is click-family only:
        //    a single Type / PressKey / IsWrite step disqualifies the WHOLE template (no partial execution).
        foreach (var step in steps)
        {
            if (step.Kind is TemplateStepKind.Type or TemplateStepKind.PressKey || step.IsWrite)
                return new GatedReplayResult(GatedReplayOutcome.TypeStepsUnsupportedV1, 0, step.Ordinal,
                    $"step {step.Ordinal}: {step.Kind}{(step.IsWrite ? "/write" : "")} not supported in v1");
        }

        // 2. PREFLIGHT before starting (kill-switch / pause / never-blind-on-live-PMS). Same gate as the loop.
        var pre = _safety.Preflight(objective);
        if (pre.Decision == SafetyDecision.Deny)
            return new GatedReplayResult(GatedReplayOutcome.GateDenied, 0, null, pre.Reason);

        var processName = TemplatePlanCompiler.ProcessFromGlob(template.ProcessNameGlob);
        var completed = 0;

        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];

            // 5. HALT on takeover/cancel — checked between every step.
            if (ct.IsCancellationRequested)
                return new GatedReplayResult(GatedReplayOutcome.Cancelled, completed, step.Ordinal, null);

            // 2 (cont). Re-preflight before EACH step so a mid-run kill/pause halts instantly.
            pre = _safety.Preflight(objective);
            if (pre.Decision == SafetyDecision.Deny)
                return new GatedReplayResult(GatedReplayOutcome.GateDenied, completed, step.Ordinal, pre.Reason);

            // 3/4. Perceive + scrub-assert + ELEMENT-CENTRIC verify the CURRENT screen still matches this step's
            //      ExpectedVisible. For step 0 this IS the entry verify (mismatch ⇒ EntryMismatch, actuate
            //      nothing); for later steps a mismatch is drift ⇒ ScreenDrift. Same predicate as the keystone.
            var screen = await SettleAsync(options, ct).ConfigureAwait(false);
            if (screen is null)
                return new GatedReplayResult(GatedReplayOutcome.PerceiveFailed, completed, step.Ordinal, "no_perception");
            if (!_safety.AssertScrubbed(screen))
                return new GatedReplayResult(GatedReplayOutcome.GateDenied, completed, step.Ordinal, "unscrubbed_frame");
            if (!StructuralMatch(step.ExpectedVisible, step.MinElementsRequired, screen))
                return new GatedReplayResult(
                    i == 0 ? GatedReplayOutcome.EntryMismatch : GatedReplayOutcome.ScreenDrift,
                    completed, step.Ordinal, "expected_visible_not_matched");

            // WaitForElement / VerifyElement are NON-acting: the structural verify above WAS the check. Nothing
            // to gate or actuate — advance.
            if (step.Kind is TemplateStepKind.WaitForElement or TemplateStepKind.VerifyElement)
            {
                completed++;
                continue;
            }

            // Click step. Rebuild the EXACT click via the shared compiler (click_by_signature +
            // automationId/controlType/className/process_name) — the same verb the actuator understands.
            var action = TemplatePlanCompiler.CompileStep(step, processName).Action!;

            // Per-action safety gate. A destructive click only actuates on a HARD Allow; Deny AND AllowDryRun
            // both STOP (an unearned/forced-dry-run step is NEVER driven live and NEVER silently dry-run here).
            var verdict = _safety.GateAction(action, objective);
            if (verdict.Decision != SafetyDecision.Allow)
                return new GatedReplayResult(GatedReplayOutcome.GateDenied, completed, step.Ordinal, verdict.Reason);

            var outcome = await _actuator.ActAsync(action, baseContext, ct).ConfigureAwait(false);
            if (outcome.Status != ActStatus.Success)
                return new GatedReplayResult(GatedReplayOutcome.StepRejected, completed, step.Ordinal,
                    outcome.RejectionCode ?? outcome.Status.ToString());

            // 4 (cont). Settle, then verify ExpectedAfter (if present) element-centrically. NotMet ⇒ STOP +
            //           escalate — this CLOSES the change-detection gap for our path (we assert the screen moved
            //           to the EXPECTED post-state, not merely that it changed).
            if (step.ExpectedAfter is { Count: > 0 } after)
            {
                var post = await SettleAsync(options, ct).ConfigureAwait(false);
                if (post is null)
                    return new GatedReplayResult(GatedReplayOutcome.PerceiveFailed, completed, step.Ordinal, "no_perception_post");
                if (!_safety.AssertScrubbed(post))
                    return new GatedReplayResult(GatedReplayOutcome.GateDenied, completed, step.Ordinal, "unscrubbed_frame_post");
                if (!StructuralMatch(after, ExpectedAfterMin(step, after.Count), post))
                    return new GatedReplayResult(GatedReplayOutcome.PostconditionFailed, completed, step.Ordinal,
                        "expected_after_not_met");
            }

            completed++;
        }

        return new GatedReplayResult(GatedReplayOutcome.Completed, completed, null, null);
    }

    /// <summary>Element-centric structural match — the SAME predicate the observe→assist keystone
    /// (<see cref="SuavoAgent.Core.State.AgentStateDb.FindActiveTemplateByScreenSignatures"/>) applies:
    /// parse the screen's "controlType|automationId" signatures into match atoms (className unknown on the
    /// wire ⇒ null, which <see cref="ElementSignature.MatchesStructurally"/> treats null-tolerantly), then
    /// count how many <paramref name="expected"/> signatures are present and require ≥ <paramref name="minRequired"/>.
    /// NOT a ContentHash compare — this is the element-centric match the template is built around.</summary>
    private static bool StructuralMatch(
        IReadOnlyList<ElementSignature> expected, int minRequired, PerceivedScreen screen)
    {
        if (expected.Count == 0) return minRequired <= 0;
        var wire = screen.Signatures;
        if (wire is null || wire.Count == 0) return false;

        var perceived = new List<ElementSignature>(wire.Count);
        foreach (var s in wire)
        {
            var parts = s.Split('|');
            if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                continue;
            try { perceived.Add(new ElementSignature(parts[0], parts[1], null)); }
            catch (ArgumentException) { /* malformed atom — skip */ }
        }

        var hits = 0;
        foreach (var e in expected)
            if (perceived.Any(p => e.MatchesStructurally(p)))
                hits++;
        return hits >= minRequired;
    }

    /// <summary>Min ExpectedAfter matches required, mirroring <c>TemplateRuleGenerator</c>'s VerifyAfter K:
    /// the step's own MinElementsRequired/ExpectedVisible ratio applied to the post-set (clamped to [1, count]),
    /// so the postcondition tolerance is IDENTICAL to what the approved rule encodes.</summary>
    private static int ExpectedAfterMin(TemplateStep step, int afterCount)
    {
        var ratio = step.ExpectedVisible.Count == 0
            ? 1.0
            : (double)step.MinElementsRequired / step.ExpectedVisible.Count;
        var k = Math.Max(1, (int)Math.Ceiling(afterCount * ratio));
        return Math.Min(k, afterCount);
    }

    /// <summary>Poll-until-stable re-perceive (defeats the async-UI repaint race), bounded; never hangs.
    /// Mirrors <see cref="TemplateReplayer"/> / <see cref="VerifiedSkillReplayer"/> so the settle race is identical.</summary>
    private async Task<PerceivedScreen?> SettleAsync(TemplateReplayOptions options, CancellationToken ct)
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
