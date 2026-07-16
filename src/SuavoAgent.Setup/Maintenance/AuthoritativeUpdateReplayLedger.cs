using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal enum AuthoritativeReplayState
{
    Claimed,
    Activating,
    Completed,
    RolledBack,
    Failed,
}

internal sealed record AuthoritativeReplayEntry(
    string ReplayId,
    string StagingId,
    string TargetVersion,
    AuthoritativeReplayState State,
    DateTimeOffset ClaimedAtUtc,
    DateTimeOffset UpdatedAtUtc);

internal sealed record AuthoritativeReplayRecord(
    int SchemaVersion,
    IReadOnlyList<AuthoritativeReplayEntry> Entries);

/// <summary>
/// Authoritative SYSTEM/Admin-only update replay record. Watchdog's ledger is
/// only a launch lease because Core can write its parent directory. Entries are
/// retained across success/failure so an old signed request can never reactivate.
/// </summary>
internal sealed class AuthoritativeUpdateReplayLedger
{
    private const int SchemaVersion = 1;
    private const int MaxLedgerBytes = 1024 * 1024;
    private const int MaxEntries = 10_000;
    private readonly string _path;
    private readonly object _gate = new();

    public AuthoritativeUpdateReplayLedger(string path) =>
        _path = Path.GetFullPath(path ?? throw new ArgumentNullException(nameof(path)));

    public bool TryReserve(
        string replayId,
        string stagingId,
        string targetVersion,
        DateTimeOffset now,
        out AuthoritativeReplayEntry entry)
    {
        lock (_gate)
        {
            var entries = Read().ToList();
            var existing = entries.SingleOrDefault(item =>
                string.Equals(item.ReplayId, replayId, StringComparison.Ordinal));
            if (existing is not null)
            {
                entry = existing;
                return string.Equals(existing.StagingId, stagingId, StringComparison.Ordinal) &&
                       string.Equals(existing.TargetVersion, targetVersion, StringComparison.OrdinalIgnoreCase) &&
                       existing.State is AuthoritativeReplayState.Claimed or AuthoritativeReplayState.Activating;
            }

            entry = new AuthoritativeReplayEntry(
                replayId,
                stagingId,
                targetVersion,
                AuthoritativeReplayState.Claimed,
                now,
                now);
            entries.Add(entry);
            if (entries.Count > MaxEntries)
                entries = entries
                    .OrderByDescending(item => item.UpdatedAtUtc)
                    .Take(MaxEntries)
                    .ToList();
            Write(entries);
            return true;
        }
    }

    public bool TryTransition(
        string replayId,
        AuthoritativeReplayState expected,
        AuthoritativeReplayState next,
        DateTimeOffset now)
    {
        lock (_gate)
        {
            var entries = Read().ToList();
            var index = entries.FindIndex(item =>
                string.Equals(item.ReplayId, replayId, StringComparison.Ordinal));
            if (index < 0 || entries[index].State != expected) return false;
            entries[index] = entries[index] with { State = next, UpdatedAtUtc = now };
            Write(entries);
            return true;
        }
    }

    public AuthoritativeReplayEntry? Find(string replayId)
    {
        lock (_gate)
            return Read().SingleOrDefault(item =>
                string.Equals(item.ReplayId, replayId, StringComparison.Ordinal));
    }

    private IReadOnlyList<AuthoritativeReplayEntry> Read()
    {
        if (!File.Exists(_path)) return Array.Empty<AuthoritativeReplayEntry>();
        var record = JsonSerializer.Deserialize<AuthoritativeReplayRecord>(
            BoundedFile.ReadUtf8(_path, MaxLedgerBytes),
            UpdateActivationContract.JsonOptions)
            ?? throw new InvalidDataException("Authoritative update replay ledger is null.");
        if (record.SchemaVersion != SchemaVersion ||
            record.Entries.Count > MaxEntries ||
            record.Entries.Select(item => item.ReplayId).Distinct(StringComparer.Ordinal).Count() != record.Entries.Count ||
            record.Entries.Any(item =>
                !IsSha256(item.ReplayId) ||
                !IsSha256(item.StagingId) ||
                string.IsNullOrWhiteSpace(item.TargetVersion) ||
                item.TargetVersion.Length > 80))
            throw new InvalidDataException("Authoritative update replay ledger content is invalid.");
        return record.Entries;
    }

    private void Write(IReadOnlyList<AuthoritativeReplayEntry> entries)
    {
        var directory = Path.GetDirectoryName(_path)
                        ?? throw new InvalidOperationException("Replay ledger directory is missing.");
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(
                temp,
                JsonSerializer.Serialize(
                    new AuthoritativeReplayRecord(SchemaVersion, entries),
                    UpdateActivationContract.JsonOptions));
            File.Move(temp, _path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    private static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);
}
