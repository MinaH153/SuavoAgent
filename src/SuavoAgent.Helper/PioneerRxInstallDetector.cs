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
    /// <summary>
    /// Eval/CI seam. A bare sim box (e.g. Queen) runs PioneerPharmacy.exe from a
    /// rehearsal dir and satisfies neither <see cref="KnownPaths"/> nor
    /// <see cref="RegistryKeys"/>, so <see cref="IsInstalled"/> is false and the
    /// Helper skips the whole UIA attach loop — the interaction observer never
    /// subscribes and the FSD eval's Observe stage is structurally 0. Setting this
    /// machine env var to "1" forces the attach loop so the live moat can be graded
    /// against the sim (attach itself still matches by process name, unchanged).
    /// OFF by default; NEVER set on a real pharmacy box — there IsInstalled() is
    /// already true and this would be a redundant no-op anyway.
    /// </summary>
    internal const string ForceAttachEnvVar = "SUAVOAGENT_FORCE_PMS_ATTACH";

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
    /// <summary>
    /// Whether the Helper should enter the PMS attach-polling loop. True if the
    /// eval/CI override (<see cref="ForceAttachEnvVar"/>=1) is set, otherwise
    /// delegates to <see cref="IsInstalled"/>. Program.cs gates the attach loop on
    /// THIS, not IsInstalled directly, so a sim/eval box can observe without a real
    /// PioneerRx footprint. IsInstalled stays a pure "is the PMS on disk/registry?".
    /// </summary>
    public static bool ShouldPollForPms(ILogger logger)
    {
        if (string.Equals(
                Environment.GetEnvironmentVariable(ForceAttachEnvVar), "1", StringComparison.Ordinal))
        {
            logger.Information(
                "PMS attach forced via {Var}=1 (eval/CI override) — entering attach polling despite no detected install",
                ForceAttachEnvVar);
            return true;
        }
        return IsInstalled(logger);
    }

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
