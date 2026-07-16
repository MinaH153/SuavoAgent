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
    private async Task ProcessSignedCommandAsync(JsonElement response, CancellationToken ct)
    {
        try
        {
            if (!response.TryGetProperty("data", out var data)) return;
            if (!data.TryGetProperty("signedCommand", out var scEl)) return;
            if (scEl.ValueKind == JsonValueKind.Null) return;

            if (_commandVerifier is null)
            {
                _logger.LogWarning("Signed command received but verifier not configured (no AgentId)");
                return;
            }

            // Compute data hash from the raw JSON data payload for signature verification.
            // This prevents payload tampering — the hash is included in the signed canonical.
            var dataHashValue = "";
            if (scEl.TryGetProperty("data", out var dataEl) && dataEl.ValueKind != JsonValueKind.Null)
                dataHashValue = SignedCommandVerifier.ComputeDataHash(dataEl.GetRawText());
            else
                dataHashValue = SignedCommandVerifier.ComputeDataHash(null);
            var expiresAt = dataEl.ValueKind == JsonValueKind.Object &&
                dataEl.TryGetProperty("expiresAt", out var expiresAtElement) &&
                expiresAtElement.ValueKind == JsonValueKind.String
                    ? expiresAtElement.GetString()
                    : null;

            var cmd = new SignedCommand(
                Command: scEl.TryGetProperty("command", out var c) ? c.GetString() ?? "" : "",
                AgentId: scEl.TryGetProperty("agentId", out var a) ? a.GetString() ?? "" : "",
                MachineFingerprint: scEl.TryGetProperty("machineFingerprint", out var m) ? m.GetString() ?? "" : "",
                Timestamp: scEl.TryGetProperty("timestamp", out var t) ? t.GetString() ?? "" : "",
                Nonce: scEl.TryGetProperty("nonce", out var n) ? n.GetString() ?? "" : "",
                KeyId: scEl.TryGetProperty("keyId", out var k) ? k.GetString() ?? "" : "",
                Signature: scEl.TryGetProperty("signature", out var s) ? s.GetString() ?? "" : "",
                DataHash: dataHashValue,
                ExpiresAt: expiresAt);

            var deferNonceConsumption = string.Equals(
                cmd.Command,
                PioneerRxApprovalInstallCommandContract.CommandName,
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                PricingApprovalContract.InstallCommandName,
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                PricingApprovalContract.RevokeCommandName,
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                "set_vision_config",
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                "update",
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                Release1ConvergenceCommand.Name,
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                SelfUninstallContract.CommandName,
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                "run_pricing_job",
                StringComparison.Ordinal) || string.Equals(
                cmd.Command,
                "find_and_run_pricing_job",
                StringComparison.Ordinal);
            var result = _commandVerifier.Verify(
                cmd,
                consumeNonce: !deferNonceConsumption);
            if (!result.IsValid)
            {
                // A Broker acceptance is the durable execution boundary. After
                // that receipt exists, Core may have been delayed/restarted past
                // the original cloud lease; recover the exact accepted nonce
                // from the receipt-bound request instead of stranding it. No new
                // actuation is admitted here: Broker already accepted it while
                // authority was current and the coordinator revalidates that
                // signed receipt, request digest, nonce, audience, and data.
                if (string.Equals(
                        cmd.Command,
                        SelfUninstallContract.CommandName,
                        StringComparison.Ordinal) &&
                    await HandleSelfUninstallAsync(scEl, cmd, ct)
                        .ConfigureAwait(false))
                {
                    _stateDb.TryRecordNonce(cmd.Nonce);
                    _commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce);
                    return;
                }
                _logger.LogWarning("core.command.signature_rejected");
                return;
            }

            // Command signatures authenticate the control plane; they never
            // confer workstation-observation authority. This is the single
            // dispatch boundary for every command, including future/unknown
            // names. Only the explicit audited maintenance set can run while
            // dormant. Observation commands hold a current signed execution
            // lease for the complete inline handler lifetime.
            var commandLifetime = ct;
            using var observationAdmission =
                ObservationActivationCommandPolicy.Admit(
                    cmd.Command,
                    _observationAuthority,
                    commandLifetime);
            if (!observationAdmission.Admitted)
            {
                RejectObservationCommand(cmd, observationAdmission.Code);
                return;
            }
            ct = observationAdmission.Token;

            // One-time Release 1 signing-root convergence is an append-only,
            // PHI-negative outbox. Persist the complete verified command before
            // consuming either nonce; all network phases then retry independently.
            if (string.Equals(
                    cmd.Command,
                    Release1ConvergenceCommand.Name,
                    StringComparison.Ordinal))
            {
                var registered = await HandleRelease1ConvergenceChallengeAsync(
                        scEl,
                        cmd,
                        ct)
                    .ConfigureAwait(false);
                if (registered)
                {
                    if (!_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
                        _logger.LogDebug(
                            "Release 1 challenge nonce already consumed in memory");
                    if (!_stateDb.TryRecordNonce(cmd.Nonce))
                        _logger.LogDebug(
                            "Release 1 challenge nonce already persisted");
                }
                return;
            }

            // fetch_patient is a one-shot cloud command. Persist its exact hash-only binding before
            // recording the nonce so a crash between verification and execution still leaves a
            // durable local retry. No patient query occurs during registration.
            if (string.Equals(cmd.Command, "fetch_patient", StringComparison.Ordinal))
            {
                if (!_commandVerifier.VerifyExecutionAuthority(cmd).IsValid)
                {
                    _logger.LogWarning("core.command.execution_authority_expired");
                    return;
                }
                if (!RegisterFetchPatientCommand(scEl, cmd)) return;
                if (!_stateDb.TryRecordNonce(cmd.Nonce))
                {
                    _logger.LogWarning("core.command.replay_rejected");
                    return;
                }

                _logger.LogInformation("core.command.verified");
                if (_approvedPatientRetrieval is not null)
                    await _approvedPatientRetrieval.RetryPendingAsync(ct);
                return;
            }

            // Register the exact PHI-minimal command before nonce persistence.
            // A crash or sent-command redelivery then converges through the
            // durable ledger without issuing a second verified SQL write.
            if (string.Equals(cmd.Command, "delivery_writeback", StringComparison.Ordinal))
            {
                var deliveryData = scEl.TryGetProperty("data", out var deliveryDataElement)
                    ? deliveryDataElement
                    : default;
                if (!DeliveryWritebackCommandContract.TryParse(
                        deliveryData,
                        out var deliveryCommand,
                        out var deliveryRejectionCode) ||
                    deliveryCommand is null)
                {
                    _logger.LogWarning(
                        "delivery_writeback rejected: {Reason}",
                        deliveryRejectionCode);
                    return;
                }
                var deliveryCommandId = deliveryCommand.CommandId;
                if (!_commandVerifier.VerifyExecutionAuthority(cmd).IsValid)
                {
                    _logger.LogWarning("core.command.execution_authority_expired");
                    return;
                }
                using var autopilotRun = _autopilotRuns.Register(
                    AutopilotRunKind.DeliveryWriteback,
                    ct);
                if (!autopilotRun.Admitted)
                {
                    // Persist the verified envelope rejection before ACK. Otherwise an ACK transport
                    // failure plus service restart could replay a command that local Stop had refused.
                    _stateDb.TryRecordNonce(cmd.Nonce);
                    await AckAutopilotAdmissionRejectedAsync(
                        deliveryCommandId,
                        autopilotRun,
                        ct).ConfigureAwait(false);
                    return;
                }

                if (!RegisterDeliveryWritebackCommand(deliveryCommand)) return;
                if (!_stateDb.TryRecordNonce(cmd.Nonce))
                    _logger.LogDebug(
                        "delivery_writeback envelope nonce already persisted: {EnvelopeNonce}",
                        cmd.Nonce);
                else
                    _logger.LogInformation(
                        "Verified signed delivery_writeback envelope {EnvelopeNonce}",
                        cmd.Nonce);

                if (_deliveryWriteback is not null)
                {
                    try
                    {
                        await _deliveryWriteback.RetryPendingAsync(autopilotRun.Token)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        RecordCancellationAudit(new AuditEntry(
                            TaskId: deliveryCommandId ?? cmd.Nonce,
                            EventType: "delivery_writeback_autopilot_cancelled",
                            FromState: "in_progress",
                            ToState: "retry_pending",
                            Trigger: "local_autopilot_control",
                            CommandId: cmd.Nonce,
                            RequesterId: "operator",
                            Actor: "operator",
                            SourceComponent: "heartbeat_worker",
                            CaptureReason: "local_autopilot_control"));
                        _logger.LogInformation(
                            "Delivery writeback processing cancelled by local Autopilot control; durable retry retained");
                    }
                }
                return;
            }

            // Approval installation is crash-resumable and command-idempotent. Execute before
            // persisting the envelope nonce so a kill between either atomic file replace can be
            // repaired by redelivery of the same signed envelope after restart.
            if (string.Equals(
                    cmd.Command,
                    PioneerRxApprovalInstallCommandContract.CommandName,
                    StringComparison.Ordinal))
            {
                var terminal = await HandleInstallPioneerRxApprovalAsync(scEl, ct).ConfigureAwait(false);
                if (terminal)
                {
                    if (!_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
                        _logger.LogDebug("PioneerRx approval envelope nonce already consumed in memory");
                    if (!_stateDb.TryRecordNonce(cmd.Nonce))
                        _logger.LogDebug("PioneerRx approval envelope nonce already persisted");
                }
                return;
            }

            // PIC pricing authority is an append-only, verified local ledger.
            // Consume the envelope nonce only after the durable result has been
            // acknowledged; a failed ACK can then be retried idempotently.
            if (cmd.Command is PricingApprovalContract.InstallCommandName or
                PricingApprovalContract.RevokeCommandName)
            {
                var acknowledged = await HandlePricingApprovalLedgerCommandAsync(
                        scEl,
                        cmd,
                        ct)
                    .ConfigureAwait(false);
                if (acknowledged)
                {
                    if (!_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
                        _logger.LogDebug(
                            "Pricing approval envelope nonce already consumed in memory");
                    if (!_stateDb.TryRecordNonce(cmd.Nonce))
                        _logger.LogDebug(
                            "Pricing approval envelope nonce already persisted");
                }
                return;
            }

            // Vision configuration is a durable local outbox. Register the
            // exact verified payload before consuming either nonce. Once that
            // commit succeeds, registry apply and cloud ACK retry on every
            // heartbeat even though the control plane serves the command once.
            if (string.Equals(cmd.Command, "set_vision_config", StringComparison.Ordinal))
            {
                var durablyRegistered = await HandleSetVisionConfigAsync(scEl, cmd, ct)
                    .ConfigureAwait(false);
                if (durablyRegistered)
                {
                    if (!_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
                        _logger.LogDebug("Vision configuration envelope nonce already consumed in memory");
                    if (!_stateDb.TryRecordNonce(cmd.Nonce))
                        _logger.LogDebug("Vision configuration envelope nonce already persisted");
                }
                return;
            }

            // Self-uninstall is evidence-first and crash-resumable. Do not burn
            // either nonce until the exact signed request is atomically published.
            // A transient archive failure may then be redelivered; a crash after
            // publication converges through the existing authenticated request.
            if (string.Equals(
                    cmd.Command,
                    SelfUninstallContract.CommandName,
                    StringComparison.Ordinal))
            {
                if (!_commandVerifier.VerifyExecutionAuthority(cmd).IsValid)
                {
                    _logger.LogWarning("core.command.execution_authority_expired");
                    return;
                }
                var published = await HandleSelfUninstallAsync(scEl, cmd, ct)
                    .ConfigureAwait(false);
                if (published)
                {
                    if (!_stateDb.TryRecordNonce(cmd.Nonce))
                        _logger.LogDebug("Self-uninstall envelope nonce already persisted");
                    if (!_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
                        _logger.LogDebug("Self-uninstall envelope nonce already consumed in memory");
                }
                return;
            }

            // Persistent nonce check (survives restarts). Record only AFTER
            // cryptographic verification, otherwise an attacker can burn a
            // future valid nonce by sending a forged envelope first.
            if (!_commandVerifier.VerifyExecutionAuthority(cmd).IsValid)
            {
                _logger.LogWarning("core.command.execution_authority_expired");
                return;
            }
            var isPricingCommand = cmd.Command is
                "run_pricing_job" or "find_and_run_pricing_job";
            var pricingCommandId = string.Empty;
            var pricingApprovalId = string.Empty;
            var pricingGrantDigest = string.Empty;
            if (isPricingCommand &&
                (_pricingTerminalAckOutbox is null ||
                 !TryReadPricingCommandId(scEl, out pricingCommandId) ||
                 !TryReadPricingAuthorityBinding(
                    scEl,
                    out pricingApprovalId,
                    out pricingGrantDigest)))
            {
                _logger.LogWarning("pricing_result_command_ineligible");
                return;
            }
            var pricingIntentRegistered = isPricingCommand;
            var authorityRecorded = pricingIntentRegistered
                ? _pricingTerminalAckOutbox!.TryRegisterVerifiedCommand(
                    cmd,
                    pricingCommandId,
                    cmd.Command,
                    pricingApprovalId,
                    pricingGrantDigest)
                : _stateDb.TryRecordNonce(cmd.Nonce);
            if (!authorityRecorded)
            {
                _logger.LogWarning("core.command.replay_rejected");
                return;
            }
            if (pricingIntentRegistered &&
                !_commandVerifier.TryConsumeVerifiedNonce(cmd.Nonce))
            {
                _logger.LogWarning("core.command.replay_rejected");
                await _pricingTerminalAckOutbox!.StageAndTryDeliverAsync(
                    pricingCommandId,
                    PricingTerminalAck.Early("pricing_execution_exception"),
                    ct).ConfigureAwait(false);
                return;
            }

            _logger.LogInformation("core.command.verified");

            switch (cmd.Command)
            {
                case "decommission":
                    await HandleRetiredDecommissionAsync(scEl, ct);
                    break;
                case "repair":
                case "repair_agent":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleRepairAgentAsync(scEl, cmd, ct));
                    break;
                case "collect_health_probe":
                    await HandleCollectHealthProbeAsync(scEl, cmd, ct);
                    break;
                case "fetch_diagnostics":
                    await HandleFetchDiagnosticsAsync(scEl, cmd, ct);
                    break;
                case "export_pioneerrx_shadow_fixture":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd,
                        () => PioneerRxShadowFixtureCommand.HandleAsync(
                            scEl, cmd, _options, _serviceProvider, _stateDb,
                            _cloudClient, _logger, ct));
                    break;
                case "update":
                    await HandleUpdateAsync(scEl, cmd, ct);
                    break;
                case "approve_pom":
                    await HandleApprovePomAsync(scEl, cmd, ct);
                    break;
                case "acknowledge_drift":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleAcknowledgeDriftAsync(scEl, ct));
                    break;
                case "approve_candidate":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                        return Task.CompletedTask;
                    });
                    break;
                case "reject_candidate":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.Demote);
                        return Task.CompletedTask;
                    });
                    break;
                case "reapprove_candidate":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.Promote);
                        return Task.CompletedTask;
                    });
                    break;
                case "force_relearn":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.ReLearn);
                        return Task.CompletedTask;
                    });
                    break;
                case "adjust_window":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.Recalibrate);
                        return Task.CompletedTask;
                    });
                    break;
                case "acknowledge_stale":
                    await ExecuteLiveCommandIfCurrentAsync(cmd, () =>
                    {
                        HandleFeedbackCommand(scEl, cmd, DirectiveType.Prune);
                        return Task.CompletedTask;
                    });
                    break;
                case "run_pricing_job":
                    // The nonce + in-flight intent were committed atomically
                    // before this detached task can exist. Recovery can only
                    // emit a finite failure or reconcile result evidence; it
                    // never invokes this handler again.
                    _ = Task.Run(
                        () => ExecutePricingCommandIfCurrentAsync(
                            cmd,
                            pricingCommandId,
                            commandLifetime,
                            token => HandleRunPricingJobAsync(scEl, token)),
                        CancellationToken.None);
                    break;
                case "find_and_run_pricing_job":
                    _ = Task.Run(
                        () => ExecutePricingCommandIfCurrentAsync(
                            cmd,
                            pricingCommandId,
                            commandLifetime,
                            token => HandleFindAndRunPricingJobAsync(scEl, token)),
                        CancellationToken.None);
                    break;
                case "show_cursor":
                case "show_intent_cursor":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleShowIntentCursorAsync(scEl, cmd, ct));
                    break;
                case "computer_use_observe":
                case "computer_use_propose":
                    await HandleComputerUseObserveProposeAsync(scEl, cmd, ct);
                    break;
                case "transition_auto_rule_approval":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleTransitionAutoRuleApprovalAsync(scEl, ct));
                    break;
                case "run_workflow":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleRunWorkflowAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "abort_workflow":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleAbortWorkflowAsync(scEl, cmd, ct));
                    break;
                case "update_selector":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleUpdateSelectorCommandAsync(scEl, cmd, ct));
                    break;
                case "navigate_app":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleNavigateAppAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "navigate_pricing":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleNavigatePricingAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "replay_template":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleReplayTemplateAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "run_learned_template":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleRunLearnedTemplateAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "explore_sandbox":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleSandboxExploreAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "replay_skill":
                    _ = Task.Run(
                        () => ExecuteDetachedObservationCommandIfCurrentAsync(
                            cmd,
                            commandLifetime,
                            token => HandleReplaySkillAsync(scEl, cmd, token)),
                        CancellationToken.None);
                    break;
                case "abort_navigation":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleAbortNavigationAsync(scEl, cmd, ct));
                    break;
                case "force_restart":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleForceRestartAsync(scEl, cmd, ct));
                    break;
                case "force_learning_phase":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleForceLearningPhaseAsync(scEl, cmd, ct));
                    break;
                case "extend_app_allowlist":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleExtendAppAllowlistAsync(scEl, cmd, ct));
                    break;
                case "discover_elements":
                    await HandleDiscoverElementsAsync(scEl, cmd, ct);
                    break;
                case "chat":
                    await HandleChatAsync(scEl, cmd, ct);
                    break;
                case "set_reasoning_config":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleSetReasoningConfigAsync(scEl, cmd, ct));
                    break;
                case "restart_helper":
                    await ExecuteLiveCommandIfCurrentAsync(
                        cmd, () => HandleRestartHelperAsync(scEl, cmd, ct));
                    break;
                default:
                    _logger.LogDebug("Unknown signed command: {Command}", cmd.Command);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "Signed command processing failed ({ErrorType})",
                ex.GetType().Name);
        }
    }

    private async Task ExecuteLiveCommandIfCurrentAsync(
        SignedCommand command,
        Func<Task> execute)
    {
        if (_commandVerifier is null ||
            !_commandVerifier.VerifyExecutionAuthority(command).IsValid)
        {
            _logger.LogWarning("core.command.execution_authority_expired");
            return;
        }
        await execute().ConfigureAwait(false);
    }

    private void RejectObservationCommand(SignedCommand command, string code)
    {
        // Persist the exact verified nonce before returning. An authority or
        // release-policy rejection may never execute later after activation,
        // restart, or control-plane redelivery.
        _stateDb.TryRecordNonce(command.Nonce);
        _commandVerifier?.TryConsumeVerifiedNonce(command.Nonce);
        _logger.LogWarning(
            "core.command.observation_policy_rejected code={Code}",
            code);
    }

    private async Task ExecuteDetachedObservationCommandIfCurrentAsync(
        SignedCommand command,
        CancellationToken lifetime,
        Func<CancellationToken, Task> execute)
    {
        if (lifetime.IsCancellationRequested || _commandVerifier is null ||
            !_commandVerifier.VerifyExecutionAuthority(command).IsValid)
        {
            _logger.LogWarning("core.command.execution_authority_expired");
            return;
        }

        using var admission = ObservationActivationCommandPolicy.Admit(
            command.Command,
            _observationAuthority,
            lifetime);
        if (!admission.Admitted)
        {
            _logger.LogWarning(
                "core.command.observation_policy_rejected code={Code}",
                admission.Code);
            return;
        }
        await execute(admission.Token).ConfigureAwait(false);
    }

    private async Task ExecutePricingCommandIfCurrentAsync(
        SignedCommand command,
        string commandId,
        CancellationToken lifetime,
        Func<CancellationToken, Task> execute)
    {
        if (lifetime.IsCancellationRequested || _commandVerifier is null ||
            !_commandVerifier.VerifyExecutionAuthority(command).IsValid)
        {
            _logger.LogWarning("core.command.execution_authority_expired");
            await AckPricingFailureAsync(
                    commandId,
                    PricingTerminalAck.Early("pricing_execution_exception"),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }

        using var admission = ObservationActivationCommandPolicy.Admit(
            command.Command,
            _observationAuthority,
            lifetime);
        if (!admission.Admitted)
        {
            _logger.LogWarning(
                "core.command.observation_policy_rejected code={Code}",
                admission.Code);
            await AckPricingFailureAsync(
                    commandId,
                    PricingTerminalAck.Early("pricing_execution_exception"),
                    CancellationToken.None)
                .ConfigureAwait(false);
            return;
        }
        await execute(admission.Token).ConfigureAwait(false);
    }

}
