using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// On-disk persistence for OTA-fetched <see cref="RulesetBundle"/> bundles
/// (Codex Comp 2 chunk D HIGH + chunk B MEDIUM RESOLVED).
/// </summary>
/// <remarks>
/// <para>
/// Three-file slot per the design: <c>ruleset-current.json</c> is the
/// active bundle; <c>ruleset-previous.json</c> is the SINGLE-slot rollback
/// state populated by <see cref="File.Replace(string, string, string?)"/>
/// during atomic updates. Each successful save overwrites the prior backup —
/// we intentionally don't keep a history. The embedded ruleset in the
/// agent assembly remains the always-available fallback if both
/// <c>current</c> and <c>previous</c> are corrupt.
/// </para>
/// <para>
/// First install (no <c>current</c> exists) uses <see cref="File.Move(string, string)"/>;
/// subsequent updates use <see cref="File.Replace(string, string, string?)"/>
/// which is atomic on NTFS (Windows) and produces a recoverable backup.
/// On APFS / ext4, <see cref="File.Replace"/> degrades to copy+rename but
/// the open-file-handle preservation semantic still holds; the agent's
/// runtime swap path is built off the <see cref="RulesetRuntime"/>
/// snapshot, not the file handle.
/// </para>
/// <para>
/// <see cref="InitializeAsync"/> runs once at startup and deletes any
/// <c>ruleset-*.tmp</c> file older than <see cref="OrphanAge"/> (1 hour)
/// — partial-write crashes mid-<see cref="File.Replace"/> can leave these
/// behind. Younger temps may be in-flight from a sibling process and are
/// left alone (defense-in-depth against accidentally clobbering a parallel
/// install / smoke test).
/// </para>
/// </remarks>
public sealed class RulesetSyncStore
{
    internal const string CurrentFileName = "ruleset-current.json";
    internal const string PreviousFileName = "ruleset-previous.json";
    internal const string TempPrefix = "ruleset-";
    internal const string TempSuffix = ".tmp";
    internal static readonly TimeSpan OrphanAge = TimeSpan.FromHours(1);

    private readonly string _directory;
    private readonly ILogger<RulesetSyncStore>? _logger;

    public RulesetSyncStore(string directory, ILogger<RulesetSyncStore>? logger = null)
    {
        _directory = directory ?? throw new ArgumentNullException(nameof(directory));
        _logger = logger;
    }

    /// <summary>Absolute path to the active bundle on disk.</summary>
    public string CurrentPath => Path.Combine(_directory, CurrentFileName);

    /// <summary>Absolute path to the single-slot rollback backup.</summary>
    public string PreviousPath => Path.Combine(_directory, PreviousFileName);

    /// <summary>
    /// Ensure the directory exists and sweep orphan-temp files. Runs once
    /// at startup before the first <see cref="SaveAsync"/> call. Never
    /// throws past the caller boundary; failures log + continue (the
    /// next save's <see cref="Directory.CreateDirectory(string)"/> call
    /// retries the mkdir if needed).
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(_directory);
            await SweepOrphanTempFilesAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogSafeWarning(ex);
        }
    }

    /// <summary>
    /// Read <see cref="CurrentPath"/> and deserialise to a
    /// <see cref="RulesetBundle"/>, or <c>null</c> if the file is absent /
    /// unreadable / malformed. Never throws past the caller boundary —
    /// the calling worker's failure-mode contract treats missing / corrupt
    /// cache as "fall back to embedded" + alarm.
    /// </summary>
    public RulesetBundle? TryLoadCurrent()
    {
        if (!File.Exists(CurrentPath))
        {
            return null;
        }
        try
        {
            using var stream = File.OpenRead(CurrentPath);
            return JsonSerializer.Deserialize<RulesetBundle>(stream, BundleJsonOptions);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger?.LogSafeWarning(ex);
            return null;
        }
    }

    /// <summary>
    /// Currently-cached <c>ruleset_version</c> string, or <c>null</c> if
    /// no cache. Display-only (string compare is lexicographically unsafe);
    /// the worker uses <see cref="GetCurrentVersionInt"/> for OTA freshness
    /// gating.
    /// </summary>
    public string? GetCurrentVersion() => TryLoadCurrent()?.Ruleset.RulesetVersion;

    /// <summary>
    /// Currently-cached <c>ruleset_version_int</c>, or <c>0</c> if no cache.
    /// Codex Comp 2 round-7 HIGH: monotonic int compared by the OTA
    /// freshness gate in <see cref="Workers.ConfigSyncWorker"/>.
    /// </summary>
    public int GetCurrentVersionInt() => TryLoadCurrent()?.Ruleset.RulesetVersionInt ?? 0;

    /// <summary>
    /// Atomically write <paramref name="bundle"/> to disk. First install
    /// uses <see cref="File.Move(string, string)"/>; subsequent saves use
    /// <see cref="File.Replace(string, string, string?)"/> which produces
    /// a single-slot backup at <see cref="PreviousPath"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Codex Comp 2 chunk B MEDIUM RESOLVED. The Phase 1 design's
    /// <see cref="File.Move(string, string)"/>-only path only suits FIRST
    /// install when no final file exists. For subsequent updates,
    /// <see cref="File.Replace"/> preserves name identity (open file
    /// handles continue working) and writes a recoverable backup.
    /// </para>
    /// <para>
    /// Throws on disk failure (read-only FS, ENOSPC, ACL denial). The
    /// caller's failure-mode contract treats disk-write fail as "in-memory
    /// swap still happens; alarm; next restart loads embedded".
    /// </para>
    /// </remarks>
    public async Task SaveAsync(RulesetBundle bundle, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        Directory.CreateDirectory(_directory);

        // Write temp in the SAME directory as final so File.Replace stays
        // atomic — cross-volume moves degrade to copy+delete and lose the
        // atomicity guarantee (Codex Comp 2 chunk B MEDIUM).
        var tempPath = Path.Combine(_directory, $"{TempPrefix}{Guid.NewGuid():N}{TempSuffix}");

        try
        {
            await using (var fs = new FileStream(
                tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(fs, bundle, BundleJsonOptions, ct).ConfigureAwait(false);
                await fs.FlushAsync(ct).ConfigureAwait(false);
                // fsync to survive power loss between write and rename.
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(CurrentPath))
            {
                // Atomic on NTFS; near-atomic on APFS/ext4. Backup goes to
                // PreviousPath — overwriting any prior backup.
                File.Replace(tempPath, CurrentPath, PreviousPath);
            }
            else
            {
                File.Move(tempPath, CurrentPath);
            }
        }
        catch
        {
            // Best-effort cleanup if the rename/replace failed but the temp
            // landed on disk. Errors swallowed; the orphan-sweep on next
            // Initialize picks up the slack.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch { /* swallow */ }
            throw;
        }
    }

    private Task SweepOrphanTempFilesAsync(CancellationToken ct)
    {
        var cutoffUtc = DateTime.UtcNow - OrphanAge;
        var pattern = $"{TempPrefix}*{TempSuffix}";
        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(_directory, pattern))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var info = new FileInfo(path);
                if (info.LastWriteTimeUtc < cutoffUtc)
                {
                    File.Delete(path);
                    deleted++;
                }
            }
            catch (Exception ex)
                when (ex is IOException or UnauthorizedAccessException or FileNotFoundException)
            {
                _logger?.LogSafeWarning(ex);
            }
        }
        if (deleted > 0)
        {
            _logger?.LogInformation("RulesetSyncStore: swept {Count} orphan temp files", deleted);
        }
        return Task.CompletedTask;
    }

    private static readonly JsonSerializerOptions BundleJsonOptions = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
