using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace SuavoAgent.Broker.Honeytoken;

/// <summary>
/// Builds + caches the NT-device → drive-letter map and forward-resolves a Win32 directory to its NT-device
/// prefix, so the hot ETW callback can do ONE Ordinal compare (NT-to-NT) per file open. Kernel-File ETW emits
/// <c>\Device\HarddiskVolumeN\…</c>, NOT <c>C:\…</c> — a naive <c>C:\</c> StartsWith never matches and the
/// oracle goes silently deaf while logging healthy (the silent-blindness footgun). The pure
/// <see cref="Normalize"/> core + the <see cref="Refresh(System.Func{System.Collections.Generic.IEnumerable{string}}, System.Func{string, string?})"/>
/// seam are headless-testable; the public <see cref="Refresh()"/> wraps them with the real Win32 calls.
/// </summary>
public sealed class DevicePathNormalizer
{
    // NT-device → drive (e.g. "\Device\HarddiskVolume3" → "C:"); OrdinalIgnoreCase keys. Volatile atomic swap.
    private volatile IReadOnlyDictionary<string, string> _deviceToDrive =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Constructs with an EMPTY map (no OS work — safe to construct on any platform, incl. the Linux CI host).
    /// The caller must invoke <see cref="Refresh()"/> on Windows before <see cref="ToNtPrefix"/> resolves
    /// anything. (An un-refreshed normalizer returns null → the tracer self-test refuses to arm — never blind.)
    /// </summary>
    public DevicePathNormalizer() { }

    /// <summary>Rebuild the device map from the live OS (Windows-only).</summary>
    [SupportedOSPlatform("windows")]
    public void Refresh() => Refresh(EnumerateDriveRoots, QueryDosDevice);

    /// <summary>Headless seam: rebuild from injected providers (no P/Invoke). drive roots like "C:\".</summary>
    internal void Refresh(Func<IEnumerable<string>> driveRootProvider, Func<string, string?> deviceResolver)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in driveRootProvider())
        {
            // "C:\" → "C:" (QueryDosDevice wants the drive without a trailing slash).
            var drive = root.TrimEnd('\\', '/');
            if (drive.Length < 2) continue;
            var device = deviceResolver(drive);
            if (!string.IsNullOrEmpty(device)) map[device!] = drive;
        }
        if (map.Count > 0) _deviceToDrive = map; // never replace a good map with an empty one
    }

    /// <summary>
    /// Resolve a Win32 directory ("C:\ProgramData\SuavoAgent\honeytokens") to its NT-device prefix
    /// ("\Device\HarddiskVolume3\ProgramData\SuavoAgent\honeytokens"), or null if the drive is unmapped
    /// (→ caller must treat as BLIND and refuse to arm, never match-all).
    /// </summary>
    public string? ToNtPrefix(string win32Dir) => Normalize(_deviceToDrive, win32Dir);

    /// <summary>Pure, headless-testable core.</summary>
    internal static string? Normalize(IReadOnlyDictionary<string, string> deviceToDrive, string win32Dir)
    {
        if (string.IsNullOrWhiteSpace(win32Dir)) return null;
        var full = win32Dir.Trim().TrimEnd('\\', '/');
        if (full.Length < 2 || full[1] != ':') return null;       // need a "X:" drive-qualified path
        var drive = full[..2];                                    // "C:"
        var tail = full[2..];                                     // "\ProgramData\SuavoAgent\honeytokens"

        // Find the NT device whose mapped drive equals this one (the map is device→drive).
        foreach (var kvp in deviceToDrive)
        {
            if (string.Equals(kvp.Value, drive, StringComparison.OrdinalIgnoreCase))
                return kvp.Key + tail;
        }
        return null;
    }

    // --- Win32 ------------------------------------------------------------------

    [SupportedOSPlatform("windows")]
    private static IEnumerable<string> EnumerateDriveRoots()
    {
        // DriveInfo is the simplest reliable enumerator of logical drive roots ("C:\", "D:\").
        foreach (var d in DriveInfo.GetDrives())
            yield return d.Name;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDeviceW(string lpDeviceName, StringBuilder lpTargetPath, uint ucchMax);

    [SupportedOSPlatform("windows")]
    private static string? QueryDosDevice(string drive)
    {
        var sb = new StringBuilder(1024);
        var len = QueryDosDeviceW(drive, sb, (uint)sb.Capacity);
        return len == 0 ? null : sb.ToString();
    }
}
