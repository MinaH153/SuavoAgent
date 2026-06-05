using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Core.Agentic.Replication;

/// <summary>Why a template replay stopped.</summary>
public enum ReplayOutcome
{
    /// <summary>Every step actuated/verified successfully.</summary>
    Completed,
    /// <summary>The safety gate denied an actuating step.</summary>
    GateDenied,
    /// <summary>An actuating step was rejected/failed by the actuator.</summary>
    ActionFailed,
    /// <summary>A WaitForElement/VerifyElement check did not find its expected elements.</summary>
    CheckFailed,
    /// <summary>A writeback step's ExpectedAfter postcondition was not met.</summary>
    PostconditionFailed,
    /// <summary>Cancellation observed.</summary>
    Cancelled,
}

public sealed record ReplayResult(ReplayOutcome Outcome, int StepsCompleted, int? FailedOrdinal, string? Detail);

public sealed record TemplateReplayOptions
{
    public int SettleStableReads { get; init; } = 2;
    public int SettleMaxPolls { get; init; } = 5;
    public TimeSpan SettlePollInterval { get; init; } = TimeSpan.FromMilliseconds(250);
}

/// <summary>
/// Deterministic executor for a compiled learned workflow. Unlike the NL loop, the steps are NOT
/// reasoner-chosen — the template IS the plan; the replayer drives each step in order through the
/// SAME fail-closed IActuator + ISafetyGate seam (so replay is gated, audited, dry-run-able and
/// never-blind exactly like NL navigation). Pure control flow over injected ports ⇒ fully unit-testable.
///
/// Stops fail-closed at the first denial / action failure / unmet check or writeback postcondition,
/// reporting the offending ordinal so the learning loop can patch the selector and re-extract.
/// </summary>
public sealed class TemplateReplayer
{
    private readonly IPerceiver _perceiver;
    private readonly IActuator _actuator;
    private readonly ISafetyGate _safety;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public TemplateReplayer(
        IPerceiver perceiver, IActuator actuator, ISafetyGate safety,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _perceiver = perceiver ?? throw new ArgumentNullException(nameof(perceiver));
        _actuator = actuator ?? throw new ArgumentNullException(nameof(actuator));
        _safety = safety ?? throw new ArgumentNullException(nameof(safety));
        _delay = delay ?? Task.Delay;
    }

    public async Task<ReplayResult> ReplayAsync(
        IReadOnlyList<CompiledStep> plan,
        AgentObjective objective,
        ActuationContext baseContext,
        TemplateReplayOptions options,
        CancellationToken ct)
    {
        var completed = 0;

        foreach (var step in plan)
        {
            if (ct.IsCancellationRequested)
                return new ReplayResult(ReplayOutcome.Cancelled, completed, step.Ordinal, null);

            if (step.IsCheck)
            {
                var screen = await SettleAsync(options, ct).ConfigureAwait(false);
                if (screen is null || !AllPresent(step.ExpectedVisible, screen))
                    return new ReplayResult(ReplayOutcome.CheckFailed, completed, step.Ordinal, "expected_elements_not_visible");
                completed++;
                continue;
            }

            // Actuating step — gate, then act through the fail-closed dispatch.
            var verdict = _safety.GateAction(step.Action!, objective);
            if (verdict.Decision == SafetyDecision.Deny)
                return new ReplayResult(ReplayOutcome.GateDenied, completed, step.Ordinal, verdict.Reason);

            var dryRun = baseContext.DryRun || verdict.Decision == SafetyDecision.AllowDryRun;
            var outcome = await _actuator
                .ActAsync(step.Action!, baseContext with { DryRun = dryRun }, ct)
                .ConfigureAwait(false);

            if (outcome.Status != ActStatus.Success)
                return new ReplayResult(ReplayOutcome.ActionFailed, completed, step.Ordinal, outcome.RejectionCode);

            // Writeback safety: confirm the ExpectedAfter elements appeared before moving on.
            if (step.IsWrite)
            {
                var after = await SettleAsync(options, ct).ConfigureAwait(false);
                if (after is null || !AllPresent(step.ExpectedAfter ?? Array.Empty<ElementSignature>(), after))
                    return new ReplayResult(ReplayOutcome.PostconditionFailed, completed, step.Ordinal, "writeback_postcondition_not_met");
            }

            completed++;
        }

        return new ReplayResult(ReplayOutcome.Completed, completed, null, null);
    }

    /// <summary>Structural match: every expected (controlType|automationId) signature must be on screen.</summary>
    private static bool AllPresent(IReadOnlyList<ElementSignature> expected, PerceivedScreen screen)
    {
        if (expected.Count == 0)
            return true;
        var present = screen.Signatures ?? Array.Empty<string>();
        return expected.All(e =>
            present.Contains(SignatureKey(e.ControlType, e.AutomationId), StringComparer.OrdinalIgnoreCase));
    }

    public static string SignatureKey(string controlType, string automationId) => $"{controlType}|{automationId}";

    /// <summary>Poll-until-stable re-perceive (defeats the async-UI repaint race), bounded; never hangs.</summary>
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
