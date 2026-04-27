using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Periodically triggers Helper-side screen captures while a learning session
/// is active. The Helper has had the <c>capture_screen</c> handler wired
/// since v3.13 but no Core caller existed (Codex 2026-04-26 review flagged
/// this gap explicitly) — without this worker, vision is dormant even when
/// <see cref="VisionOptions.Enabled"/> is true.
///
/// Gates (all required to fire a capture):
///   1. <see cref="VisionOptions.Enabled"/> = true (master HIPAA gate)
///   2. <see cref="VisionPeriodicCaptureOptions.Enabled"/> = true (cadence sub-toggle)
///   3. A learning session is active in <c>state.db</c> (otherwise audit
///      entries would land on a placeholder and the dashboard wouldn't see them)
///   4. Helper accepts the IPC (Helper rejects with <c>not_foreground</c>
///      when the PMS isn't the active window — that's the alt-tab gate)
///
/// Audit chain contract (per IpcCommandServer.cs:175-191): every capture is
/// preceded by an <c>AppendChainedAuditEntry</c> with EventType="vision_capture"
/// and a CaptureReason describing why the worker fired. On success we record
/// a follow-up entry with the StorageId so forensic reconstruction can pair
/// the audit row to the encrypted screen file.
/// </summary>
public sealed class VisionCaptureWorker : BackgroundService
{
    private readonly ILogger<VisionCaptureWorker> _logger;
    private readonly AgentOptions _agentOptions;
    private readonly VisionOptions _visionOptions;
    private readonly AgentStateDb _stateDb;
    private readonly IIpcCommandClient _ipc;
    private readonly TimeProvider _clock;

    public VisionCaptureWorker(
        ILogger<VisionCaptureWorker> logger,
        IOptions<AgentOptions> agentOptions,
        IOptions<VisionOptions> visionOptions,
        AgentStateDb stateDb,
        IIpcCommandClient ipc,
        TimeProvider? clock = null)
    {
        _logger = logger;
        _agentOptions = agentOptions.Value;
        _visionOptions = visionOptions.Value;
        _stateDb = stateDb;
        _ipc = ipc;
        _clock = clock ?? TimeProvider.System;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_visionOptions.Enabled)
        {
            _logger.LogInformation(
                "VisionCaptureWorker idle — Vision.Enabled=false (master HIPAA gate)");
            return;
        }
        if (!_visionOptions.PeriodicCapture.Enabled)
        {
            _logger.LogInformation(
                "VisionCaptureWorker idle — PeriodicCapture.Enabled=false (cadence toggle off)");
            return;
        }

        var interval = TimeSpan.FromSeconds(
            Math.Max(1, _visionOptions.PeriodicCapture.IntervalSeconds));

        _logger.LogInformation(
            "VisionCaptureWorker started — interval={IntervalSec}s, requireForeground={ReqFg}",
            interval.TotalSeconds, _visionOptions.PeriodicCapture.RequireForegroundMatch);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Defensive: a single tick error must not kill the loop. The
                // Helper-side handler already returns structured errors for
                // capture_failed / not_foreground / vision_unavailable, so
                // unexpected exceptions reaching here are network/serialization
                // edge cases worth logging but not crashing for.
                _logger.LogWarning(ex,
                    "VisionCaptureWorker tick error — continuing");
            }

            try
            {
                await Task.Delay(interval, _clock, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// One periodic capture attempt. Public for testability — production
    /// code should let <see cref="ExecuteAsync"/> drive the cadence.
    /// </summary>
    public async Task<TickResult> TickAsync(CancellationToken ct)
    {
        var pharmacyId = _agentOptions.PharmacyId ?? string.Empty;
        if (string.IsNullOrEmpty(pharmacyId))
        {
            return TickResult.Skipped("no_pharmacy_id");
        }

        var sessionId = _stateDb.GetActiveSessionId(pharmacyId);
        if (string.IsNullOrEmpty(sessionId))
        {
            // No active learning session — nothing to attribute the capture to.
            // Heartbeat counters won't see it either since they query the
            // active session id. Skip silently (LearningWorker will create a
            // session when it boots).
            return TickResult.Skipped("no_active_session");
        }

        // Audit chain entry BEFORE the IPC send — the Helper-side dispatch
        // log explicitly notes the caller MUST do this, and a downstream
        // forensic auditor relies on the pre-send entry to know we ATTEMPTED
        // the capture even if Helper failed / was rate-limited / rejected.
        var commandId = Guid.NewGuid().ToString("N");
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId,
            EventType: "vision_capture",
            FromState: "trigger",
            ToState: "request",
            Trigger: "periodic_worker",
            CommandId: commandId,
            RequesterId: nameof(VisionCaptureWorker),
            Actor: "system",
            SourceComponent: nameof(VisionCaptureWorker),
            CaptureReason: "periodic_pms_observation"));

        var connected = _ipc.IsConnected ||
            await _ipc.ConnectAsync(TimeSpan.FromSeconds(2), ct);
        if (!connected)
        {
            _logger.LogDebug("VisionCaptureWorker: Helper IPC not connected — skip");
            return TickResult.Skipped("ipc_disconnected");
        }

        var request = new IpcRequest(commandId, IpcCommands.CaptureScreen, 1, null);
        var response = await _ipc.SendAsync(request, TimeSpan.FromSeconds(15), ct);

        if (response == null)
        {
            _logger.LogDebug("VisionCaptureWorker: capture timed out / connection dropped");
            return TickResult.Failed("ipc_timeout");
        }

        if (response.Status != 200)
        {
            // not_foreground / capture_failed / vision_unavailable — all
            // expected outcomes that don't warrant a panic. Log at debug
            // and let the next tick try again.
            var code = response.Error?.Code ?? "unknown";
            _logger.LogDebug(
                "VisionCaptureWorker: capture rejected status={Status} code={Code}",
                response.Status, code);

            if (code == "not_foreground" && !_visionOptions.PeriodicCapture.RequireForegroundMatch)
            {
                // Operator chose to ignore the gate; this is unusual but
                // bubble up the result so callers can decide.
                _logger.LogWarning(
                    "VisionCaptureWorker: not_foreground returned but RequireForegroundMatch=false — capture refused at Helper anyway");
            }

            return TickResult.Failed(code);
        }

        // Followup audit entry — pairs the storage id with the trigger.
        // CaptureReason matches the trigger entry above; StorageId is the
        // forensic link to the encrypted screen file on disk.
        string? storageId = null;
        try
        {
            if (response.Data is { } data && data.TryGetProperty("storageId", out var s))
                storageId = s.GetString();
        }
        catch { /* malformed payload — log via TickResult */ }

        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId,
            EventType: "vision_capture",
            FromState: "request",
            ToState: "complete",
            Trigger: "periodic_worker",
            CommandId: commandId,
            RequesterId: nameof(VisionCaptureWorker),
            Actor: "system",
            SourceComponent: nameof(VisionCaptureWorker),
            CaptureReason: "periodic_pms_observation",
            StorageId: storageId));

        _logger.LogInformation(
            "VisionCaptureWorker: capture committed — storageId={StorageId}", storageId);
        return TickResult.Captured(storageId, commandId);
    }

    public readonly record struct TickResult(
        bool Success,
        string? StorageId,
        string? CommandId,
        string? Reason)
    {
        public static TickResult Captured(string? storageId, string commandId) =>
            new(true, storageId, commandId, null);
        public static TickResult Skipped(string reason) =>
            new(false, null, null, reason);
        public static TickResult Failed(string reason) =>
            new(false, null, null, reason);
    }
}
