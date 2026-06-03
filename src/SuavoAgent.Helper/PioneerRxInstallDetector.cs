using Microsoft.Win32;
using Serilog;

namespace SuavoAgent.Helper;

/// <summary>
/// Lightweight presence check for PioneerRx on this host. Returns true if the
/// PMS is installed (binary on disk OR registry key), false otherwise. The
/// running-process check is intentionally OMITTED — Helper's polling loop
/// exists precisely to wait for PioneerPharmacy.exe to start, so checking the
/// process here would defeat the purpose. We only want to skip polling
/// entirely on machines where the PMS isn't installed at all (sandboxes, dev
/// workstations, no-PMS pilots).
///
/// Mirror of the discovery logic in <see cref="SuavoAgent.Setup.PioneerRxDiscovery"/>
/// but trimmed to a yes/no answer — Helper doesn't need the install path.
///
/// Why this exists: pre-2026-05-06 the Helper attach loop polled every 10
/// seconds for up to 30 attempts (5 min), then exited 1 to be respawned by
/// Broker. On Queen and other no-PMS sandboxes, that meant 30 warning lines
/// + a Helper restart cycle every 5 minutes — log noise + Broker churn for
/// no operational reason. This check lets Helper short-circuit cleanly.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class PioneerRxInstallDetector
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

    /// <summary>
    /// Returns true if PioneerRx appears to be installed on this host.
    /// Errors during detection are logged at debug and treated as "installed"
    /// (fail-open) so a registry permissions hiccup doesn't accidentally
    /// suppress polling on a real PMS box.
    /// </summary>
    public static bool IsInstalled(ILogger logger)
    {
        try
        {
            foreach (var path in KnownPaths)
            {
                var exe = Path.Combine(path, "PioneerPharmacy.exe");
                if (File.Exists(exe))
                {
                    logger.Information("PioneerRx detected on disk: {Path}", path);
                    return true;
                }
            }

            foreach (var regKey in RegistryKeys)
            {
                try
                {
                    using var key = Registry.LocalMachine.OpenSubKey(regKey);
                    var installPath = key?.GetValue("InstallPath") as string;
                    if (!string.IsNullOrEmpty(installPath))
                    {
                        logger.Information(
                            "PioneerRx detected via registry: {Key} -> {Path}",
                            regKey, installPath);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.Debug(ex, "PioneerRx registry probe failed for {Key} (non-fatal)", regKey);
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            // Fail-open: if detection itself errors, assume PioneerRx is
            // present and let the polling loop sort it out. Better one
            // log line than a wrongly-suppressed PMS attach.
            logger.Warning(ex, "PioneerRx install detection failed — assuming installed (fail-open)");
            return true;
        }
    }
}
