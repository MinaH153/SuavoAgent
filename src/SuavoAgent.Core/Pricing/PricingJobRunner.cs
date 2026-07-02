using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Orchestrates a full pricing job:
///   1. Read NDCs from Excel
///   2. For each NDC: send IpcCommandClient → Helper → PricingWorkflow → read grid
///   3. Persist each result to SQLite (crash-resumable)
///   4. Write results back to Excel when done
/// </summary>
public sealed class PricingJobRunner
{
    private readonly ExcelPricingReader _reader;
    private readonly ExcelPricingWriter _writer;
    private readonly AgentStateDb _db;
    private readonly ILogger<PricingJobRunner> _logger;
    private readonly PricingBrainEvaluator? _brainEvaluator;
    private readonly TimeSpan _interLookupDelay;

    // Timeout per NDC lookup. MUST exceed the Helper PricingWorkflow's worst-case internal UIA budget
    // (its sequential step timeouts sum to ~42s: WaitForWindow 8 + SearchByNdc 8 + VerifyLoadedNdc 8 +
    // ClickPricingTab 8 + grid-wait 5 + WaitForStableRows 5) PLUS the actual click/type/read work — else
    // Core tears down the pipe on a slow-but-WORKING lookup, miscounts it HelperUnreachable, and aborts
    // the whole job after 3 (QA wave-1 C1). 90s gives generous headroom and still sits far below the
    // Helper's 5-min dispatch-wedge ceiling, so a genuinely hung lookup is still caught (self-amputation).
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(90);
    // Back-compat default if the caller doesn't specify a throttle (UIA wiring picks a slower value).
    private static readonly TimeSpan DefaultInterLookupDelay = TimeSpan.FromMilliseconds(500);
    // Hard upper bound — anything above this is almost certainly a misconfiguration that would stall jobs.
    private static readonly TimeSpan MaxInterLookupDelay = TimeSpan.FromMilliseconds(30000);

    // B1: after this many CONSECUTIVE IPC lookups that return no response at all (Helper hung /
    // disconnected), abort the job early. Without this a dead Helper makes every NDC fail silently —
    // the loop grinds the whole workbook (each row eats a ~2s reconnect + up-to-30s timeout), marks
    // everything "failed", and reports a finished job, masking "Helper IPC is down" as "nothing priced".
    internal const int MaxConsecutiveIpcFailuresBeforeAbort = 3;

    // QA I2: after this many CONSECUTIVE lookups where the Helper RESPONDED but PioneerRx isn't attached
    // (its main window is unavailable — e.g. PMS closed/restarted), HALT the job instead of grinding the
    // whole workbook into all-error rows and reporting Completed. A green "done" file that priced nothing
    // is worse than a clear "PioneerRx not open" halt. Like the IPC abort, these rows stay resumable.
    internal const int MaxConsecutivePmsUnavailableBeforeHalt = 3;

    // The Helper's signal (PricingWorkflow.Lookup) that PioneerRx is not attached for a lookup.
    private const string PmsUnavailableMarker = "main window not available";

    // Result of one NDC lookup. HelperUnreachable = the Helper returned NO response at all (timeout /
    // reconnect failure / pipe error) — an infrastructure failure, distinct from a Helper that
    // responded with "not found". Drives the early-abort + keeps the row unpersisted (resumable).
    private readonly record struct LookupOutcome(SupplierPriceResult Result, bool HelperUnreachable);

