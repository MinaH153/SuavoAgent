using System;
using System.Globalization;
using System.IO;

namespace SuavoAgent.Diagnostics;

/// <summary>
/// Cross-process liveness beacons for hang/deadlock detection. Each long-running component (Core,
/// Broker) refreshes its beacon (a Unix-ms timestamp file) only while its main loop is actually
/// servicing; the Watchdog reads the beacon alongside the SCM state. A process the SCM reports
/// RUNNING but whose beacon is stale is deadlocked — invisible to SCM, caught here. Writes are atomic
/// (temp + replace); reads fail-open to null (no beacon ⇒ the HangEvaluator's startup grace applies).
/// </summary>
public sealed class LivenessBeaconStore
{
    private readonly string _dir;

    public LivenessBeaconStore(string directory)
    {
        if (string.IsNullOrEmpty(directory)) throw new ArgumentException("directory required", nameof(directory));
        _dir = directory;
        try { Directory.CreateDirectory(_dir); } catch { /* writer/reader handle absence gracefully */ }
    }

    public static string DefaultDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "SuavoAgent", "liveness");

    private string PathFor(string component) => Path.Combine(_dir, $"{component}.beacon");

    /// <summary>Refresh a component's beacon. Atomic; best-effort (a transient I/O error is not fatal to the loop).</summary>
    public void Write(string component, DateTimeOffset now)
    {
        var path = PathFor(component);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Most recent beacon timestamp for a component, or null if none/unreadable.</summary>
    public DateTimeOffset? Read(string component)
    {
        try
        {
            var path = PathFor(component);
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
