using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Pricing;

namespace SuavoAgent.Core.Workers;

public sealed partial class HeartbeatWorker
{
    private sealed record PricingPathExecutionOutcome(
        PricingJobExecutionResult? Execution,
        bool AutonomyRecorded,
        string TerminalReason);

    internal static async Task<T> ContinueAfterBestEffortInitialProgressAsync<T>(
        PricingCommandProgressPublisher progressPublisher,
        Func<CancellationToken, Task<T>> continuation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progressPublisher);
        ArgumentNullException.ThrowIfNull(continuation);
        _ = await progressPublisher.PublishWaitingToStartAsync(cancellationToken)
            .ConfigureAwait(false);
        return await continuation(cancellationToken).ConfigureAwait(false);
    }

    private async Task<PricingPathExecutionOutcome> ExecutePricingPathAsync(
        string chosenPath,
        string commandId,
        string approvalId,
        string grantDigest,
        AutonomyExecutionMode autonomyExecutionMode,
        PricingAutonomyAdmission autonomyAdmission,
        string autonomyRunId,
        Func<PricingTerminalAck, Task> ackAsync,
        string costBasis,
        CancellationToken cancellationToken,
        PricingCommandProgressPublisher? progressPublisher = null,
        bool requireUserVisiblePublication = false)
    {
        if (_pricingJobExecutor is null)
        {
            await ackAsync(PricingTerminalAck.Early(
                "pricing_executor_unavailable")).ConfigureAwait(false);
            return new(null, false, "pricing_executor_unavailable");
        }
        if (!IsExcelPathSafe(chosenPath, out var canonical, out var unsafeReason))
        {
            _logger.LogWarning("core.command.pricing_path_rejected");
            await ackAsync(PricingTerminalAck.PathRejected(unsafeReason))
                .ConfigureAwait(false);
            return new(null, false, "pricing_path_rejected");
        }
        if (!await _pricingJobSemaphore.WaitAsync(TimeSpan.Zero, cancellationToken)
                .ConfigureAwait(false))
        {
            await ackAsync(PricingTerminalAck.Early("pricing_job_in_flight"))
                .ConfigureAwait(false);
            return new(null, false, "pricing_job_in_flight");
        }

        PricingJobExecutionResult? execution = null;
        var autonomyRecorded = false;
        var terminalReason = "execution_not_completed";
        try
        {
            if (HelperRestartRequest.IsPending(
                    HelperRestartRequest.DefaultPath(),
                    DateTimeOffset.UtcNow))
            {
                await ackAsync(PricingTerminalAck.Early(
                    "helper_restart_in_progress")).ConfigureAwait(false);
                return new(null, false, "helper_restart_in_progress");
            }

            var proposedSpec = new PricingJobSpec(
                Guid.NewGuid().ToString("N"),
                canonical,
                PricingJobDefaults.NdcColumn,
                costBasis == PricingApprovalContract.PackageCostBasis
                    ? PricingJobDefaults.PackageSupplierColumn
                    : PricingJobDefaults.SupplierColumn,
                costBasis == PricingApprovalContract.PackageCostBasis
                    ? PricingJobDefaults.PackageCostColumn
                    : PricingJobDefaults.CostColumn,
                approvalId,
                grantDigest,
                costBasis);
            var jobSpec = (_pricingJobExecutor as IRecoverablePricingJobExecutor)?
                .GetRecoverableSpec(proposedSpec, commandId) ?? proposedSpec;
            if (!string.Equals(jobSpec.ApprovalId, approvalId, StringComparison.Ordinal) ||
                !string.Equals(jobSpec.GrantDigest, grantDigest, StringComparison.Ordinal))
            {
                await ackAsync(PricingTerminalAck.Early(
                    "pricing_job_authority_binding_invalid")).ConfigureAwait(false);
                return new(null, false, "pricing_job_authority_binding_invalid");
            }

            if (!ReferenceEquals(jobSpec, proposedSpec) &&
                jobSpec.JobId != proposedSpec.JobId)
            {
                _logger.LogInformation(
                    "core.command.pricing_same_job_resume_admitted");
            }
            _pricingJobCloudUploader?.PrepareDelivery(
                jobSpec,
                commandId,
                null,
                _options.PricingExecutor);
            if (!_stateDb.MarkPricingCommandIntentAdmitted(
                    commandId,
                    PricingExecutionMode(_options.PricingExecutor),
                    autonomyExecutionMode == AutonomyExecutionMode.Auto
                        ? "auto"
                        : "supervised",
                    autonomyAdmission.Scope.ScopeDigest,
                    autonomyAdmission.TrustedIdentity))
            {
                await ackAsync(PricingTerminalAck.Early(
                    "pricing_execution_exception")).ConfigureAwait(false);
                return new(null, false, "pricing_execution_exception");
            }

            _logger.LogInformation("core.command.pricing_auto_run_started");
            execution = progressPublisher is not null &&
                        _pricingJobExecutor is IProgressReportingPricingJobExecutor
                            progressExecutor
                ? await progressExecutor.RunAsync(
                        jobSpec,
                        async (progress, token) =>
                        {
                            _ = await progressPublisher.PublishPricingAsync(progress, token)
                                .ConfigureAwait(false);
                        },
                        cancellationToken)
                    .ConfigureAwait(false)
                : await _pricingJobExecutor.RunAsync(jobSpec, cancellationToken)
                    .ConfigureAwait(false);
            var progress = execution.Progress;
            if (!execution.Ok)
            {
                terminalReason = "execution_terminal";
                autonomyRecorded = TryRecordPricingAutonomy(
                    autonomyRunId,
                    autonomyAdmission,
                    execution,
                    autonomyExecutionMode);
                await ackAsync(PricingTerminalAck.PricingFailed(
                        jobSpec.JobId,
                        execution.Mode,
                        progress.TotalItems,
                        progress.CompletedItems,
                        progress.FailedItems,
                        PricingTerminalFailureCode(progress.HaltReason),
                        jobSpec.CostBasis))
                    .ConfigureAwait(false);
                return new(execution, autonomyRecorded, terminalReason);
            }

            if (requireUserVisiblePublication)
            {
                terminalReason = "pricing_output_publication_failed";
                if (_pricedWorkbookPublisher is null ||
                    string.IsNullOrWhiteSpace(execution.DeliverablePath))
                {
                    await ackAsync(PricingTerminalAck.Early(
                        "pricing_output_publication_failed")).ConfigureAwait(false);
                    return new(execution, false, terminalReason);
                }
                var publication = await _pricedWorkbookPublisher.PublishAsync(
                        commandId,
                        execution.DeliverablePath,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!publication.Published)
                {
                    _logger.LogWarning(
                        "core.command.pricing_output_publication_failed code={Code}",
                        publication.Code);
                    await ackAsync(PricingTerminalAck.Early(
                        "pricing_output_publication_failed")).ConfigureAwait(false);
                    return new(execution, false, terminalReason);
                }
            }

            terminalReason = "result_sync_failed";
            var uploadReceipt = _pricingJobCloudUploader is null
                ? null
                : await _pricingJobCloudUploader.UploadAsync(
                        jobSpec,
                        execution,
                        commandId,
                        cancellationToken)
                    .ConfigureAwait(false);
            if (uploadReceipt?.Accepted != true)
            {
                var terminalAck = PricingTerminalAckPolicy.FromResultSync(
                    uploadReceipt,
                    jobSpec.JobId,
                    execution,
                    jobSpec.CostBasis);
                if (terminalAck is not null)
                {
                    await ackAsync(terminalAck).ConfigureAwait(false);
                    return new(execution, false, terminalReason);
                }
                _pricingTerminalAckOutbox?.MarkResultPending(commandId);
                _logger.LogWarning("core.command.pricing_result_sync_deferred");
                return new(execution, false, terminalReason);
            }

            _pricingTerminalAckOutbox?.MarkCompleted(commandId);
            terminalReason = "execution_terminal";
            autonomyRecorded = TryRecordPricingAutonomy(
                autonomyRunId,
                autonomyAdmission,
                execution,
                autonomyExecutionMode);
            return new(execution, autonomyRecorded, terminalReason);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "find_and_run_pricing_job: unexpected failure during execution ({ErrorType})",
                exception.GetType().Name);
            await ackAsync(PricingTerminalAck.Early(
                "pricing_execution_exception")).ConfigureAwait(false);
            return new(execution, autonomyRecorded, "pricing_execution_exception");
        }
        finally
        {
            _pricingJobSemaphore.Release();
        }
    }
}
