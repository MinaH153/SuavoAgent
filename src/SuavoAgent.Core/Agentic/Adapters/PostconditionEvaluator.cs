using System;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Execution-grounded post-state verdict for the live agentic loop — the "is the result REAL?" trust
/// line, expressed as a pure function (mirrors <see cref="NavigateSafety"/> / <see cref="NavigateReasoning"/>
/// so it is trivially unit-testable without standing up the tiered brain).
///
/// <para>
/// This is the moat's load-bearing piece. It REPLACES the previous always-<c>Ambiguous</c> stub in
/// <see cref="TieredBrainReasoner.VerifyPostconditionAsync"/>, which silently treated every actuating
/// step as "not yet confirmed" and so never caught a no-op action (dead button, wrong target,
/// permission error). With this, an action that did nothing is caught as <see cref="PostconditionVerdict.NotMet"/>.
/// </para>
///
/// <para>
/// v1 = screen-change detection: an actuating action (<see cref="NextActionKind.Act"/>) that produced
/// NO observable change in the perceived screen → <see cref="PostconditionVerdict.NotMet"/>; an Act
/// that changed the screen produced a real effect → <see cref="PostconditionVerdict.Met"/>. A terminal
/// step (Done/Escalate) has nothing to ground a post-state on → <see cref="PostconditionVerdict.Ambiguous"/>
/// (the loop treats Ambiguous as NotMet ⇒ re-reason; fail-closed). The verdict now carries REAL signal
/// into the prior-actions transcript, no-progress detection, and (next increment) verified-only learning.
/// </para>
///
/// <para>
/// Known limitation, addressed by the next increment: a value-only change the perceiver does not surface
/// in the screen content hash could read as NotMet. The stronger checks — a declared expected-element
/// appearance and SQL write-confirmation for writeback actions — layer ON TOP of this baseline; they do
/// not replace it. Change-detection is strictly better than the always-Ambiguous stub it supersedes.
/// </para>
/// </summary>
public static class PostconditionEvaluator
{
    /// <summary>
    /// Verdict for the action just executed, given the screen BEFORE and AFTER (post-settle).
    /// Pure + side-effect-free.
    /// </summary>
    public static PostconditionVerdict Evaluate(NextAction lastAction, PerceivedScreen before, PerceivedScreen after)
    {
        // Only actuating actions have a post-state to ground a verdict on. Terminal steps
        // (Done/Escalate) are reasoner decisions, not effects to verify.
        if (lastAction is null || lastAction.Kind != NextActionKind.Act)
            return PostconditionVerdict.Ambiguous;

        // No observable screen change after an actuating action = the action had no effect → fail-closed.
        var changed = !string.Equals(before?.ContentHash, after?.ContentHash, StringComparison.Ordinal);
        return changed ? PostconditionVerdict.Met : PostconditionVerdict.NotMet;
    }
}
