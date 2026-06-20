using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Tone keys for the presence cursor. Brand DNA: gold = acting,
/// sage = observing/learning, wine = awaiting confirmation.</summary>
public static class PresenceTones
{
    public const string Acting = "acting";        // gold #C8A96A
    public const string Observing = "observing";  // sage
    public const string Confirm = "confirm";      // wine-red danger
}

/// <summary>Presence-layer preferences. Source of truth is the dashboard
/// (synced over heartbeat, Phase 5); a local presence.json + SafeDefault keep
/// it working offline. Visuals are cosmetic and never gate actuation.</summary>
public sealed record PresencePreferences
{
    public bool Enabled { get; init; } = true;
    public bool CursorVisible { get; init; } = true;
    public bool BubbleVisible { get; init; } = true;
    public bool GlowVisible { get; init; } = true;
    public bool ObserveVisualsVisible { get; init; } = true;
    public string Tone { get; init; } = PresenceTones.Acting;
    public int CursorSizePx { get; init; } = 34;
    public int GlideSpeedPxPerSec { get; init; } = 1600;
    public string Easing { get; init; } = "ease-in-out-cubic";
    public double GlowIntensity { get; init; } = 0.6;
    public string BubbleVerbosity { get; init; } = "labels"; // off | labels | labels+llm
    public bool AutoObserveOnTakeover { get; init; } = true;
    public int TargetMonitor { get; init; } = 0; // 0 = primary
    public bool MirrorToDashboard { get; init; } = true;
    public bool SuppressWhenSessionDisconnected { get; init; } = true;

    /// <summary>Cursor renders only when enabled AND visible. Either false = silent agent.</summary>
    public bool IsCursorActive => Enabled && CursorVisible;

    public static PresencePreferences SafeDefault() => new();

    private sealed record Json(
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("cursorVisible")] bool? CursorVisible,
        [property: JsonPropertyName("bubbleVisible")] bool? BubbleVisible,
        [property: JsonPropertyName("glowVisible")] bool? GlowVisible,
        [property: JsonPropertyName("observeVisualsVisible")] bool? ObserveVisualsVisible,
        [property: JsonPropertyName("tone")] string? Tone,
        [property: JsonPropertyName("cursorSizePx")] int? CursorSizePx,
        [property: JsonPropertyName("glideSpeedPxPerSec")] int? GlideSpeedPxPerSec,
        [property: JsonPropertyName("easing")] string? Easing,
        [property: JsonPropertyName("glowIntensity")] double? GlowIntensity,
        [property: JsonPropertyName("bubbleVerbosity")] string? BubbleVerbosity,
        [property: JsonPropertyName("autoObserveOnTakeover")] bool? AutoObserveOnTakeover,
        [property: JsonPropertyName("targetMonitor")] int? TargetMonitor,
        [property: JsonPropertyName("mirrorToDashboard")] bool? MirrorToDashboard,
        [property: JsonPropertyName("suppressWhenSessionDisconnected")] bool? SuppressWhenSessionDisconnected);

    /// <summary>Parse + clamp operator JSON. Any failure → SafeDefault (never throws).</summary>
    public static PresencePreferences FromJson(string? raw, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SafeDefault();
        var safe = SafeDefault();
        try
        {
            var j = JsonSerializer.Deserialize<Json>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (j is null) return safe;
            return safe with
            {
                Enabled = j.Enabled ?? safe.Enabled,
                CursorVisible = j.CursorVisible ?? safe.CursorVisible,
                BubbleVisible = j.BubbleVisible ?? safe.BubbleVisible,
                GlowVisible = j.GlowVisible ?? safe.GlowVisible,
                ObserveVisualsVisible = j.ObserveVisualsVisible ?? safe.ObserveVisualsVisible,
                Tone = j.Tone ?? safe.Tone,
                CursorSizePx = j.CursorSizePx is { } cs ? Math.Clamp(cs, 8, 200) : safe.CursorSizePx,
                GlideSpeedPxPerSec = j.GlideSpeedPxPerSec is { } gs ? Math.Clamp(gs, 200, 8000) : safe.GlideSpeedPxPerSec,
                Easing = j.Easing ?? safe.Easing,
                GlowIntensity = j.GlowIntensity is { } gi ? Math.Clamp(gi, 0.0, 1.0) : safe.GlowIntensity,
                BubbleVerbosity = j.BubbleVerbosity ?? safe.BubbleVerbosity,
                AutoObserveOnTakeover = j.AutoObserveOnTakeover ?? safe.AutoObserveOnTakeover,
                TargetMonitor = j.TargetMonitor is { } tm ? Math.Clamp(tm, 0, 16) : safe.TargetMonitor,
                MirrorToDashboard = j.MirrorToDashboard ?? safe.MirrorToDashboard,
                SuppressWhenSessionDisconnected = j.SuppressWhenSessionDisconnected ?? safe.SuppressWhenSessionDisconnected,
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Presence: failed to parse preferences JSON, using safe default");
            return safe;
        }
    }
}
