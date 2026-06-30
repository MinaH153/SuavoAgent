using System.Text.Json;

namespace SuavoAgent.Core.Compliance;

/// <summary>
/// Persists the last-known-good <see cref="ComplianceMode"/> to a JSON file
/// in the agent's data directory. Used by the installer at onboarding time to
/// record the posture after a verified verticalConfig, and by Core at startup
/// to refuse downgrade.
///
/// Atomic write: tmp → fsync → rename, so a crash never leaves a truncated file.
/// </summary>
public static class LastKnownGoodStore
{
    private const string FileName = "vertical-compliance-lkg.json";

    private sealed record LkgDto(string complianceMode);

    /// <summary>
    /// Read the stored LKG compliance mode. Returns null when no file exists
    /// (fresh install — caller should treat as Hipaa fail-closed default).
    /// </summary>
    public static ComplianceMode? TryRead(string dataDir)
    {
        var path = System.IO.Path.Combine(dataDir, FileName);
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            var dto = JsonSerializer.Deserialize<LkgDto>(json);
            return dto is null ? null : CompliancePosture.Resolve(dto.complianceMode);
        }
        catch
        {
            // Corrupt file → fail-closed: treat as Hipaa
            return ComplianceMode.Hipaa;
        }
    }

    /// <summary>
    /// Persist the compliance mode atomically. No-op if dataDir doesn't exist
    /// yet (installer creates it before calling this).
    /// </summary>
    public static void Write(string dataDir, ComplianceMode mode)
    {
        Directory.CreateDirectory(dataDir);
        var path = System.IO.Path.Combine(dataDir, FileName);
        var tmp = path + ".tmp";
        var payload = JsonSerializer.Serialize(new LkgDto(mode.ToString().ToLowerInvariant()));
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None,
                   bufferSize: 4096, FileOptions.WriteThrough))
        using (var w = new StreamWriter(fs, System.Text.Encoding.UTF8))
        {
            w.Write(payload);
            w.Flush();
            fs.Flush(flushToDisk: true);
        }
        File.Move(tmp, path, overwrite: true);
    }
}
