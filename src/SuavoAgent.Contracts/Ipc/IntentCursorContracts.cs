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

/// <summary>
/// Visual-only pointer intent. Deliberately carries no text, labels, window
/// titles, Rx identifiers, or captured UI content so it cannot become a PHI
/// side channel.
/// </summary>
public sealed record IntentCursorRequest(
    [property: JsonPropertyName("x")] double? X = null,
    [property: JsonPropertyName("y")] double? Y = null,
    [property: JsonPropertyName("coordinateSpace")] string CoordinateSpace = IntentCursorCoordinateSpaces.Screen,
    [property: JsonPropertyName("durationMs")] int DurationMs = 1200,
    [property: JsonPropertyName("diameterPx")] int DiameterPx = 34,
    [property: JsonPropertyName("opacity")] double Opacity = 0.72,
    [property: JsonPropertyName("tone")] string Tone = IntentCursorTones.Agent,
    [property: JsonPropertyName("anchor")] string? Anchor = null);

public sealed record IntentCursorResponse(
    [property: JsonPropertyName("shown")] bool Shown,
    [property: JsonPropertyName("coordinateSpace")] string CoordinateSpace,
    [property: JsonPropertyName("durationMs")] int DurationMs,
    [property: JsonPropertyName("diameterPx")] int DiameterPx,
    [property: JsonPropertyName("tone")] string Tone);
