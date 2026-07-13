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
    private async Task HandleReplayTemplateAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() : null;
        if (string.IsNullOrWhiteSpace(taskKey))
        {
            await AckAsync(false, null, "missing_taskKey");
            return;
        }

        SuavoAgent.Contracts.Learning.WorkflowTemplate? template = null;
        try
        {
            if (dataEl.TryGetProperty("template", out var t) && t.ValueKind == JsonValueKind.Object)
                template = t.Deserialize<SuavoAgent.Contracts.Learning.WorkflowTemplate>();
        }
        catch (Exception) { template = null; }

        if (template is null || template.Steps is null || template.Steps.Count == 0)
        {
            await AckAsync(false, null, "malformed_template");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r ? r : Guid.NewGuid().ToString("n");
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;
        var dryRun = !(dataEl.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False);

        // This legacy surface accepts an inline template and has no durable approvalId/digest/run ledger.
        // It is therefore verification-only. All live learned actuation must use run_learned_template,
        // whose exact DB + active-registry binding and no-replay ledger are enforced before every click.
        if (!dryRun)
        {
            _logger.LogWarning(
                "replay_template: forcing dry-run — live learned actuation requires the durable run_learned_template path");
            dryRun = true;
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Navigation, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAutopilotAdmissionRejectedAsync(commandId, autopilotRun, ct)
                .ConfigureAwait(false);
            return;
        }
        var runToken = autopilotRun.Token;

        // Share the navigation semaphore: replay and navigate both actuate, so never concurrently.
        var semaphoreHeld = false;
        try
        {
            if (!await _navigationSemaphore.WaitAsync(0, runToken).ConfigureAwait(false))
            {
                await AckAsync(false, null, "actuation_already_running");
                return;
            }
            semaphoreHeld = true;

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "replay_template_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"replay template={template.TemplateId} dryRun={dryRun} steps={template.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: _options.EnableTaskAutonomy,
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            var helperGateState = await ReadHelperActuationGateAsync(
                "replay_template",
                runToken).ConfigureAwait(false);

            var replayer = SuavoAgent.Core.Agentic.Replication.ReplayFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline,
                helperGateState);

            var plan = SuavoAgent.Core.Agentic.Replication.TemplatePlanCompiler.Compile(template);
            var baseContext = new SuavoAgent.Core.Agentic.ActuationContext(pharmacyId, taskKey, dryRun);
            var objective = new SuavoAgent.Core.Agentic.AgentObjective($"replay:{template.SkillId}", taskKey, pharmacyId);
            var options = new SuavoAgent.Core.Agentic.Replication.TemplateReplayOptions();

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            navCts.CancelAfter(TimeSpan.FromSeconds(deadlineSeconds));
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.Replication.ReplayResult result;
            try
            {
                result = await replayer.ReplayAsync(plan, objective, baseContext, options, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            _logger.LogInformation(
                "replay_template run={RunId} outcome={Outcome} steps={Steps} failedOrdinal={Ord}",
                runId, result.Outcome, result.StepsCompleted, result.FailedOrdinal);

            await AckAsync(
                ok: result.Outcome == SuavoAgent.Core.Agentic.Replication.ReplayOutcome.Completed,
                result: new
                {
                    run_id = runId,
                    outcome = result.Outcome.ToString(),
                    steps_completed = result.StepsCompleted,
                    failed_ordinal = result.FailedOrdinal,
                    detail = result.Detail,
                },
                err: result.Outcome == SuavoAgent.Core.Agentic.Replication.ReplayOutcome.Completed ? null : result.Outcome.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var cancellationSource = autopilotRun.Token.IsCancellationRequested
                ? "local_autopilot_control"
                : "dashboard_abort";
            RecordCancellationAudit(new AuditEntry(
                TaskId: runId,
                EventType: "replay_template_cancelled",
                FromState: "in_progress",
                ToState: "cancelled",
                Trigger: cancellationSource,
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: cancellationSource));
            await AckAsync(
                false,
                new
                {
                    run_id = runId,
                    outcome = "Cancelled",
                    steps_completed = 0,
                    failed_ordinal = (int?)null,
                },
                "navigation_cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "replay_template execution exception ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "navigation_execution_exception");
        }
        finally
        {
            if (semaphoreHeld) _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// run_learned_template — FSD "autopilot engage": deterministically execute an operator-APPROVED learned
    /// <see cref="SuavoAgent.Contracts.Learning.WorkflowTemplate"/> by replaying its CLICK-family steps through
    /// the same fail-closed perceiver / safety / actuator seam via
    /// <see cref="SuavoAgent.Core.Agentic.Replication.GatedTemplateExecutor"/>. Distinct from replay_template
    /// (the internal harvest path) by four fail-closed gates: the template must exist + be active; its auto-rule
    /// approval must be <c>Approved</c> (never Pending / Shadow / Rejected / missing); any Type / PressKey /
    /// writeback step refuses the WHOLE template (v1 click-only); and every click still passes the per-action
    /// safety gate (a dry-run verdict STOPS — it never actuates). Shares the navigation semaphore + the
    /// abort_navigation cancellation seam. Ack is STRUCTURAL ONLY (outcome + counts + template-id prefix, no PHI).
    /// </summary>
    private async Task HandleRunLearnedTemplateAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        _ = cmd; // Envelope nonce is replay protection only; business idempotency uses data.commandId.
        var dataEl = scEl.TryGetProperty("data", out var data) ? data : default;
        if (!AutoRuleCommandContracts.TryParseRun(dataEl, out var parsed, out var schemaError))
        {
            var malformedCommandId = AutoRuleCommandContracts.TryGetCommandId(dataEl);
            if (malformedCommandId is not null && _cloudClient is not null)
                await _cloudClient.AckCommandAsync(malformedCommandId, false, null, schemaError, ct);
            _logger.LogWarning("run_learned_template rejected: {Code}", schemaError);
            return;
        }

        var command = parsed!;
        var runtimeExact = _activeLearnedRules is not null &&
            _activeLearnedRules.TryGetExact(
                command.ApprovalId, command.RuleId, command.TemplateId, command.YamlSha256, out _);
        var begin = _stateDb.BeginAutoRuleRun(command, _autoRuleRunOwnerId, runtimeExact);

        async Task AckTerminalAsync(bool ok, string outcome, int steps, int? failedOrdinal)
        {
            if (_cloudClient is null) return;
            await _cloudClient.AckCommandAsync(
                command.CommandId,
                ok,
                new
                {
                    approval_id = command.ApprovalId,
                    rule_id = command.RuleId,
                    template_id = command.TemplateId,
                    run_id = command.RunId,
                    outcome,
                    steps_completed = steps,
                    failed_ordinal = failedOrdinal,
                },
                ok ? null : outcome,
                ct);
        }

        async Task CommitFailureAndAckAsync(string outcome)
        {
            if (_stateDb.CompleteAutoRuleRun(
                    command.CommandId, _autoRuleRunOwnerId, false, outcome, 0, null))
            {
                await AckTerminalAsync(false, outcome, 0, null);
            }
            else
            {
                _logger.LogError(
                    "run_learned_template refused ACK because terminal commit failed for {CommandId}",
                    command.CommandId);
            }
        }

        if (begin.Kind == AgentStateDb.AutoRuleRunBeginKind.InProgress)
            return; // The first delivery owns execution and will ACK after its terminal commit.
        if (begin.Kind is AgentStateDb.AutoRuleRunBeginKind.Terminal or AgentStateDb.AutoRuleRunBeginKind.Conflict)
        {
            await AckTerminalAsync(begin.Succeeded, begin.OutcomeCode, begin.StepsCompleted, begin.FailedOrdinal);
            return;
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Navigation, ct);
        if (!autopilotRun.Admitted)
        {
            await CommitFailureAndAckAsync(autopilotRun.RejectionCode ?? "autopilot_paused")
                .ConfigureAwait(false);
            return;
        }
        var runToken = autopilotRun.Token;

        var semaphoreHeld = false;
        try
        {
            SuavoAgent.Contracts.Ipc.ActuationGateState? gateState = null;
            if (_actuationGateway is not null)
            {
                using var gateCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
                gateCts.CancelAfter(TimeSpan.FromSeconds(5));
                try { gateState = await _actuationGateway.GetStateAsync(gateCts.Token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException || !runToken.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "run_learned_template could not read Helper gate state ({ErrorType})",
                        ex.GetType().Name);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var gateOpen = gateState is
            {
                Enabled: true,
                DryRun: false,
                KillSwitchTrippedUtc: null,
                CompromiseDetected: false,
            } && (gateState.PausedUntilUtc is null || gateState.PausedUntilUtc <= now);
            if (!gateOpen)
            {
                await CommitFailureAndAckAsync("helper_gate_closed");
                return;
            }

            if (!await _navigationSemaphore.WaitAsync(0, runToken).ConfigureAwait(false))
            {
                await CommitFailureAndAckAsync("navigation_already_running");
                return;
            }
            semaphoreHeld = true;

            var row = _stateDb.GetWorkflowTemplate(command.TemplateId);
            if (row is null || row.RetiredAt is not null || row.CaptureOnly)
            {
                await CommitFailureAndAckAsync("template_not_active");
                return;
            }

            SuavoAgent.Contracts.Learning.WorkflowTemplate template;
            try { template = SuavoAgent.Core.Learning.TemplateRuleGenerator.Rehydrate(row); }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                await CommitFailureAndAckAsync("template_unreadable");
                return;
            }

            var idSuffix = command.TemplateId[..12];
            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: command.RunId,
                EventType: "run_learned_template_received",
                FromState: "queued",
                ToState: "running",
                Trigger: "signed_command",
                CommandId: command.CommandId,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"run approved template={idSuffix} skill={row.SkillId} steps={template.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(command.DeadlineSeconds);
            var approvedScopes = SuavoAgent.Core.Agentic.Replication.TemplatePlanCompiler
                .Compile(template)
                .Where(step => step.Action is { } action &&
                    SuavoAgent.Core.Agentic.Adapters.NavigateSafety.IsDestructive(action))
                .Select(step => SuavoAgent.Core.Autonomy.TaskAutonomyScope.Build(
                    row.SkillId,
                    SuavoAgent.Core.Agentic.Adapters.NavigateSafety.TargetProcess(step.Action!),
                    step.Action!.Verb ?? step.Action.Kind.ToString(),
                    PricingExecutorMode.UiaFirst))
                .ToHashSet(StringComparer.Ordinal);
            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: true,
                ExecutorMode: PricingExecutorMode.UiaFirst,
                AllowLiveActuation: true,
                OperatorApprovedScopes: approvedScopes);

            SuavoAgent.Contracts.Ipc.ActuationGateState? ExactGateState() =>
                _activeLearnedRules is not null && _activeLearnedRules.TryGetExact(
                    command.ApprovalId, command.RuleId, command.TemplateId, command.YamlSha256, out _)
                    ? gateState
                    : null;

            var executor = SuavoAgent.Core.Agentic.Replication.ReplayFactory.CreateExecutor(
                _serviceProvider, safetyOptions, charter, audit, deadline, ExactGateState);
            var baseContext = new SuavoAgent.Core.Agentic.ActuationContext(
                pharmacyId, row.SkillId, DryRun: false);
            var objective = new SuavoAgent.Core.Agentic.AgentObjective(
                $"run:{idSuffix}", row.SkillId, pharmacyId);
            var options = new SuavoAgent.Core.Agentic.Replication.TemplateReplayOptions();

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            navCts.CancelAfter(TimeSpan.FromSeconds(command.DeadlineSeconds));
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = command.RunId;
            }

            SuavoAgent.Core.Agentic.Replication.GatedReplayResult result;
            try
            {
                result = await executor.ExecuteAsync(
                    template, objective, baseContext, options, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock)
                {
                    _activeNavigationCts = null;
                    _activeNavigationRunId = null;
                }
            }

            var succeeded = result.Outcome ==
                SuavoAgent.Core.Agentic.Replication.GatedReplayOutcome.Completed;
            var outcome = result.Outcome.ToString();
            if (!_stateDb.CompleteAutoRuleRun(
                    command.CommandId, _autoRuleRunOwnerId, succeeded, outcome,
                    result.StepsCompleted, result.FailedOrdinal))
            {
                _logger.LogError(
                    "run_learned_template terminal commit rejected for command {CommandId}",
                    command.CommandId);
                return;
            }

            _logger.LogInformation(
                "run_learned_template run={RunId} template={Tid} outcome={Outcome} steps={Steps}",
                command.RunId, idSuffix, result.Outcome, result.StepsCompleted);
            await AckTerminalAsync(succeeded, outcome, result.StepsCompleted, result.FailedOrdinal);
        }
        catch (OperationCanceledException)
        {
            var committed = _stateDb.CompleteAutoRuleRun(
                command.CommandId, _autoRuleRunOwnerId, false, "cancelled_no_replay", 0, null);
            if (committed && !ct.IsCancellationRequested)
                await AckTerminalAsync(false, "cancelled_no_replay", 0, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "run_learned_template execution exception ({ErrorType})",
                ex.GetType().Name);
            if (_stateDb.CompleteAutoRuleRun(
                    command.CommandId, _autoRuleRunOwnerId, false, "execution_exception", 0, null))
                await AckTerminalAsync(false, "execution_exception", 0, null);
        }
        finally
        {
            if (semaphoreHeld) _navigationSemaphore.Release();
        }
    }

    private async Task HandleAbortNavigationAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requestedRunId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            ? rid.GetString() : null;
        var requestedReason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        CancellationTokenSource? activeCts;
        string? activeRunId;
        lock (_activeNavigationLock)
        {
            activeCts = _activeNavigationCts;
            activeRunId = _activeNavigationRunId;
        }

        // A specific runId that doesn't match the active run is a no-op ack; no runId ⇒ abort whatever's active.
        if (!string.IsNullOrEmpty(requestedRunId) && !string.Equals(activeRunId, requestedRunId, StringComparison.Ordinal))
        {
            await AckAsync(true, new { aborted = false, reason = "no_active_run_with_id" }, null);
            return;
        }

        try { activeCts?.Cancel(); }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "abort_navigation: cancel threw ({ErrorType})",
                ex.GetType().Name);
        }

        await AckAsync(true, new { aborted = true, run_id = activeRunId, reason = requestedReason }, null);
    }

    private async Task HandleForceRestartAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;
        var reason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: cmd.Nonce,
            EventType: "force_restart_requested",
            FromState: "running",
            ToState: "restarting",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: reason ?? "operator_force_restart"));

        // ACK before exiting so the cloud confirms the restart was accepted (it won't hear from us again
        // until the new process comes up). ECDSA + persistent-nonce verification already gated this command.
        if (!string.IsNullOrEmpty(commandId) && _cloudClient != null)
        {
            try { await _cloudClient.AckCommandAsync(commandId, true, new { restarting = true, reason }, null, ct); }
            catch (Exception ex) { _logger.LogSafeDebug(ex); }
        }

        _logger.LogWarning("core.command.force_restart_accepted");

        // Brief grace to flush the ACK, then exit non-zero → Core stops → the Watchdog restarts it.
        try { await Task.Delay(TimeSpan.FromMilliseconds(500), ct); } catch (OperationCanceledException) { }
        Environment.Exit(1);
    }

    /// <summary>
    /// restart_helper — the remote recovery lever for a stranded Helper command pipe. Core
    /// (LocalService) cannot kill/relaunch a process in the interactive session, so it drops
    /// the <see cref="HelperRestartRequest"/> sentinel; the Broker (LocalSystem) consumes it
    /// within ~5s, kills every Helper, and relaunches a fresh one into the active console
    /// session. Distinct from force_restart (which restarts CORE — useless against a wedged
    /// Helper holding the single-instance pipe).
    ///
    /// Safety gates, in order:
    ///   1. REFUSE while any actuation is mid-flight — all actuating paths single-flight
    ///      through the pricing/workflow/navigation semaphores, so try-acquiring ALL THREE
    ///      with zero wait is an exact "nothing is actuating" test. The sentinel is written
    ///      WHILE holding them, so no job can begin between the check and the write.
    ///   2. Jobs that start after the write see the pending sentinel (checked after semaphore
    ///      acquisition in the pricing handlers) and refuse until the Broker consumes it.
    ///   3. The sentinel is freshness-bounded (2 min) and consumed exactly once Broker-side.
    /// Idempotent: a second restart_helper while one is pending overwrites the same sentinel
    /// and collapses into a single restart.
    /// </summary>
    private async Task HandleRestartHelperAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;
        var reason = dataEl.TryGetProperty("reason", out var rr) && rr.ValueKind == JsonValueKind.String
            ? rr.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct); }
            catch (Exception ex) { _logger.LogSafeWarning(ex); }
        }

        // Gate 1: nothing may be actuating. Fixed acquisition order + zero wait + full rollback
        // on partial acquisition — cannot deadlock, cannot leak a permit.
        var held = new List<SemaphoreSlim>(3);
        try
        {
            foreach (var sem in new[] { _pricingJobSemaphore, _workflowSemaphore, _navigationSemaphore })
            {
                if (!await sem.WaitAsync(TimeSpan.Zero, ct))
                {
                    _logger.LogWarning(
                        "restart_helper REFUSED — an actuation (pricing/workflow/navigation) is mid-flight; " +
                        "yanking the Helper under a live run is never allowed");
                    await AckAsync(false, new { state = "refused_actuation_in_flight" },
                        "refused: an actuation is mid-flight — retry when the job finishes or abort it first");
                    return;
                }
                held.Add(sem);
            }

            var payload = new HelperRestartRequest.Payload(
                DateTimeOffset.UtcNow,
                string.IsNullOrWhiteSpace(reason) ? "operator_restart_helper" : reason!,
                "operator",
                commandId);
            HelperRestartRequest.Write(HelperRestartRequest.DefaultPath(), payload);

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: cmd.Nonce,
                EventType: "helper_restart_requested",
                FromState: "running",
                ToState: "restart_pending",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: payload.Reason));

            _logger.LogWarning(
                "restart_helper accepted (reason={Reason}) — sentinel written; Broker will kill + relaunch " +
                "the Helper into the active console session within ~5s", payload.Reason);

            await AckAsync(true, new
            {
                state = "restart_pending",
                requestedAtUtc = payload.RequestedAtUtc.ToString("o"),
                note = "Broker consumes the request within ~5s; watch helper.actuation.ready flip true on the next probe (≤60s)",
            }, null);
        }
        catch (Exception ex)
        {
            _logger.LogSafeError(ex);
            await AckAsync(false, null, $"restart_helper_failed:{ex.GetType().Name}");
        }
        finally
        {
            foreach (var sem in held) sem.Release();
        }
    }

    private MissionCharter BuildEphemeralCharter() => new(
        CharterId: Guid.Empty,
        PharmacyId: _options.PharmacyId ?? "",
        Version: 0,
        EffectiveFrom: DateTimeOffset.UtcNow,
        Objectives: Array.Empty<MissionObjective>(),
        Constraints: Array.Empty<MissionConstraint>(),
        PriorityOrdering: new MissionPriorityOrdering(Array.Empty<string>()),
        Tolerance: new MissionToleranceThresholds(0, 0, 0.0),
        SignedByOperator: "agent_ephemeral",
        SignedAt: DateTimeOffset.UtcNow);

}
