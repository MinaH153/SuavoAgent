using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Behavioral;

public enum BehavioralEventType
{
    TreeSnapshot,
    Interaction,
    KeystrokeCategory,
    AppFocusChange,
    SessionChange,
    StationProfile
}

/// <summary>
/// Immutable behavioral event — PHI-free UIA observation.
/// All Name/text fields are HMAC-hashed before reaching this type.
/// </summary>
public sealed record BehavioralEvent
{
    [JsonPropertyName("seq")]
    public long Seq { get; init; }

    [JsonPropertyName("type")]
    public BehavioralEventType Type { get; init; }

    [JsonPropertyName("subtype")]
    public string? Subtype { get; init; }

    [JsonPropertyName("treeHash")]
    public string? TreeHash { get; init; }

    [JsonPropertyName("elementId")]
    public string? ElementId { get; init; }

    [JsonPropertyName("controlType")]
    public string? ControlType { get; init; }

    [JsonPropertyName("className")]
    public string? ClassName { get; init; }

    [JsonPropertyName("nameHash")]
    public string? NameHash { get; init; }

    [JsonPropertyName("boundingRect")]
    public string? BoundingRect { get; init; }

    [JsonPropertyName("keystroke")]
    public KeystrokeCategory? KeystrokeCat { get; init; }

    [JsonPropertyName("timing")]
    public TimingBucket? Timing { get; init; }

    [JsonPropertyName("keystrokeCount")]
    public int? KeystrokeCount { get; init; }

    [JsonPropertyName("occurrenceCount")]
    public int OccurrenceCount { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    // ── Factory methods ──────────────────────────────────────────────────────

    public static BehavioralEvent Interaction(
        string subtype,
        string? treeHash,
        string? elementId,
        string? controlType,
        string? className,
        string? nameHash) =>
        new()
        {
            Type = BehavioralEventType.Interaction,
            Subtype = subtype,
            TreeHash = treeHash,
            ElementId = elementId,
            ControlType = controlType,
            ClassName = className,
            NameHash = nameHash,
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    public static BehavioralEvent TreeSnapshot(string treeHash) =>
        new()
        {
            Type = BehavioralEventType.TreeSnapshot,
            TreeHash = treeHash,
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    /// <summary>
    /// Keystroke event. Digit category caps sequenceCount at 3 (HIPAA: no exact digit count).
    /// </summary>
    public static BehavioralEvent Keystroke(
        KeystrokeCategory category,
        TimingBucket timing,
        int sequenceCount)
    {
        var cappedCount = category == KeystrokeCategory.Digit
            ? Math.Min(sequenceCount, 3)
            : sequenceCount;

        return new()
        {
            Type = BehavioralEventType.KeystrokeCategory,
            KeystrokeCat = category,
            Timing = timing,
            KeystrokeCount = cappedCount,
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };
    }

    public static BehavioralEvent AppFocusChange(
        string fromProcessName, string toProcessName,
        string? windowTitleHash, long focusDurationMs) =>
        new()
        {
            Type = BehavioralEventType.AppFocusChange,
            Subtype = "focus_change",
            ElementId = toProcessName,
            ClassName = fromProcessName,
            NameHash = windowTitleHash,
            KeystrokeCount = (int)Math.Min(focusDurationMs, int.MaxValue),
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    public static BehavioralEvent SessionChange(string changeType, string? userSidHash) =>
        new()
        {
            Type = BehavioralEventType.SessionChange,
            Subtype = changeType,
            NameHash = userSidHash,
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    public static BehavioralEvent StationProfileEvent(string profileJson) =>
        new()
        {
            Type = BehavioralEventType.StationProfile,
            Subtype = "station_profile",
            TreeHash = profileJson,
            OccurrenceCount = 1,
            Timestamp = DateTimeOffset.UtcNow
        };

    /// <summary>Returns a new event with Seq assigned (immutable copy).</summary>
    public BehavioralEvent WithSeq(long seq) => this with { Seq = seq };
}

/// <summary>
/// HIPAA-safe scrubbed UIA element.
/// RED tier properties (Value, Text, Selection, HelpText, ItemStatus) are EXCLUDED.
/// GREEN tier only: structural + hash identifiers.
/// </summary>
public sealed record ScrubbedElement(
    string? ControlType,
    string? AutomationId,
    string? ClassName,
    string? NameHash,
    string? BoundingRect,
    int Depth,
    int ChildIndex);
