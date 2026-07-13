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
    private async Task HandleComputerUseObserveProposeAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (ContainsUnsafeComputerUseField(dataEl, cmd.Command))
        {
            _logger.LogWarning("{Command}: rejected unsafe observe/propose payload", cmd.Command);
            await AckAsync(false, null, "computer-use observe/propose payload must be synthetic and non-PHI");
            return;
        }

        var pack = dataEl.TryGetProperty("pack", out var packEl) ? packEl.GetString() : null;
        var proposal = dataEl.TryGetProperty("proposal", out var proposalEl) ? proposalEl.GetString() : null;

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: cmd.Command == "computer_use_observe"
                ? "computer_use_observe_command"
                : "computer_use_propose_command",
            FromState: "requested",
            ToState: "recorded",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "synthetic_non_phi_observe_propose"));

        await AckAsync(true, new
        {
            mode = "synthetic",
            action = cmd.Command,
            pack,
            proposal,
            executed = false,
            mutated = false,
            screenshotsCaptured = false
        }, null);
    }

    private async Task HandleCollectHealthProbeAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        if (ContainsUnsafeHealthProbeField(dataEl))
        {
            _logger.LogWarning("collect_health_probe: rejected unsafe payload");
            await AckAsync(false, null, "health probe payload must be reason-only and non-PHI");
            return;
        }

        var reason = dataEl.TryGetProperty("reason", out var reasonEl)
            ? reasonEl.GetString() ?? "dashboard_diagnostics"
            : "dashboard_diagnostics";

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId ?? cmd.Nonce,
            EventType: "health_probe_command",
            FromState: "requested",
            ToState: "collected",
            Trigger: "signed_command",
            CommandId: cmd.Nonce,
            RequesterId: requesterId,
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "non_phi_health_probe"));

        await AckAsync(true, BuildHealthProbeResult(reason), null);
    }

    /// <summary>
    /// <c>fetch_diagnostics</c> — gathers a PHI-safe snapshot of the box's config,
    /// SQL connectivity, helper/IPC health, and error-mesh counters and acks it to
    /// the cloud. Read-only: never drives the PMS, never mutates the box. This is
    /// how Claude debugs an agent remotely without touching the PHI workstation.
    /// Secrets (ApiKey/SqlPassword/HmacSalt/CloudCertPin) are never gathered;
    /// <see cref="DiagnosticsSnapshotBuilder"/> enforces the safe field set and the
    /// ack POST runs the snapshot through OutboundPhiGuard.
    /// </summary>
    private async Task HandleFetchDiagnosticsAsync(JsonElement scEl, SignedCommand cmd, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;
        var requesterId = dataEl.TryGetProperty("requesterId", out var rid) ? rid.GetString() : "operator";

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // Build + audit + ack inside a try so a throw NEVER leaves the command stuck
        // at 'sent'. AckCommandAsync is best-effort cloud-side, but the cloud can only
        // resolve a command it hears about — if snapshot build (RuntimeHealthEvidence
        // .Collect / BuildWatchdogPayload / DiagnosticsSnapshotBuilder.Build) or the
        // audit append throws, no ack would be sent and the operator's cockpit would
        // spin forever. On failure, ack 'failed' with a SAFE, exception-TYPE-only reason
        // (never the message — it could carry a path or other infra detail).
        try
        {
            var rxWorker = _serviceProvider.GetService<RxDetectionWorker>();
            var pharmacies = _options.GetEffectivePharmacies();
            var firstPharmacy = pharmacies.Count > 0 ? pharmacies[0] : null;
            var sql = new DiagnosticsSnapshotBuilder.SqlDiagnostics(
                Configured: pharmacies.Count > 0,
                Connected: rxWorker?.IsSqlConnected ?? false,
                Server: firstPharmacy?.SqlServer ?? _options.SqlServer,
                Database: firstPharmacy?.SqlDatabase ?? _options.SqlDatabase,
                User: firstPharmacy?.SqlUser ?? _options.SqlUser);

            var wire = new DiagnosticsSnapshotBuilder.WireDiagnostics(
                SentryInitialized: global::SuavoAgent.Diagnostics.Wire.SentryInitialized,
                EventsEmittedTotal: global::SuavoAgent.Diagnostics.Wire.EventsEmittedTotal,
                WireHandlerFailedTotal: global::SuavoAgent.Diagnostics.Wire.WireHandlerFailedTotal,
                SentryEnqueuedTotal: global::SuavoAgent.Diagnostics.Wire.SentryEnqueuedTotal,
                SentryEnqueueFailedTotal: global::SuavoAgent.Diagnostics.Wire.SentryEnqueueFailedTotal,
                SentryBeforeSendFailedTotal: global::SuavoAgent.Diagnostics.Wire.SentryBeforeSendFailedTotal,
                RulesetVersion: global::SuavoAgent.Diagnostics.Wire.RulesetVersion);

            // Live Helper actuation-gate state — surfaces WHY actuation is gated (gate_disabled /
            // paused / kill-switch / honeytoken-compromise) remotely. Best-effort; never fail the snapshot.
            object? actuationGate = null;
            if (_actuationGateway != null)
            {
                try
                {
                    actuationGate = await _actuationGateway.GetStateAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception gateEx)
                {
                    actuationGate = new { error = gateEx.GetType().Name };
                    _logger.LogSafeDebug(gateEx);
                }
            }

            var snapshot = DiagnosticsSnapshotBuilder.Build(
                _options,
                sql,
                wire,
                helper: BuildHelperPayload(_serviceProvider.GetService<IpcPipeServer>()),
                watchdog: BuildWatchdogPayload(),
                runtimeHealth: RuntimeHealthEvidence.Collect(),
                commandPipeConnected: _ipcCommandClient?.IsConnected ?? false,
                uptimeSeconds: (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                processId: Environment.ProcessId,
                collectedAtUtc: DateTimeOffset.UtcNow,
                actuationGate: actuationGate);

            _stateDb.AppendChainedAuditEntry(new AuditEntry(
                TaskId: commandId ?? cmd.Nonce,
                EventType: "fetch_diagnostics_command",
                FromState: "requested",
                ToState: "collected",
                Trigger: "signed_command",
                CommandId: cmd.Nonce,
                RequesterId: requesterId,
                Actor: "operator",
                SourceComponent: "heartbeat_worker",
                CaptureReason: "non_phi_diagnostics"));

            await AckAsync(true, snapshot, null);
        }
        catch (Exception ex)
        {
            _logger.LogSafeError(ex);
            // Exception TYPE only — safe to surface; the message may contain infra detail.
            await AckAsync(false, null, $"diagnostics_collection_failed:{ex.GetType().Name}");
        }
    }

    private object BuildHealthProbeResult(string reason)
    {
        var runtime = RuntimeHealthEvidence.Collect();
        var maintenancePath = Path.Combine(AppContext.BaseDirectory, MaintenanceContract.ExecutableName);
        var installStatePath = Path.Combine(AppContext.BaseDirectory, MaintenanceContract.InstallStateFileName);
        var crashEvidenceCount = runtime.CrashLogs.Count(log => log.Exists && log.Bytes > 0);
        var configFailed =
            runtime.ConfigSync.Present &&
            (string.Equals(runtime.ConfigSync.Status, "failed", StringComparison.OrdinalIgnoreCase) ||
             runtime.ConfigSync.ConsecutiveFailures >= 3);
        var cloudAuthFailed =
            runtime.CloudAuth.Present &&
            !string.Equals(runtime.CloudAuth.Status, "ok", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime.CloudAuth.Status, "success", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(runtime.CloudAuth.Status, "recovered", StringComparison.OrdinalIgnoreCase);
        var status = configFailed || cloudAuthFailed || crashEvidenceCount > 0 ? "needs_attention" : "healthy";

        return new
        {
            schema = "suavo.agent.health_probe.v1",
            status,
            reason,
            checkedAtUtc = DateTimeOffset.UtcNow.ToString("o"),
            screenshotsCaptured = false,
            mutated = false,
            agent = new
            {
                version = _options.Version,
                uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
                processId = Environment.ProcessId,
            },
            install = new
            {
                maintenanceHostPresent = File.Exists(maintenancePath),
                maintenanceHostSha256Prefix = SafeFileSha256Prefix(maintenancePath),
                installStatePresent = File.Exists(installStatePath),
            },
            configSync = runtime.ConfigSync,
            cloudAuth = runtime.CloudAuth,
            crashLogs = runtime.CrashLogs,
            audit = new
            {
                chainValid = _lastAuditChainValid,
                entryCount = _stateDb.GetAuditEntryCount(),
            },
            serviceProbe = CollectServiceProbe(),
        };
    }

    private static object CollectServiceProbe()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new
            {
                platform = "non_windows",
                supported = false,
                services = Array.Empty<object>(),
            };
        }

        var services = new List<object>();
        foreach (var serviceName in new[] { "SuavoAgent.Core", "SuavoAgent.Broker", "SuavoAgent.Watchdog" })
        {
            try
            {
                using var controller = new System.ServiceProcess.ServiceController(serviceName);
                services.Add(new
                {
                    serviceName,
                    status = controller.Status.ToString(),
                    canStop = controller.CanStop,
                });
            }
            catch
            {
                services.Add(new
                {
                    serviceName,
                    status = "not_found",
                    canStop = false,
                });
            }
        }

        return new
        {
            platform = "windows",
            supported = true,
            services,
        };
    }

    private static string? SafeFileSha256Prefix(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var hash = SHA256.HashData(stream);
            return Convert.ToHexString(hash).ToLowerInvariant()[..16];
        }
        catch
        {
            return "unreadable";
        }
    }

    private static bool ContainsUnsafeHealthProbeField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            var normalized = NormalizeIntentCursorFieldName(property.Name);
            if (normalized is not ("reason" or "commandid" or "requesterid") ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                IsBlockedComputerUseField(property.Name) ||
                HasUnsafeHealthProbeValue(normalized, property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasUnsafeHealthProbeValue(string normalizedName, JsonElement value)
    {
        return normalizedName switch
        {
            "reason" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    "dashboard_diagnostics" or
                    "post_install_probe" or
                    "operator_requested" or
                    "before_repair" or
                    "after_repair" or
                    "watchdog_unhealthy"),
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            _ => true,
        };
    }

    private static bool ContainsUnsafeComputerUseField(JsonElement element, string command)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!IsAllowedComputerUseField(property.Name, command) ||
                IsBlockedComputerUseField(property.Name) ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                HasUnsafeComputerUseValue(property.Name, property.Value, command))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedComputerUseField(string name, string command)
    {
        var normalized = NormalizeIntentCursorFieldName(name);
        if (normalized is "pack" or "mode" or "commandid" or "requesterid")
        {
            return true;
        }

        return command == "computer_use_propose" && normalized == "proposal";
    }

    private static bool HasUnsafeComputerUseValue(string name, JsonElement value, string command)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized switch
        {
            "pack" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not ("workstation_health" or "pioneerrx_shadow" or "inbox_shadow"),
            "mode" => value.ValueKind != JsonValueKind.String ||
                value.GetString() != "synthetic",
            "proposal" => command != "computer_use_propose" ||
                value.ValueKind != JsonValueKind.String ||
                value.GetString() is not ("run_diagnostics" or "queue_repair" or "show_intent_cursor" or "open_delivery_inbox"),
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            _ => true,
        };
    }

    private static bool IsBlockedComputerUseField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return IsBlockedIntentCursorField(name) ||
            normalized is
                "screenshot" or
                "image" or
                "ocr" or
                "click" or
                "type" or
                "key" or
                "mouse" or
                "coordinates" or
                "address" or
                "phone";
    }

    private static bool ContainsUnsafeIntentCursorField(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (!IsAllowedIntentCursorField(property.Name) ||
                IsBlockedIntentCursorField(property.Name) ||
                property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array ||
                HasUnsafeIntentCursorValue(property.Name, property.Value))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAllowedIntentCursorField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);
        return normalized is
            "x" or
            "y" or
            "coordinatespace" or
            "durationms" or
            "diameterpx" or
            "opacity" or
            "tone" or
            "anchor" or
            // Glide target + easing (v3.18.x). toX/toY are numbers; toAnchor and
            // easing are closed enums (see HasUnsafeIntentCursorValue) so none can
            // smuggle PHI through a free-form string.
            "tox" or
            "toy" or
            "toanchor" or
            "easing" or
            "commandid" or
            "requesterid" or
            "expiresat";
    }

    private static bool HasUnsafeIntentCursorValue(string name, JsonElement value)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized switch
        {
            "coordinatespace" => value.ValueKind != JsonValueKind.String ||
                !string.Equals(value.GetString(), IntentCursorCoordinateSpaces.Screen, StringComparison.Ordinal),
            "tone" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    IntentCursorTones.Agent or
                    IntentCursorTones.Attention or
                    IntentCursorTones.Success or
                    IntentCursorTones.Warning),
            "anchor" or "toanchor" => value.ValueKind != JsonValueKind.String ||
                !string.Equals(value.GetString(), IntentCursorAnchors.PrimaryCenter, StringComparison.Ordinal),
            "easing" => value.ValueKind != JsonValueKind.String ||
                value.GetString() is not (
                    IntentCursorEasings.Linear or
                    IntentCursorEasings.EaseInOutCubic),
            "x" or "y" or "tox" or "toy" or "durationms" or "diameterpx" or "opacity" =>
                value.ValueKind != JsonValueKind.Number,
            "commandid" or "requesterid" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 128,
            "expiresat" =>
                value.ValueKind != JsonValueKind.String || value.GetString()?.Length > 64,
            _ => true,
        };
    }

    private static bool IsBlockedIntentCursorField(string name)
    {
        var normalized = NormalizeIntentCursorFieldName(name);

        return normalized is
            "text" or
            "label" or
            "windowtitle" or
            "rx" or
            "rxnumber" or
            "rxid" or
            "prescription" or
            "prescriptionid" or
            "patient" or
            "patientid" or
            "patientname" or
            "patientfirstname" or
            "patientlastname" or
            "medication" or
            "ndc";
    }

    private static string NormalizeIntentCursorFieldName(string name) =>
        new(name
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private async Task HandleRepairAgentAsync(
        JsonElement scEl,
        SignedCommand command,
        CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var commandId = dataEl.TryGetProperty("commandId", out var cid) ? cid.GetString() : null;

        async Task AckAsync(bool ok, object? result, string? err)
        {
            if (string.IsNullOrEmpty(commandId) || _cloudClient == null) return;
            await _cloudClient.AckCommandAsync(commandId, ok, result, err, ct);
        }

        // Reject unrecognized reasons up front instead of silently re-mapping
        // them. The NACK lists only the closed safe set; it never echoes an
        // attacker-controlled value that could contain PHI.
        var (_, validation) = WatchdogRepairRequestWriter.InspectReason(dataEl);
        if (validation == WatchdogRepairRequestWriter.ReasonValidation.Rejected)
        {
            _logger.LogWarning(
                "repair_agent: rejecting unknown reason — must be one of [{Allowed}]",
                string.Join(", ", WatchdogRepairRequestWriter.AllowedReasons));
            await AckAsync(
                false,
                new
                {
                    status = "rejected",
                    allowed_reasons = WatchdogRepairRequestWriter.AllowedReasons,
                },
                "unknown_repair_reason");
            return;
        }

        var reason = WatchdogRepairRequestWriter.ReadReason(dataEl);
        var rawDataJson = dataEl.GetRawText();

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: _options.AgentId ?? "agent",
            EventType: "repair_command_received",
            FromState: "",
            ToState: "requested",
            Trigger: "repair_agent",
            CommandId: commandId,
            RequesterId: "operator",
            Actor: "operator",
            SourceComponent: "heartbeat_worker",
            CaptureReason: "signed_remote_repair"));

        try
        {
            var requestPath = WatchdogRepairRequestWriter.Queue(
                _options.WatchdogRepairRequestPath,
                command,
                rawDataJson);
            _logger.LogWarning("core.command.repair_queued");
            await AckAsync(true, new { status = "queued_for_watchdog" }, null);
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            await AckAsync(false, new { status = "queue_failed" }, "failed to queue repair");
        }
    }

    private bool RegisterFetchPatientCommand(JsonElement scEl, SignedCommand cmd)
    {
        _ = cmd; // Agent/fingerprint binding was already verified against this exact envelope.
        if (_approvedPatientRetrieval is null)
        {
            _logger.LogWarning("fetch_patient rejected: protected retrieval pipeline is unavailable");
            return false;
        }

        var rejectionCode = "fetch_data_missing";
        if (!scEl.TryGetProperty("data", out var dataEl) ||
            !FetchPatientCommandContract.TryParse(dataEl, out var command, out rejectionCode) ||
            command is null)
        {
            _logger.LogWarning("core.command.patient_retrieval_rejected");
            return false;
        }

        var registration = _approvedPatientRetrieval.Register(command);
        if (!registration.Accepted)
        {
            _logger.LogWarning("core.command.patient_retrieval_registration_rejected");
            return false;
        }

        return true;
    }

    private bool RegisterDeliveryWritebackCommand(AgentDeliveryWritebackCommand command)
    {
        if (_deliveryWriteback is null)
        {
            _logger.LogWarning("delivery_writeback rejected: protected writeback pipeline is unavailable");
            return false;
        }

        var registration = _deliveryWriteback.Register(command);
        if (!registration.Accepted)
        {
            _logger.LogWarning(
                "delivery_writeback rejected for command {CommandId}: {Reason}",
                command.CommandId,
                registration.Reason);
            return false;
        }

        return true;
    }

    /// <summary>
    /// The legacy decommission command is permanently retired. It performs zero local mutation;
    /// the evidence-preserving <c>self_uninstall</c> maintenance cohort is the only removal route.
    /// </summary>
}
