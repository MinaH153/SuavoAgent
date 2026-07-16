using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Discovery;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Writeback;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Diagnostics;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Receipts;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private static string PricingExecutionMode(PricingExecutorMode mode) => mode switch
    {
        PricingExecutorMode.SqlFirst => "sql",
        PricingExecutorMode.UiaFirst => "uia",
        PricingExecutorMode.VisionFirst => "vision",
        _ => throw new ArgumentOutOfRangeException(nameof(mode)),
    };

    private async Task HandleRunPricingJobAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = CommandDataObject(scEl);
        var ndcColumn = PricingJobDefaults.NdcColumn;
        var supplierColumn = PricingJobDefaults.SupplierColumn;
        var costColumn = PricingJobDefaults.CostColumn;
        var rawCommandId = ReadStringProperty(dataEl, "commandId");
        if (!IsCanonicalUuidV4(rawCommandId, out var commandId) ||
            !TryReadPricingAuthorityBinding(
                scEl,
                out var approvalId,
                out var grantDigest))
        {
            _logger.LogWarning("pricing_result_command_ineligible");
            return;
        }
        var autonomyExecutionMode = ReadAutonomyExecutionMode(dataEl);
        var terminalAckStaged = false;

        Task AckAsync(PricingTerminalAck ack)
        {
            terminalAckStaged = true;
            return AckPricingFailureAsync(commandId!, ack, ct);
        }

        var pricingCandidateToken = ReadStringProperty(
            dataEl,
            "pricingCandidateToken");

        if (_pricingJobExecutor == null)
        {
            _logger.LogWarning("run_pricing_job: pricing executor not registered");
            await AckAsync(PricingTerminalAck.Early("pricing_executor_unavailable"));
            return;
        }

        PricingJobSpec? recoverableSpec = null;
        try
        {
            recoverableSpec = (_pricingJobExecutor as IRecoverablePricingJobExecutor)?
                .GetRecoverableSpecForCommand(commandId!);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
        }
        if (recoverableSpec is not null &&
            (!string.Equals(
                recoverableSpec.ApprovalId,
                approvalId,
                StringComparison.Ordinal) ||
             !string.Equals(
                recoverableSpec.GrantDigest,
                grantDigest,
                StringComparison.Ordinal)))
        {
            await AckAsync(PricingTerminalAck.Early(
                "pricing_job_authority_binding_invalid"));
            return;
        }

        var autonomyAdmission = CapturePricingAutonomyAdmission(autonomyExecutionMode);
        if (!autonomyAdmission.Allowed)
        {
            _logger.LogWarning("run_pricing_job: autonomous authority unavailable");
            await AckAsync(PricingTerminalAck.Early("autonomy_not_earned"));
            return;
        }

        if (string.IsNullOrEmpty(pricingCandidateToken) && recoverableSpec is null)
        {
            _logger.LogWarning("run_pricing_job: missing opaque workbook target");
            await AckAsync(PricingTerminalAck.Early("pricing_candidate_token_required"));
            return;
        }

        // Pre-flight: a UIA job drives the LIVE PMS screen. Fail fast (don't touch the
        // screen) unless the Helper is reachable, answering, and in the interactive
        // session. SqlFirst reads SQL and never actuates, so it needs no live Helper.
        if (_options.PricingExecutor is PricingExecutorMode.UiaFirst or PricingExecutorMode.VisionFirst)
        {
            HelperPreflightResult preflight;
            try
            {
                preflight = await HelperInteractivePreflight.CheckAsync(
                    _ipcCommandClient,
                    TimeSpan.FromSeconds(10),
                    TimeSpan.FromSeconds(5),
                    ct);
            }
            catch (OperationCanceledException)
            {
                _stateDb.StagePricingTerminalAck(
                    commandId!,
                    PricingTerminalAck.Cancelled());
                _stateDb.MarkPricingCommandIntentTerminal(commandId!);
                terminalAckStaged = true;
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    "run_pricing_job: Helper pre-flight threw ({ErrorType})",
                    ex.GetType().Name);
                await AckAsync(PricingTerminalAck.Early("helper_preflight_failed"));
                return;
            }
            if (!preflight.Ok)
            {
                var preflightCode = preflight.Code ?? "helper_preflight_failed";
                _logger.LogError(
                    "run_pricing_job: Helper pre-flight failed ({Code})",
                    preflightCode);
                await AckAsync(PricingTerminalAck.Early(
                    PricingEarlyFailureCode(preflightCode)));
                return;
            }
            _logger.LogInformation(
                "run_pricing_job: Helper interactive pre-flight OK (session {Session})", preflight.HelperSessionId);
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Pricing, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAsync(PricingTerminalAck.AutopilotRejected(
                    PricingAutopilotRejectionCode(autopilotRun.RejectionCode)))
                .ConfigureAwait(false);
            return;
        }
        if (!EnforcePricingAdmissionIdentity(autonomyAdmission))
        {
            await AckAsync(PricingTerminalAck.Early(
                "autonomy_latch_persistence_failed"));
            return;
        }
        var runToken = autopilotRun.Token;
        var autonomyRunId = Guid.NewGuid().ToString("D");
        var autonomyRecorded = false;
        PricingJobExecutionResult? autonomyExecution = null;
        var autonomyTerminalResult = AutonomySemanticResult.Failed;
        var autonomyTerminalReason = "admitted_run_failed";

        // [M-3] Only one pricing job at a time — reject concurrent commands.
        var semaphoreHeld = false;
        try
        {
            if (!await _pricingJobSemaphore.WaitAsync(TimeSpan.Zero, runToken).ConfigureAwait(false))
            {
                _logger.LogWarning("run_pricing_job: another job is already running, command ignored");
                await AckAsync(PricingTerminalAck.Early("pricing_job_in_flight"));
                return;
            }
            semaphoreHeld = true;

            // A pending Helper restart means the Broker is about to KILL the Helper (≤5s).
            // Starting a UIA job now would have it yanked mid-typing. Checked AFTER acquiring
            // the semaphore (restart_helper writes the sentinel while holding it — no TOCTOU).
            if (HelperRestartRequest.IsPending(HelperRestartRequest.DefaultPath(), DateTimeOffset.UtcNow))
            {
                _logger.LogWarning("run_pricing_job: refused — a Helper restart is pending");
                await AckAsync(PricingTerminalAck.Early("helper_restart_in_progress"));
                return;
            }

            // Consume the one-use local capability only after every admission
            // gate above has passed. A pause, concurrent run, or pending Helper
            // restart must not burn the pharmacist's local workbook selection.
            runToken.ThrowIfCancellationRequested();
            PricingJobSpec spec;
            if (recoverableSpec is not null)
            {
                if (!IsExcelPathSafe(
                        recoverableSpec.ExcelPath,
                        out var recoveredCanonical,
                        out var recoveredPathReason))
                {
                    _logger.LogWarning("run_pricing_job: persisted recovery path rejected");
                    await AckAsync(PricingTerminalAck.PathRejected(recoveredPathReason));
                    return;
                }
                spec = recoverableSpec with { ExcelPath = recoveredCanonical };
                _logger.LogInformation("core.command.pricing_same_job_resume_admitted");
            }
            else
            {
                string? excelPath;
                try
                {
                    excelPath = _stateDb.TryResolvePricingDiscoveryCandidate(
                        pricingCandidateToken!);
                }
                catch (Exception ex)
                {
                    _logger.LogError(
                        "run_pricing_job: candidate resolution failed ({ErrorType})",
                        ex.GetType().Name);
                    await AckAsync(PricingTerminalAck.Early(
                        "pricing_candidate_resolution_failed"));
                    return;
                }
                if (string.IsNullOrEmpty(excelPath))
                {
                    _logger.LogWarning("run_pricing_job: pricing candidate token was not found");
                    await AckAsync(PricingTerminalAck.Early("pricing_candidate_expired"));
                    return;
                }
                if (!IsExcelPathSafe(excelPath, out var canonicalPath, out var pathReason))
                {
                    _logger.LogWarning("run_pricing_job: local workbook path rejected");
                    await AckAsync(PricingTerminalAck.PathRejected(pathReason));
                    return;
                }

                spec = new PricingJobSpec(
                    Guid.NewGuid().ToString("N"),
                    canonicalPath,
                    ndcColumn,
                    supplierColumn,
                    costColumn,
                    approvalId,
                    grantDigest);
            }
            var jobId = spec.JobId;
            _pricingJobCloudUploader?.PrepareDelivery(
                spec, commandId, null, _options.PricingExecutor);
            if (!_stateDb.MarkPricingCommandIntentAdmitted(
                    commandId!,
                    PricingExecutionMode(_options.PricingExecutor),
                    autonomyExecutionMode == AutonomyExecutionMode.Auto
                        ? "auto"
                        : "supervised",
                    autonomyAdmission.Scope.ScopeDigest,
                    autonomyAdmission.TrustedIdentity))
            {
                await AckAsync(PricingTerminalAck.Early(
                    "pricing_execution_exception"));
                return;
            }

            _logger.LogInformation("core.command.pricing_started");

            var execution = await _pricingJobExecutor.RunAsync(spec, runToken);
            autonomyExecution = execution;
            var progress = execution.Progress;
            var failureCode = PricingTerminalFailureCode(progress.HaltReason);
            if (!execution.Ok)
            {
                autonomyTerminalReason = "execution_terminal";
                autonomyRecorded = TryRecordPricingAutonomy(
                    autonomyRunId, autonomyAdmission, execution, autonomyExecutionMode);
                _logger.LogInformation(
                    "core.command.pricing_finished status={Status} completed={Completed} total={Total}",
                    progress.Status,
                    progress.CompletedItems,
                    progress.TotalItems);
                await AckAsync(PricingTerminalAck.PricingFailed(
                    jobId,
                    execution.Mode,
                    progress.TotalItems,
                    progress.CompletedItems,
                    progress.FailedItems,
                    failureCode,
                    spec.CostBasis));
                return;
            }

            autonomyTerminalReason = "result_sync_failed";
            var uploadReceipt = _pricingJobCloudUploader is null
                ? null
                : await _pricingJobCloudUploader.UploadAsync(
                    spec, execution, commandId, runToken).ConfigureAwait(false);
            if (uploadReceipt?.Accepted != true)
            {
                var terminalAck = PricingTerminalAckPolicy.FromResultSync(
                    uploadReceipt,
                    jobId,
                    execution,
                    spec.CostBasis);
                if (terminalAck is not null)
                {
                    await AckAsync(terminalAck);
                    return;
                }
                _pricingTerminalAckOutbox?.MarkResultPending(commandId!);
                _logger.LogWarning("core.command.pricing_result_sync_deferred");
                return;
            }

            _pricingTerminalAckOutbox?.MarkCompleted(commandId!);

            autonomyTerminalReason = "execution_terminal";
            autonomyRecorded = TryRecordPricingAutonomy(
                autonomyRunId, autonomyAdmission, execution, autonomyExecutionMode);

            _logger.LogInformation(
                "core.command.pricing_finished status={Status} completed={Completed} total={Total}",
                progress.Status,
                progress.CompletedItems,
                progress.TotalItems);

            // The immutable cloud result receipt, not a shape-only ACK, is the
            // completion authority. UploadAsync commits that receipt atomically;
            // the durable result outbox retries when no acceptance was observed.
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            autonomyTerminalResult = AutonomySemanticResult.Cancelled;
            autonomyTerminalReason = "local_autopilot_cancelled";
            RecordCancellationAudit(new AuditEntry(
                TaskId: commandId ?? "pricing",
                EventType: "pricing_run_cancelled",
                FromState: "in_progress",
                ToState: "cancelled",
                Trigger: "local_autopilot_control",
                CommandId: commandId,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: "local_autopilot_control"));
            if (!PreserveDurablePricingOutcome(commandId!))
                await AckAsync(PricingTerminalAck.Cancelled()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            autonomyTerminalResult = AutonomySemanticResult.Cancelled;
            autonomyTerminalReason = "host_cancelled";
            if (!terminalAckStaged && !PreserveDurablePricingOutcome(commandId!))
            {
                _stateDb.StagePricingTerminalAck(
                    commandId!,
                    PricingTerminalAck.Cancelled());
                _stateDb.MarkPricingCommandIntentTerminal(commandId!);
                terminalAckStaged = true;
            }
            throw;
        }
        catch (Exception ex)
        {
            // Fire-and-forget dispatch: without this the SqlFirst executor's unguarded SQLite/workbook
            // boundary (a `database is locked`/IO throw) would become an unobserved detached-task
            // exception with NO failure ack — the operator's portal would hang "running" forever.
            // Match the run_workflow handler: always ack failure.
            _logger.LogError(
                "run_pricing_job: unexpected failure ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(PricingTerminalAck.Early("pricing_execution_exception"));
        }
        finally
        {
            if (!autonomyRecorded)
            {
                autonomyRecorded = TryRecordPricingAutonomy(
                    autonomyRunId,
                    autonomyAdmission,
                    autonomyExecution,
                    autonomyExecutionMode,
                    autonomyTerminalResult,
                    autonomyTerminalReason);
            }
            if (semaphoreHeld) _pricingJobSemaphore.Release();
        }
    }

    /// <summary>
    /// v3.13 discovery-mediated pricing job. Operator clicks "auto-find and
    /// run" in the portal; agent runs <see cref="SuavoAgent.Core.Discovery.FileLocatorService"/>
    /// via Helper IPC to locate the file, then:
    /// <list type="bullet">
    ///   <item><b>AutoUse</b> — runs the pricing job immediately on the
    ///     discovered path, ACKs success with progress.</item>
    ///   <item><b>RequireConfirm / Inconclusive</b> — fails closed with a
    ///     structural local-confirmation status. Local paths and workbook
    ///     metadata never cross the cloud boundary.</item>
    ///   <item><b>NotFound</b> — fails closed with a structural status.</item>
    /// </list>
    /// </summary>
    private async Task HandleFindAndRunPricingJobAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = CommandDataObject(scEl);
        var pack = ReadStringProperty(dataEl, "pack");
        var rawCommandId = ReadStringProperty(dataEl, "commandId");
        if (!IsCanonicalUuidV4(rawCommandId, out var commandId) ||
            !TryReadPricingAuthorityBinding(
                scEl,
                out var approvalId,
                out var grantDigest))
        {
            _logger.LogWarning("pricing_result_command_ineligible");
            return;
        }
        var autonomyExecutionMode = ReadAutonomyExecutionMode(dataEl);
        var terminalAckStaged = false;

        Task AckAsync(PricingTerminalAck ack)
        {
            terminalAckStaged = true;
            return AckPricingFailureAsync(commandId!, ack, ct);
        }

        var generatedV2Pack = string.Equals(
            pack,
            "pharmacy_rx_generate_v2",
            StringComparison.Ordinal);
        var top500PackageCostV3Pack = string.Equals(
            pack,
            "pharmacy_rx_top500_package_cost_v3",
            StringComparison.Ordinal);
        if (!generatedV2Pack &&
            !top500PackageCostV3Pack &&
            !string.Equals(pack, "pharmacy_rx", StringComparison.Ordinal))
        {
            _logger.LogWarning("core.command.pricing_pack_rejected");
            await AckAsync(PricingTerminalAck.Early("unknown_pack"));
            return;
        }

        var autonomyAdmission = CapturePricingAutonomyAdmission(autonomyExecutionMode);
        if (!autonomyAdmission.Allowed)
        {
            _logger.LogWarning("find_and_run_pricing_job: autonomous authority unavailable");
            await AckAsync(PricingTerminalAck.Early("autonomy_not_earned"));
            return;
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Pricing, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAsync(PricingTerminalAck.AutopilotRejected(
                    PricingAutopilotRejectionCode(autopilotRun.RejectionCode)))
                .ConfigureAwait(false);
            return;
        }
        if (!EnforcePricingAdmissionIdentity(autonomyAdmission))
        {
            await AckAsync(PricingTerminalAck.Early(
                "autonomy_latch_persistence_failed"));
            return;
        }
        var runToken = autopilotRun.Token;
        var autonomyRunId = Guid.NewGuid().ToString("D");
        var autonomyRecorded = false;
        PricingJobExecutionResult? autonomyExecution = null;
        var autonomyTerminalResult = AutonomySemanticResult.Failed;
        var autonomyTerminalReason = "admitted_discovery_not_completed";

        try
        {
            if (_ipcCommandClient == null || _pricingJobExecutor == null)
            {
                _logger.LogWarning("find_and_run_pricing_job: IPC/pricing executor not registered");
                await AckAsync(PricingTerminalAck.Early(
                    "pricing_executor_unavailable"));
                return;
            }

        // Pre-flight: discovery runs FileLocatorService in the Helper's user session and the
        // pricing run drives the live PMS screen — both need the Helper alive, answering, and
        // in the interactive session. Fail fast before touching anything if it isn't.
        var preflight = await HelperInteractivePreflight.CheckAsync(
            _ipcCommandClient, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(5), runToken);
        if (!preflight.Ok)
        {
            var preflightCode = preflight.Code ?? "helper_preflight_failed";
            _logger.LogError(
                "find_and_run_pricing_job: Helper pre-flight failed ({Code})",
                preflightCode);
            await AckAsync(PricingTerminalAck.Early(
                PricingEarlyFailureCode(preflightCode)));
            return;
        }
        _logger.LogInformation(
            "find_and_run_pricing_job: Helper interactive pre-flight OK (session {Session})", preflight.HelperSessionId);

        var spec = SuavoAgent.Core.Verticals.Pharmacy.PharmacyPresets.NdcPricingList();

        // Connect to Helper IPC.
        if (!_ipcCommandClient.IsConnected)
        {
            var connected = await _ipcCommandClient.ConnectAsync(TimeSpan.FromSeconds(10), runToken);
            if (!connected)
            {
                _logger.LogError("find_and_run_pricing_job: cannot connect to Helper command pipe");
                await AckAsync(PricingTerminalAck.Early("helper_unreachable"));
                return;
            }
        }

        if (top500PackageCostV3Pack)
        {
            if (_cloudClient is null ||
                _topDispensedWorklistProgressBuilder is null ||
                _pricedWorkbookPublisher is null ||
                _pricingJobExecutor is not IProgressReportingPricingJobExecutor)
            {
                await AckAsync(PricingTerminalAck.Early(
                    "pricing_worklist_source_unavailable"));
                return;
            }
            var progressPublisher = new PricingCommandProgressPublisher(
                _cloudClient,
                commandId!);
            var worklist = await ContinueAfterBestEffortInitialProgressAsync(
                    progressPublisher,
                    token => _topDispensedWorklistProgressBuilder.BuildAsync(
                        commandId!,
                        async (progress, progressToken) =>
                        {
                            _ = await progressPublisher.PublishFixedAsync(
                                    progress,
                                    progressToken)
                                .ConfigureAwait(false);
                        },
                        token),
                    runToken)
                .ConfigureAwait(false);
            if (!worklist.Ok || worklist.WorkbookPath is null)
            {
                await AckAsync(PricingTerminalAck.Early(
                    worklist.ErrorCode ?? "pricing_worklist_generation_failed"));
                return;
            }
            _logger.LogInformation(
                "core.command.pricing_worklist_generated count={Count}",
                worklist.ItemCount);
            var outcome = await ExecutePricingPathAsync(
                    worklist.WorkbookPath,
                    commandId!,
                    approvalId!,
                    grantDigest!,
                    autonomyExecutionMode,
                    autonomyAdmission,
                    autonomyRunId,
                    AckAsync,
                    PricingApprovalContract.PackageCostBasis,
                    runToken,
                    progressPublisher,
                    requireUserVisiblePublication: true)
                .ConfigureAwait(false);
            autonomyExecution = outcome.Execution;
            autonomyRecorded = outcome.AutonomyRecorded;
            autonomyTerminalReason = outcome.TerminalReason;
            return;
        }

        if (generatedV2Pack)
        {
            if (_topDispensedWorklistBuilder is null)
            {
                await AckAsync(PricingTerminalAck.Early(
                    "pricing_worklist_source_unavailable"));
                return;
            }
            var worklist = await _topDispensedWorklistBuilder.BuildAsync(
                    commandId!,
                    runToken)
                .ConfigureAwait(false);
            if (!worklist.Ok || worklist.WorkbookPath is null)
            {
                await AckAsync(PricingTerminalAck.Early(
                    worklist.ErrorCode ?? "pricing_worklist_generation_failed"));
                return;
            }
            _logger.LogInformation(
                "core.command.pricing_worklist_generated count={Count}",
                worklist.ItemCount);
            var outcome = await ExecutePricingPathAsync(
                    worklist.WorkbookPath,
                    commandId!,
                    approvalId!,
                    grantDigest!,
                    autonomyExecutionMode,
                    autonomyAdmission,
                    autonomyRunId,
                    AckAsync,
                    PricingApprovalContract.CostPerUnitBasis,
                    runToken)
                .ConfigureAwait(false);
            autonomyExecution = outcome.Execution;
            autonomyRecorded = outcome.AutonomyRecorded;
            autonomyTerminalReason = outcome.TerminalReason;
            return;
        }

        if (_discoveryClient is null)
        {
            _logger.LogWarning("find_and_run_pricing_job: discovery not registered");
            await AckAsync(PricingTerminalAck.Early(
                "pricing_discovery_unavailable"));
            return;
        }

        // Run discovery.
        var discoveryJobId = Guid.NewGuid().ToString("N");
        var discovery = await _discoveryClient.FindAsync(_ipcCommandClient, discoveryJobId, spec, runToken);
        if (!discovery.Succeeded)
        {
            var failure = discovery.Failure!;
            _logger.LogError(
                "find_and_run_pricing_job: discovery failed reason={Reason}",
                failure.ReasonCode);
            await AckAsync(PricingTerminalAck.DiscoveryFailed(
                PricingDiscoveryFailureCode(failure.ReasonCode),
                failure.HelperVersionSuspect));
            return;
        }
        var discoveryResult = discovery.Result!;

        _logger.LogInformation(
            "find_and_run_pricing_job: discovery resolution={Resolution} hasBest={HasBest} confidence={Conf}",
            discoveryResult.Resolution,
            discoveryResult.Best is not null,
            discoveryResult.Best?.Confidence.ToString("F2") ?? "-");

        if (discoveryResult.Resolution == FileDiscoveryResolution.AutoUse &&
            discoveryResult.Best is not null)
        {
            var outcome = await ExecutePricingPathAsync(
                    discoveryResult.Best.Candidate.Candidate.AbsolutePath,
                    commandId!,
                    approvalId!,
                    grantDigest!,
                    autonomyExecutionMode,
                    autonomyAdmission,
                    autonomyRunId,
                    AckAsync,
                    PricingApprovalContract.CostPerUnitBasis,
                    runToken)
                .ConfigureAwait(false);
            autonomyExecution = outcome.Execution;
            autonomyRecorded = outcome.AutonomyRecorded;
            autonomyTerminalReason = outcome.TerminalReason;
            return;
        }

        // Not confident enough to auto-run — surface candidates for operator pick.
        if (discoveryResult.Resolution == FileDiscoveryResolution.NotFound)
        {
            await AckAsync(PricingTerminalAck.NotFound());
            return;
        }

        var candidateCount =
            (discoveryResult.Best is null ? 0 : 1) + discoveryResult.Alternatives.Count;
        if (candidateCount == 0)
        {
            await AckAsync(PricingTerminalAck.NotFound());
            return;
        }

        await AckAsync(PricingTerminalAck.LocalConfirmation(candidateCount));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            autonomyTerminalResult = AutonomySemanticResult.Cancelled;
            autonomyTerminalReason = "local_autopilot_cancelled";
            RecordCancellationAudit(new AuditEntry(
                TaskId: commandId ?? "pricing_discovery",
                EventType: "pricing_run_cancelled",
                FromState: "in_progress",
                ToState: "cancelled",
                Trigger: "local_autopilot_control",
                CommandId: commandId,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: "local_autopilot_control"));
            if (!PreserveDurablePricingOutcome(commandId!))
                await AckAsync(PricingTerminalAck.Cancelled()).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            autonomyTerminalResult = AutonomySemanticResult.Cancelled;
            autonomyTerminalReason = "host_cancelled";
            if (!terminalAckStaged && !PreserveDurablePricingOutcome(commandId!))
            {
                _stateDb.StagePricingTerminalAck(
                    commandId!,
                    PricingTerminalAck.Cancelled());
                _stateDb.MarkPricingCommandIntentTerminal(commandId!);
                terminalAckStaged = true;
            }
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(
                "find_and_run_pricing_job: unexpected failure ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(PricingTerminalAck.Early("pricing_discovery_exception"));
        }
        finally
        {
            if (!autonomyRecorded)
            {
                autonomyRecorded = TryRecordPricingAutonomy(
                    autonomyRunId,
                    autonomyAdmission,
                    autonomyExecution,
                    autonomyExecutionMode,
                    autonomyTerminalResult,
                    autonomyTerminalReason);
            }
        }
    }

}
