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

    // Timeout per NDC lookup — UIA navigation can be slow
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);
    // Back-compat default if the caller doesn't specify a throttle (UIA wiring picks a slower value).
    private static readonly TimeSpan DefaultInterLookupDelay = TimeSpan.FromMilliseconds(500);
    // Hard upper bound — anything above this is almost certainly a misconfiguration that would stall jobs.
    private static readonly TimeSpan MaxInterLookupDelay = TimeSpan.FromMilliseconds(30000);

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
        IpcCommandClient commandClient,
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
        bool haltedByBrain = false;
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

            var result = await LookupNdcAsync(spec.JobId, row, commandClient, activePatches, ct);
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
                    haltedByBrain = true;
                    haltReason = brainDecision.Reason;
                    _logger.LogWarning(
                        "PricingJobRunner: brain halted job {JobId} after row {Row} — tier={Tier} reason=\"{Reason}\"",
                        spec.JobId, row.RowIndex, brainDecision.Tier, brainDecision.Reason);
                    break;
                }
            }

            await Task.Delay(_interLookupDelay, ct);
        }

        if (haltedByBrain)
        {
            // Skip the Excel writeback — the job stopped mid-stream, and a
            // partial writeback would misrepresent a resumable halt as final.
            _db.UpsertPricingJob(spec, PricingJobStatus.Halted, totalItems, completed, failed);
            _logger.LogInformation(
                "PricingJobRunner: job {JobId} Halted — {Completed}/{Total} found, {Failed} failed, reason=\"{Reason}\"",
                spec.JobId, completed, totalItems, failed, haltReason);
            return new PricingJobProgress(spec.JobId, totalItems, completed, failed, PricingJobStatus.Halted);
        }

        // Write all results (including previously completed rows) to a SIBLING file by default.
        // This avoids the Codex-flagged "file locked by Excel.exe" failure mode where 499 rows
        // succeed and the final File.Move throws an IOException.
        var allResults = _db.GetPricingResults(spec.JobId);
        var write = _writer.Write(spec.ExcelPath, allResults, spec.SupplierColumn, spec.CostColumn);

        var finalStatus = write.Success ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        _db.UpsertPricingJob(spec, finalStatus, totalItems, completed, failed);

        _logger.LogInformation(
            "PricingJobRunner: job {JobId} {Status} — {Completed}/{Total} found, {Failed} failed",
            spec.JobId, finalStatus, completed, totalItems, failed);

        return new PricingJobProgress(spec.JobId, totalItems, completed, failed, finalStatus);
    }

    private async Task<SupplierPriceResult> LookupNdcAsync(
        string jobId, NdcRow row, IpcCommandClient commandClient,
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

            if (response == null)
                return Fail(jobId, row, "No response from Helper");

            // [C-2] Reject mismatched response IDs to prevent pipe desync data corruption
            if (response.Id != request.Id)
                return Fail(jobId, row, $"Response ID mismatch: expected {request.Id}, got {response.Id}");

            if (response.Status != IpcStatus.Ok)
                return Fail(jobId, row, response.Error?.Message ?? $"Status {response.Status}");

            if (response.Data == null)
                return Fail(jobId, row, "Empty response data");

            return JsonSerializer.Deserialize<SupplierPriceResult>(response.Data.Value) ??
                   Fail(jobId, row, "Failed to deserialize result");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PricingJobRunner: lookup error for NDC {Ndc}", row.NdcNormalized);
            return Fail(jobId, row, ex.Message);
        }
    }

    private static SupplierPriceResult Fail(string jobId, NdcRow row, string error) =>
        new(jobId, row.RowIndex, row.NdcNormalized, false, null, null, error);
}
