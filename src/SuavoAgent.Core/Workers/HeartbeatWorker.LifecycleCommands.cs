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
    private async Task HandleRetiredDecommissionAsync(JsonElement scEl, CancellationToken ct)
    {
        var data = scEl.TryGetProperty("data", out var value) ? value : scEl;
        var commandId = data.ValueKind == JsonValueKind.Object &&
                        data.TryGetProperty("commandId", out var id)
            ? id.GetString()
            : null;
        _logger.LogWarning("Retired decommission command rejected; use self_uninstall");
        if (!string.IsNullOrWhiteSpace(commandId) && _cloudClient is not null)
        {
            try
            {
                await _cloudClient.AckCommandAsync(
                    commandId,
                    false,
                    new { code = "command_retired_use_self_uninstall" },
                    "command_retired_use_self_uninstall",
                    ct);
            }
            catch { /* acknowledgement is non-authoritative and must not cause local mutation */ }
        }
    }

    /// <summary>
    /// Evidence-safe remote self-uninstall. The exact already-verified command is handed to Broker
    /// only after the terminal audit event is appended, the resulting archive is stored, and the
    /// cloud returns a digest-matched signed receipt. Raw command/archive payloads are never logged.
    /// </summary>
    private async Task<bool> HandleSelfUninstallAsync(
        JsonElement scEl,
        SignedCommand command,
        CancellationToken ct)
    {
        if (!scEl.TryGetProperty("data", out var dataElement) ||
            dataElement.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("Self-uninstall blocked: command_data_missing");
            return false;
        }

        var dataJson = dataElement.GetRawText();
        if (!SelfUninstallContract.TryReadCommandId(dataJson, out var commandId))
        {
            _logger.LogWarning("Self-uninstall blocked: command_id_invalid");
            return false;
        }

        async Task AckAsync(bool ok, object? result, string? error)
        {
            if (_cloudClient is null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, error, ct); }
            catch { /* command acknowledgement is non-authoritative */ }
        }

        if (_cloudClient is null)
        {
            _logger.LogWarning("Self-uninstall blocked: cloud_client_unavailable");
            return false;
        }

        var requestPath = string.IsNullOrWhiteSpace(_options.SelfUninstallRequestPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent",
                SelfUninstallContract.RequestFileName)
            : _options.SelfUninstallRequestPath;

        var result = await SelfUninstallCoordinator.PrepareAsync(
            _stateDb,
            _options,
            command,
            dataJson,
            commandId,
            requestPath,
            _cloudClient.UploadAuditArchiveAsync,
            RemoteCommandTrust.CreateProductionKeyRegistry(),
            () => DateTimeOffset.UtcNow,
            ct);

        if (!result.IsReady)
        {
            _logger.LogWarning("Self-uninstall blocked: {Code}", result.Code);
            // Transport/storage failures remain sent and retryable. Structural
            // failures are terminally NACKed so the operator sees the exact gate.
            if (!IsRetryableSelfUninstallPreparation(result.Code))
                await AckAsync(false, new { phase = "blocked", code = result.Code }, result.Code);
            return false;
        }

        _logger.LogWarning(
            "Self-uninstall evidence archived and authenticated request queued for Broker");
        // Do not ACK success here. The cloud treats a successful self_uninstall ACK as terminal
        // and revokes the agent immediately, but Broker has only queued detached maintenance.
        // Final executed/revoke must be owned by a future verified maintenance-completion receipt.
        return true;
    }

    private static bool IsRetryableSelfUninstallPreparation(string code) =>
        code is "archive_upload_failed" or "archive_ack_missing" or
            "request_write_failed" or "self_uninstall_preparation_failed" or
            "broker_acceptance_pending";

    /// <summary>
    /// Signed cohort staging. Core downloads only into ProgramData and publishes the exact verified
    /// command envelope for the LocalSystem Watchdog; it never mutates Program Files or self-restarts.
    /// </summary>
    private async Task HandleUpdateAsync(
        JsonElement scEl,
        SignedCommand command,
        CancellationToken ct)
    {
        if (_updateInProgress) return;

        string? targetVersion = null;
        string? targetChannel = null;
        try
        {
            var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
            var manifestStr = dataEl.TryGetProperty("manifest", out var m) ? m.GetString() : null;
            var signatureHex = dataEl.TryGetProperty("manifestSignature", out var sig) ? sig.GetString() : null;
            var commandId = dataEl.TryGetProperty("commandId", out var commandIdElement)
                ? commandIdElement.GetString()
                : null;
            targetChannel = dataEl.TryGetProperty("channel", out var ch) ? ch.GetString() : "stable";

            if (string.IsNullOrEmpty(manifestStr))
            {
                _logger.LogWarning("Signed update command missing manifest — rejecting");
                WriteUpdateHealthEvidence(
                    "failed",
                    targetVersion: null,
                    lastErrorKind: "missing_manifest",
                    consecutiveFailures: 1,
                    channel: targetChannel);
                return;
            }

            var manifest = UpdateManifest.Parse(manifestStr);
            if (manifest is null)
            {
                _logger.LogWarning("Signed update command has malformed manifest — rejecting");
                WriteUpdateHealthEvidence(
                    "failed",
                    targetVersion: null,
                    lastErrorKind: "malformed_manifest",
                    consecutiveFailures: 1,
                    channel: targetChannel);
                return;
            }
            targetVersion = manifest.Version;

            if (string.IsNullOrWhiteSpace(commandId))
            {
                _logger.LogWarning("Signed update command missing command identity — rejecting");
                WriteUpdateHealthEvidence(
                    "failed", targetVersion, "missing_command_id", 1, targetChannel);
                return;
            }

            var receipt = _stateDb.RegisterUpdateCommandReceipt(
                commandId,
                command.Nonce,
                command.DataHash,
                manifest.Version);
            if (!receipt.Accepted)
            {
                _logger.LogWarning("Signed update command receipt binding rejected: {Code}", receipt.Code);
                WriteUpdateHealthEvidence(
                    "failed", targetVersion, receipt.Code, 1, targetChannel);
                return;
            }

            var isSameVersion = UpdateActivationContract.VersionsEquivalent(
                manifest.Version,
                _options.Version);
            if (!isSameVersion && receipt.State is "staged" or "confirmed")
            {
                WriteUpdateHealthEvidence(
                    receipt.State == "confirmed" ? "current" : "staged_for_system_activation",
                    targetVersion, null, 0, targetChannel);
                return;
            }

            if (isSameVersion)
            {
                HandleSameVersionUpdate(
                    dataEl,
                    manifest,
                    manifestStr,
                    signatureHex,
                    commandId,
                    command,
                    receipt.State,
                    targetChannel);
                return;
            }

            // Canary channel validation: only apply updates matching our assigned channel.
            // Cloud assigns channel (stable/canary/beta) via heartbeat response.
            var myChannel = _lastUpdateChannel ?? _options.UpdateChannel;
            if (!string.Equals(targetChannel, myChannel, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(targetChannel, "stable", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug("Update channel mismatch: target={Target}, mine={Mine} — skipping",
                    targetChannel, myChannel);
                WriteUpdateHealthEvidence(
                    "skipped_channel",
                    targetVersion,
                    lastErrorKind: null,
                    consecutiveFailures: 0,
                    channel: targetChannel);
                return;
            }

            _updateInProgress = true;
            _logger.LogInformation("Signed package update: v{Version} ({Count} binaries)",
                manifest.Version, manifest.HasMaintenance ? 5 : manifest.HasWatchdog ? 4 : 3);
            WriteUpdateHealthEvidence(
                "applying",
                targetVersion,
                lastErrorKind: null,
                consecutiveFailures: 0,
                channel: targetChannel);

            var dataJson = dataEl.GetRawText();
            var staged = await SelfUpdater.TryApplyPackageUpdateAsync(
                manifest,
                signatureHex ?? "",
                command,
                dataJson,
                _logger,
                ct);
            if (staged)
                _stateDb.MarkUpdateCommandReceipt(commandId, "staged");
            WriteUpdateHealthEvidence(
                staged ? "staged_for_system_activation" : "failed",
                targetVersion,
                lastErrorKind: staged ? null : "staging_failed",
                consecutiveFailures: staged ? 0 : 1,
                channel: targetChannel);
            _updateInProgress = false;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            WriteUpdateHealthEvidence(
                "failed",
                targetVersion,
                lastErrorKind: ex.GetType().Name,
                consecutiveFailures: 1,
                channel: targetChannel);
            _updateInProgress = false;
        }
    }

    private void WriteUpdateHealthEvidence(
        string status,
        string? targetVersion,
        string? lastErrorKind,
        int consecutiveFailures,
        string? channel)
    {
        try
        {
            RuntimeHealthEvidence.WriteUpdateHealth(
                RuntimeHealthEvidence.UpdateHealthPath(),
                status,
                targetVersion,
                DateTimeOffset.UtcNow,
                status is "current" ? DateTimeOffset.UtcNow : null,
                consecutiveFailures,
                lastErrorKind,
                channel ?? _lastUpdateChannel ?? _options.UpdateChannel);
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
        }
    }

    private async Task HandleApprovePomAsync(
        JsonElement scEl,
        SignedCommand signedCommand,
        CancellationToken ct)
    {
        _ = signedCommand; // signature/audience/nonce were already verified by the dispatcher.
        var dataEl = scEl.TryGetProperty("data", out var data) ? data : default;

        async Task SendCommittedReceiptAsync(
            PomApprovalCommand committedCommand,
            AgentStateDb.PomApprovalLedgerResult result)
        {
            if (PomApprovalCommandContract.IsExpired(
                    committedCommand,
                    DateTimeOffset.UtcNow))
                return;
            if (_cloudClient is null) return;
            try
            {
                var ledger = _stateDb.GetPomApprovalLedger(committedCommand.CommandId)
                    ?? throw new InvalidOperationException("POM approval ledger is missing.");
                var signer = _serviceProvider.GetService<IDeviceAuthoritySigner>()
                    ?? throw new InvalidOperationException("Device authority signer is unavailable.");
                var persisted = _stateDb.GetOrCreatePomDeviceReceipt(
                    committedCommand,
                    result,
                    ledger,
                    _options,
                    signer);
                var cloudReceipt = await _cloudClient.SendPomActivationReceiptAsync(
                    persisted.Signed,
                    ct);
                if (cloudReceipt is null) return;
                if (result.Succeeded != (cloudReceipt.Status == "executed"))
                    throw new InvalidOperationException("Cloud POM receipt status mismatch.");
                _stateDb.MarkPomDeviceReceiptAccepted(
                    committedCommand.CommandId,
                    cloudReceipt.SourceBindingId);
            }
            catch (Exception ex)
            {
                // The SQLite counter + exact signed envelope committed before
                // this call. Signed-command redelivery retries those same bytes.
                _logger.LogWarning(
                    "approve_pom device receipt delivery deferred (errorType={ErrorType})",
                    ex.GetType().Name);
            }
        }

        if (!PomApprovalCommandContract.TryParse(dataEl, out var command, out var schemaError))
        {
            if (PomApprovalCommandContract.TryGetLedgerIdentity(
                    dataEl,
                    out var malformedCommandId,
                    out var malformedPayloadDigest))
            {
                try
                {
                    var recorded = _stateDb.RecordMalformedPomApproval(
                        malformedCommandId,
                        malformedPayloadDigest,
                        schemaError);
                    _logger.LogWarning(
                        "approve_pom malformed command durably rejected ({Code}); no generic ACK is permitted",
                        recorded.OutcomeCode);
                }
                catch (Exception ex)
                {
                    // No ACK without a durable local terminal row.
                    _logger.LogWarning(
                        "approve_pom malformed-command ledger failed (errorType={ErrorType})",
                        ex.GetType().Name);
                }
            }
            else
            {
                _logger.LogWarning("approve_pom rejected: command_identity_invalid");
            }
            return;
        }
        var approvalCommand = command!;

        if (PomApprovalCommandContract.IsExpired(
                approvalCommand,
                DateTimeOffset.UtcNow))
        {
            try
            {
                _stateDb.CompletePomApproval(
                    approvalCommand,
                    succeeded: false,
                    "pom_approval_command_expired");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "approve_pom expiry ledger failed (errorType={ErrorType})",
                    ex.GetType().Name);
            }
            return;
        }

        AgentStateDb.PomApprovalLedgerResult applied;
        try
        {
            applied = _stateDb.ApplyPomApproval(approvalCommand, _options.PharmacyId ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "approve_pom local transaction failed (errorType={ErrorType})",
                ex.GetType().Name);
            try
            {
                var committedFailure = _stateDb.CompletePomApproval(
                    approvalCommand,
                    succeeded: false,
                    "pom_approval_local_commit_failed");
                await SendCommittedReceiptAsync(approvalCommand, committedFailure);
            }
            catch
            {
                // Fail closed: an uncommitted outcome is never acknowledged.
            }
            return;
        }

        if (applied.Kind is AgentStateDb.PomApprovalLedgerKind.Terminal or
            AgentStateDb.PomApprovalLedgerKind.Conflict)
        {
            await SendCommittedReceiptAsync(approvalCommand, applied);
            return;
        }

        // The local DB validation above may be slow on a damaged workstation.
        // Re-check immediately before the active adapter pointer changes.
        if (PomApprovalCommandContract.IsExpired(
                approvalCommand,
                DateTimeOffset.UtcNow))
        {
            try
            {
                _stateDb.CompletePomApproval(
                    approvalCommand,
                    succeeded: false,
                    "pom_approval_command_expired");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "approve_pom pre-activation expiry ledger failed (errorType={ErrorType})",
                    ex.GetType().Name);
            }
            return;
        }

        var registry = _serviceProvider.GetService<IActivePmsAdapterRegistry>();
        var activation = registry?.ActivateApproved(approvalCommand.SessionId);
        var succeeded = activation?.IsActive == true;
        var outcomeCode = activation?.Outcome switch
        {
            AdapterActivationOutcome.Activated => "pom_approval_activated",
            AdapterActivationOutcome.AlreadyActive => "pom_approval_already_active",
            AdapterActivationOutcome.Rejected => SafePomActivationFailure(
                activation.Reason,
                "pom_approval_activation_rejected"),
            AdapterActivationOutcome.Failed => SafePomActivationFailure(
                activation.Reason,
                "pom_approval_activation_failed"),
            _ => "pom_approval_registry_unavailable",
        };

        AgentStateDb.PomApprovalLedgerResult terminal;
        try
        {
            terminal = _stateDb.CompletePomApproval(approvalCommand, succeeded, outcomeCode);
        }
        catch (Exception ex)
        {
            // Registry state without a committed receipt is deliberately not
            // ACKed. Redelivery resumes from the durable "applying" row.
            _logger.LogWarning(
                "approve_pom terminal ledger failed (errorType={ErrorType})",
                ex.GetType().Name);
            return;
        }

        if (terminal.Succeeded)
        {
            try
            {
                _stateDb.AppendLearningAudit(
                    approvalCommand.SessionId,
                    "worker",
                    "pom_approval_activated",
                    $"command:{approvalCommand.CommandId}",
                    phiScrubbed: false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "approve_pom local audit append failed (errorType={ErrorType})",
                    ex.GetType().Name);
            }
        }

        await SendCommittedReceiptAsync(approvalCommand, terminal);
    }

    private static string SafePomActivationFailure(string reason, string fallback)
    {
        var candidate = $"pom_approval_{reason}";
        return PomApprovalCommandContract.IsSafeResultCode(candidate) ? candidate : fallback;
    }

    private void HandleFeedbackCommand(JsonElement scEl, SignedCommand cmd, DirectiveType directiveType)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var correlationKey = dataEl.TryGetProperty("correlationKey", out var ck) ? ck.GetString() ?? "" : "";
        var sessionId = _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");

        if (string.IsNullOrEmpty(correlationKey) || string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("{Command}: missing correlationKey or no active session", cmd.Command);
            return;
        }

        var payloadJson = dataEl.ValueKind != JsonValueKind.Undefined
            ? dataEl.GetRawText()
            : null;

        var evt = new FeedbackEvent(
            SessionId: sessionId,
            EventType: "operator_command",
            Source: "operator",
            SourceId: cmd.Nonce,
            TargetType: "correlation_key",
            TargetId: correlationKey,
            PayloadJson: payloadJson,
            DirectiveType: directiveType,
            DirectiveJson: payloadJson,
            CausalChainJson: null);

        _stateDb.InsertFeedbackEvent(evt);

        _logger.LogInformation("core.feedback.queued directive={Directive}", directiveType);

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: correlationKey,
            EventType: "feedback_command",
            FromState: "",
            ToState: directiveType.ToString(),
            Trigger: cmd.Command,
            CommandId: cmd.Nonce,
            RequesterId: "operator"));
    }

    /// <summary>
    /// Applies the exact signed transition under one SQLite transaction. The durable command id,
    /// not the fresh envelope nonce, is the business idempotency key. Approval admits the exact
    /// digest into the live registry; every other target status removes it before ACK.
    /// </summary>
    private async Task HandleTransitionAutoRuleApprovalAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var data) ? data : default;
        if (!AutoRuleCommandContracts.TryParseTransition(dataEl, out var parsed, out var schemaError))
        {
            var malformedCommandId = AutoRuleCommandContracts.TryGetCommandId(dataEl);
            if (malformedCommandId is not null && _cloudClient is not null)
                await _cloudClient.AckCommandAsync(malformedCommandId, false, null, schemaError, ct);
            _logger.LogWarning("transition_auto_rule_approval rejected: {Code}", schemaError);
            return;
        }

        var command = parsed!;
        SuavoAgent.Core.Reasoning.PreparedLearnedRule? prepared = null;
        if (command.ToStatus == AgentStateDb.AutoRuleStatus.Approved)
        {
            try
            {
                prepared = _activeLearnedRules?.Prepare(
                    command.ApprovalId, command.RuleId, command.TemplateId, command.YamlSha256);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogSafeWarning(ex);
            }
        }

        var result = _stateDb.ApplyAutoRuleTransition(command, prepared is not null);
        if (result.Succeeded)
        {
            if (command.ToStatus == AgentStateDb.AutoRuleStatus.Approved && prepared is null)
            {
                if (_cloudClient is not null)
                    await _cloudClient.AckCommandAsync(
                        command.CommandId, false, null, "runtime_registry_admission_failed", ct);
                return;
            }
            try
            {
                if (command.ToStatus == AgentStateDb.AutoRuleStatus.Approved)
                    _activeLearnedRules!.Admit(prepared!);
                else
                    _activeLearnedRules?.Remove(command.RuleId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogSafeWarning(ex);
                if (_cloudClient is not null)
                    await _cloudClient.AckCommandAsync(
                        command.CommandId, false, null, "runtime_registry_admission_failed", ct);
                return;
            }

            if (!result.Replay)
            {
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: command.RuleId,
                    EventType: "auto_rule_approval_transition",
                    FromState: command.FromStatus.ToString(),
                    ToState: command.ToStatus.ToString(),
                    Trigger: "signed_command",
                    CommandId: command.CommandId,
                    RequesterId: command.ApprovedBy ?? "operator"));
            }
        }

        if (_cloudClient is not null)
        {
            await _cloudClient.AckCommandAsync(
                command.CommandId,
                result.Succeeded,
                new
                {
                    approval_id = command.ApprovalId,
                    rule_id = command.RuleId,
                    status = command.ToStatus.ToString(),
                    result_code = result.ResultCode,
                },
                result.Succeeded ? null : result.ResultCode,
                ct);
        }
    }

    private async Task HandleAcknowledgeDriftAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var action = dataEl.TryGetProperty("action", out var a) ? a.GetString() : null;
        var incidentId = dataEl.TryGetProperty("incidentId", out var iid) ? iid.GetString() : null;
        var pharmacyId = _options.PharmacyId ?? "";

        if (string.IsNullOrEmpty(action))
        {
            _logger.LogWarning("acknowledge_drift: missing action");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            pharmacyId, "canary_ack", "drift_hold", action,
            $"acknowledge_drift:{action}",
            CommandId: incidentId));

        if (action == "resume_supervised")
        {
            _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
            _logger.LogInformation("Drift acknowledged — resuming in supervised mode");
        }
        else if (action == "approve_new_baseline")
        {
            var targetEpoch = dataEl.TryGetProperty("targetSchemaEpoch", out var te) ? te.GetInt32() : 0;
            _stateDb.ClearCanaryHold(pharmacyId, "pioneerrx");
            _logger.LogInformation("Drift acknowledged — new baseline approved, epoch {Epoch}", targetEpoch);
        }
        else
        {
            _logger.LogWarning("acknowledge_drift: unknown action '{Action}'", action);
        }

        await Task.CompletedTask;
    }

}
