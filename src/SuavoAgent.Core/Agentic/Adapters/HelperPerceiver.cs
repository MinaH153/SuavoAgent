using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Ipc;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Production <see cref="IPerceiver"/>. Pulls a fresh scrubbed snapshot on demand over the
/// Core→Helper <c>capture_screen</c> IPC (the Helper runs ScreenCaptureController.CaptureAndExtractAsync,
/// already PHI-scrubbed at the factory) and maps the returned <see cref="ScreenFrame"/> into the
/// loop's <see cref="PerceivedScreen"/>. Fail-closed: any IPC failure, non-200 status (e.g.
/// not_foreground), or missing frame returns null — the loop treats null as a no-perception strike,
/// never as "empty screen".
/// </summary>
public sealed class HelperPerceiver : IPerceiver
{
    private static readonly JsonSerializerOptions FrameJson = new() { PropertyNameCaseInsensitive = true };

    private readonly IIpcCommandClient _ipc;
    private readonly Func<string> _commandId;
    private readonly TimeSpan _captureTimeout;
    // Non-null ONLY for explore_sandbox runs — embeds {targetProcess} in the capture_screen request
    // so the Helper routes to the window-scoped sandbox capture path. Null for PMS/navigate/replay.
    private readonly string? _targetProcess;

    public HelperPerceiver(IIpcCommandClient ipc, Func<string>? commandId = null, TimeSpan? captureTimeout = null,
        string? targetProcess = null)
    {
        _ipc = ipc ?? throw new ArgumentNullException(nameof(ipc));
        _commandId = commandId ?? (() => Guid.NewGuid().ToString("n"));
        _captureTimeout = captureTimeout ?? TimeSpan.FromSeconds(15);
        _targetProcess = targetProcess;
    }

    public async Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct)
    {
        var connected = _ipc.IsConnected || await _ipc.ConnectAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        if (!connected)
            return null;

        // Sandbox explore runs embed targetProcess so the Helper uses window-scoped capture; all other
        // callers send null Data (PMS cadence path), preserving the existing contract byte-for-byte.
        JsonElement? requestData = _targetProcess is null
            ? null
            : JsonSerializer.SerializeToElement(new { targetProcess = _targetProcess });
        var request = new IpcRequest(_commandId(), IpcCommands.CaptureScreen, 1, requestData);
        var response = await _ipc.SendAsync(request, _captureTimeout, ct).ConfigureAwait(false);

        if (response is null || response.Status != 200 || response.Data is not { } data)
            return null;

        if (!data.TryGetProperty("frame", out var frameEl) || frameEl.ValueKind != JsonValueKind.Object)
            return null;

        ScreenFrame? frame;
        try
        {
            frame = frameEl.Deserialize<ScreenFrame>(FrameJson);
        }
        catch (JsonException)
        {
            return null;
        }

        return frame is null ? null : PerceivedScreenMapper.FromFrame(frame);
    }
}

/// <summary>
/// Pure mapping ScreenFrame → PerceivedScreen. Builds a flat, scrubbed element/text summary the
/// reasoner can ground on, and a deterministic content hash for the loop's settle + no-progress
/// detection (same screen content ⇒ same hash, independent of capture timestamp / extractor id).
/// </summary>
public static class PerceivedScreenMapper
{
    public static PerceivedScreen FromFrame(ScreenFrame frame)
    {
        var summary = new List<string>(frame.Elements.Count + frame.TextRegions.Count);
        var signatures = new List<string>();

        foreach (var e in frame.Elements)
        {
            summary.Add(string.IsNullOrEmpty(e.Name) ? e.Role : $"{e.Role}:{e.Name}");
            // GREEN-tier structural signature ("controlType|automationId") for template-replay checks.
            if (!string.IsNullOrEmpty(e.AutomationId))
                signatures.Add($"{e.Role}|{e.AutomationId}");
        }

        foreach (var t in frame.TextRegions)
            if (!string.IsNullOrWhiteSpace(t.Text))
                summary.Add($"text:{t.Text}");

        // Frames from the Helper are PHI-scrubbed by construction (PhiScrubbingExtractor wraps every
        // extractor at the factory). The Scrubbed flag carries that invariant to ISafetyGate.AssertScrubbed.
        return new PerceivedScreen(ComputeContentHash(summary), Scrubbed: true, summary, WindowTitle: null, Signatures: signatures);
    }

    private static string ComputeContentHash(IReadOnlyList<string> lines)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", lines)));
        return Convert.ToHexString(bytes, 0, 8); // 16 hex chars — enough to distinguish screen states
    }
}
