using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Ipc;

public static class IntentCursorCoordinateSpaces
{
    public const string Screen = "screen";
}

public static class IntentCursorAnchors
{
    public const string PrimaryCenter = "primary_center";
}

public static class IntentCursorTones
{
    public const string Agent = "agent";
    public const string Attention = "attention";
    public const string Success = "success";
    public const string Warning = "warning";
}

public static class IntentCursorEasings
{
    /// <summary>Accelerate-then-decelerate S-curve — the "agentic" feel.</summary>
    public const string EaseInOutCubic = "ease_in_out_cubic";
    public const string Linear = "linear";

    public const string Default = EaseInOutCubic;
}

/// <summary>
/// Visual-only pointer intent. Deliberately carries no text, labels, window
/// titles, Rx identifiers, or captured UI content so it cannot become a PHI
/// side channel.
///
/// When a target (<see cref="ToX"/>/<see cref="ToY"/> or <see cref="ToAnchor"/>)
/// is supplied, the cursor GLIDES from the start position to the target over
/// <see cref="DurationMs"/>, interpolated on-box every frame with <see cref="Easing"/>.
/// Without a target it renders a static halo at the start position (back-compat).
/// </summary>
public sealed record IntentCursorRequest(
    [property: JsonPropertyName("x")] double? X = null,
    [property: JsonPropertyName("y")] double? Y = null,
    [property: JsonPropertyName("coordinateSpace")] string CoordinateSpace = IntentCursorCoordinateSpaces.Screen,
    [property: JsonPropertyName("durationMs")] int DurationMs = 1200,
    [property: JsonPropertyName("diameterPx")] int DiameterPx = 34,
    [property: JsonPropertyName("opacity")] double Opacity = 0.72,
    [property: JsonPropertyName("tone")] string Tone = IntentCursorTones.Agent,
    [property: JsonPropertyName("anchor")] string? Anchor = null,
    [property: JsonPropertyName("toX")] double? ToX = null,
    [property: JsonPropertyName("toY")] double? ToY = null,
    [property: JsonPropertyName("toAnchor")] string? ToAnchor = null,
    [property: JsonPropertyName("easing")] string? Easing = null);

public sealed record IntentCursorResponse(
    [property: JsonPropertyName("shown")] bool Shown,
    [property: JsonPropertyName("coordinateSpace")] string CoordinateSpace,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("diameterPx")] int DiameterPx,
    [property: JsonPropertyName("tone")] string Tone);
