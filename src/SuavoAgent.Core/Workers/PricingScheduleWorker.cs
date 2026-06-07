using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// The autonomous daily pricing loop ("the bot does it on its own" — Nadim's documented overnight
/// 500-row batch). When <see cref="PricingScheduleOptions.Enabled"/> is true and a
/// <see cref="PricingScheduleOptions.WorkbookPath"/> is set, this worker runs the configured workbook
/// through the SqlFirst pricing pipeline once per day at <see cref="PricingScheduleOptions.RunAtLocalTime"/>
/// with NO cockpit command.
///
/// <para>
/// PRECEDENCE-1 SAFETY: this worker is SqlFirst-ONLY. The injected executor is the read-only
/// <see cref="SqlFirstPricingJobExecutor"/> (reads pricing from the PioneerRx SQL backend and writes a
/// sibling Excel — it never drives the live PMS UI), AND <see cref="RunOnceAsync"/> additionally refuses
/// to run unless <see cref="AgentOptions.PricingExecutor"/> is <see cref="PricingExecutorMode.SqlFirst"/>.
/// Autonomous keystroke-driving of a live PMS (UiaFirst) is never started on a timer — that path stays
/// operator-triggered. See feedback-never-blind-run-agent-automation-on-live-pms.
/// </para>
///
/// <para>
/// Gates (all required to fire a run; each returns a named <see cref="PricingScheduleRunOutcome"/> reason
/// so a skipped run is never silent): schedule enabled, executor is SqlFirst, an executor is wired, a
/// workbook path is configured, and the workbook file exists. A single-flight gate prevents a scheduled
/// run from overlapping itself; overlap with a manual cockpit run is benign (isolated job ids, sibling
/// output files).
/// </para>
/// </summary>
public sealed class PricingScheduleWorker : BackgroundService, IDisposable
{
    private readonly ILogger<PricingScheduleWorker> _logger;
    private readonly IOptionsMonitor<AgentOptions> _options;
    private readonly IPricingJobExecutor? _executor;
    private readonly PricingJobCloudUploader? _uploader;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>While disabled or misconfigured, re-check config this often so a config-sync flip
    /// (enable / fix the time) takes effect without a service restart.</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromMinutes(5);

    public PricingScheduleWorker(
        ILogger<PricingScheduleWorker> logger,
        IOptionsMonitor<AgentOptions> options,
        IPricingJobExecutor? executor = null,
        PricingJobCloudUploader? uploader = null,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _options = options;
        _executor = executor;
        _uploader = uploader;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("PricingScheduleWorker started — waiting for the autonomous pricing schedule");

        while (!stoppingToken.IsCancellationRequested)
        {
            var sched = _options.CurrentValue.PricingSchedule;

            // Cheap idle gate: don't compute a next-run we won't honor. Poll for a config-sync flip.
            if (!sched.Enabled)
            {
                if (!await DelaySafe(IdlePoll, stoppingToken)) break;
                continue;
            }

            if (!PricingScheduleCalculator.TryParseRunAt(sched.RunAtLocalTime, out var runAt))
            {
                _logger.LogWarning(
                    "PricingScheduleWorker: invalid RunAtLocalTime '{Value}' (expected 24h HH:mm) — staying idle until corrected",
                    sched.RunAtLocalTime);
                if (!await DelaySafe(IdlePoll, stoppingToken)) break;
                continue;
            }

            var now = _clock.GetUtcNow();
            var next = PricingScheduleCalculator.NextRun(now, runAt, _clock.LocalTimeZone);
            var wait = next - now;
            _logger.LogInformation(
                "PricingScheduleWorker: next autonomous pricing run at {Next:yyyy-MM-dd HH:mm zzz} (in {Hours:F1}h)",
                next, wait.TotalHours);

            if (!await DelaySafe(wait, stoppingToken)) break;

            try
            {
                var outcome = await RunOnceAsync(stoppingToken);
                // A scheduled fire that skips is NOT silent — config can change during the wait (the flag
                // flipped off, the executor switched to UiaFirst, the workbook moved). Name the reason so
                // "the overnight run didn't happen" is always explainable from the logs.
                if (outcome.Status == PricingScheduleRunStatus.Skipped)
                    _logger.LogWarning(
                        "PricingScheduleWorker: scheduled run at {Next:yyyy-MM-dd HH:mm zzz} skipped — {Reason}",
                        next, outcome.Reason);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single failed run must not kill the daily loop; the next day's run still fires.
                _logger.LogWarning(ex, "PricingScheduleWorker: run tick error — continuing");
            }
        }

        _logger.LogInformation("PricingScheduleWorker stopped");
    }

