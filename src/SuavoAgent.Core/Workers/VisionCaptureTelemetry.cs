namespace SuavoAgent.Core.Workers;

/// <summary>
/// Process-local counters for the periodic vision worker. The audit chain is
/// still the legal record; this snapshot exists so cloud can tell the
/// difference between "capture disabled", "Helper unavailable", and "capturing".
/// </summary>
public sealed class VisionCaptureTelemetry
{
    private readonly object _lock = new();
    private VisionCaptureTelemetrySnapshot _snapshot = VisionCaptureTelemetrySnapshot.Empty;

    public VisionCaptureTelemetrySnapshot Snapshot()
    {
        lock (_lock)
        {
            return _snapshot;
        }
    }

    public void RecordSkipped(string reason) => Record("skipped", reason, null, null);

    public void RecordFailed(string reason, string? commandId) => Record("failed", reason, commandId, null);

    public void RecordCaptured(string? storageId, string commandId) => Record("captured", "captured", commandId, storageId);

    private void Record(string outcome, string reason, string? commandId, string? storageId)
    {
        var now = DateTimeOffset.UtcNow;
        var safeReason = Sanitize(reason);
        lock (_lock)
        {
            _snapshot = _snapshot with
            {
                AttemptCount = _snapshot.AttemptCount + 1,
                CapturedCount = _snapshot.CapturedCount + (outcome == "captured" ? 1 : 0),
                FailedCount = _snapshot.FailedCount + (outcome == "failed" ? 1 : 0),
                SkippedCount = _snapshot.SkippedCount + (outcome == "skipped" ? 1 : 0),
                LastOutcome = outcome,
                LastReason = safeReason,
                LastCommandId = commandId,
                LastStorageId = storageId,
                LastAttemptAt = now,
                LastCapturedAt = outcome == "captured" ? now : _snapshot.LastCapturedAt,
            };
        }
    }

    private static string Sanitize(string? reason)
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

public sealed record VisionCaptureTelemetrySnapshot(
    long AttemptCount,
    long CapturedCount,
    long FailedCount,
    long SkippedCount,
    string? LastOutcome,
    string? LastReason,
    string? LastCommandId,
    string? LastStorageId,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? LastCapturedAt)
{
    public static VisionCaptureTelemetrySnapshot Empty { get; } = new(
        0, 0, 0, 0, null, null, null, null, null, null);

    public object ToPayload() => new
    {
        attemptCount = AttemptCount,
        capturedCount = CapturedCount,
        failedCount = FailedCount,
        skippedCount = SkippedCount,
        lastOutcome = LastOutcome,
        lastReason = LastReason,
        lastCommandId = LastCommandId,
        lastStorageId = LastStorageId,
        lastAttemptAt = LastAttemptAt?.ToString("o"),
        lastCapturedAt = LastCapturedAt?.ToString("o"),
    };
}
