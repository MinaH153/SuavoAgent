using System.Diagnostics;
using System.Globalization;
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
    private async Task HandleForceLearningPhaseAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (!_options.TestHooks.Enabled)
        {
            _logger.LogWarning("force_learning_phase: rejected — Agent.TestHooks.Enabled is false");
            await AckAsync(false, null, "test_hooks_disabled");
            return;
        }

        var targetPhase = dataEl.TryGetProperty("targetPhase", out var tp) ? tp.GetString() : null;
        if (string.IsNullOrWhiteSpace(targetPhase))
        {
            _logger.LogWarning("force_learning_phase: missing targetPhase");
            await AckAsync(false, null, "missing targetPhase");
            return;
        }

        // Only the gate-exercising learning phases may be forced. 'approved'/'active' are
        // NEVER forceable: reaching them must go through the approve_pom digest-verified flow,
        // and forcing them here would bypass the POM approval gate (and trigger adapter
        // activation on an unapproved model). 'discovery' is the start state, not a forward target.
        if (targetPhase is not ("pattern" or "model"))
        {
            _logger.LogWarning("force_learning_phase: phase '{Phase}' is not forceable (only pattern/model)", targetPhase);
            await AckAsync(false, null, "phase_not_forceable");
            return;
        }

        var explicitSession = dataEl.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
        var sessionId = !string.IsNullOrWhiteSpace(explicitSession)
            ? explicitSession
            : _stateDb.GetActiveSessionId(_options.PharmacyId ?? "");
        if (string.IsNullOrEmpty(sessionId))
        {
            _logger.LogWarning("force_learning_phase: no active learning session");
            await AckAsync(false, null, "no_active_learning_session");
            return;
        }

        var session = _stateDb.GetLearningSession(sessionId);
        if (session is null)
        {
            _logger.LogWarning("core.command.learning_session_not_found");
            await AckAsync(false, null, "session_not_found");
            return;
        }
        var fromPhase = session.Value.Phase;

        try
        {
            // Enforces IsValidPhaseTransition (single-step forward) + stamps phase_changed_at=now,
            // which makes the PhaseGate's 72h calendar floor fail on the fresh phase → gate holds.
            _stateDb.UpdateLearningPhase(sessionId, targetPhase);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            await AckAsync(false, null, $"invalid_transition: {fromPhase} -> {targetPhase}");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: sessionId,
            EventType: "test_force_phase",
            FromState: fromPhase,
            ToState: targetPhase,
            Trigger: "force_learning_phase",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: $"test hook forced {fromPhase} -> {targetPhase}"));

        _logger.LogInformation("core.command.learning_phase_forced");
        await AckAsync(true, new { sessionId, fromPhase, toPhase = targetPhase }, null);
    }

    private async Task HandleShowIntentCursorAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (_intentCursorClient is null)
        {
            _logger.LogWarning("show_intent_cursor: intent cursor client not registered");
            await AckAsync(false, null, "intent cursor client unavailable");
            return;
        }

        if (ContainsUnsafeIntentCursorField(dataEl))
        {
            _logger.LogWarning("show_intent_cursor: rejected unsafe payload shape");
            await AckAsync(false, null, "intent cursor payload shape is invalid");
            return;
        }

        IntentCursorRequest? request;
        try
        {
            request = dataEl.Deserialize<IntentCursorRequest>();
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            await AckAsync(false, null, "malformed intent cursor payload");
            return;
        }

        if (request is null)
        {
            await AckAsync(false, null, "missing intent cursor payload");
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: "intent_cursor_command",
            FromState: "requested",
            ToState: "dispatched",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "visual_only_cursor_overlay"));

        var result = await _intentCursorClient.ShowAsync(request, ct);
        if (!result.Success)
        {
            await AckAsync(false, null, result.ErrorCode ?? "intent cursor failed");
            return;
        }

        await AckAsync(true, result.Response, null);
    }

    private async Task HandleRunWorkflowAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Workflow, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAutopilotAdmissionRejectedAsync(commandId, autopilotRun, ct)
                .ConfigureAwait(false);
            return;
        }
        var runToken = autopilotRun.Token;

        if (_workflowExecutor is null)
        {
            _logger.LogWarning("run_workflow received but WorkflowExecutor not registered (DI gap)");
            await AckAsync(false, null, "workflow_executor_unavailable");
            return;
        }

        var semaphoreHeld = false;
        try
        {
            if (!await _workflowSemaphore.WaitAsync(0, runToken).ConfigureAwait(false))
            {
                _logger.LogWarning("run_workflow rejected: another workflow is already running");
                await AckAsync(false, null, "workflow_already_running");
                return;
            }
            semaphoreHeld = true;

            WorkflowDefinitionDto? definition;
            try { definition = dataEl.Deserialize<WorkflowDefinitionDto>(); }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    "run_workflow: malformed payload ({ErrorType})",
                    ex.GetType().Name);
                await AckAsync(false, null, "malformed_workflow_payload");
                return;
            }

            if (definition is null
                || string.IsNullOrEmpty(definition.WorkflowRunId)
                || string.IsNullOrEmpty(definition.WorkflowName)
                || definition.Steps is null
                || definition.Steps.Count == 0)
            {
                await AckAsync(false, null, "invalid_workflow_definition");
                return;
            }

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: definition.WorkflowRunId,
                EventType: "workflow_run_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"workflow={definition.WorkflowName}@{definition.WorkflowVersion} steps={definition.Steps.Count}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var auditChain = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();

            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var actor = $"agent:{_options.AgentId ?? "?"}";

            using var workflowCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            lock (_activeWorkflowLock)
            {
                _activeWorkflowCts = workflowCts;
                _activeWorkflowRunId = definition.WorkflowRunId;
            }

            WorkflowExecutor.WorkflowExecutionResult execResult;
            try
            {
                execResult = await _workflowExecutor.ExecuteAsync(
                    definition,
                    _serviceProvider,
                    auditChain,
                    charter,
                    pharmacyId,
                    actor,
                    workflowCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeWorkflowLock)
                {
                    _activeWorkflowCts = null;
                    _activeWorkflowRunId = null;
                }
            }

            _logger.LogInformation(
                "run_workflow run={RunId} outcome={Outcome} steps={Done}/{Total} reason={Reason}",
                definition.WorkflowRunId,
                execResult.Outcome,
                execResult.StepsCompleted,
                execResult.TotalSteps,
                execResult.AbortReason);

            await AckAsync(
                ok: execResult.Outcome == WorkflowRunOutcome.Completed,
                result: new { run_id = definition.WorkflowRunId, outcome = execResult.Outcome.ToString(), steps_completed = execResult.StepsCompleted },
                err: execResult.AbortReason);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            RecordCancellationAudit(new AuditEntry(
                TaskId: commandId ?? cmd.Nonce,
                EventType: "workflow_run_cancelled",
                FromState: "in_progress",
                ToState: "cancelled",
                Trigger: autopilotRun.Token.IsCancellationRequested
                    ? "local_autopilot_control"
                    : "dashboard_abort",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: autopilotRun.Token.IsCancellationRequested
                    ? "local_autopilot_control"
                    : "dashboard_abort"));
            await AckAsync(
                false,
                new { outcome = "cancelled", steps_completed = 0 },
                "workflow_cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "run_workflow execution exception ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "workflow_execution_exception");
        }
        finally
        {
            if (semaphoreHeld) _workflowSemaphore.Release();
        }
    }

    private async Task HandleAbortWorkflowAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.ValueKind == JsonValueKind.Object &&
            dataEl.TryGetProperty("commandId", out var cid) &&
            cid.ValueKind == JsonValueKind.String
                ? cid.GetString()
                : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (!TryParseAbortWorkflowCommand(
                dataEl,
                out var requestedRunId,
                out var requestedReason))
        {
            await AckAsync(false, null, "invalid_abort_workflow_payload");
            return;
        }

        CancellationTokenSource? activeCts;
        string? activeRunId;
        lock (_activeWorkflowLock)
        {
            activeCts = _activeWorkflowCts;
            activeRunId = _activeWorkflowRunId;
        }

        if (!string.Equals(activeRunId, requestedRunId, StringComparison.Ordinal))
        {
            _logger.LogInformation(
                "abort_workflow received for run {Requested}, but active run is {Active} (no-op ack)",
                requestedRunId,
                activeRunId ?? "<none>");
            await AckAsync(true, new { aborted = false, reason = "no_active_run_with_id" }, null);
            return;
        }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: requestedRunId,
            EventType: "workflow_run_abort_requested",
            FromState: "in_progress",
            ToState: "aborting",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: requestedReason));

        try { activeCts?.Cancel(); }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "abort_workflow: cancel threw ({ErrorType})",
                ex.GetType().Name);
        }

        await AckAsync(true, new { aborted = true, run_id = requestedRunId, reason = requestedReason }, null);
    }

    private static bool TryParseAbortWorkflowCommand(
        JsonElement data,
        out string workflowRunId,
        out string reasonCode)
    {
        workflowRunId = string.Empty;
        reasonCode = string.Empty;
        if (data.ValueKind != JsonValueKind.Object)
            return false;

        var properties = data.EnumerateObject().Select(property => property.Name).ToArray();
        if (properties.Length != 5 ||
            properties.Distinct(StringComparer.Ordinal).Count() != 5 ||
            !properties.ToHashSet(StringComparer.Ordinal).SetEquals(new[]
            {
                "schemaVersion", "workflowRunId", "reasonCode",
                "commandId", "expiresAt",
            }))
            return false;

        if (!data.TryGetProperty("schemaVersion", out var schemaVersion) ||
            schemaVersion.ValueKind != JsonValueKind.Number ||
            !schemaVersion.TryGetInt32(out var version) || version != 1 ||
            !data.TryGetProperty("workflowRunId", out var runIdElement) ||
            runIdElement.ValueKind != JsonValueKind.String ||
            !IsCanonicalUuidV4(runIdElement.GetString(), out workflowRunId) ||
            !data.TryGetProperty("reasonCode", out var reasonElement) ||
            reasonElement.ValueKind != JsonValueKind.String ||
            !string.Equals(
                reasonElement.GetString(),
                "dashboard_abort",
                StringComparison.Ordinal) ||
            !data.TryGetProperty("commandId", out var commandIdElement) ||
            commandIdElement.ValueKind != JsonValueKind.String ||
            !IsCanonicalUuidV4(commandIdElement.GetString(), out _) ||
            !data.TryGetProperty("expiresAt", out var expiresAtElement) ||
            expiresAtElement.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParseExact(
                expiresAtElement.GetString(),
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _))
            return false;

        reasonCode = "dashboard_abort";
        return true;
    }

    private static bool IsCanonicalUuidV4(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (value is not { Length: 36 } ||
            !Guid.TryParseExact(value, "D", out var parsed) ||
            !string.Equals(value, parsed.ToString("D"), StringComparison.Ordinal) ||
            value[14] != '4' || value[19] is not ('8' or '9' or 'a' or 'b'))
            return false;

        normalized = value;
        return true;
    }

    private async Task HandleNavigateAppAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // ValueKind-guarded so a signed-but-malformed payload (e.g. numeric objective) acks cleanly
        // instead of throwing GetString() unobserved in this fire-and-forget task (Codex Q5).
        var objective = dataEl.TryGetProperty("objective", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String
            ? tk.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(taskKey))
        {
            await AckAsync(false, null, "malformed_navigate_payload");
            return;
        }

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Navigation, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAutopilotAdmissionRejectedAsync(commandId, autopilotRun, ct)
                .ConfigureAwait(false);
            return;
        }
        var runToken = autopilotRun.Token;

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r
            ? r : Guid.NewGuid().ToString("n");
        var maxSteps = dataEl.TryGetProperty("maxSteps", out var ms) && ms.TryGetInt32(out var msv) ? msv : 25;
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;
        // Fail-safe default: dry-run unless the operator EXPLICITLY sends dryRun=false.
        var dryRun = !(dataEl.TryGetProperty("dryRun", out var dr) && dr.ValueKind == JsonValueKind.False);

        var semaphoreHeld = false;
        try
        {
            if (!await _navigationSemaphore.WaitAsync(0, runToken).ConfigureAwait(false))
            {
                await AckAsync(false, null, "navigation_already_running");
                return;
            }
            semaphoreHeld = true;

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: runId,
                EventType: "navigate_run_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"navigate dryRun={dryRun} maxSteps={maxSteps}"));

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
                "navigate_app",
                runToken).ConfigureAwait(false);

            var runner = SuavoAgent.Core.Agentic.NavigateLoopFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline,
                helperGateState);

            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions
            {
                MaxSteps = maxSteps,
                Deadline = TimeSpan.FromSeconds(deadlineSeconds),
                DryRun = dryRun,
                AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log",
                },
            };

            var objectiveModel = new SuavoAgent.Core.Agentic.AgentObjective(objective!, taskKey!, pharmacyId);

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.AgenticLoopResult result;
            try
            {
                // MOAT Increment 2 — replay-first: a HEALTHY banked skill whose entry StateHash matches
                // the live screen replays deterministically (zero LLM) INSTEAD of the loop. Same composite
                // gate (preflight / M3 autonomy / never-blind-on-live-PMS) as the loop; any miss/drift
                // falls through to the loop below unchanged. navigate banks with app="" (see
                // HarvestVerifiedSkill call), so the lookup uses the same key.
                if (_options.ReplayFirst)
                {
                    var rf = await TryReplayFirstAsync(
                        objectiveModel, app: string.Empty, loopOptions, charter, audit, deadline,
                        safetyOptions, safetyOverride: null,
                        helperGateState: helperGateState,
                        targetProcess: null, runId: runId, ct: navCts.Token)
                        .ConfigureAwait(false);
                    if (rf is { ReplayCompleted: true, Replay: { } replayed })
                    {
                        await AckAsync(true, new
                        {
                            run_id = runId,
                            termination = SuavoAgent.Core.Agentic.TerminationReason.Done.ToString(),
                            steps = replayed.StepsCompleted,
                            escalated = false,
                            detail = "replay_first",
                            replay_first = true,
                        }, null);
                        return; // skip the loop — the skill just re-verified itself (no re-harvest needed)
                    }
                }

                result = await runner.RunAsync(objectiveModel, loopOptions, navCts.Token).ConfigureAwait(false);
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
                "navigate_app run={RunId} termination={Term} steps={Steps} cloud={Cloud} escalated={Esc}",
                runId, result.Termination, result.StepCount, result.CloudCallsUsed, result.EscalationEmitted);

            // MOAT — feed this run's VERIFIED verdict trail into the Physarum edge-conductance memory:
            // a Met step thickens its (state→action) edge, an unverified/failed step evaporates it. This
            // is the discarded-history gap closed (Phase 1.2) + the substrate PhysarumActionPolicy will
            // explore over (Phase 2). Verified-only by construction (EdgeReinforcement). Exploration stays
            // shadow/dry-run confined upstream; this only RECORDS what the real verifier already decided,
            // so it is safe regardless of dryRun. Best-effort: a learning-layer fault must never fail the
            // navigate ack, but we log it rather than swallow (no silent failure).
            try
            {
                var conductance = _serviceProvider.GetService<SuavoAgent.Core.Agentic.IEdgeConductanceStore>()
                    ?? new SuavoAgent.Core.State.AgentStateDbEdgeConductanceStore(_stateDb);
                SuavoAgent.Core.Agentic.EdgeReinforcement.ApplyRun(
                    conductance, pharmacyId, taskKey!, result.FinalMemory.History,
                    SuavoAgent.Core.Agentic.ConductanceParams.Default);
            }
            catch (Exception reinforceEx)
            {
                _logger.LogWarning(
                    "navigate_app run={RunId} edge-conductance reinforcement failed ({ErrorType}; run unaffected)",
                    runId,
                    reinforceEx.GetType().Name);
            }

            // Amortize ratchet: a successful verified navigate trajectory becomes a replayable skill.
            HarvestVerifiedSkill(objectiveModel, string.Empty, result, runId);

            await AckAsync(
                ok: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done,
                result: new
                {
                    run_id = runId,
                    termination = result.Termination.ToString(),
                    steps = result.StepCount,
                    escalated = result.EscalationEmitted,
                    detail = result.Detail,
                    // Observe→assist bridge: a NON-EXECUTING learned-template offer for the presence layer to
                    // render (purple-cursor "want me to?"). Null unless the run terminated on a terminal Assist.
                    // steps_summary is STRUCTURAL ONLY (kinds + control-type counts) — no raw element text.
                    assisted_template = result.AssistedTemplate is { } at ? new
                    {
                        template_id = at.TemplateId,
                        confidence = at.Confidence,
                        observation_count = at.ObservationCount,
                        steps_summary = at.StepsSummary,
                    } : null,
                },
                err: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done ? null : result.Termination.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var cancellationSource = autopilotRun.Token.IsCancellationRequested
                ? "local_autopilot_control"
                : "dashboard_abort";
            RecordCancellationAudit(new AuditEntry(
                TaskId: runId,
                EventType: "navigate_run_cancelled",
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
                    termination = "Cancelled",
                    steps = 0,
                    escalated = false,
                },
                "navigation_cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "navigate_app execution exception ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "navigation_execution_exception");
        }
        finally
        {
            if (semaphoreHeld) _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// navigate_pricing — wire the reasoning NAVIGATOR to drive the pricing navigation for an NDC. Builds
    /// the pricing objective (<see cref="SuavoAgent.Core.Pricing.PricingNavObjective"/>) and runs it through
    /// the SAME navigate loop (perceive→reason→act, replay-first, edge-reinforcement, skill-banking) as
    /// navigate_app — so the on-device brain REASONS its way to the Supplier-Catalog grid on whatever
    /// PioneerRx version is on screen, instead of firing the hardcoded PricingWorkflow selectors. The
    /// money-critical grid READ stays deterministic (the separate pricing-lookup path owns the actual
    /// cost); the navigator only gets us to the grid. Delegates to <see cref="HandleNavigateAppAsync"/> so
    /// there is ONE navigate implementation — no duplicated loop / harvest / safety-gate logic. Same
    /// fail-safe posture (dry-run unless dryRun=false is sent explicitly).
    /// </summary>
}
