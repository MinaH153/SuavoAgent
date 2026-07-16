using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Maintenance;
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
    private readonly PricingObservationContract _observationContract;
    private readonly PricingCostBasisAuthority _authority;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;
    // M1 savings enrichment (optional). When present, each FOUND cheapest-cost result is enriched
    // with the pharmacy's baseline cost + dispensed quantity (by SQL or Vision) so the cloud can
    // compute a dollar savings. Null = today's cheapest-cost-only behavior (savings stays NULL).
    private readonly IPharmacyBaselineVolumeProvider? _baselineVolume;
    // M1 savings config: optional workbook column hints (the pharmacist's own current cost + volume
    // — the most honest baseline, no PMS/Vision needed) + the plausibility-guard caps. Null = no
    // savings enrichment (today's cheapest-cost-only run).
    private readonly PricingSavingsOptions? _savings;

    private static readonly TimeSpan InterLookupDelay = TimeSpan.FromMilliseconds(20);

    public SqlPricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ISupplierPriceLookup lookup,
        ILogger<SqlPricingJobRunner> logger,
        PricingObservationContract observationContract,
        PricingCostBasisAuthority authority,
        IPharmacyBaselineVolumeProvider? baselineVolume = null,
        PricingSavingsOptions? savings = null,
        TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? trustedApprovalKeys = null)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _lookup = lookup;
        _logger = logger;
        _observationContract = observationContract ?? throw new ArgumentNullException(nameof(observationContract));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _clock = clock ?? TimeProvider.System;
        _trustedApprovalKeys = trustedApprovalKeys ??
            RemoteCommandTrust.CreateProductionKeyRegistry();
        _baselineVolume = baselineVolume;
        _savings = savings;
    }

    public async Task<PricingJobProgress> RunAsync(PricingJobSpec spec, CancellationToken ct)
    {
        if (!_db.TryAdmitPricingCloudAuthority(
                _clock.GetUtcNow(),
                out var initialAuthorityCode))
        {
            _logger.LogWarning(
                "core.sql_pricing.cloud_authority_paused code={Code}",
                initialAuthorityCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Halted, 0, 0, 0);
            return new PricingJobProgress(
                spec.JobId,
                0,
                0,
                0,
                PricingJobStatus.Halted,
                HaltReason: initialAuthorityCode);
        }
        if (!PricingObservationPolicy.TryMatchJobAuthority(
                spec, _authority, out var commandAuthorityCode))
        {
            _db.UpsertPricingJob(spec, PricingJobStatus.Halted, 0, 0, 0);
            return new PricingJobProgress(
                spec.JobId, 0, 0, 0, PricingJobStatus.Halted,
                HaltReason: commandAuthorityCode);
        }

        if (!PricingWorkbookContentPolicy.TryPrepareForExecution(
                spec.ExcelPath, out var preparedWorkbook, out var validationCode))
        {
            _logger.LogWarning(
                "core.sql_pricing.workbook_validation_failed code={Code}",
                validationCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(
                spec.JobId, 0, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_workbook_validation_failed");
        }
        using var executionWorkbook = preparedWorkbook!;
        var executionPath = executionWorkbook.WorkbookPath;

        var readResult = _reader.Read(
            executionPath, spec.NdcColumn, _savings?.BaselineColumnHint, _savings?.QuantityColumnHint);
        if (!readResult.Success)
        {
            _logger.LogError("core.sql_pricing.excel_read_failed");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed);
        }

        // PHI-safe column discovery: surface the workbook's column shape (headers + numeric ranges,
        // never text values) so the baseline-cost + quantity columns can be identified from telemetry
        // and wired via the cloud config-override — the path to remote M1 go-live without shipping the
        // workbook off the box.
        var workbookColumns = PricingWorkbookInspector.Describe(executionPath);
        _logger.LogInformation(
            "core.sql_pricing.workbook_shape columns={Columns}",
            workbookColumns.Count);

        var rows = readResult.Rows;
        var totalItems = rows.Count + readResult.Invalid.Count;
        if (!PricingResultPayloadBudget.CanAdmitWorkload(rows.Count, totalItems))
        {
            _logger.LogWarning(
                "core.sql_pricing.result_payload_preflight_failed rows={Rows} total={Total}",
                rows.Count,
                totalItems);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_result_payload_too_large");
        }

        if (!PricingRunIntegrity.TryCreateManifest(readResult, out var manifest))
        {
            _logger.LogWarning("core.sql_pricing.input_manifest_invalid");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_input_manifest_invalid");
        }

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, 0, 0);
        if (!_db.TryBindPricingInputIdentity(
                spec.JobId,
                executionWorkbook.SourceSha256,
                manifest.RowFingerprint,
                _observationContract,
                _authority,
                _clock.GetUtcNow(),
                out var identityCode))
        {
            _logger.LogWarning(
                "core.sql_pricing.input_identity_rejected code={Code}", identityCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: identityCode);
        }

        if (!TryAdmitJobAuthority(spec, out var boundAuthorityCode))
        {
            _logger.LogWarning(
                "core.sql_pricing.job_authority_paused code={Code}",
                boundAuthorityCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Halted, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Halted,
                HaltReason: boundAuthorityCode);
        }

        var previousResults = _db.GetPricingResults(spec.JobId);
        if (!PricingRunIntegrity.TryValidatePersistedResults(
                spec.JobId, manifest, previousResults, out var resumeCode))
        {
            _logger.LogWarning(
                "core.sql_pricing.resume_integrity_rejected code={Code}", resumeCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_resume_integrity_failed");
        }
        var alreadyDone = previousResults.Select(r => r.RowIndex).ToHashSet();
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();
        int completed = previousResults.Count(r => r.Found);
        int failed_ = previousResults.Count(r => !r.Found);

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed_);
        _logger.LogInformation(
            "core.sql_pricing.run_started total={Total} invalid={Invalid} pending={Pending}",
            totalItems,
            readResult.Invalid.Count,
            pending.Count);

        var integrityFailed = false;
        string? authorityHaltReason = null;
        if (readResult.Invalid.Count > 0)
        {
            foreach (var i in readResult.Invalid)
            {
                if (alreadyDone.Contains(i.RowIndex)) continue;
                if (!TryAdmitJobAuthority(spec, out var invalidAuthorityCode))
                {
                    authorityHaltReason = invalidAuthorityCode;
                    break;
                }
                var failed = PricingResultContentPolicy.InvalidNdcRow(
                    spec.JobId, i.RowIndex);
                _db.SavePricingResult(failed);
                failed_++;
            }
        }

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryAdmitJobAuthority(
                    spec,
                    out var beforeLookupAuthorityCode))
            {
                authorityHaltReason = beforeLookupAuthorityCode;
                _logger.LogWarning(
                    "core.sql_pricing.job_authority_paused code={Code}",
                    authorityHaltReason);
                break;
            }

            var result = await _lookup.FindCheapestSupplierAsync(
                spec.JobId, row.RowIndex, row.NdcNormalized, ct);

            if (!TryAdmitJobAuthority(
                    spec,
                    out var afterLookupAuthorityCode))
            {
                authorityHaltReason = afterLookupAuthorityCode;
                _logger.LogWarning(
                    "core.sql_pricing.job_authority_paused code={Code}",
                    authorityHaltReason);
                break;
            }

            if (!PricingRunIntegrity.TryValidateLookupResult(
                    spec.JobId, row, result, out var lookupIntegrityCode))
            {
                integrityFailed = true;
                _logger.LogCritical(
                    "core.sql_pricing.result_integrity_failed code={Code}",
                    lookupIntegrityCode);
                break;
            }

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
                        _logger.LogSafeWarning(ex);
                    }
                }

                if (!TryAdmitJobAuthority(
                        spec,
                        out var afterBaselineAuthorityCode))
                {
                    authorityHaltReason = afterBaselineAuthorityCode;
                    _logger.LogWarning(
                        "core.sql_pricing.job_authority_paused code={Code}",
                        authorityHaltReason);
                    break;
                }

                // Plausibility guard: drop impossible baseline/quantity (data/column/unit error) so
                // no garbage savings can form; flag a merely-very-large savings for human review.
                if (_savings is { } sv && (baseline is not null || quantity is not null))
                {
                    var guard = SavingsPlausibilityGuard.Evaluate(
                        baseline, result.CostPerUnit, quantity,
                        sv.MaxUnitCost, sv.MaxQuantity, sv.SuspiciousSavingsFraction);
                    if (guard.RejectReason is not null)
                        _logger.LogWarning("core.sql_pricing.savings_rejected");
                    if (guard.ReviewFlag is not null)
                        _logger.LogWarning("core.sql_pricing.savings_review_required");
                    baseline = guard.Baseline;
                    quantity = guard.Quantity;
                }

                if (baseline is not null || quantity is not null)
                    result = result with { BaselineCostPerUnit = baseline, Quantity = quantity };
            }

            if (!PricingRunIntegrity.TryValidateLookupResult(
                    spec.JobId, row, result, out var enrichedIntegrityCode))
            {
                integrityFailed = true;
                _logger.LogCritical(
                    "core.sql_pricing.enriched_result_integrity_failed code={Code}",
                    enrichedIntegrityCode);
                break;
            }

            _db.SavePricingResult(result);

            if (!TryAdmitJobAuthority(
                    spec,
                    out var afterPersistAuthorityCode))
            {
                authorityHaltReason = afterPersistAuthorityCode;
                _logger.LogWarning(
                    "core.sql_pricing.job_authority_paused code={Code}",
                    authorityHaltReason);
                break;
            }

            if (result.Found) completed++;
            else failed_++;

            _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed_);

            if (InterLookupDelay > TimeSpan.Zero)
                await Task.Delay(InterLookupDelay, ct);
        }

        if (authorityHaltReason is not null)
        {
            _db.UpsertPricingJob(
                spec,
                PricingJobStatus.Halted,
                totalItems,
                completed,
                failed_);
            return new PricingJobProgress(
                spec.JobId,
                totalItems,
                completed,
                failed_,
                PricingJobStatus.Halted,
                HaltReason: authorityHaltReason);
        }

        if (integrityFailed)
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Failed, totalItems, completed, failed_);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed_,
                PricingJobStatus.Failed,
                HaltReason: "pricing_result_integrity_failed");
        }

        var allResults = _db.GetPricingResults(spec.JobId);
        if (!TryAdmitJobAuthority(spec, out var beforeWriteAuthorityCode))
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Halted, totalItems, completed, failed_);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed_,
                PricingJobStatus.Halted,
                HaltReason: beforeWriteAuthorityCode);
        }
        if (!PricingRunIntegrity.TryValidatePersistedResults(
                spec.JobId, manifest, allResults, out var terminalIntegrityCode))
        {
            _logger.LogCritical(
                "core.sql_pricing.terminal_integrity_failed code={Code}",
                terminalIntegrityCode);
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Failed, totalItems, completed, failed_);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed_,
                PricingJobStatus.Failed,
                HaltReason: "pricing_terminal_integrity_failed");
        }
        var write = _writer.WriteAuthorized(
            executionPath,
            allResults,
            publish => PublishUnderJobAuthority(spec, publish),
            spec.SupplierColumn,
            spec.CostColumn,
            headerRow: readResult.HeaderRowIndex,
            siblingPathAnchor: spec.ExcelPath);

        if (write.PublicationWasDenied)
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Halted, totalItems, completed, failed_);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed_,
                PricingJobStatus.Halted,
                HaltReason: write.Error);
        }

        if (!TryAdmitJobAuthority(spec, out var afterWriteAuthorityCode))
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Halted, totalItems, completed, failed_);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed_,
                PricingJobStatus.Halted,
                HaltReason: afterWriteAuthorityCode);
        }

        // The sibling workbook is a review artifact, not proof that every lookup succeeded.
        // Preserve it on row failures while keeping the terminal result fail-closed.
        var cleanCompletion = PricingRunIntegrity.IsTerminallyComplete(
            spec.JobId, manifest, allResults, write, completed, failed_);
        var finalStatus = cleanCompletion ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        var terminalReason = cleanCompletion ? null : "pricing_job_failed";
        _db.UpsertPricingJob(spec, finalStatus, totalItems, completed, failed_);

        _logger.LogInformation(
            "core.sql_pricing.run_finished status={Status} completed={Completed} total={Total} failed={Failed}",
            finalStatus,
            completed,
            totalItems,
            failed_);

        return new PricingJobProgress(
            spec.JobId, totalItems, completed, failed_, finalStatus,
            HaltReason: terminalReason);
    }

    private bool TryAdmitJobAuthority(PricingJobSpec spec, out string code) =>
        _db.TryAdmitPricingJobAuthority(
            spec.JobId,
            spec.ApprovalId,
            spec.GrantDigest,
            _clock.GetUtcNow(),
            _trustedApprovalKeys,
            out code);

    private PricingPublicationDecision PublishUnderJobAuthority(
        PricingJobSpec spec,
        Action publish)
    {
        var published = _db.TryPublishPricingArtifact(
            spec.JobId,
            spec.ApprovalId,
            spec.GrantDigest,
            _clock,
            _trustedApprovalKeys,
            publish,
            out var code);
        return new PricingPublicationDecision(published, code);
    }
}
