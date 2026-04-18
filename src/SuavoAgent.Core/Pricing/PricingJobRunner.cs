using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
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

    // Timeout per NDC lookup — UIA navigation can be slow
    private static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(30);
    // Delay between lookups to avoid overwhelming PioneerRx UI
    private static readonly TimeSpan InterLookupDelay = TimeSpan.FromMilliseconds(500);

    public PricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ILogger<PricingJobRunner> logger)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _logger = logger;
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
        var alreadyDone = _db.GetCompletedPricingRows(spec.JobId);
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, rows.Count, alreadyDone.Count, 0);
        _logger.LogInformation("PricingJobRunner: {Total} NDCs, {Pending} pending, job {JobId}",
            rows.Count, pending.Count, spec.JobId);

        int completed = alreadyDone.Count;
        int failed = 0;

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            var result = await LookupNdcAsync(spec.JobId, row, commandClient, ct);
            _db.SavePricingResult(result);

            if (result.Found) completed++;
            else failed++;

            _db.UpsertPricingJob(spec, PricingJobStatus.Running, rows.Count, completed, failed);

            _logger.LogDebug("PricingJobRunner: row {Row} NDC {Ndc} → {Supplier} @ {Cost}",
                row.RowIndex, row.NdcNormalized, result.SupplierName ?? "N/A", result.CostPerUnit?.ToString("F4") ?? "N/A");

            await Task.Delay(InterLookupDelay, ct);
        }

        // Write all results (including previously completed rows) back to Excel
        var allResults = _db.GetPricingResults(spec.JobId);
        var writeOk = _writer.Write(spec.ExcelPath, allResults, spec.SupplierColumn, spec.CostColumn);

        var finalStatus = writeOk ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        _db.UpsertPricingJob(spec, finalStatus, rows.Count, completed, failed);

        _logger.LogInformation("PricingJobRunner: job {JobId} {Status} — {Completed}/{Total} found, {Failed} failed",
            spec.JobId, finalStatus, completed, rows.Count, failed);

        return new PricingJobProgress(spec.JobId, rows.Count, completed, failed, finalStatus);
    }

    private async Task<SupplierPriceResult> LookupNdcAsync(
        string jobId, NdcRow row, IpcCommandClient commandClient, CancellationToken ct)
    {
        try
        {
            var request = new IpcRequest(
                Id: Guid.NewGuid().ToString("N"),
                Command: IpcCommands.PricingLookup,
                Version: 1,
                Data: JsonSerializer.SerializeToElement(new NdcPricingRequest(jobId, row.RowIndex, row.NdcNormalized)));

            var response = await commandClient.SendAsync(request, LookupTimeout, ct);

            if (response == null)
                return Fail(jobId, row, "No response from Helper");

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
