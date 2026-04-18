using SuavoAgent.Contracts.Vision;

namespace SuavoAgent.Helper.Vision;

/// <summary>
/// Encrypts and retains captured screens on disk. Every stored screen is
/// DPAPI-encrypted at the LocalMachine scope so a disk exfiltration alone
/// cannot reveal pixels. Directory is ACL-locked to SYSTEM + the interactive
/// user; other users on the machine cannot read.
///
/// Retention: TTL-based with an absolute cap. Old screens auto-purge so
/// disk usage stays bounded even if capture cadence spikes.
/// </summary>
public interface IScreenStore
{
    /// <summary>
    /// Encrypts and persists a screen. Returns the stored record's id on success,
    /// null on failure (disk full, ACL error, etc.). Never throws.
    /// </summary>
    Task<string?> StoreAsync(ScreenBytes screen, CancellationToken ct);

    /// <summary>
    /// Decrypts and returns the PNG bytes for a previously stored screen.
    /// Returns null if the id is unknown or the file has been purged.
    /// </summary>
    Task<ScreenBytes?> LoadAsync(string id, CancellationToken ct);

    /// <summary>
    /// Runs the retention purge manually. Normally called on capture + periodically.
    /// Returns the number of files removed.
    /// </summary>
    Task<int> PurgeExpiredAsync(CancellationToken ct);
}