    /// <summary>
    /// Evaluate the gates and, if all pass, run one autonomous SqlFirst pricing job on the configured
    /// workbook. Public for testability — production lets <see cref="ExecuteAsync"/> drive the cadence.
    /// Never throws for a gate miss; returns a named <see cref="PricingScheduleRunOutcome"/> instead.
    /// </summary>
    public async Task<PricingScheduleRunOutcome> RunOnceAsync(CancellationToken ct)
    {
        var opts = _options.CurrentValue;
        var sched = opts.PricingSchedule;

        if (!sched.Enabled)
            return PricingScheduleRunOutcome.Skipped("disabled");

        // PRECEDENCE-1: never autonomously drive the live PMS UI. Only the read-only SQL path runs on a
        // timer; if the operator has chosen UiaFirst, the scheduler stays out of the way.
        if (opts.PricingExecutor != PricingExecutorMode.SqlFirst)
            return PricingScheduleRunOutcome.Skipped("not_sqlfirst");

        if (_executor is null)
            return PricingScheduleRunOutcome.Skipped("no_executor");

        if (string.IsNullOrWhiteSpace(sched.WorkbookPath))
            return PricingScheduleRunOutcome.Skipped("no_workbook");

        var fullPath = Path.GetFullPath(sched.WorkbookPath.Trim());
        if (!File.Exists(fullPath))
        {
            _logger.LogWarning(
                "PricingScheduleWorker: configured workbook does not exist — skipping run ({Path})", fullPath);
            return PricingScheduleRunOutcome.Skipped("workbook_missing");
        }

        // Single-flight across scheduled runs: never let two timer-driven runs overlap (a fresh fire
        // while a slow run is still going just skips). This gate does NOT cover a concurrent manual
        // cockpit run — that path has its own gate in HeartbeatWorker. Cross-trigger overlap on the
        // SAME workbook is safe only because ExcelPricingWriter sibling output is timestamped to the
        // second ({stem}-priced-yyyyMMdd-HHmmss); a true sub-second collision on the identical file is a
        // pre-existing writer property, not introduced here, and astronomically unlikely for a 02:00 batch.
        if (!await _gate.WaitAsync(0, ct))
            return PricingScheduleRunOutcome.Skipped("already_running");

        try
        {
            var spec = new PricingJobSpec(
                Guid.NewGuid().ToString("N"),
                fullPath,
                sched.NdcColumn,
                sched.SupplierColumn,
                sched.CostColumn);

            _logger.LogInformation(
                "PricingScheduleWorker: starting autonomous SqlFirst pricing run {JobId} on {Path}",
                spec.JobId, fullPath);

            var result = await _executor.RunAsync(spec, ct);

            // Surface the autonomous run in the cloud cockpit — the savings ledger AND the downloadable
            // price list ("Files I made for you") — exactly like a cockpit-triggered run. commandId is
            // null because no operator command exists. Best-effort: the priced workbook is already
            // written and UploadAsync swallows its own (non-cancellation) errors, so a cloud hiccup never
            // fails the local run. Mirrors HeartbeatWorker's post-run upload.
            if (_uploader is not null)
                await _uploader.UploadAsync(spec, result, commandId: null, ct);

            if (result.Ok)
            {
                _logger.LogInformation(
                    "PricingScheduleWorker: run {JobId} completed — {Completed}/{Total} priced, {Failed} failed",
                    spec.JobId, result.Progress.CompletedItems, result.Progress.TotalItems, result.Progress.FailedItems);
                return new PricingScheduleRunOutcome(PricingScheduleRunStatus.Completed, "ok", result.Progress);
            }

            _logger.LogWarning(
                "PricingScheduleWorker: run {JobId} ended without success — {Error}", spec.JobId, result.Error);
            return new PricingScheduleRunOutcome(
                PricingScheduleRunStatus.Failed, result.Error ?? "pricing run failed", result.Progress);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> DelaySafe(TimeSpan delay, CancellationToken ct)
    {
        if (delay < TimeSpan.Zero) delay = TimeSpan.Zero;
        try
        {
            await Task.Delay(delay, _clock, ct);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public override void Dispose()
    {
        _gate.Dispose();
        base.Dispose();
    }
}

/// <summary>Terminal status of one <see cref="PricingScheduleWorker.RunOnceAsync"/> attempt.</summary>
public enum PricingScheduleRunStatus
{
    /// <summary>A gate was not satisfied — see <see cref="PricingScheduleRunOutcome.Reason"/>.</summary>
    Skipped,

    /// <summary>The pricing job ran and reported success.</summary>
    Completed,

    /// <summary>The pricing job ran but reported failure (e.g. SQL unavailable).</summary>
    Failed,
}

/// <summary>
/// Outcome of one autonomous pricing-schedule run attempt. <see cref="Reason"/> is a stable machine
/// code ("disabled", "not_sqlfirst", "no_executor", "no_workbook", "workbook_missing", "already_running",
/// "ok", or an executor error) so a skip is observable rather than silent.
/// </summary>
public sealed record PricingScheduleRunOutcome(
    PricingScheduleRunStatus Status,
    string Reason,
    PricingJobProgress? Progress = null)
{
    public static PricingScheduleRunOutcome Skipped(string reason) =>
        new(PricingScheduleRunStatus.Skipped, reason);
}
