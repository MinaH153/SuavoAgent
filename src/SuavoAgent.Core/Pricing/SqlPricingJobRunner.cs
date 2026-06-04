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
    // M1 savings enrichment (optional). When present, each FOUND cheapest-cost result is enriched
    // with the pharmacy's baseline cost + dispensed quantity (by SQL or Vision) so the cloud can
    // compute a dollar savings. Null = today's cheapest-cost-only behavior (savings stays NULL).
    private readonly IPharmacyBaselineVolumeProvider? _baselineVolume;
    // M1 savings: optional workbook column headers carrying the pharmacist's own current cost +
    // dispensed volume. When present they are the most honest baseline (and need no PMS/Vision);
    // the provider only fills whatever the workbook omits.
    private readonly string? _baselineCostColumnHint;
    private readonly string? _quantityColumnHint;

    private static readonly TimeSpan InterLookupDelay = TimeSpan.FromMilliseconds(20);

    public SqlPricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ISupplierPriceLookup lookup,
        ILogger<SqlPricingJobRunner> logger,
        IPharmacyBaselineVolumeProvider? baselineVolume = null,
        string? baselineCostColumnHint = null,
        string? quantityColumnHint = null)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookup = lookup;
        _logger = logger;
        _baselineVolume = baselineVolume;
        _baselineCostColumnHint = baselineCostColumnHint;
        _quantityColumnHint = quantityColumnHint;
    }

    public async Task<PricingJobProgress> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        var readResult = _reader.Read(
            spec.ExcelPath, spec.NdcColumn, _baselineCostColumnHint, _quantityColumnHint);
        if (!readResult.Success)
        {
            _logger.LogError("SqlPricingJobRunner: cannot read Excel — {Error}", readResult.Error);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed);
        }

        var rows = readResult.Rows;
        var totalItems = rows.Count + readResult.Invalid.Count;
        var previousResults = _db.GetPricingResults(spec.JobId);
        var alreadyDone = previousResults.Select(r => r.RowIndex).ToHashSet();
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();
        int completed = previousResults.Count(r => r.Found);
        int failed_ = previousResults.Count(r => !r.Found);

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed_);
        _logger.LogInformation(
            "SqlPricingJobRunner: {Total} NDCs ({Invalid} unparseable skipped), {Pending} pending, job {JobId}",
            totalItems, readResult.Invalid.Count, pending.Count, spec.JobId);

        if (readResult.Invalid.Count > 0)
        {
            foreach (var i in readResult.Invalid)
            {
                if (alreadyDone.Contains(i.RowIndex)) continue;
                var failed = new SupplierPriceResult(
                    spec.JobId, i.RowIndex, i.NdcRaw, false, null, null, $"Invalid NDC: {i.Reason}");
                _db.SavePricingResult(failed);
                failed_++;
            }
        }

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            var result = await _lookup.FindCheapestSupplierAsync(
                spec.JobId, row.RowIndex, row.NdcNormalized, ct);

            // M1 savings: enrich a found result with the pharmacy's baseline cost + dispensed
            // quantity. Precedence: the pharmacist's OWN workbook values (most honest) first, then
            // the provider (SQL volume / Vision baseline) fills only what the workbook omits.
            // Fail-soft — any provider error leaves the gap null (savings NULL, never a wrong number).
            if (result.Found)
            {
                var baseline = row.BaselineCostPerUnit;
                var quantity = row.Quantity;

                if (_baselineVolume is not null && (baseline is null || quantity is null))
                {
                    try
                    {
                        var bv = await _baselineVolume.GetAsync(row.NdcNormalized, ct);
                        baseline ??= bv.BaselineCostPerUnit;
                        quantity ??= bv.Quantity;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "SqlPricingJobRunner: baseline/volume enrichment failed for NDC {Ndc}; gap left null",
                            row.NdcNormalized);
                    }
                }

                if (baseline is not null || quantity is not null)
                    result = result with { BaselineCostPerUnit = baseline, Quantity = quantity };
            }

            _db.SavePricingResult(result);

            if (result.Found) completed++;
            else failed_++;

            _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed_);

            if (InterLookupDelay > TimeSpan.Zero)
                await Task.Delay(InterLookupDelay, ct);
        }

        var allResults = _db.GetPricingResults(spec.JobId);
        var write = _writer.Write(spec.ExcelPath, allResults, spec.SupplierColumn, spec.CostColumn);

        var finalStatus = write.Success ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        _db.UpsertPricingJob(spec, finalStatus, totalItems, completed, failed_);

        _logger.LogInformation(
            "SqlPricingJobRunner: job {JobId} {Status} — {Completed}/{Total} found, {Failed} failed",
            spec.JobId, finalStatus, completed, totalItems, failed_);

        return new PricingJobProgress(spec.JobId, totalItems, completed, failed_, finalStatus);
    }
}
