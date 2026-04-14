using Microsoft.Win32;
using System.Diagnostics;

namespace SuavoAgent.Setup;

/// <summary>
/// Discovers PioneerRx installation path via registry, known paths, and running process.
/// Returns (pioneerDir, pioneerExe, pioneerConfig) or null if not found.
/// </summary>
internal static class PioneerRxDiscovery
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

    public sealed record DiscoveryResult(
        string PioneerDir,
        string PioneerExe,
        string PioneerConfig);

    public static DiscoveryResult? Discover()
    {
        // Strategy 1: Registry
        var regResult = TryRegistry();
        if (regResult != null) return regResult;

        // Strategy 2: Known paths
        var pathResult = TryKnownPaths();
        if (pathResult != null) return pathResult;

        // Strategy 3: Running process
        return TryRunningProcess();
    }

    private static DiscoveryResult? TryRegistry()
    {
        foreach (var regKey in RegistryKeys)
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(regKey);
                if (key == null) continue;

                var installPath = key.GetValue("InstallPath") as string;
                if (string.IsNullOrEmpty(installPath)) continue;

                ConsoleUI.WriteInfo($"Registry found: {regKey}");
                var result = ValidatePath(installPath);
                if (result != null) return result;
            }
            catch
            {
                // Registry access may fail — not critical
            }
        }
        return null;
    }

    private static DiscoveryResult? TryKnownPaths()
    {
        foreach (var path in KnownPaths)
        {
            var result = ValidatePath(path);
            if (result != null) return result;
        }
        return null;
    }

    private static DiscoveryResult? TryRunningProcess()
    {
        try
        {
            var processes = Process.GetProcessesByName("PioneerPharmacy");
            if (processes.Length == 0) return null;

            var proc = processes[0];
            var exePath = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(exePath)) return null;

            var dir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrEmpty(dir)) return null;

            var configPath = exePath + ".config";
            if (!File.Exists(configPath)) return null;

            ConsoleUI.WriteInfo("Found via running PioneerPharmacy.exe process");
            return new DiscoveryResult(dir, exePath, configPath);
        }
        catch
        {
            return null;
        }
    }

    private static DiscoveryResult? ValidatePath(string dir)
    {
        if (!Directory.Exists(dir)) return null;

        var exe = Path.Combine(dir, "PioneerPharmacy.exe");
        var config = Path.Combine(dir, "PioneerPharmacy.exe.config");

        if (File.Exists(exe) && File.Exists(config))
            return new DiscoveryResult(dir, exe, config);

        return null;
    }
}
