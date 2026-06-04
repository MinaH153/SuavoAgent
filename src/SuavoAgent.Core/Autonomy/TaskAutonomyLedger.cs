using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Autonomy;

/// <summary>
/// Persistent record of how far each (task, pharmacy) has EARNED up the autonomy ladder. Wraps the
/// pure <see cref="TaskAutonomyEvaluator"/> over the <c>task_autonomy</c> store: a clean verified run
/// extends the streak, any stumble resets it, and a task only ever rises to
/// <see cref="AutonomyLevel.Eligible"/>. Whether an eligible task actually runs unattended is a
/// separate, fail-closed decision (<see cref="MayRunUnsupervised"/>) that also needs an explicit
/// deployment enable.
/// </summary>
public sealed class TaskAutonomyLedger
{
    private readonly AgentStateDb _db;
    private readonly int _cleanRunsThreshold;

    public TaskAutonomyLedger(AgentStateDb db, int cleanRunsThreshold)
    {
        _db = db;
        // A non-positive threshold would mean "eligible from zero" — never allow that.
        _cleanRunsThreshold = cleanRunsThreshold < 1 ? 1 : cleanRunsThreshold;
    }

    /// <summary>Record a finished run's verdict and return the task's new standing.</summary>
    public TaskAutonomyState RecordRun(string taskKey, string pharmacyId, bool clean, string? outcome = null)
    {
        var (currentStreak, currentTotal, _) = _db.GetTaskAutonomyRaw(taskKey, pharmacyId);
        var nextStreak = TaskAutonomyEvaluator.NextStreak(currentStreak, clean);
        var totalRuns = currentTotal + 1;
        _db.UpsertTaskAutonomy(taskKey, pharmacyId, nextStreak, totalRuns, outcome);
        return new TaskAutonomyState(
            taskKey, pharmacyId, nextStreak, totalRuns,
            TaskAutonomyEvaluator.LevelFor(nextStreak, _cleanRunsThreshold), outcome);
    }

    /// <summary>The task's current standing (without recording a run).</summary>
    public TaskAutonomyState GetState(string taskKey, string pharmacyId)
    {
        var (streak, total, outcome) = _db.GetTaskAutonomyRaw(taskKey, pharmacyId);
        return new TaskAutonomyState(
            taskKey, pharmacyId, streak, total,
            TaskAutonomyEvaluator.LevelFor(streak, _cleanRunsThreshold), outcome);
    }

    /// <summary>
    /// Fail-closed: may this task run unsupervised right now? Only if it has earned eligibility AND
    /// unsupervised execution is explicitly enabled for this deployment.
    /// </summary>
    public bool MayRunUnsupervised(string taskKey, string pharmacyId, bool unsupervisedExecutionEnabled)
        => TaskAutonomyEvaluator.MayRunUnsupervised(
            GetState(taskKey, pharmacyId).Level, unsupervisedExecutionEnabled);
}
