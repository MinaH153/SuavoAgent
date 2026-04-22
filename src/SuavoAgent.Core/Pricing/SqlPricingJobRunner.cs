using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// SQL-first price-shopper orchestrator. Intended for the 500-row overnight batch described in
/// wedge-a-price-shopper-architecture-2026-04-22.md. Runs wholly inside Core (no IPC to Helper),
/// which removes Codex's pharmacist-collision concern with the UIA runner.
///
/// Flow:
///   1. Read Excel → list of (rowIndex, canonical-11 NDC) via <see cref="ExcelPricingReader"/>.
///   2. Skip rows already completed in SQLite (crash-resumable, same pattern as the UIA runner).
///   3. For each pending row, call <see cref="ISupplierPriceLookup.FindCheapestSupplierAsync"/>.
///   4. Persist each result + update job progress.
///   5. At the end, write the sibling priced.xlsx via <see cref="ExcelPricingWriter"/>.
///
/// The runner is ignorant of whether the lookup is SQL-backed, UIA-backed, or a fake — wire the
/// concrete <see cref="ISupplierPriceLookup"/> at composition time.
/// </summary>
public sealed class SqlPricingJobRunner
{
    private readonly ExcelPricingReader _reader;
    private readonly ExcelPricingWriter _writer;
    private readonly AgentStateDb _db;
    private readonly ISupplierPriceLookup _lookup;
    private readonly ILogger<SqlPricingJobRunner> _logger;

    private static readonly TimeSpan InterLookupDelay = TimeSpan.FromMilliseconds(20);

    public SqlPricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ISupplierPriceLookup lookup,
        ILogger<SqlPricingJobRunner> logger)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookup = lookup;
        _logger = logger;
    }

    public async Task<PricingJobProgress> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        var readResult = _reader.Read(spec.ExcelPath, spec.NdcColumn);
        if (!readResult.Success)
        {
            _logger.LogError("SqlPricingJobRunner: cannot read Excel — {Error}", readResult.Error);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed);
        }

        var rows = readResult.Rows;
        var alreadyDone = _db.GetCompletedPricingRows(spec.JobId);
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, rows.Count, alreadyDone.Count, 0);
        _logger.LogInformation(
            "SqlPricingJobRunner: {Total} NDCs ({Invalid} unparseable skipped), {Pending} pending, job {JobId}",
            rows.Count, readResult.Invalid.Count, pending.Count, spec.JobId);

        if (readResult.Invalid.Count > 0)
        {
            foreach (var i in readResult.Invalid)
            {
                var failed = new SupplierPriceResult(
                    spec.JobId, i.RowIndex, i.NdcRaw, false, null, null, $"Invalid NDC: {i.Reason}");
                _db.SavePricingResult(failed);
            }
        }

        int completed = alreadyDone.Count;
        int failed_ = 0;

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _lookup.FindCheapestSupplierAsync(
                spec.JobId, row.RowIndex, row.NdcNormalized, ct);
            _db.SavePricingResult(result);

            if (result.Found) completed++;
            else failed_++;

            _db.UpsertPricingJob(spec, PricingJobStatus.Running, rows.Count, completed, failed_);

            if (InterLookupDelay > TimeSpan.Zero)
                await Task.Delay(InterLookupDelay, ct);
        }

        var allResults = _db.GetPricingResults(spec.JobId);
        var write = _writer.Write(spec.ExcelPath, allResults, spec.SupplierColumn, spec.CostColumn);

        var finalStatus = write.Success ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        _db.UpsertPricingJob(spec, finalStatus, rows.Count, completed, failed_);

        _logger.LogInformation(
            "SqlPricingJobRunner: job {JobId} {Status} — {Completed}/{Total} found, {Failed} failed, output={Out}",
            spec.JobId, finalStatus, completed, rows.Count, failed_, write.OutputPath ?? "(no output)");

        return new PricingJobProgress(spec.JobId, rows.Count, completed, failed_, finalStatus);
    }
}
