using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Resumes only a previously verified and admitted pricing command. It never
/// re-registers the signed envelope or nonce; the immutable command checkpoint,
/// delivery intent, input identity, authority, and current autonomy identity
/// must all converge before the executor is called.
/// </summary>
internal sealed class PricingCommandRecoveryCoordinator
{
    private readonly AgentStateDb _db;
    private readonly IPricingJobExecutor _executor;
    private readonly PricingJobCloudUploader? _uploader;
    private readonly PricingTerminalAckOutbox _terminalOutbox;
    private readonly Func<string?> _currentScopeDigest;
    private readonly ILogger<PricingCommandRecoveryCoordinator> _logger;
    private readonly IReadOnlyDictionary<string, string> _trustedCommandKeys;
    private readonly TimeProvider _timeProvider;

    internal PricingCommandRecoveryCoordinator(
        AgentStateDb db,
        IPricingJobExecutor executor,
        PricingJobCloudUploader? uploader,
        PricingTerminalAckOutbox terminalOutbox,
        Func<string?> currentScopeDigest,
        ILogger<PricingCommandRecoveryCoordinator> logger,
        IReadOnlyDictionary<string, string>? trustedCommandKeys = null,
        TimeProvider? timeProvider = null)
    {
        _db = db;
        _executor = executor;
        _uploader = uploader;
        _terminalOutbox = terminalOutbox;
        _currentScopeDigest = currentScopeDigest;
        _logger = logger;
        _trustedCommandKeys = trustedCommandKeys ??
            RemoteCommandTrust.CreateProductionKeyRegistry();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task RecoverAsync(CancellationToken ct)
    {
        var recoverableExecutor = _executor as IRecoverablePricingJobExecutor;
        var now = _timeProvider.GetUtcNow();
        foreach (var intent in _db.GetExpiredAdmittedPricingAuthorityCommandIntents(
                     _terminalOutbox.OwnerId,
                     maximum: 20,
                     now,
                     _trustedCommandKeys))
        {
            ct.ThrowIfCancellationRequested();
            if (_db.GetPricingCommandRecoveryEvidence(intent.CommandId).Kind !=
                AgentStateDb.PricingCommandRecoveryKind.None)
                continue;
            _logger.LogWarning(
                "core.pricing.signed_admission_recovery_pic_authority_expired");
            await TerminalizeEarlyAsync(
                    intent.CommandId,
                    "pricing_cost_basis_approval_expired",
                    ct)
                .ConfigureAwait(false);
        }

        foreach (var intent in _db.GetResumableAdmittedPricingCommandIntents(
                     _terminalOutbox.OwnerId,
                     maximum: 20,
                     _trustedCommandKeys,
                     now))
        {
            ct.ThrowIfCancellationRequested();
            if (_db.GetPricingCommandRecoveryEvidence(intent.CommandId).Kind !=
                AgentStateDb.PricingCommandRecoveryKind.None)
                continue;

            var currentScope = _currentScopeDigest();
            if (!intent.TrustedIdentity ||
                currentScope is null ||
                !string.Equals(
                    currentScope,
                    intent.AdmissionScopeDigest,
                    StringComparison.Ordinal))
            {
                await TerminalizeEarlyAsync(intent.CommandId, ct)
                    .ConfigureAwait(false);
                continue;
            }

            PricingJobSpec? spec = null;
            try
            {
                spec = recoverableExecutor?.GetRecoverableSpecForCommand(
                    intent.CommandId);
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
            }
            if (spec is null)
            {
                await TerminalizeEarlyAsync(intent.CommandId, ct)
                    .ConfigureAwait(false);
                continue;
            }
            if (intent.ApprovalId is null || intent.GrantDigest is null ||
                !string.Equals(
                    intent.ApprovalId,
                    spec.ApprovalId,
                    StringComparison.Ordinal) ||
                !string.Equals(
                    intent.GrantDigest,
                    spec.GrantDigest,
                    StringComparison.Ordinal))
            {
                await TerminalizeEarlyAsync(
                        intent.CommandId,
                        "pricing_job_authority_binding_invalid",
                        ct)
                    .ConfigureAwait(false);
                continue;
            }

            if (!_db.TryAdmitPricingJobAuthority(
                    spec.JobId,
                    intent.ApprovalId,
                    intent.GrantDigest,
                    _timeProvider.GetUtcNow(),
                    _trustedCommandKeys,
                    out var jobAuthorityCode))
            {
                if (IsPermanentJobAuthorityFailure(jobAuthorityCode))
                {
                    await TerminalizeEarlyAsync(
                            intent.CommandId,
                            jobAuthorityCode,
                            ct)
                        .ConfigureAwait(false);
                }
                else
                {
                    _logger.LogWarning(
                        "core.pricing.signed_admission_recovery_authority_deferred code={Code}",
                        jobAuthorityCode);
                }
                continue;
            }

            try
            {
                if (!_db.TryReadVerifiedPricingCheckpoint(
                        intent.CommandId,
                        _trustedCommandKeys,
                        out var signedCommand) ||
                    signedCommand is null ||
                    !SignedCommandVerifier.VerifyExecutionAuthorityAt(
                        signedCommand,
                        _timeProvider.GetUtcNow()).IsValid)
                {
                    _logger.LogWarning(
                        "core.pricing.signed_admission_recovery_authority_expired");
                    await TerminalizeEarlyAsync(
                            intent.CommandId,
                            "pricing_command_authority_expired",
                            ct)
                        .ConfigureAwait(false);
                    continue;
                }
                _logger.LogInformation(
                    "core.pricing.signed_admission_recovery_started");
                var execution = await _executor.RunAsync(spec, ct)
                    .ConfigureAwait(false);
                if (!execution.Ok)
                {
                    await _terminalOutbox.StageAndTryDeliverAsync(
                        intent.CommandId,
                        PricingTerminalAck.PricingFailed(
                            spec.JobId,
                            execution.Mode,
                            execution.Progress.TotalItems,
                            execution.Progress.CompletedItems,
                            execution.Progress.FailedItems,
                            "pricing_job_failed"),
                        ct).ConfigureAwait(false);
                    continue;
                }

                if (_uploader is null)
                {
                    await TerminalizeEarlyAsync(intent.CommandId, ct)
                        .ConfigureAwait(false);
                    continue;
                }

                if (!_db.TryAdmitPricingJobAuthority(
                        spec.JobId,
                        intent.ApprovalId,
                        intent.GrantDigest,
                        _timeProvider.GetUtcNow(),
                        _trustedCommandKeys,
                        out var uploadAuthorityCode))
                {
                    if (IsPermanentJobAuthorityFailure(uploadAuthorityCode))
                    {
                        await TerminalizeEarlyAsync(
                                intent.CommandId,
                                uploadAuthorityCode,
                                ct)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        _terminalOutbox.MarkResultPending(intent.CommandId);
                    }
                    continue;
                }

                var receipt = await _uploader.UploadAsync(
                    spec,
                    execution,
                    intent.CommandId,
                    ct).ConfigureAwait(false);
                if (receipt.Accepted)
                {
                    _terminalOutbox.MarkCompleted(intent.CommandId);
                    continue;
                }

                var terminal = PricingTerminalAckPolicy.FromResultSync(
                    receipt,
                    spec.JobId,
                    execution);
                if (terminal is not null)
                {
                    await _terminalOutbox.StageAndTryDeliverAsync(
                        intent.CommandId,
                        terminal,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    _terminalOutbox.MarkResultPending(intent.CommandId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
                await TerminalizeEarlyAsync(intent.CommandId, ct)
                    .ConfigureAwait(false);
            }
        }
    }

    private Task TerminalizeEarlyAsync(
        string commandId,
        CancellationToken ct) => TerminalizeEarlyAsync(
            commandId,
            "pricing_execution_exception",
            ct);

    private Task TerminalizeEarlyAsync(
        string commandId,
        string code,
        CancellationToken ct) => _terminalOutbox.StageAndTryDeliverAsync(
            commandId,
            PricingTerminalAck.Early(code),
            ct);

    private static bool IsPermanentJobAuthorityFailure(string code) => code is
        "pricing_cost_basis_approval_revoked" or
        "pricing_cloud_authority_revoked" or
        "pricing_result_manual_reconciliation_required" or
        "pricing_cost_basis_approval_expired" or
        "pricing_cost_basis_approval_invalid" or
        "pricing_cost_basis_approval_required" or
        "pricing_job_authority_identity_invalid" or
        "pricing_job_authority_binding_missing" or
        "pricing_job_authority_binding_invalid";

}
