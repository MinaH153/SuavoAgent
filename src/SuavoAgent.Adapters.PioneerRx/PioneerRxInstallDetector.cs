using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace SuavoAgent.Adapters.PioneerRx;

/// <summary>
/// Cheap presence check for PioneerRx on this host — used to skip SQL
/// connection attempts entirely on no-PMS sandboxes (Queen, dev workstations,
/// agent-only pilots). Pre-fix, RxDetectionWorker would burn one 30s connect
/// timeout every ~6 minutes on these hosts, logging a warning each time.
///
/// Detection distinguishes installed, absent, and indeterminate. Probe errors never activate PMS
/// capability and are surfaced separately from a clean absence.
///
/// Mirror of <see cref="SuavoAgent.Helper.PioneerRxInstallDetector"/> intended
/// for cross-project consumption — Helper's copy stays for its own attach
/// loop, this one is reachable from Core/Workers without taking a Helper
/// project dependency.
/// </summary>
public static class PioneerRxInstallDetector
{
    public enum DetectionStatus { Installed, NotInstalled, Indeterminate }
    public sealed record DetectionResult(DetectionStatus Status, string Code);
    private static readonly string[] KnownPaths =
    [
        @"C:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
        @"C:\Program Files\New Tech Computer Systems\PioneerRx",
        @"D:\Program Files (x86)\New Tech Computer Systems\PioneerRx",
        @"D:\Program Files\New Tech Computer Systems\PioneerRx",
    ];

    private static readonly string[] RegistryKeys =
    [
        @"SOFTWARE\WOW6432Node\New Tech Computer Systems",
        @"SOFTWARE\New Tech Computer Systems",
    ];

    public static bool IsInstalled(ILogger logger) =>
        Detect(logger).Status == DetectionStatus.Installed;

    public static DetectionResult Detect(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new DetectionResult(DetectionStatus.NotInstalled, "platform_not_windows");
        }

        return DetectFromProbes(path => File.Exists(path), ReadRegistryInstallPath, logger);
    }

    [SupportedOSPlatform("windows")]
    private static string? ReadRegistryInstallPath(string key)
    {
        using var registryKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(key);
        return registryKey?.GetValue("InstallPath") as string;
    }

    internal static DetectionResult DetectFromProbes(
        Func<string, bool> fileExists,
        Func<string, string?> registryInstallPath,
        ILogger logger)
    {
        var probeFailed = false;
        try
        {
            foreach (var path in KnownPaths)
            {
                var exe = Path.Combine(path, "PioneerPharmacy.exe");
                if (fileExists(exe))
                {
                    logger.LogInformation("PioneerRx installation footprint detected");
                    return new DetectionResult(DetectionStatus.Installed, "executable_present");
                }
            }
        }
        catch (Exception)
        {
            probeFailed = true;
            logger.LogWarning("PioneerRx filesystem install probe failed");
        }

        foreach (var regKey in RegistryKeys)
        {
            try
            {
                var installPath = registryInstallPath(regKey);
                if (!string.IsNullOrEmpty(installPath))
                {
                    logger.LogInformation("PioneerRx registry footprint detected");
                    return new DetectionResult(DetectionStatus.Installed, "registry_present");
                }
            }
            catch (Exception)
            {
                probeFailed = true;
                logger.LogWarning("A PioneerRx registry install probe failed");
            }
        }
        return probeFailed
            ? new DetectionResult(DetectionStatus.Indeterminate, "probe_failed")
            : new DetectionResult(DetectionStatus.NotInstalled, "footprint_absent");
    }
}