    public PricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ILogger<PricingJobRunner> logger,
        PricingBrainEvaluator? brainEvaluator = null,
        TimeSpan? interLookupDelay = null)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _logger = logger;
        _brainEvaluator = brainEvaluator;

        // Clamp the throttle. Negative inputs collapse to zero; absurd values are capped so a typo
        // in appsettings can't silently turn a 12-minute job into a multi-hour stall.
        var requested = interLookupDelay ?? DefaultInterLookupDelay;
        if (requested < TimeSpan.Zero) requested = TimeSpan.Zero;
        if (requested > MaxInterLookupDelay) requested = MaxInterLookupDelay;
        _interLookupDelay = requested;
    }

    /// <summary>
    /// Runs the full pricing job. If the job was previously interrupted,
    /// skips rows that already have results in SQLite (crash-resumable).
    /// </summary>
    public async Task<PricingJobProgress> RunAsync(
        PricingJobSpec spec,
        IIpcCommandClient commandClient,
        CancellationToken ct)
    {
        var readResult = _reader.Read(spec.ExcelPath, spec.NdcColumn);
        if (!readResult.Success)
        {
            _logger.LogError("PricingJobRunner: cannot read Excel — {Error}", readResult.Error);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed);
        }

        var rows = readResult.Rows;
        var totalItems = rows.Count + readResult.Invalid.Count;
        var previousResults = _db.GetPricingResults(spec.JobId);
        var alreadyDone = previousResults.Select(r => r.RowIndex).ToHashSet();
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();
        int completed = previousResults.Count(r => r.Found);
        int failed = previousResults.Count(r => !r.Found);

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed);
        _logger.LogInformation("PricingJobRunner: {Total} NDCs ({Invalid} unparseable skipped), {Pending} pending, job {JobId}",
            totalItems, readResult.Invalid.Count, pending.Count, spec.JobId);

        // M2b: load the job's active learned selector patches once and hand them to the Helper
        // with each lookup. Empty (the case until M2c distributes one) = builtin-only behavior.
        var activePatches = _db.GetActiveSelectorPatches();
        if (activePatches.Count > 0)
            _logger.LogInformation("PricingJobRunner: {Count} active selector patch(es) in effect for job {JobId}",
                activePatches.Count, spec.JobId);

        int consecutiveFailures = 0;
        int consecutiveIpcFailures = 0; // B1: only no-response-at-all lookups (Helper hung/disconnected)
        int consecutivePmsUnavailable = 0; // QA I2: Helper responded but PioneerRx not attached
        bool halted = false;
        string? haltReason = null;

        if (readResult.Invalid.Count > 0)
        {
            foreach (var i in readResult.Invalid)
            {
                if (alreadyDone.Contains(i.RowIndex)) continue;
                _db.SavePricingResult(new SupplierPriceResult(
                    spec.JobId, i.RowIndex, i.NdcRaw, false, null, null, $"Invalid NDC: {i.Reason}"));
                failed++;
            }
        }

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            var lookup = await LookupNdcAsync(spec.JobId, row, commandClient, activePatches, ct);

            if (lookup.HelperUnreachable)
            {
                // B1: no response at all → Helper hung/disconnected. Do NOT persist this as a pricing
                // result — a saved Fail would exclude the row from `pending` on resume, so it would
                // never get priced after the Helper recovers. Leave it pending, and abort the whole
                // job early once we're confident the Helper is gone instead of grinding the workbook.
                consecutiveIpcFailures++;
                if (consecutiveIpcFailures >= MaxConsecutiveIpcFailuresBeforeAbort)
                {
                    halted = true;
                    haltReason = "helper_unreachable"; // stable code for the cockpit; the count detail is in the CRITICAL log below
                    _logger.LogCritical(
                        "PricingJobRunner: job {JobId} ABORTED — Helper unreachable for {N} consecutive lookups. " +
                        "Stopped early ({Remaining} NDCs left unpriced + resumable) instead of marking the workbook failed.",
                        spec.JobId, consecutiveIpcFailures, totalItems - completed - failed);
                    break;
                }
                await Task.Delay(_interLookupDelay, ct); // brief pause — the Helper may be mid-restart
                continue;
            }

            consecutiveIpcFailures = 0; // the Helper responded → it's alive
            var result = lookup.Result;

            // QA I2: the Helper responded but PioneerRx isn't attached (main window unavailable — PMS
            // closed/restarted). Like a HelperUnreachable, don't persist this (a saved Fail would exclude
            // the row on resume) — leave it pending and HALT after N consecutive, instead of grinding the
            // workbook into all-error rows and reporting a green "Completed" that priced nothing.
            if (IsPmsUnavailable(result))
            {
                consecutivePmsUnavailable++;
                if (consecutivePmsUnavailable >= MaxConsecutivePmsUnavailableBeforeHalt)
                {
                    halted = true;
                    haltReason = "pioneerrx_not_attached";
                    _logger.LogCritical(
                        "PricingJobRunner: job {JobId} HALTED — PioneerRx not attached for {N} consecutive lookups " +
                        "({Remaining} NDCs left unpriced + resumable). Open PioneerRx and re-run the job.",
                        spec.JobId, consecutivePmsUnavailable, totalItems - completed - failed);
                    break;
                }
                await Task.Delay(_interLookupDelay, ct);
                continue;
            }
            consecutivePmsUnavailable = 0;

            _db.SavePricingResult(result);

            if (result.Found)
            {
                completed++;
                consecutiveFailures = 0;
            }
            else
            {
                failed++;
                consecutiveFailures++;
            }

            _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed);

            _logger.LogDebug("PricingJobRunner: row {Row} NDC {Ndc} → {Supplier} @ {Cost}",
                row.RowIndex, row.NdcNormalized, result.SupplierName ?? "N/A", result.CostPerUnit?.ToString("F4") ?? "N/A");

            if (_brainEvaluator != null)
            {
                var stats = new PricingRunStats
                {
                    TotalItems = totalItems,
                    CompletedItems = completed,
                    FailedItems = failed,
                    ConsecutiveFailures = consecutiveFailures,
                };
                var brainDecision = await _brainEvaluator.EvaluateAsync(row, result, stats, ct);
                if (brainDecision.ShouldHalt)
                {
                    halted = true;
                    haltReason = brainDecision.Reason;
                    _logger.LogWarning(
                        "PricingJobRunner: brain halted job {JobId} after row {Row} — tier={Tier} reason=\"{Reason}\"",
                        spec.JobId, row.RowIndex, brainDecision.Tier, brainDecision.Reason);
                    break;
                }
            }

            await Task.Delay(_interLookupDelay, ct);
        }

        if (halted)
        {
            // Skip the Excel writeback — the job stopped mid-stream, and a
            // partial writeback would misrepresent a resumable halt as final.
            _db.UpsertPricingJob(spec, PricingJobStatus.Halted, totalItems, completed, failed);
            _logger.LogInformation(
                "PricingJobRunner: job {JobId} Halted — {Completed}/{Total} found, {Failed} failed, reason=\"{Reason}\"",
                spec.JobId, completed, totalItems, failed, haltReason);
            return new PricingJobProgress(spec.JobId, totalItems, completed, failed, PricingJobStatus.Halted, HaltReason: haltReason);
        }

        // Write all results (including previously completed rows) to a SIBLING file by default.
        // This avoids the Codex-flagged "file locked by Excel.exe" failure mode where 499 rows
        // succeed and the final File.Move throws an IOException.
        var allResults = _db.GetPricingResults(spec.JobId);
        var write = _writer.Write(spec.ExcelPath, allResults, spec.SupplierColumn, spec.CostColumn,
            headerRow: readResult.HeaderRowIndex);

        var finalStatus = write.Success ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        _db.UpsertPricingJob(spec, finalStatus, totalItems, completed, failed);

        _logger.LogInformation(
            "PricingJobRunner: job {JobId} {Status} — {Completed}/{Total} found, {Failed} failed",
            spec.JobId, finalStatus, completed, totalItems, failed);

        return new PricingJobProgress(spec.JobId, totalItems, completed, failed, finalStatus);
    }

    private async Task<LookupOutcome> LookupNdcAsync(
        string jobId, NdcRow row, IIpcCommandClient commandClient,
        IReadOnlyList<SelectorPatch> patches, CancellationToken ct)
    {
        try
        {
            var request = new IpcRequest(
                Id: Guid.NewGuid().ToString("N"),
                Command: IpcCommands.PricingLookup,
                Version: 1,
                Data: JsonSerializer.SerializeToElement(
                    new NdcPricingRequest(jobId, row.RowIndex, row.NdcNormalized, patches)));

            var response = await commandClient.SendAsync(request, LookupTimeout, ct);

            // No response at all = Helper hung/disconnected (infrastructure failure), NOT a price miss.
            if (response == null)
                return new LookupOutcome(Fail(jobId, row, "No response from Helper"), HelperUnreachable: true);

            // From here the Helper responded — it's alive. Any failure below is a per-row/data problem,
            // so HelperUnreachable stays false and the run continues normally.

            // [C-2] Reject mismatched response IDs to prevent pipe desync data corruption
            if (response.Id != request.Id)
                return new LookupOutcome(Fail(jobId, row, $"Response ID mismatch: expected {request.Id}, got {response.Id}"), false);

            if (response.Status != IpcStatus.Ok)
                return new LookupOutcome(Fail(jobId, row, response.Error?.Message ?? $"Status {response.Status}"), false);

            if (response.Data == null)
                return new LookupOutcome(Fail(jobId, row, "Empty response data"), false);

            var parsed = JsonSerializer.Deserialize<SupplierPriceResult>(response.Data.Value)
                         ?? Fail(jobId, row, "Failed to deserialize result");
            return new LookupOutcome(parsed, false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PricingJobRunner: lookup error for NDC {Ndc}", row.NdcNormalized);
            return new LookupOutcome(Fail(jobId, row, ex.Message), false);
        }
    }

    private static SupplierPriceResult Fail(string jobId, NdcRow row, string error) =>
        new(jobId, row.RowIndex, row.NdcNormalized, false, null, null, error);

    // QA I2: a not-found result whose error is the Helper's "PioneerRx not attached" signal.
    internal static bool IsPmsUnavailable(SupplierPriceResult result) =>
        !result.Found
        && result.ErrorMessage is { } msg
        && msg.Contains(PmsUnavailableMarker, StringComparison.OrdinalIgnoreCase);
}
