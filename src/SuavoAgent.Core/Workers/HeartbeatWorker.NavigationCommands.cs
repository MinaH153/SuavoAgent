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
    private async Task HandleNavigatePricingAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        // ValueKind-guard every read: JsonElement.GetString/TryGetInt32 THROW on a wrong-kind value (a
        // signed-but-malformed payload, e.g. commandId as a JSON number), and this handler runs in a
        // fire-and-forget Task.Run — an unobserved throw would drop the command with no ACK (cloud sees
        // only a timeout). Guarded reads instead ack malformed_navigate_pricing_payload.
        var commandId = dataEl.TryGetProperty("commandId", out var cid) && cid.ValueKind == JsonValueKind.String
            ? cid.GetString() : null;

        var ndc = dataEl.TryGetProperty("ndc", out var n) && n.ValueKind == JsonValueKind.String
            ? n.GetString() : null;
        if (!SuavoAgent.Core.Pricing.PricingNavObjective.IsPlausibleNdc(ndc))
        {
            if (!string.IsNullOrEmpty(commandId) && _cloudClient != null)
                await _cloudClient.AckCommandAsync(commandId, false, null, "malformed_navigate_pricing_payload", ct)
                    .ConfigureAwait(false);
            return;
        }

        // Compose the navigate_app payload from the pricing objective; carry the optional nav knobs so the
        // operator can still tune steps/deadline/dryRun. Then delegate — one navigate implementation.
        var navData = new Dictionary<string, object?>
        {
            ["commandId"] = commandId,
            ["objective"] = SuavoAgent.Core.Pricing.PricingNavObjective.Build(ndc!),
            ["taskKey"] = SuavoAgent.Core.Pricing.PricingNavObjective.TaskKey,
        };
        if (dataEl.TryGetProperty("runId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
            navData["runId"] = ridEl.GetString();
        if (dataEl.TryGetProperty("maxSteps", out var msEl) && msEl.ValueKind == JsonValueKind.Number && msEl.TryGetInt32(out var msv))
            navData["maxSteps"] = msv;
        if (dataEl.TryGetProperty("deadlineSeconds", out var dsEl) && dsEl.ValueKind == JsonValueKind.Number && dsEl.TryGetInt32(out var dsv))
            navData["deadlineSeconds"] = dsv;
        if (dataEl.TryGetProperty("dryRun", out var drEl) && drEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            navData["dryRun"] = drEl.ValueKind == JsonValueKind.True;

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new { data = navData }));
        await HandleNavigateAppAsync(doc.RootElement, cmd, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// MOAT — explore_sandbox: run the Physarum frontier explorer against an ALLOWLISTED sandbox app to
    /// learn UI navigation from real, verified screen changes. This is the ONLY place the permissive
    /// <see cref="SuavoAgent.Core.Agentic.Adapters.SandboxExploreSafetyGate"/> + ExploreMode policy are
    /// constructed (Codex Q3 invariant). Real execution (DryRun=false) is structurally confined to the
    /// sandbox allowlist; the live PMS is never allowlisted, and v1 actuation is click-only (Codex Q1).
    /// Verified verdicts reinforce conductance post-run (same as navigate); the store is read-only mid-run.
    /// </summary>
    private async Task HandleSandboxExploreAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var objective = dataEl.TryGetProperty("objective", out var o) && o.ValueKind == JsonValueKind.String
            ? o.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String
            ? tk.GetString() : null;
        var app = dataEl.TryGetProperty("app", out var ap) && ap.ValueKind == JsonValueKind.String
            ? ap.GetString() : null;
        if (string.IsNullOrWhiteSpace(objective) || string.IsNullOrWhiteSpace(taskKey) || string.IsNullOrWhiteSpace(app))
        {
            await AckAsync(false, null, "malformed_explore_payload");
            return;
        }

        // Fail-closed: the sandbox app MUST be in the actuation allowlist (the Helper re-checks too). The
        // live PMS is never in this set, so explore can never target it.
        var allowed = ActuationAllowlistedSandboxApps.ProcessNames.Keys
            .Concat(ActuationAllowlistedSandboxApps.ProcessNames.Values)
            .Any(a => string.Equals(a, app, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            await AckAsync(false, null, "app_not_allowlisted");
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

        // Shared with navigate/replay — actuation is one-at-a-time, which also serializes conductance writes.
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
                EventType: "explore_sandbox_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"explore app={app} maxSteps={maxSteps}"));

            var charter = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>()
                ?? new SuavoAgent.Core.Audit.AuditChain();
            var pharmacyId = _options.PharmacyId ?? charter.PharmacyId;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var safetyOptions = new SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions(
                EnableTaskAutonomy: false,   // explore bypasses graduation via the sandbox gate, not autonomy
                ExecutorMode: _options.PricingExecutor,
                AllowLiveActuation: false);

            var helperGateState = await ReadHelperActuationGateAsync(
                "explore_sandbox",
                runToken).ConfigureAwait(false);

            // Codex Q3 invariant: the sandbox gate is constructed HERE and passed as an explicit override
            // — never DI-registered. It receives Helper truth over authenticated IPC and fails closed
            // when that truth is unavailable; Helper remains authoritative at act time.
            var exploreGate = new SuavoAgent.Core.Agentic.Adapters.SandboxExploreSafetyGate(
                gateState: () => helperGateState);

            var policyOptions = new SuavoAgent.Core.Agentic.PhysarumPolicyOptions { ProcessName = app! };

            var runner = SuavoAgent.Core.Agentic.NavigateLoopFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline,
                helperGateState,
                policyOptions: policyOptions, safetyOverride: exploreGate);

            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions
            {
                MaxSteps = maxSteps,
                Deadline = TimeSpan.FromSeconds(deadlineSeconds),
                DryRun = false, // real execution — confined to the sandbox allowlist by the gate + Helper
                AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "Click", "Type", "PressKey", "WaitForElement", "VerifyElement", "Log",
                },
            };

            var objectiveModel = new SuavoAgent.Core.Agentic.AgentObjective(objective!, taskKey!, pharmacyId);

            // MOAT pre-launch: explore_sandbox perceives via WINDOW-SCOPED capture of the launch-established
            // window, so the app MUST be running + foreground (and _activeTarget set in the Helper) before the
            // loop's first perceive — else capture returns sandbox_not_launched. CandidateEnumerator is
            // click-only, so the loop never launches on its own. Goes through the real Helper ActuationGate
            // (allowlist + Enabled), so on a disabled/locked box it fails fast with a clear reason, not a blind loop.
            if (_actuationGateway is null)
            {
                await AckAsync(false, null, "actuation_gateway_unavailable");
                return;
            }
            SuavoAgent.Contracts.Ipc.ActuationResult launchResult;
            try
            {
                launchResult = await _actuationGateway
                    .LaunchSandboxAppAsync(new SuavoAgent.Contracts.Ipc.LaunchSandboxAppRequest(app!, DryRun: false), runToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception launchEx)
            {
                _logger.LogWarning(
                    "explore_sandbox: pre-launch exception app={App} ({ErrorType})",
                    app,
                    launchEx.GetType().Name);
                await AckAsync(false, null, "launch_exception");
                return;
            }
            if (!launchResult.Ok)
            {
                _logger.LogWarning("explore_sandbox: pre-launch rejected app={App} code={Code}", app, launchResult.RejectionCode);
                await AckAsync(false, null, $"launch_rejected:{launchResult.RejectionCode ?? "unknown"}");
                return;
            }
            _logger.LogInformation(
                "explore_sandbox: pre-launched app={App} (durationMs={Ms}) — settling before perceive",
                app, launchResult.DurationMs);
            try { await Task.Delay(TimeSpan.FromMilliseconds(750), runToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            lock (_activeNavigationLock)
            {
                _activeNavigationCts = navCts;
                _activeNavigationRunId = runId;
            }

            SuavoAgent.Core.Agentic.AgenticLoopResult result;
            try
            {
                // MOAT Increment 2 — replay-first for explore: the app is pre-launched (above), so the
                // window-scoped precheck perceive sees the same entry state the banked skill starts at.
                // Uses the SAME explore gate instance + "sandbox" capture path the loop would use.
                if (_options.ReplayFirst)
                {
                    var rf = await TryReplayFirstAsync(
                        objectiveModel, app!, loopOptions, charter, audit, deadline,
                        safetyOptions, safetyOverride: exploreGate,
                        helperGateState: helperGateState, targetProcess: "sandbox",
                        runId: runId, ct: navCts.Token)
                        .ConfigureAwait(false);
                    if (rf is { ReplayCompleted: true, Replay: { } replayed })
                    {
                        await AckAsync(true, new
                        {
                            run_id = runId,
                            app,
                            termination = SuavoAgent.Core.Agentic.TerminationReason.Done.ToString(),
                            steps = replayed.StepsCompleted,
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
                "explore_sandbox run={RunId} app={App} termination={Term} steps={Steps}",
                runId, app, result.Termination, result.StepCount);

            // Verified verdict trail → conductance reinforcement (same as navigate). Best-effort, logged.
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
                    "explore_sandbox run={RunId} edge-conductance reinforcement failed ({ErrorType}; run unaffected)",
                    runId,
                    reinforceEx.GetType().Name);
            }

            // Amortize ratchet: a successful verified explore trajectory becomes a replayable skill.
            HarvestVerifiedSkill(objectiveModel, app!, result, runId);

            await AckAsync(
                ok: result.Termination == SuavoAgent.Core.Agentic.TerminationReason.Done,
                result: new
                {
                    run_id = runId,
                    app,
                    termination = result.Termination.ToString(),
                    steps = result.StepCount,
                    detail = result.Detail,
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
                EventType: "explore_sandbox_cancelled",
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
                },
                "navigation_cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "explore_sandbox execution exception ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "navigation_execution_exception");
        }
        finally
        {
            if (semaphoreHeld) _navigationSemaphore.Release();
        }
    }

    /// <summary>
    /// MOAT — the amortize step (lever 3): after a run, bank a SUCCESSFUL execution-verified trajectory as a
    /// replayable <see cref="SuavoAgent.Core.Agentic.VerifiedSkill"/>. Re-verifying the same path thickens
    /// its success count rather than duplicating. Verified-only by construction (Done + every banked step
    /// Met). Off the hot path (post-run) + best-effort (logged, never fails the run). Returns silently when
    /// the run didn't succeed or had no verified steps.
    /// </summary>
    private void HarvestVerifiedSkill(
        SuavoAgent.Core.Agentic.AgentObjective objective, string app,
        SuavoAgent.Core.Agentic.AgenticLoopResult result, string runId)
    {
        try
        {
            var skill = SuavoAgent.Core.Agentic.VerifiedTrajectoryHarvester.Harvest(objective, app, result, out var refusalReason);
            if (skill is null)
            {
                // PHI-certification refusal (Phase-3B): the reason is an operational code, never a value
                // — safe to log. Silent-null (non-Done run / nothing verified) stays silent.
                if (refusalReason is not null)
                    _logger.LogInformation(
                        "verified-skill harvest refused run={RunId} reason={Reason}", runId, refusalReason);
                return;
            }
            var count = _stateDb.UpsertVerifiedSkill(
                skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);
            _logger.LogInformation(
                "verified-skill banked run={RunId} skill={SkillId} steps={Steps} successCount={Count}",
                runId, skill.SkillId[..12], skill.Steps.Count, count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "verified-skill harvest failed run={RunId} ({ErrorType}; run unaffected)",
                runId,
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// MOAT Increment 2 — replay-first attempt for a navigate/explore objective. Looks up the
    /// most-confirmed banked skill for (pharmacy, taskKey, app) and hands it to
    /// <see cref="SuavoAgent.Core.Agentic.ReplayFirstRunner"/>, wired by
    /// <see cref="SuavoAgent.Core.Agentic.NavigateReplayFactory"/> to the SAME perceiver path + safety
    /// gate + actuator the loop for this command uses. Returns null when nothing is banked or the
    /// attempt machinery itself faults — the caller falls through to the agentic loop in every
    /// non-Completed case, so replay-first can never make a run LESS safe or LESS capable than the loop.
    /// Best-effort by construction: only cancellation propagates.
    /// </summary>
    private async Task<SuavoAgent.Core.Agentic.ReplayFirstResult?> TryReplayFirstAsync(
        SuavoAgent.Core.Agentic.AgentObjective objective,
        string app,
        SuavoAgent.Core.Agentic.AgenticLoopOptions loopOptions,
        MissionCharter charter,
        SuavoAgent.Core.Audit.AuditChain audit,
        DateTimeOffset deadline,
        SuavoAgent.Core.Agentic.Adapters.NavigateSafetyOptions safetyOptions,
        SuavoAgent.Core.Agentic.ISafetyGate? safetyOverride,
        SuavoAgent.Contracts.Ipc.ActuationGateState? helperGateState,
        string? targetProcess,
        string runId,
        CancellationToken ct)
    {
        try
        {
            var best = _stateDb.GetBestVerifiedSkillForTask(objective.PharmacyId, objective.TaskKey, app);
            if (best is null)
                return null; // nothing banked — the routine miss, silent
            var bv = best.Value;

            // Reconstruct + PIN the banked steps to the stored steps_hash (the identity Phase-3B
            // certified). A row that does not deserialize or does not hash back to its pin is
            // Unparseable-class — decay it (3 strikes retires) so a corrupt row cannot pin replay-first
            // off for this task forever, and fall through to the loop.
            IReadOnlyList<SuavoAgent.Core.Agentic.VerifiedStep>? steps = null;
            try { steps = SuavoAgent.Core.Agentic.VerifiedSkill.DeserializeSteps(bv.StepsJson); }
            catch (JsonException) { /* corrupt row — handled below */ }
            if (steps is null || steps.Count == 0
                || !string.Equals(
                    SuavoAgent.Core.Agentic.VerifiedSkill.ComputeStepsHash(steps), bv.StepsHash, StringComparison.Ordinal))
            {
                RecordReplayFirstOutcome(bv.SkillId, success: false, runId);
                _logger.LogWarning(
                    "replay-first run={RunId} skill={SkillId} banked steps unreadable or steps-hash mismatch — skipped + decayed",
                    runId, bv.SkillId[..12]);
                return null;
            }

            var skill = new SuavoAgent.Core.Agentic.VerifiedSkill(
                bv.SkillId, objective.PharmacyId, objective.TaskKey, app, steps, bv.StepsHash);

            var replayRunner = SuavoAgent.Core.Agentic.NavigateReplayFactory.Create(
                _serviceProvider, safetyOptions, charter, audit, deadline,
                helperGateState, targetProcess, safetyOverride);

            var result = await replayRunner.TryReplayAsync(
                skill, bv.SuccessCount, objective, loopOptions, _options.ReplayFirstAllowTypeSteps,
                (skillId, ok) => RecordReplayFirstOutcome(skillId, ok, runId), ct).ConfigureAwait(false);

            if (result.Replay is { } replay)
            {
                // A replay actually fired (entry fingerprint matched + every gate passed) — chain it into
                // the audit log with the outcome. Reasons/outcomes are operational codes, never values.
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: runId,
                    EventType: "replay_first_attempt",
                    FromState: "starting",
                    ToState: result.ReplayCompleted ? "replayed" : "fell_through",
                    Trigger: "replay_first",
                    RequesterId: "agent",
                    Actor: "agent",
                    SourceComponent: "heartbeat_worker",
                    CaptureReason: $"skill={bv.SkillId[..12]} outcome={replay.Outcome} steps={replay.StepsCompleted}"));
                _logger.LogInformation(
                    "replay-first run={RunId} skill={SkillId} outcome={Outcome} stepsCompleted={Steps}",
                    runId, bv.SkillId[..12], replay.Outcome, replay.StepsCompleted);
            }
            else
            {
                // Pre-replay skips are deliberately quiet (entry mismatch is the routine case) — debug-only.
                _logger.LogDebug(
                    "replay-first run={RunId} skill={SkillId} skipped reason={Reason}",
                    runId, bv.SkillId[..12], result.SkipReason);
            }

            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "replay-first run={RunId} attempt failed ({ErrorType}; falling through to agentic loop)",
                runId,
                ex.GetType().Name);
            return null;
        }
    }

    /// <summary>Slime-mold skill hygiene for replay-first, best-effort: a hygiene write must never
    /// change the run's control flow (same contract as the replay_skill handler's update).</summary>
    private void RecordReplayFirstOutcome(string skillId, bool success, string runId)
    {
        try
        {
            _stateDb.RecordSkillReplayOutcome(skillId, success);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "replay-first run={RunId} skill-hygiene update failed ({ErrorType}; run unaffected)",
                runId,
                ex.GetType().Name);
        }
    }

    /// <summary>
    /// MOAT — cash in the amortize ratchet: deterministically REPLAY a banked VerifiedSkill (zero reasoning,
    /// no LLM). Loads the skill by id, or the most-confirmed skill for (task, app), reconstructs it, and runs
    /// <see cref="SuavoAgent.Core.Agentic.VerifiedSkillReplayer"/> — which executes each step only while the
    /// live screen matches the verified path and STOPS fail-closed on any drift. Shares the navigation
    /// semaphore (one actuating run at a time); sandbox-confined like the run that learned it.
    /// </summary>
    private async Task HandleReplaySkillAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        var skillId = dataEl.TryGetProperty("skillId", out var sid) && sid.ValueKind == JsonValueKind.String ? sid.GetString() : null;
        var taskKey = dataEl.TryGetProperty("taskKey", out var tk) && tk.ValueKind == JsonValueKind.String ? tk.GetString() : null;
        var app = dataEl.TryGetProperty("app", out var ap) && ap.ValueKind == JsonValueKind.String ? ap.GetString() : null;

        var charterForId = _serviceProvider.GetService<MissionCharter>() ?? BuildEphemeralCharter();
        var pharmacyId = _options.PharmacyId ?? charterForId.PharmacyId;

        // Resolve the skill: explicit skillId wins; else the most-confirmed skill for (task, app).
        string resolvedSkillId, stepsJson, stepsHash, resolvedTask, resolvedApp;
        if (!string.IsNullOrWhiteSpace(skillId))
        {
            var row = _stateDb.GetVerifiedSkill(skillId);
            if (row is null) { await AckAsync(false, null, "skill_not_found"); return; }
            var rv = row.Value;
            if (!string.Equals(rv.PharmacyId, pharmacyId, StringComparison.Ordinal)) { await AckAsync(false, null, "skill_pharmacy_mismatch"); return; }
            resolvedSkillId = skillId!;
            resolvedTask = rv.TaskKey;
            resolvedApp = rv.App;
            stepsJson = rv.StepsJson;
            stepsHash = rv.StepsHash;
        }
        else if (!string.IsNullOrWhiteSpace(taskKey) && !string.IsNullOrWhiteSpace(app))
        {
            var best = _stateDb.GetBestVerifiedSkillForTask(pharmacyId, taskKey, app);
            if (best is null) { await AckAsync(false, null, "no_skill_for_task"); return; }
            var bv = best.Value;
            resolvedSkillId = bv.SkillId;
            stepsJson = bv.StepsJson;
            stepsHash = bv.StepsHash;
            resolvedTask = taskKey!;
            resolvedApp = app!;
        }
        else
        {
            await AckAsync(false, null, "malformed_replay_skill_payload");
            return;
        }

        var runId = dataEl.TryGetProperty("runId", out var rid) && rid.ValueKind == JsonValueKind.String
            && rid.GetString() is { Length: > 0 } r ? r : Guid.NewGuid().ToString("n");
        var deadlineSeconds = dataEl.TryGetProperty("deadlineSeconds", out var ds) && ds.TryGetInt32(out var dsv) ? dsv : 120;

        IReadOnlyList<SuavoAgent.Core.Agentic.VerifiedStep> steps;
        try
        {
            steps = SuavoAgent.Core.Agentic.VerifiedSkill.DeserializeSteps(stepsJson);
        }
        catch (JsonException)
        {
            await AckAsync(false, null, "skill_unreadable");
            return;
        }
        if (steps.Count == 0) { await AckAsync(false, null, "empty_skill"); return; }
        var skill = new SuavoAgent.Core.Agentic.VerifiedSkill(resolvedSkillId, pharmacyId, resolvedTask, resolvedApp, steps, stepsHash);

        using var autopilotRun = _autopilotRuns.Register(AutopilotRunKind.Navigation, ct);
        if (!autopilotRun.Admitted)
        {
            await AckAutopilotAdmissionRejectedAsync(commandId, autopilotRun, ct)
                .ConfigureAwait(false);
            return;
        }
        var runToken = autopilotRun.Token;

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
                EventType: "replay_skill_received",
                FromState: "queued",
                ToState: "starting",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: "operator",
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: $"replay skill={resolvedSkillId[..12]} task={resolvedTask} app={resolvedApp} steps={steps.Count}"));

            var charter = charterForId;
            var audit = _serviceProvider.GetService<SuavoAgent.Core.Audit.AuditChain>() ?? new SuavoAgent.Core.Audit.AuditChain();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(deadlineSeconds);

            var helperGateState = await ReadHelperActuationGateAsync(
                "replay_skill",
                runToken).ConfigureAwait(false);
            var replayer = SuavoAgent.Core.Agentic.ReplaySkillFactory.Create(
                _serviceProvider, charter, audit, deadline, helperGateState);
            var loopOptions = new SuavoAgent.Core.Agentic.AgenticLoopOptions { DryRun = false, Deadline = TimeSpan.FromSeconds(deadlineSeconds) };
            var objective = new SuavoAgent.Core.Agentic.AgentObjective($"replay skill {resolvedSkillId[..12]}", resolvedTask, pharmacyId);

            using var navCts = CancellationTokenSource.CreateLinkedTokenSource(runToken);
            lock (_activeNavigationLock) { _activeNavigationCts = navCts; _activeNavigationRunId = runId; }

            SuavoAgent.Core.Agentic.SkillReplayResult result;
            try
            {
                result = await replayer.ReplayAsync(skill, objective, loopOptions, navCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_activeNavigationLock) { _activeNavigationCts = null; _activeNavigationRunId = null; }
            }

            _logger.LogInformation(
                "replay_skill run={RunId} skill={SkillId} outcome={Outcome} stepsCompleted={Steps}",
                runId, resolvedSkillId[..12], result.Outcome, result.StepsCompleted);

            // Slime-mold skill hygiene: a Completed replay re-thickens; a drift / skill-fault outcome decays
            // and may retire the skill (so a stale path stops being auto-selected + exploration re-learns it).
            // Environmental denials (kill-switch, perceive-fail, cancel, no-steps) are NOT the skill's fault →
            // ignored. Best-effort: a hygiene write must never change the run's ack.
            try
            {
                var o = result.Outcome;
                if (o == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed)
                    _stateDb.RecordSkillReplayOutcome(resolvedSkillId, success: true);
                else if (o is SuavoAgent.Core.Agentic.SkillReplayOutcome.StateMismatch
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.PostconditionFailed
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.Unparseable
                    or SuavoAgent.Core.Agentic.SkillReplayOutcome.StepRejected)
                    _stateDb.RecordSkillReplayOutcome(resolvedSkillId, success: false);
            }
            catch (Exception hygieneEx)
            {
                _logger.LogWarning(
                    "replay_skill run={RunId} skill-hygiene update failed ({ErrorType}; run unaffected)",
                    runId,
                    hygieneEx.GetType().Name);
            }

            await AckAsync(
                ok: result.Outcome == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed,
                result: new { run_id = runId, skill_id = resolvedSkillId, outcome = result.Outcome.ToString(), steps_completed = result.StepsCompleted, detail = result.Detail },
                err: result.Outcome == SuavoAgent.Core.Agentic.SkillReplayOutcome.Completed ? null : result.Outcome.ToString());
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            var cancellationSource = autopilotRun.Token.IsCancellationRequested
                ? "local_autopilot_control"
                : "dashboard_abort";
            RecordCancellationAudit(new AuditEntry(
                TaskId: runId,
                EventType: "replay_skill_cancelled",
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
                    skill_id = resolvedSkillId,
                    outcome = "Cancelled",
                    steps_completed = 0,
                },
                "navigation_cancelled").ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "replay_skill execution exception ({ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "navigation_execution_exception");
        }
        finally
        {
            if (semaphoreHeld) _navigationSemaphore.Release();
        }
    }

}
