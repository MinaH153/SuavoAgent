using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Maintenance;
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
    private readonly TimeProvider _clock;
    private readonly IReadOnlyDictionary<string, string> _trustedApprovalKeys;

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
    private readonly record struct LookupOutcome(
        SupplierPriceResult Result,
        bool HelperUnreachable,
        bool IntegrityFailure);

    public PricingJobRunner(
        ExcelPricingReader reader,
        ExcelPricingWriter writer,
        AgentStateDb db,
        ILogger<PricingJobRunner> logger,
        PricingBrainEvaluator? brainEvaluator = null,
        TimeSpan? interLookupDelay = null,
        TimeProvider? clock = null,
        IReadOnlyDictionary<string, string>? trustedApprovalKeys = null)
    {
        _reader = reader;
        _writer = writer;
        _db = db;
        _logger = logger;
        _brainEvaluator = brainEvaluator;
        _clock = clock ?? TimeProvider.System;
        _trustedApprovalKeys = trustedApprovalKeys ??
            RemoteCommandTrust.CreateProductionKeyRegistry();

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
        PricingObservationContract observationContract,
        PricingCostBasisAuthority authority,
        CancellationToken ct) => await RunAsync(
            spec,
            commandClient,
            observationContract,
            authority,
            _db.GetActiveSelectorPatches().ToArray(),
            null,
            null,
            ct).ConfigureAwait(false);

    internal async Task<PricingJobProgress> RunAsync(
        PricingJobSpec spec,
        IIpcCommandClient commandClient,
        PricingObservationContract observationContract,
        PricingCostBasisAuthority authority,
        IReadOnlyList<SelectorPatch> activePatches,
        string? pmsFingerprint,
        string? screenSignature,
        CancellationToken ct)
    {
        if (!_db.TryAdmitPricingCloudAuthority(
                _clock.GetUtcNow(),
                out var initialAuthorityCode))
        {
            _logger.LogWarning(
                "core.pricing.cloud_authority_paused code={Code}",
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
                spec, authority, out var commandAuthorityCode))
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
                "core.pricing.workbook_validation_failed code={Code}",
                validationCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(
                spec.JobId, 0, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_workbook_validation_failed");
        }
        using var executionWorkbook = preparedWorkbook!;
        var executionPath = executionWorkbook.WorkbookPath;

        var readResult = _reader.Read(executionPath, spec.NdcColumn);
        if (!readResult.Success)
        {
            _logger.LogError("core.pricing.excel_read_failed");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, 0, 0, 0);
            return new PricingJobProgress(spec.JobId, 0, 0, 0, PricingJobStatus.Failed);
        }

        var rows = readResult.Rows;
        var totalItems = rows.Count + readResult.Invalid.Count;
        if (!PricingResultPayloadBudget.CanAdmitWorkload(rows.Count, totalItems))
        {
            _logger.LogWarning(
                "core.pricing.result_payload_preflight_failed rows={Rows} total={Total}",
                rows.Count,
                totalItems);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_result_payload_too_large");
        }

        if (!PricingRunIntegrity.TryCreateManifest(readResult, out var manifest))
        {
            _logger.LogWarning("core.pricing.input_manifest_invalid");
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_input_manifest_invalid");
        }

        // Bind the job before admitting any prior row state. Both the source
        // digest and ordered row/NDC fingerprint are immutable for this job_id.
        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, 0, 0);
        if (!_db.TryBindPricingInputIdentity(
                spec.JobId,
                executionWorkbook.SourceSha256,
                manifest.RowFingerprint,
                observationContract,
                authority,
                _clock.GetUtcNow(),
                out var identityCode))
        {
            _logger.LogWarning(
                "core.pricing.input_identity_rejected code={Code}", identityCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: identityCode);
        }

        if (!TryAdmitJobAuthority(spec, out var boundAuthorityCode))
        {
            _logger.LogWarning(
                "core.pricing.job_authority_paused code={Code}",
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
                "core.pricing.resume_integrity_rejected code={Code}", resumeCode);
            _db.UpsertPricingJob(spec, PricingJobStatus.Failed, totalItems, 0, 0);
            return new PricingJobProgress(
                spec.JobId, totalItems, 0, 0, PricingJobStatus.Failed,
                HaltReason: "pricing_resume_integrity_failed");
        }
        var alreadyDone = previousResults.Select(r => r.RowIndex).ToHashSet();
        var pending = rows.Where(r => !alreadyDone.Contains(r.RowIndex)).ToList();
        int completed = previousResults.Count(r => r.Found);
        int failed = previousResults.Count(r => !r.Found);

        _db.UpsertPricingJob(spec, PricingJobStatus.Running, totalItems, completed, failed);
        _logger.LogInformation(
            "core.pricing.run_started total={Total} invalid={Invalid} pending={Pending}",
            totalItems,
            readResult.Invalid.Count,
            pending.Count);

        // M2b: load the job's active learned selector patches once and hand them to the Helper
        // with each lookup. Empty (the case until M2c distributes one) = builtin-only behavior.
        if (activePatches.Count > 0)
            _logger.LogInformation(
                "core.pricing.selector_patches_active count={Count}",
                activePatches.Count);

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
                if (!TryAdmitJobAuthority(spec, out var invalidAuthorityCode))
                {
                    halted = true;
                    haltReason = invalidAuthorityCode;
                    break;
                }
                _db.SavePricingResult(
                    PricingResultContentPolicy.InvalidNdcRow(
                        spec.JobId, i.RowIndex));
                failed++;
            }
        }

        foreach (var row in pending)
        {
            ct.ThrowIfCancellationRequested();

            if (!TryAdmitJobAuthority(
                    spec,
                    out var beforeLookupAuthorityCode))
            {
                halted = true;
                haltReason = beforeLookupAuthorityCode;
                _logger.LogWarning(
                    "core.pricing.job_authority_paused code={Code}",
                    haltReason);
                break;
            }

            var lookup = await LookupNdcAsync(
                spec.JobId,
                row,
                commandClient,
                activePatches,
                pmsFingerprint,
                screenSignature,
                ct);

            if (!TryAdmitJobAuthority(
                    spec,
                    out var afterLookupAuthorityCode))
            {
                halted = true;
                haltReason = afterLookupAuthorityCode;
                _logger.LogWarning(
                    "core.pricing.job_authority_paused code={Code}",
                    haltReason);
                break;
            }

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
                        "core.pricing.helper_unreachable n={N} remaining={Remaining}",
                        consecutiveIpcFailures,
                        totalItems - completed - failed);
                    break;
                }
                await Task.Delay(_interLookupDelay, ct); // brief pause — the Helper may be mid-restart
                continue;
            }

            if (lookup.IntegrityFailure)
            {
                halted = true;
                haltReason = "pricing_result_integrity_failed";
                _logger.LogCritical(
                    "core.pricing.helper_response_integrity_failed");
                break;
            }

            consecutiveIpcFailures = 0; // the Helper responded → it's alive
            var result = lookup.Result;

            // Helper is a separate trust boundary. The outer IPC correlation
            // ID is insufficient: the inner result must bind to this exact job,
            // row, and canonical NDC and carry a coherent success/failure shape.
            if (!PricingRunIntegrity.TryValidateLookupResult(
                    spec.JobId, row, result, out var resultIntegrityCode))
            {
                halted = true;
                haltReason = "pricing_result_integrity_failed";
                _logger.LogCritical(
                    "core.pricing.result_integrity_failed code={Code}",
                    resultIntegrityCode);
                break;
            }

            // Safety closure is an immediate batch boundary, not a per-row price miss.
            // Do not persist the row (resume must retry it), do not consult the optional
            // brain, and do not continue to another NDC after kill/pause/dry-run.
            if (PricingSafetyErrors.IsActuationGateClosed(result.ErrorMessage))
            {
                halted = true;
                haltReason = "actuation_gate_closed";
                _logger.LogCritical(
                    "core.pricing.actuation_gate_closed remaining={Remaining}",
                    totalItems - completed - failed);
                break;
            }

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
                        "core.pricing.pms_unavailable n={N} remaining={Remaining}",
                        consecutivePmsUnavailable,
                        totalItems - completed - failed);
                    break;
                }
                await Task.Delay(_interLookupDelay, ct);
                continue;
            }
            consecutivePmsUnavailable = 0;

            _db.SavePricingResult(result);

            if (!TryAdmitJobAuthority(
                    spec,
                    out var afterPersistAuthorityCode))
            {
                halted = true;
                haltReason = afterPersistAuthorityCode;
                _logger.LogWarning(
                    "core.pricing.job_authority_paused code={Code}",
                    haltReason);
                break;
            }

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

            _logger.LogDebug("core.pricing.lookup_completed found={Found}", result.Found);

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
                    // Brain rationale may be free-form and can incorporate local
                    // workstation context. Only a fixed result code may leave the
                    // device through the pricing acknowledgement contract.
                    haltReason = "pricing_brain_operator_required";
                    _logger.LogWarning(
                        "core.pricing.brain_halt tier={Tier}",
                        brainDecision.Tier);
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
                "core.pricing.run_halted completed={Completed} total={Total} failed={Failed}",
                completed,
                totalItems,
                failed);
            return new PricingJobProgress(spec.JobId, totalItems, completed, failed, PricingJobStatus.Halted, HaltReason: haltReason);
        }

        if (!TryAdmitJobAuthority(spec, out var beforeWriteAuthorityCode))
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Halted, totalItems, completed, failed);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed,
                PricingJobStatus.Halted,
                HaltReason: beforeWriteAuthorityCode);
        }

        // Write all results (including previously completed rows) to a SIBLING file by default.
        // This avoids the Codex-flagged "file locked by Excel.exe" failure mode where 499 rows
        // succeed and the final File.Move throws an IOException.
        var allResults = _db.GetPricingResults(spec.JobId);
        if (!PricingRunIntegrity.TryValidatePersistedResults(
                spec.JobId, manifest, allResults, out var terminalIntegrityCode))
        {
            _logger.LogCritical(
                "core.pricing.terminal_integrity_failed code={Code}",
                terminalIntegrityCode);
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Failed, totalItems, completed, failed);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed,
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
                spec, PricingJobStatus.Halted, totalItems, completed, failed);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed,
                PricingJobStatus.Halted,
                HaltReason: write.Error);
        }

        if (!TryAdmitJobAuthority(spec, out var afterWriteAuthorityCode))
        {
            _db.UpsertPricingJob(
                spec, PricingJobStatus.Halted, totalItems, completed, failed);
            return new PricingJobProgress(
                spec.JobId, totalItems, completed, failed,
                PricingJobStatus.Halted,
                HaltReason: afterWriteAuthorityCode);
        }

        // A review workbook is still useful when individual lookups fail, but writing that
        // artifact does not make the pricing job successful. Only a complete, zero-failure
        // run may cross the terminal success boundary.
        var cleanCompletion = PricingRunIntegrity.IsTerminallyComplete(
            spec.JobId, manifest, allResults, write, completed, failed);
        var finalStatus = cleanCompletion ? PricingJobStatus.Completed : PricingJobStatus.Failed;
        var terminalReason = cleanCompletion ? null : "pricing_job_failed";
        _db.UpsertPricingJob(spec, finalStatus, totalItems, completed, failed);

        _logger.LogInformation(
            "core.pricing.run_finished status={Status} completed={Completed} total={Total} failed={Failed}",
            finalStatus,
            completed,
            totalItems,
            failed);

        return new PricingJobProgress(
            spec.JobId, totalItems, completed, failed, finalStatus,
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

    private async Task<LookupOutcome> LookupNdcAsync(
        string jobId, NdcRow row, IIpcCommandClient commandClient,
        IReadOnlyList<SelectorPatch> patches,
        string? pmsFingerprint,
        string? screenSignature,
        CancellationToken ct)
    {
        try
        {
            var request = new IpcRequest(
                Id: Guid.NewGuid().ToString("N"),
                Command: IpcCommands.PricingLookup,
                Version: 1,
                Data: JsonSerializer.SerializeToElement(
                    new NdcPricingRequest(
                        jobId,
                        row.RowIndex,
                        row.NdcNormalized,
                        patches,
                        pmsFingerprint,
                        screenSignature)));

            var response = await commandClient.SendAsync(request, LookupTimeout, ct);

            // No response at all = Helper hung/disconnected (infrastructure failure), NOT a price miss.
            if (response == null)
                return new LookupOutcome(
                    Fail(jobId, row, "No response from Helper"),
                    HelperUnreachable: true,
                    IntegrityFailure: false);

            // From here the Helper responded — it's alive. Any failure below is a per-row/data problem,
            // so HelperUnreachable stays false and the run continues normally.

            // [C-2] Reject mismatched response IDs to prevent pipe desync data corruption
            if (response.Id != request.Id ||
                response.Command != request.Command)
                return new LookupOutcome(
                    Fail(jobId, row, "Helper response envelope mismatch"),
                    HelperUnreachable: false,
                    IntegrityFailure: true);

            if (response.Status != IpcStatus.Ok)
                return new LookupOutcome(
                    Fail(jobId, row, response.Error?.Message ?? $"Status {response.Status}"),
                    HelperUnreachable: false,
                    IntegrityFailure: false);

            if (response.Data == null || response.Error is not null)
                return new LookupOutcome(
                    Fail(jobId, row, "Helper response payload invalid"),
                    HelperUnreachable: false,
                    IntegrityFailure: true);

            var parsed = JsonSerializer.Deserialize<SupplierPriceResult>(response.Data.Value);
            if (parsed is null)
                return new LookupOutcome(
                    Fail(jobId, row, "Helper response payload invalid"),
                    HelperUnreachable: false,
                    IntegrityFailure: true);
            return new LookupOutcome(
                parsed,
                HelperUnreachable: false,
                IntegrityFailure: false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (JsonException ex)
        {
            _logger.LogSafeWarning(ex);
            return new LookupOutcome(
                Fail(jobId, row, "Helper response payload invalid"),
                HelperUnreachable: false,
                IntegrityFailure: true);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return new LookupOutcome(
                Fail(jobId, row, $"lookup_exception:{ex.GetType().Name}"),
                HelperUnreachable: false,
                IntegrityFailure: false);
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
