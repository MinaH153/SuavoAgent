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
    private async Task<bool> HandleInstallPioneerRxApprovalAsync(
        JsonElement signedCommand,
        CancellationToken ct)
    {
        var data = signedCommand.TryGetProperty("data", out var nested) ? nested : signedCommand;
        var commandId = data.TryGetProperty("commandId", out var idElement) &&
                        idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString()
            : null;

        async Task AckAsync(bool succeeded, object? result, string? error)
        {
            if (_cloudClient is null || string.IsNullOrWhiteSpace(commandId)) return;
            await _cloudClient.AckCommandAsync(commandId, succeeded, result, error, ct)
                .ConfigureAwait(false);
        }

        if (!PioneerRxApprovalInstallCommandContract.TryParse(
                data,
                out var command,
                out var parseCode) ||
            command is null)
        {
            await AckAsync(false, null, parseCode).ConfigureAwait(false);
            return true;
        }

        if (PioneerRxApprovalInstallStager.HasExactCompletion(
                command,
                out var completionCode))
        {
            _options.ValidatedSqlServerCertificatePath = null;
            await AckAsync(
                    true,
                    new
                    {
                        status = completionCode,
                        receiptId = command.Receipt.ReceiptId,
                        approvalCounter = command.Receipt.ApprovalCounter,
                    },
                    null)
                .ConfigureAwait(false);
            return true;
        }

        // Core can only stage this untrusted request. Watchdog launches the signed maintenance host
        // as LocalSystem, and that host independently validates and commits the protected generation.
        var staged = PioneerRxApprovalInstallStager.Stage(command);
        if (!staged.Succeeded)
        {
            _logger.LogWarning(
                "PioneerRx approval request could not be staged code={Code}",
                staged.Code);
            return false;
        }

        // A very fast SYSTEM transaction may finish before this method yields. Otherwise leave the
        // signed envelope nonce unconsumed so the next heartbeat can observe completion and ACK it.
        if (!PioneerRxApprovalInstallStager.HasExactCompletion(
                command,
                out completionCode))
        {
            _logger.LogInformation(
                "PioneerRx approval request staged; awaiting SYSTEM completion");
            return false;
        }

        _options.ValidatedSqlServerCertificatePath = null;
        await AckAsync(
                true,
                new
                {
                    status = completionCode,
                    receiptId = command.Receipt.ReceiptId,
                    approvalCounter = command.Receipt.ApprovalCounter,
                },
                null)
            .ConfigureAwait(false);
        return true;
    }

    // Enumerate an allowlisted app's actionable UIA elements (controlType + automationId +
    // PHI-scrubbed name) and ack them as the command result — the agent's "look at the UI" capability,
    // so locators are grounded on real elements instead of guessed.
    private async Task HandleDiscoverElementsAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        try
        {
            if (_actuationGateway is null) { await AckAsync(false, null, "actuation_unavailable"); return; }
            var process = dataEl.TryGetProperty("process", out var pe) ? pe.GetString() : null;
            if (string.IsNullOrWhiteSpace(process)) { await AckAsync(false, null, "process required"); return; }
            var max = dataEl.TryGetProperty("max", out var me) && me.TryGetInt32(out var m) ? m : 60;

            var r = await _actuationGateway
                .DiscoverElementsAsync(new SuavoAgent.Contracts.Ipc.DiscoverElementsRequest(process, max), ct)
                .ConfigureAwait(false);
            if (!r.Ok) { await AckAsync(false, null, $"{r.RejectionCode}: {r.RejectionReason}"); return; }

            System.Text.Json.Nodes.JsonNode? elements = null;
            try { elements = System.Text.Json.Nodes.JsonNode.Parse(r.Payload ?? "[]"); } catch { /* leave null */ }
            var count = elements is System.Text.Json.Nodes.JsonArray arr ? arr.Count : 0;
            _logger.LogInformation("core.command.elements_discovered count={Count}", count);
            await AckAsync(true, new { process, count, elements }, null);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            await AckAsync(false, null, "discover_elements_exception");
        }
    }

    // Per-box (canary-only) enable of Tier-2 reasoning. Writes %PROGRAMDATA%\SuavoAgent\reasoning.json
    // (shaped {"Agent":{"Reasoning":{...}}}) which is layered over appsettings + survives OTA. The
    // ILocalInference factory decides at startup, so this takes effect on the NEXT restart (then the
    // model + native libs auto-provision; a 2nd restart loads them). Off everywhere it isn't pushed.
    private async Task HandleSetReasoningConfigAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        _ = cmd; // The dispatcher already verified command signature, audience, and nonce.
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        string? tmp = null;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent");
            var parsed = ReasoningConfigCommandContract.Parse(
                dataEl,
                dir,
                DateTimeOffset.UtcNow);
            if (!parsed.IsValid || parsed.Reasoning is null)
            {
                await AckAsync(false, null, parsed.Code);
                return;
            }
            if (parsed.PublisherManifest is { } publisherManifest)
            {
                var installed = await SuavoAgent.Contracts.Reasoning
                    .InstalledBrainCohortVerifier.VerifyAsync(
                        SuavoAgent.Contracts.Reasoning.BrainCohortContract.GetCohortRoot(
                            dir,
                            publisherManifest.CohortId),
                        publisherManifest,
                        DateTimeOffset.UtcNow,
                        ct);
                if (!installed.IsValid)
                {
                    await AckAsync(false, null, $"reasoning_{installed.Code}");
                    return;
                }
            }
            var reasoning = parsed.Reasoning;

            var root = new System.Text.Json.Nodes.JsonObject
            {
                ["Agent"] = new System.Text.Json.Nodes.JsonObject { ["Reasoning"] = reasoning },
            };

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "reasoning.json");
            tmp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            var bytes = System.Text.Encoding.UTF8.GetBytes(
                root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            await using (var stream = new FileStream(
                             tmp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
                stream.Flush(flushToDisk: true);
            }
            // Atomic replace — a crash/kill in the old delete-then-move window left reasoning.json
            // missing (the .tmp isn't read at boot), so startup silently bound default config.
            File.Move(tmp, path, overwrite: true);
            tmp = null;

            var enabled = reasoning["Enabled"]?.GetValue<bool>() == true;
            _logger.LogInformation("set_reasoning_config: wrote reasoning.json (Enabled={Enabled}) — takes effect on next restart", enabled);
            await AckAsync(true, new { applied = true, enabled, fields = reasoning.Select(kv => kv.Key).ToArray(), note = "restart required to activate" }, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                "set_reasoning_config failed (errorType={ErrorType})",
                ex.GetType().Name);
            await AckAsync(false, null, "reasoning_config_persist_failed");
        }
        finally
        {
            if (tmp is not null)
            {
                try { File.Delete(tmp); } catch { /* best-effort bounded temp cleanup */ }
            }
        }
    }

    /// <summary>
    /// Stages one strict, generation-numbered vision state in the Setup-owned
    /// HKLM authority. Core may replace only the value; it cannot create the
    /// key/subkeys or alter its ACL. The new state takes effect on restart.
    /// </summary>
    private async Task<bool> HandleSetVisionConfigAsync(
        JsonElement scEl,
        SignedCommand cmd,
        CancellationToken ct)
    {
        try
        {
            var dataEl = scEl.ValueKind == JsonValueKind.Object &&
                         scEl.TryGetProperty("data", out var data)
                ? data
                : scEl;
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent");
            var parsed = SuavoAgent.Core.Vision.VisionConfigurationCommandContract.Parse(dataEl, dir);
            if (_visionConfigurationOutbox is null)
            {
                _logger.LogError("set_vision_config durable outbox unavailable");
                return false;
            }
            if (!parsed.IsValid || parsed.Command is null)
            {
                _visionConfigurationOutbox.RecordStructuralFailure(
                    cmd,
                    parsed.CommandId,
                    parsed.Code);
                _logger.LogWarning(
                    "set_vision_config structural rejection code={Code} ackPossible={AckPossible}",
                    parsed.Code,
                    parsed.CommandId is not null);
                if (parsed.CommandId is not null && _cloudClient is not null)
                {
                    _ = await _cloudClient.TryAckCommandAsync(
                        parsed.CommandId,
                        false,
                        null,
                        parsed.Code,
                        ct).ConfigureAwait(false);
                }
                return true;
            }
            var registered = _visionConfigurationOutbox.RegisterVerified(parsed.Command, cmd);
            if (!registered.Accepted)
            {
                _visionConfigurationOutbox.RecordStructuralFailure(
                    cmd,
                    parsed.CommandId,
                    registered.Code);
                _logger.LogWarning(
                    "set_vision_config durable registration rejected code={Code}",
                    registered.Code);
                if (_cloudClient is not null)
                {
                    _ = await _cloudClient.TryAckCommandAsync(
                        parsed.CommandId!,
                        false,
                        null,
                        registered.Code,
                        ct).ConfigureAwait(false);
                }
                return true;
            }
            _logger.LogInformation(
                "set_vision_config durably registered commandId={CommandId} replay={Replay}",
                parsed.CommandId,
                registered.Idempotent);
            await _visionConfigurationOutbox.RetryPendingAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return false;
        }
    }

    // Cockpit "talk to the agent" → the on-device Qwen3 brain. Reply is PHI-scrubbed Core-side
    // (defense-in-depth with the cloud ack scrub) before it leaves the box. When the brain isn't ready
    // (reasoning off / model still provisioning), acks ready=false so the cockpit falls back to a
    // cloud/templated reply — the local brain is a bonus, never a hard dependency.
    // 0/1 single-flight gate for chat inference. The local brain has ONE shared LLama context, which
    // is not safe to drive concurrently; this also caps how much CPU chat can ever take from PioneerRx.
    private int _chatInFlight;

    private Task HandleChatAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            try { await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct); }
            catch (Exception ex) { _logger.LogSafeWarning(ex); }
        }

        var prompt = dataEl.TryGetProperty("prompt", out var pe) ? pe.GetString() : null;
        if (string.IsNullOrWhiteSpace(prompt))
            return AckAsync(false, null, "prompt required");

        if (_localInference is null || !_localInference.IsReady)
            return AckAsync(true, new { reply = (string?)null, ready = false, model = _localInference?.ModelId ?? "none" }, null);

        // Already answering another prompt — ack a fast "not now" (cockpit shows its templated reply)
        // rather than serialize behind a slow local generation.
        if (Interlocked.CompareExchange(ref _chatInFlight, 1, 0) != 0)
            return AckAsync(true, new { reply = (string?)null, ready = true, model = _localInference.ModelId, busy = true }, null);

        // Run inference OFF the heartbeat loop. Chat must NEVER block heartbeats or operational
        // commands: a single slow/looping local generation used to stall the whole loop (one command
        // per heartbeat, awaited inline) until the cloud expired the command 5 min later. The nonce is
        // already consumed by the signed-command verifier, so detaching here is replay-safe. ChatAsync
        // owns its own hard wall-clock ceiling, so this task always completes and resets the gate.
        _ = Task.Run(async () =>
        {
            try
            {
                var reply = await _localInference.ChatAsync(prompt!, ct).ConfigureAwait(false);
                var scrubbed = SuavoAgent.Core.Learning.PhiScrubber.ScrubText(reply);
                await AckAsync(true, new { reply = scrubbed, ready = true, model = _localInference.ModelId }, null);
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
                await AckAsync(false, null, "local_inference_exception");
            }
            finally
            {
                Interlocked.Exchange(ref _chatInFlight, 0);
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Remote allowlist widening is disabled until a workstation-local, cryptographically bound
    /// physical-approval receipt exists. Signed cloud origin alone is insufficient authorization.
    /// </summary>
    private async Task HandleExtendAppAllowlistAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        _ = cmd;
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        // Remote widening is intentionally unavailable. A signed cloud command proves
        // server origin, not that a human physically approved this executable on this
        // workstation. No cryptographically-bound local physical-approval receipt exists
        // for this command today, so accepting it would collapse the sandbox/PMS boundary.
        const string code = "remote_allowlist_widening_disabled";
        _logger.LogWarning("extend_app_allowlist rejected: {Code}", code);
        if (!string.IsNullOrEmpty(commandId) && _cloudClient is not null)
            await _cloudClient.AckCommandAsync(commandId, false, null, code, ct);
    }

    // M2d — operator DIRECT selector correction (the fast path that bypasses the slow fleet
    // aggregate→approve cycle). The signed `update_selector` command carries a single SelectorPatch,
    // already ECDSA-verified by the signed-command pipeline. It maps through the SAME fail-closed
    // validator as fleet seeds and upserts with operator provenance — single-step, signed,
    // schema-validated, audited. A malformed/non-identifiable patch is rejected, never stored.
    private async Task HandleUpdateSelectorCommandAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (!dataEl.TryGetProperty("patch", out var patchEl) || patchEl.ValueKind != JsonValueKind.Object)
        {
            _logger.LogWarning("update_selector: missing patch payload");
            await AckAsync(false, null, "missing patch payload");
            return;
        }

        SeedSelectorPatch? wire;
        try
        {
            wire = JsonSerializer.Deserialize<SeedSelectorPatch>(patchEl.GetRawText());
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            await AckAsync(false, null, "malformed patch");
            return;
        }

        var mapped = wire is null ? null : SelectorPatchMapper.TryMap(wire, $"operator:{cmd.Nonce}");
        if (mapped is null)
        {
            _logger.LogWarning("update_selector: patch rejected by fail-closed validator");
            await AckAsync(false, null, "patch rejected — malformed or non-identifiable");
            return;
        }

        try
        {
            // Atomic: the patch drives the live PMS, so applying it and recording the audit must
            // commit together or not at all. Wrapping both in one transaction means an audit-append
            // failure rolls back the upsert (the transaction disposes uncommitted), so we never leave
            // an applied-but-unaudited patch — and the ACK below truthfully reports the outcome.
            var now = DateTimeOffset.UtcNow.ToString("o");
            // Atomic: applying the patch (it drives the live PMS) and recording the chained audit entry
            // must commit together or not at all. AgentStateDb does both inside ONE transaction — the
            // handler must NOT open its own outer transaction around AppendChainedAuditEntry (which owns
            // a Serializable transaction for chain integrity); Microsoft.Data.Sqlite forbids nesting, and
            // the old outer-transaction wrapper threw `does not support nested transactions` on every
            // real apply (field-confirmed on Mina's box, 2026-06-03).
            _stateDb.UpsertSelectorPatchWithAudit(
                mapped,
                new AuditEntry(
                    TaskId: mapped.PatchId,
                    EventType: "selector_patch_applied",
                    FromState: "proposed",
                    ToState: "active",
                    Trigger: "update_selector",
                    CommandId: cmd.Nonce,
                    RequesterId: "operator",
                    Actor: "operator",
                    SourceComponent: "heartbeat_worker",
                    CaptureReason: $"step={mapped.StepId} skill={mapped.SkillId} via=operator"),
                now);
            _logger.LogInformation(
                "update_selector: applied operator patch {PatchId} for step {Step}", mapped.PatchId, mapped.StepId);
            await AckAsync(true, new { patchId = mapped.PatchId, step = mapped.StepId.ToString() }, null);
        }
        catch (Exception ex)
        {
            // Transaction rolled back on dispose — neither the patch nor the audit committed.
            _logger.LogSafeWarning(ex);
            await AckAsync(false, null, "apply failed — see agent logs");
        }
    }

    // Test-only seam, gated by Agent.TestHooks.Enabled (default false; signed config-override to flip).
    // Drives a SINGLE-STEP learning-phase transition on the active session so the M1 PhaseGate can be
    // exercised end-to-end on a real box. It NEVER bypasses a gate: UpdateLearningPhase enforces
    // IsValidPhaseTransition (one step forward only) + stamps phase_changed_at, and the PhaseGate still
    // evaluates and holds. Double-gated (ECDSA-signed command AND the flag), inert in the field.
}
