using Microsoft.Extensions.Logging;

namespace SuavoAgent.Adapters.PioneerRx;

/// <summary>
/// Cheap presence check for PioneerRx on this host — used to skip SQL
/// connection attempts entirely on no-PMS sandboxes (Queen, dev workstations,
/// agent-only pilots). Pre-fix, RxDetectionWorker would burn one 30s connect
/// timeout every ~6 minutes on these hosts, logging a warning each time.
///
/// Returns true if either a known PioneerRx install path exists on disk OR a
/// known registry key resolves on Windows. Non-Windows hosts always return
/// false (the PMS only ships on Windows). Errors are fail-open (return true)
/// so a transient permissions hiccup doesn't accidentally suppress polling on
/// a real pharmacy box.
///
/// Mirror of <see cref="SuavoAgent.Helper.PioneerRxInstallDetector"/> intended
/// for cross-project consumption — Helper's copy stays for its own attach
/// loop, this one is reachable from Core/Workers without taking a Helper
/// project dependency.
/// </summary>
public static class PioneerRxInstallDetector
{
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

    public static bool IsInstalled(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            foreach (var path in KnownPaths)
            {
                var exe = Path.Combine(path, "PioneerPharmacy.exe");
                if (File.Exists(exe))
                {
                    logger.LogInformation("PioneerRx detected on disk: {Path}", path);
                    return true;
                }
            }

            return ProbeRegistry(logger);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "PioneerRx install detection failed — assuming installed (fail-open)");
            return true;
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool ProbeRegistry(ILogger logger)
    {
        foreach (var regKey in RegistryKeys)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(regKey);
                var installPath = key?.GetValue("InstallPath") as string;
                if (!string.IsNullOrEmpty(installPath))
                {
                    logger.LogInformation(
                        "PioneerRx detected via registry: {Key} -> {Path}",
                        regKey, installPath);
                    return true;
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "PioneerRx registry probe failed for {Key} (non-fatal)", regKey);
            }
        }
        return false;
    }
}
