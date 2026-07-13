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
    private async Task<SuavoAgent.Contracts.Models.CompromiseSignalPayload?> ReadCompromiseSignalAsync(
        CancellationToken ct)
    {
        if (_actuationGateway is null) return null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            var state = await _actuationGateway.GetStateAsync(timeout.Token).ConfigureAwait(false);
            if (!state.CompromiseDetected) return null;

            return new SuavoAgent.Contracts.Models.CompromiseSignalPayload(
                Detected: true,
                HoneytokenTripped: true,
                HoneytokenId: SuavoAgent.Contracts.Models.HoneytokenConstants.ComputeId(),
                CorroborationLevel: state.CompromiseLevel ?? "degrade",
                ReasonLabel: NormalizeHoneytokenReasonLabel(
                    state.CompromiseReasonLabel),
                OccurredAtUtc: state.CompromiseAtUtc ?? DateTimeOffset.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
            return null;
        }
    }

    internal static string NormalizeHoneytokenReasonLabel(string? value) =>
        HoneytokenReasonLabels.Normalize(value);

    internal HealthCompositePayload? EmitHealthComposite()
    {
        if (_healthSignals is null || _healthCompositeCalculator is null)
            return null;

        try
        {
            var snapshot = _healthSignals.Snapshot();
            var composite = _healthCompositeCalculator.Compute(snapshot, DateTimeOffset.UtcNow);

            try
            {
                _stateDb.AppendChainedAuditEntry(new AuditEntry(
                    TaskId: _options.AgentId ?? "",
                    EventType: "agent.health_composite",
                    FromState: "",
                    ToState: composite.Status,
                    Trigger: "heartbeat_tick",
                    Actor: "system",
                    SourceComponent: "heartbeat_worker"));
            }
            catch (Exception ex)
            {
                _logger.LogSafeWarning(ex);
            }

            return composite;
        }
        catch (Exception ex)
        {
            _logger.LogSafeWarning(ex);
            return null;
        }
    }

    /// <summary>
    /// Trip A 2026-04-25 silent-IPC-failure metric. Use the atomic Snapshot()
    /// so the three telemetry fields ship together — Codex flagged the prior
    /// three-call read pattern as racy: count from Record() N could ship with
    /// reason from Record() N-1 if Record() landed between the count read and
    /// the reason read. Counter resets on Core restart — a steadily growing
    /// value between restarts is the signal of interest.
    /// </summary>
    private object BuildHelperPayload(IpcPipeServer? ipcServer)
    {
        var (rejectionCount, lastReason, lastAt) = IpcRejectionStats.Snapshot();
        return new
        {
            attached = ipcServer?.IsConnected ?? false,
            consecutiveFailures = _helperConsecutiveFailures,
            ipcRejectionCount = rejectionCount,
            lastIpcRejectReason = lastReason,
            lastIpcRejectAt = lastAt?.ToString("o"),
            // Actuation-readiness — the strand detector. `attached` above reads the EVENT pipe
            // (Helper→Core) and is structurally blind to a stranded COMMAND pipe; this block is
            // the truthful "can the agent act right now" signal the cloud composite must use.
            // Null until the first probe completes (or on agents without the readiness worker).
            actuation = BuildActuationPayload(_actuationReadiness?.Current, _selfHealCoordinator?.Snapshot(DateTimeOffset.UtcNow)),
            // Earned from the same authenticated Helper command-pipe ping.
            // Null means no runtime verdict is available; never infer ready
            // from the registry configuration alone.
            visionRuntime = BuildVisionRuntimePayload(
                _actuationReadiness?.Current?.VisionRuntime),
        };
    }

    /// <summary>
    /// Fixed PHI-free cockpit wire shape for the authenticated Helper vision
    /// verdict. The reason comes only from a compiled code table; no Helper
    /// exception, path, OCR text, screenshot id, or window title can enter.
    /// </summary>
    internal static object? BuildVisionRuntimePayload(VisionRuntimeReadiness? status)
    {
        if (status?.IsValid() != true)
            return null;
        return new
        {
            contractVersion = status.ContractVersion,
            visionEnabled = status.VisionEnabled,
            ocrConfigured = status.OcrConfigured,
            ready = status.Ready,
            ocrReady = status.OcrReady,
            requiresAttention = status.RequiresAttention,
            code = status.Code,
            reason = VisionRuntimeCodes.OperatorMessage(status.Code),
            configurationGeneration = status.ConfigurationGeneration,
            checkedAtUtc = status.CheckedAtUtc.ToString("o"),
        };
    }

    /// <summary>
    /// Wire shape of <c>helper.actuation</c> in the heartbeat payload — the agent-side half of
    /// the actuation-readiness contract. PHI-free: booleans, session ids, pid, static codes,
    /// ISO timestamps. The cloud computes its health composite component from
    /// <c>ready</c> + freshness of <c>lastConclusiveCheckAt</c> (a stale verdict is UNKNOWN,
    /// not healthy) and renders <c>failureReason</c> in the cockpit when not ready.
    /// </summary>
    internal static object? BuildActuationPayload(ActuationReadinessSnapshot? s, SelfHealState? selfHeal)
    {
        if (s is null) return null;
        return new
        {
            ready = s.Ready,
            commandPipeResponsive = s.CommandPipeResponsive,
            isConsoleInteractive = s.IsConsoleInteractive,
            helperSessionId = s.HelperSessionId,
            activeConsoleSessionId = s.ActiveConsoleSessionId,
            helperPid = s.HelperPid,
            failureCode = s.FailureCode,
            failureReason = s.FailureReason,
            lastConclusiveCheckAt = s.LastConclusiveCheckAtUtc?.ToString("o"),
            lastProbeAttemptAt = s.LastProbeAttemptAtUtc.ToString("o"),
            lastCheckSkippedReason = s.SkippedReason,
            consecutiveStrandFailures = s.ConsecutiveStrandFailures,
            selfHeal = selfHeal is null ? null : new
            {
                lastAttemptAt = selfHeal.LastAttemptAtUtc?.ToString("o"),
                attemptsInWindow = selfHeal.AttemptsInWindow,
                exhausted = selfHeal.Exhausted,
            },
        };
    }

    /// <summary>
    /// Supervised-worker liveness for the heartbeat wire — restart-looping/escalated workers so
    /// the cloud's agent-health-watch can remediate at worker granularity (closed loop) rather
    /// than only detecting a fully-silent agent. Empty array when no registry / no faults (safe to
    /// emit either way). Names are static ("rx-detection", …); no PHI.
    /// </summary>
    internal static object[] BuildWorkersPayload(WorkerHealthRegistry? registry)
        => (registry?.Snapshot() ?? Array.Empty<WorkerHealth>())
            .Select(w => (object)new
            {
                name = w.Name,
                restartCount = w.RestartCount,
                escalated = w.Escalated,
                lastFaultUtc = w.LastFaultUtc.ToString("o"),
            })
            .ToArray();

    // Phase C: surface the installer's post-install self-verify outcome to the cockpit. Reads the
    // compact {passed, summary} from install-verify.json (written by SuavoAgent.Setup's PostInstallVerifier).
    // Read-only, fail-soft; returns null when the file is absent (legacy install) or unreadable.
    private object? ReadInstallVerify()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "install-verify.json");

        try
        {
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var passed = root.TryGetProperty("passed", out var p) && p.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("summary", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString()
                : null;
            return new { passed, summary };
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
            return null;
        }
    }

    private object BuildWatchdogPayload()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            "watchdog-health.json");

        try
        {
            if (!File.Exists(path))
            {
                return new
                {
                    present = false,
                    reason = "no_watchdog_telemetry_file",
                };
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
            return new
            {
                present = false,
                reason = "watchdog_telemetry_unreadable",
            };
        }
    }

    private async Task AckAutopilotAdmissionRejectedAsync(
        string? commandId,
        AutopilotRunCoordinator.AutopilotRunLease run,
        CancellationToken commandToken)
    {
        if (!IsStructuralIdentifier(commandId) || _cloudClient is null) return;
        await _cloudClient.AckCommandAsync(
            commandId!,
            false,
            new
            {
                admitted = false,
                kind = run.Kind.ToString(),
                outcome = run.RejectionCode,
            },
            run.RejectionCode,
            commandToken).ConfigureAwait(false);
    }

    private static bool IsStructuralIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        foreach (var ch in value)
        {
            if (ch is not (>= 'a' and <= 'z') and
                not (>= '0' and <= '9') and
                not '_' and
                not '-')
                return false;
        }
        return true;
    }

    private static string? StructuralReasonCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return null;
        foreach (var ch in value)
        {
            if (ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
                return null;
        }
        return value;
    }

    private void RecordCancellationAudit(AuditEntry entry)
    {
        try
        {
            _stateDb.AppendChainedAuditEntry(entry);
        }
        catch (Exception ex)
        {
            // A local audit-store fault must not suppress the structural terminal ACK. Never attach
            // the exception object: database/IO messages can contain workstation paths or values.
            _logger.LogError(
                "Autopilot cancellation audit failed for {EventType} ({ErrorType})",
                entry.EventType,
                ex.GetType().Name);
        }
    }

    private async Task RetryPendingDeliveryWritebacksAsync(CancellationToken serviceToken)
    {
        if (_deliveryWriteback is null) return;

        using var autopilotRun = _autopilotRuns.Register(
            AutopilotRunKind.DeliveryWriteback,
            serviceToken);
        if (!autopilotRun.Admitted) return;

        try
        {
            await _deliveryWriteback.RetryPendingAsync(autopilotRun.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!serviceToken.IsCancellationRequested)
        {
            // The durable ledger stays retryable. Do not let a local pause/stop look like a failed
            // heartbeat or turn a partially processed writeback into a terminal cloud receipt.
            _logger.LogInformation(
                "Delivery writeback retry pass cancelled by local Autopilot control; durable retry retained");
        }
    }

}
