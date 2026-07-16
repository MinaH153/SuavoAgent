using SuavoAgent.Core.State;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using System.Globalization;

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
    private readonly AgentOptions? _options;
    private readonly IDeviceAuthoritySigner? _signer;
    private readonly Func<DateTimeOffset> _now;
    private int _runtimeDisabled;

    public TaskAutonomyLedger(AgentStateDb db, int cleanRunsThreshold)
        : this(db, cleanRunsThreshold, null, null, null)
    {
    }

    internal TaskAutonomyLedger(
        AgentStateDb db,
        int cleanRunsThreshold,
        AgentOptions? options,
        IDeviceAuthoritySigner? signer,
        Func<DateTimeOffset>? now = null)
    {
        _db = db;
        // A non-positive threshold would mean "eligible from zero" — never allow that.
        _cleanRunsThreshold = cleanRunsThreshold < 1 ? 1 : cleanRunsThreshold;
        _options = options;
        _signer = signer;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Atomically records the exact scope, updates the local streak, consumes the
    /// monotonic device counter, signs the semantic receipt, and commits it to the
    /// durable cloud outbox. No unsigned run can earn exact-scope autonomy.
    /// </summary>
    public TaskAutonomyState RecordRun(AutonomyRunEvidence evidence)
    {
        if (_options is null || _signer is null)
            throw new InvalidOperationException("Device autonomy evidence signer is unavailable.");
        TaskAutonomyState recorded;
        try
        {
            recorded = _db.RecordAutonomyEvidence(
                evidence,
                _options,
                _signer,
                _cleanRunsThreshold).State;
        }
        catch
        {
            try { LatchDisabled("terminal_evidence_persistence_failed"); }
            catch { /* runtime latch is already set before durable persistence */ }
            throw;
        }
        if (evidence.Supervised && evidence.Clean)
            Interlocked.Exchange(ref _runtimeDisabled, 0);
        return recorded;
    }

    public void LatchDisabled(string reasonCode)
    {
        Interlocked.Exchange(ref _runtimeDisabled, 1);
        _db.LatchAutonomyDisabled("pricing", reasonCode);
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
    {
        if (_signer is null || Volatile.Read(ref _runtimeDisabled) == 1) return false;
        try
        {
            if (_db.IsAutonomyDisabled("pricing")) return false;
        }
        catch
        {
            return false;
        }
        var state = _db.GetExactAutonomyState(taskKey, pharmacyId);
        if (!TryReadEvidenceTime(state.UpdatedAt, out var updatedAt)) return false;
        var now = _now();
        if (updatedAt < now.AddDays(-7) || updatedAt > now.AddSeconds(30)) return false;
        return string.Equals(state.DeviceKeyId, _signer.KeyId, StringComparison.Ordinal) &&
            TaskAutonomyEvaluator.MayRunUnsupervised(
                TaskAutonomyEvaluator.LevelFor(
                    state.ConsecutiveClean,
                    _cleanRunsThreshold),
                unsupervisedExecutionEnabled);
    }

    private static bool TryReadEvidenceTime(string? value, out DateTimeOffset timestamp)
        => DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out timestamp);

    public bool MayRunUnsupervised(
        AutonomyEvidenceScope scope,
        string pharmacyId,
        bool unsupervisedExecutionEnabled)
        => MayRunUnsupervised(scope.ScopeDigest, pharmacyId, unsupervisedExecutionEnabled);
}
