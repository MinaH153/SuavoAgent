using Microsoft.Win32;
using Serilog;
using SuavoAgent.Helper.Actuation;

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
    /// <summary>Legacy environment name retained only so old deployment tooling can
    /// remove it. It has no runtime effect; Release and Debug both require a signed
    /// local process approval.</summary>
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
    /// Errors during detection fail closed. Name/path discovery is not authority;
    /// the signed exact-executable approval is required by <see cref="ShouldPollForPms"/>.
    /// </summary>
    public static bool ShouldPollForPms(
        ILogger logger,
        PioneerRxProcessTrustVerifier processTrust)
    {
        ArgumentNullException.ThrowIfNull(processTrust);
        if (!processTrust.IsApproved)
        {
            logger.Warning(
                "PioneerRx attach disabled: local process approval unavailable ({Code})",
                processTrust.ApprovalCode);
            return false;
        }
        var verdict = processTrust.VerifyApprovedExecutable();
        if (!verdict.Trusted)
            logger.Warning("PioneerRx approved executable did not verify ({Code})", verdict.Code);
        return verdict.Trusted;
    }

    public static bool IsInstalled(ILogger logger)
    {
        return IsInstalledFromProbes(
            path => File.Exists(path),
            regKey =>
            {
                using var key = Registry.LocalMachine.OpenSubKey(regKey);
                return key?.GetValue("InstallPath") as string;
            },
            logger);
    }

    internal static bool IsInstalledFromProbes(
        Func<string, bool> fileExists,
        Func<string, string?> registryInstallPath,
        ILogger logger)
    {
        try
        {
            foreach (var path in KnownPaths)
            {
                var exe = Path.Combine(path, "PioneerPharmacy.exe");
                if (fileExists(exe))
                {
                    logger.Information("PioneerRx detected on disk: {Path}", path);
                    return true;
                }
            }

            foreach (var regKey in RegistryKeys)
            {
                var installPath = registryInstallPath(regKey);
                if (!string.IsNullOrEmpty(installPath))
                {
                    logger.Information("PioneerRx detected via approved registry footprint");
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "PioneerRx install detection failed — refusing attach (fail closed)");
            return false;
        }
    }
}
