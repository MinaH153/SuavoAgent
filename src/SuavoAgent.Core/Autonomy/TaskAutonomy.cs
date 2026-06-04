namespace SuavoAgent.Core.Autonomy;

/// <summary>
/// How far a (task, pharmacy) has EARNED its way up the autonomy ladder. This is capability, not
/// permission — actual unsupervised execution is gated separately and fail-closed
/// (<see cref="TaskAutonomyEvaluator.MayRunUnsupervised"/>).
/// </summary>
public enum AutonomyLevel
{
    /// <summary>Every run is supervised (human present, grabs the wheel). The default; never skipped.</summary>
    Supervised = 0,

    /// <summary>Earned a clean-verified streak ≥ threshold — ELIGIBLE for unsupervised, but still
    /// requires an explicit per-deployment enable before it actually runs unattended.</summary>
    Eligible = 1,
}

/// <summary>Immutable snapshot of one task's standing on the autonomy ladder.</summary>
public sealed record TaskAutonomyState(
    string TaskKey,
    string PharmacyId,
    int ConsecutiveCleanRuns,
    int TotalRuns,
    AutonomyLevel Level,
    string? LastOutcome);

/// <summary>
/// Pure, fail-closed policy for "earn unsupervised autonomy after N clean verified runs" — the M3
/// graduation rung of the FSD ladder. Kept separate from persistence so the safety-critical rules
/// are exhaustively unit-testable.
///
/// <para>The discipline (from the roadmap): a task EARNS the capability, but a human still flips the
/// switch. The ledger only ever raises a task to <see cref="AutonomyLevel.Eligible"/>; it can never,
/// by itself, cause an unattended run. ANY non-clean run resets the streak to zero — one stumble and
/// the task re-earns its standing.</para>
/// </summary>
public static class TaskAutonomyEvaluator
{
    /// <summary>A clean verified run extends the streak; anything else resets it to zero.</summary>
    public static int NextStreak(int currentStreak, bool clean)
    {
        if (!clean) return 0;
        return currentStreak < 0 ? 1 : currentStreak + 1;
    }

    /// <summary>Eligible once the clean-verified streak reaches the threshold; otherwise Supervised.</summary>
    public static AutonomyLevel LevelFor(int consecutiveCleanRuns, int threshold)
    {
        if (threshold < 1) threshold = 1; // a non-positive threshold must never auto-grant eligibility
        return consecutiveCleanRuns >= threshold ? AutonomyLevel.Eligible : AutonomyLevel.Supervised;
    }

    /// <summary>
    /// FAIL-CLOSED permission gate: a task may run unsupervised ONLY if it has earned eligibility AND
    /// unsupervised execution is explicitly enabled for this deployment. Both must be true; default
    /// is always supervised.
    /// </summary>
    public static bool MayRunUnsupervised(AutonomyLevel level, bool unsupervisedExecutionEnabled)
        => level == AutonomyLevel.Eligible && unsupervisedExecutionEnabled;
}
