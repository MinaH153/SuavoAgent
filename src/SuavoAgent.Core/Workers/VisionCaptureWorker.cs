using System.Collections.Frozen;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Reasoning;
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
    private const int CloudFrameMaxItems = 300;
    private const int CloudFrameMaxDimension = 20_000;
    private static readonly FrozenSet<string> CloudFrameRoles = new[]
    {
        "button", "checkbox", "combobox", "dialog", "document", "edit",
        "element", "group", "image", "link", "list", "listitem", "menu",
        "menuitem", "pane", "radio", "row", "table", "tab", "text",
        "toolbar", "tree", "window",
    }.ToFrozenSet(StringComparer.Ordinal);

    private readonly ILogger<VisionCaptureWorker> _logger;
    private readonly AgentOptions _agentOptions;
    private readonly IOptionsMonitor<VisionOptions> _visionOptions;
    private readonly AgentStateDb _stateDb;
    private readonly IIpcCommandClient _ipc;
    private readonly TimeProvider _clock;
    private readonly VisionCaptureTelemetry _telemetry;
    private readonly IVisionShadowReasoner? _reasoner;
    private readonly IPostSigner? _cloud;
    private long _frameUploadCounter;

    public VisionCaptureWorker(
        ILogger<VisionCaptureWorker> logger,
        IOptions<AgentOptions> agentOptions,
        IOptionsMonitor<VisionOptions> visionOptions,
        AgentStateDb stateDb,
        IIpcCommandClient ipc,
        TimeProvider? clock = null,
        VisionCaptureTelemetry? telemetry = null,
        IVisionShadowReasoner? reasoner = null,
        IPostSigner? cloud = null)
    {
        _logger = logger;
        _agentOptions = agentOptions.Value;
        _visionOptions = visionOptions;
        _stateDb = stateDb;
        _ipc = ipc;
        _clock = clock ?? TimeProvider.System;
        _telemetry = telemetry ?? new VisionCaptureTelemetry();
        _reasoner = reasoner;
        _cloud = cloud;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("VisionCaptureWorker started — waiting for live vision gates");

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
                _logger.LogSafeWarning(ex);
            }

            try
            {
                var options = _visionOptions.CurrentValue;
                var interval = TimeSpan.FromSeconds(
                    Math.Max(1, options.PeriodicCapture.IntervalSeconds));
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
        var options = _visionOptions.CurrentValue;
        if (!options.Enabled)
        {
            _telemetry.RecordSkipped("vision_disabled");
            return TickResult.Skipped("vision_disabled");
        }

        if (!options.PeriodicCapture.Enabled)
        {
            _telemetry.RecordSkipped("periodic_capture_disabled");
            return TickResult.Skipped("periodic_capture_disabled");
        }

        var pharmacyId = _agentOptions.PharmacyId ?? string.Empty;
        if (string.IsNullOrEmpty(pharmacyId))
        {
            _telemetry.RecordSkipped("no_pharmacy_id");
            return TickResult.Skipped("no_pharmacy_id");
        }

        var sessionId = _stateDb.GetActiveSessionId(pharmacyId);
        if (string.IsNullOrEmpty(sessionId))
        {
            // No active learning session — nothing to attribute the capture to.
            // Heartbeat counters won't see it either since they query the
            // active session id. Skip silently (LearningWorker will create a
            // session when it boots).
            _telemetry.RecordSkipped("no_active_session");
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
            AppendCaptureOutcome(commandId, "failed", "ipc_disconnected");
            _telemetry.RecordFailed("ipc_disconnected", commandId);
            return TickResult.Skipped("ipc_disconnected");
        }

        var request = new IpcRequest(commandId, IpcCommands.CaptureScreen, 1, null);
        var response = await _ipc.SendAsync(request, TimeSpan.FromSeconds(15), ct);

        if (response == null)
        {
            _logger.LogDebug("VisionCaptureWorker: capture timed out / connection dropped");
            AppendCaptureOutcome(commandId, "failed", "ipc_timeout");
            _telemetry.RecordFailed("ipc_timeout", commandId);
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

            if (code == "not_foreground" && !options.PeriodicCapture.RequireForegroundMatch)
            {
                // Operator chose to ignore the gate; this is unusual but
                // bubble up the result so callers can decide.
                _logger.LogWarning(
                    "VisionCaptureWorker: not_foreground returned but RequireForegroundMatch=false — capture refused at Helper anyway");
            }

            AppendCaptureOutcome(commandId, "failed", code);
            _telemetry.RecordFailed(code, commandId);
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

        AppendCaptureOutcome(commandId, "complete", "captured", storageId);
        _telemetry.RecordCaptured(storageId, commandId);

        // W4b: observe-only vision-grounded reasoning. The Helper already returned the scrubbed
        // ScreenFrame in this response; when enabled, ground the brain on it and log the would-be
        // decision. Best-effort — a reasoning error must never fail the capture/audit path.
        if (_reasoner != null && options.ShadowReasoning.Enabled)
            await TryObserveAsync(response, options, ct);

        // Push the scrubbed frame to the cloud so the pharmacy dashboard can show
        // a live "what I'm seeing" wireframe. Opt-in + best-effort: only the
        // already-scrubbed structure leaves the box, and an upload error must
        // never fail the capture/audit path above.
        if (_cloud != null && options.CloudFrameUpload.Enabled)
            await TryUploadFrameToCloudAsync(response, options, ct);

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

    private static readonly JsonSerializerOptions ShadowFrameJsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Best-effort observe-only step: deserialize the scrubbed ScreenFrame the Helper returned in
    /// the capture response and hand it to the reasoner. Never throws into the capture/audit path.
    /// </summary>
    private async Task TryObserveAsync(IpcResponse response, VisionOptions options, CancellationToken ct)
    {
        try
        {
            if (response.Data is { } data &&
                data.TryGetProperty("frame", out var frameEl) &&
                frameEl.ValueKind == JsonValueKind.Object)
            {
                var frame = frameEl.Deserialize<ScreenFrame>(ShadowFrameJsonOptions);
                if (frame != null)
                    await _reasoner!.ObserveAsync(frame, options.ShadowReasoning.SkillId, ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
        }
    }

    /// <summary>
    /// Best-effort: deserialize the local ScreenFrame, project it into a metadata-only contract,
    /// and POST that projection to <c>/api/agent/screen-frame</c>. The cloud payload contains only
    /// dimensions, bounded geometry, a constant status, and allow-listed roles. OCR text, element
    /// names, automation ids, extractor ids, titles, confidence, and raw observations never enter
    /// the serializer.
    /// Honors <see cref="VisionCloudFrameUploadOptions.SamplingInterval"/>. The HMAC envelope
    /// identifies the pharmacy + agent server-side. No pixels or patient data are included.
    /// Never throws into the capture/audit path.
    /// </summary>
    private async Task TryUploadFrameToCloudAsync(IpcResponse response, VisionOptions options, CancellationToken ct)
    {
        try
        {
            var n = Math.Max(1, options.CloudFrameUpload.SamplingInterval);
            if (System.Threading.Interlocked.Increment(ref _frameUploadCounter) % n != 0)
                return;

            if (response.Data is not { } data ||
                !data.TryGetProperty("frame", out var frameEl) ||
                frameEl.ValueKind != JsonValueKind.Object)
                return;

            var frame = frameEl.Deserialize<ScreenFrame>(ShadowFrameJsonOptions);
            if (frame == null)
                return;

            var metadata = CreateCloudFrameMetadata(frame, _clock.GetUtcNow());
            if (metadata == null)
            {
                _logger.LogWarning("core.vision.cloud_frame_metadata_rejected");
                return;
            }

            await _cloud!.PostSignedAsync("/api/agent/screen-frame", new { frame = metadata }, ct);
        }
        catch (Exception ex)
        {
            _logger.LogSafeDebug(ex);
        }
    }

    private static CloudFrameMetadata? CreateCloudFrameMetadata(
        ScreenFrame frame,
        DateTimeOffset capturedAt)
    {
        if (frame.Width is <= 0 or > CloudFrameMaxDimension ||
            frame.Height is <= 0 or > CloudFrameMaxDimension)
            return null;

        var regions = frame.TextRegions
            .Take(CloudFrameMaxItems)
            .Select(region => CreateCloudBounds(region.Bounds))
            .Where(bounds => bounds is not null)
            .Select(bounds => new CloudFrameRegion(bounds!))
            .ToArray();
        var elements = frame.Elements
            .Take(CloudFrameMaxItems)
            .Select(element =>
            {
                var bounds = CreateCloudBounds(element.Bounds);
                if (bounds is null) return null;
                var candidateRole = element.Role?.Trim().ToLowerInvariant() ?? "";
                var role = CloudFrameRoles.Contains(candidateRole) ? candidateRole : "element";
                return new CloudFrameElement(role, bounds);
            })
            .Where(element => element is not null)
            .Select(element => element!)
            .ToArray();

        return new CloudFrameMetadata(
            Guid.NewGuid().ToString("D"),
            capturedAt.ToUniversalTime(),
            "captured",
            frame.Width,
            frame.Height,
            regions,
            elements);
    }

    private static CloudFrameBounds? CreateCloudBounds(Rect bounds)
    {
        if (bounds.X is < 0 or > CloudFrameMaxDimension ||
            bounds.Y is < 0 or > CloudFrameMaxDimension ||
            bounds.Width is <= 0 or > CloudFrameMaxDimension ||
            bounds.Height is <= 0 or > CloudFrameMaxDimension)
            return null;
        return new CloudFrameBounds(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }

    private sealed record CloudFrameMetadata(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("capturedAt")] DateTimeOffset CapturedAt,
        [property: JsonPropertyName("status")] string Status,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height,
        [property: JsonPropertyName("regions")] IReadOnlyList<CloudFrameRegion> Regions,
        [property: JsonPropertyName("elements")] IReadOnlyList<CloudFrameElement> Elements);

    private sealed record CloudFrameRegion(
        [property: JsonPropertyName("bounds")] CloudFrameBounds Bounds);

    private sealed record CloudFrameElement(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("bounds")] CloudFrameBounds Bounds);

    private sealed record CloudFrameBounds(
        [property: JsonPropertyName("x")] int X,
        [property: JsonPropertyName("y")] int Y,
        [property: JsonPropertyName("width")] int Width,
        [property: JsonPropertyName("height")] int Height);

    private void AppendCaptureOutcome(
        string commandId,
        string toState,
        string reason,
        string? storageId = null)
    {
        var safeReason = SanitizeAuditReason(reason);
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            TaskId: commandId,
            EventType: "vision_capture",
            FromState: "request",
            ToState: toState,
            Trigger: "periodic_worker",
            CommandId: commandId,
            RequesterId: nameof(VisionCaptureWorker),
            Actor: "system",
            SourceComponent: nameof(VisionCaptureWorker),
            CaptureReason: $"periodic_pms_observation:{safeReason}",
            StorageId: storageId));
    }

    private static string SanitizeAuditReason(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
            return "unknown";

        var chars = reason
            .Where(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            .Take(64)
            .ToArray();
        return chars.Length == 0 ? "unknown" : new string(chars);
    }
}
