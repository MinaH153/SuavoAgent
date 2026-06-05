namespace SuavoAgent.Core.Cloud;

/// <summary>
/// Inputs to the OTA crash-loop guard, captured at a startup decision point. Pure data —
/// the caller reads these from the probation flag + boot counter + a health self-test +
/// the presence of the <c>.old</c> binary set, then asks <see cref="OtaProbationEvaluator"/>
/// what to do. No I/O here.
/// </summary>
/// <param name="HasPendingProbation">True when an OTA swap is on trial (probation flag present).</param>
/// <param name="HealthConfirmed">True once THIS process reached the healthy milestone (IPC up, first good heartbeat).</param>
/// <param name="BootsSinceSwap">Process starts since the swap (incremented early + persisted, current start included).</param>
/// <param name="MaxProbationBoots">Un-healthy boots TOLERATED — each is a full attempt; downgrade fires on the boot AFTER the last tolerated one (strictly greater), so a boot that would finally come up healthy is never pre-empted.</param>
/// <param name="OldSetAvailable">True when the full <c>.old</c> binary set is present to roll back to.</param>
public readonly record struct OtaProbationState(
    bool HasPendingProbation,
    bool HealthConfirmed,
    int BootsSinceSwap,
    int MaxProbationBoots,
    bool OldSetAvailable);

/// <summary>What the caller should do about a binary set that is (or isn't) on trial.</summary>
public enum OtaProbationDecision
{
    /// <summary>No OTA on trial — normal path (the caller may reap stale .old files).</summary>
    NoProbation,
    /// <summary>The new set confirmed healthy — reap .old and clear the probation flag.</summary>
    Commit,
    /// <summary>On trial, not yet healthy, under the crash-loop threshold — keep running.</summary>
    Continue,
    /// <summary>Crash-loop detected and a rollback target exists — restore the .old set and restart.</summary>
    Downgrade,
    /// <summary>Crash-loop detected but NO .old set to roll back to — can't self-heal; alert + stay up.</summary>
    EscalateNoRollback,
}

/// <summary>
/// Pure decision core of the OTA crash-loop guard. A bad OTA must not be able to take a box
/// (or the fleet) dark with no recovery: the new set runs on probation, auto-commits once it
/// proves healthy, and auto-downgrades to the last-good <c>.old</c> set if it crash-loops.
///
/// Healthy ALWAYS wins over crash-count: a set that reaches the healthy milestone on its last
/// allowed boot still commits. Fail-closed when rollback is impossible (EscalateNoRollback)
/// rather than thrashing a downgrade that can't complete.
///
/// Boundary is strictly-greater (Codex 2026-06-04 Q4): the boot counter includes the current
/// start, so downgrading at &gt;= would pre-empt the very boot that might come up healthy. With
/// &gt; and Max=3, boots 1-3 each get a full attempt and downgrade fires only on the 4th start.
/// </summary>
public static class OtaProbationEvaluator
{
    public const int DefaultMaxProbationBoots = 3;

    public static OtaProbationDecision Evaluate(OtaProbationState state)
    {
        if (!state.HasPendingProbation)
            return OtaProbationDecision.NoProbation;

        if (state.HealthConfirmed)
            return OtaProbationDecision.Commit;

        if (state.BootsSinceSwap > state.MaxProbationBoots)
            return state.OldSetAvailable
                ? OtaProbationDecision.Downgrade
                : OtaProbationDecision.EscalateNoRollback;

        return OtaProbationDecision.Continue;
    }
}
