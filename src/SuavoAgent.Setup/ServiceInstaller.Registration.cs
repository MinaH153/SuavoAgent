using Microsoft.Win32;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup;

/// <summary>
/// Owns Windows Add/Remove Programs registration for the signed native
/// maintenance host.
/// </summary>
internal static partial class ServiceInstaller
{
    // Compatibility alias only. New installs use the single signed maintenance
    // host for both native repair and uninstall.
    internal const string LegacyUninstallExeName = "SuavoAgent.Uninstall.exe";
    private const string ArpKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent";

    /// <summary>
    /// Registers SuavoAgent in Windows Add/Remove Programs. The mandatory maintenance host
    /// was staged and hash-verified before services started; ARP uses that one signed PE for
    /// uninstall and native service repair.
    /// </summary>
    public static void RegisterUninstallEntry(string installDir, string version)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var maintenanceExe = Path.Combine(installDir, MaintenanceContract.ExecutableName);
            if (!File.Exists(maintenanceExe))
                throw new FileNotFoundException("Native maintenance host is missing.", maintenanceExe);
            var commands = BuildMaintenanceCommands(installDir);

            long sizeKb = 0;
            try { sizeKb = new DirectoryInfo(installDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) / 1024; }
            catch { /* EstimatedSize is cosmetic */ }
            var (major, minor) = ParseVersion(version);

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = baseKey.CreateSubKey(ArpKeyPath);
            key.SetValue("DisplayName", "SuavoAgent");
            key.SetValue("DisplayVersion", version);
            key.SetValue("Publisher", "MKM Technologies LLC");
            key.SetValue("InstallLocation", installDir);
            key.SetValue("DisplayIcon", maintenanceExe);
            key.SetValue("UninstallString", commands.Uninstall);
            key.SetValue("QuietUninstallString", commands.QuietUninstall);
            key.SetValue("ModifyPath", commands.Repair);
            key.DeleteValue("NoModify", throwOnMissingValue: false);
            key.DeleteValue("NoRepair", throwOnMissingValue: false);
            key.SetValue("EstimatedSize", (int)Math.Min(sizeKb, int.MaxValue), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("URLInfoAbout", "https://suavollc.com");
            key.SetValue("HelpLink", "mailto:support@suavollc.com");
            key.SetValue("VersionMajor", major, RegistryValueKind.DWord);
            key.SetValue("VersionMinor", minor, RegistryValueKind.DWord);
            ConsoleUI.WriteOk("Registered native repair + uninstall in Add/Remove Programs");
        }
        catch (Exception exception)
        {
            ConsoleUI.WriteFail(
                "Windows could not register the native Repair/Uninstall entry. " +
                "Installation cannot be reported complete without a working Settings maintenance path. " +
                "Support code: SETUP-ARP-REGISTER");
            throw new InvalidOperationException("arp_registration_failed", exception);
        }
    }

    internal static MaintenanceCommands BuildMaintenanceCommands(string installDir)
    {
        var executable = Path.Combine(installDir, MaintenanceContract.ExecutableName);
        var quoted = $"\"{executable}\"";
        return new MaintenanceCommands(
            // Windows Settings opens a visible native confirmation/progress
            // flow. Quiet enterprise removal retains the headless signed mode.
            Uninstall: $"{quoted} {Program.UninstallUiSwitch} {SelfUninstallContract.PreserveDataSwitch}",
            QuietUninstall: $"{quoted} {MaintenanceContract.UninstallSwitch} --silent {SelfUninstallContract.PreserveDataSwitch}",
            Repair: $"{quoted} {Program.RepairUiSwitch} {MaintenanceContract.ReasonSwitch} " +
                    MaintenanceContract.ToWireValue(MaintenanceReason.ManualRepairRequested));
    }

    internal sealed record MaintenanceCommands(string Uninstall, string QuietUninstall, string Repair);

    /// <summary>Removes the Add/Remove Programs entry. Best-effort; tolerates a missing key.</summary>
    private static void RemoveUninstallEntry()
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            baseKey.DeleteSubKeyTree(ArpKeyPath, throwOnMissingSubKey: false);
        }
        catch { /* absent or insufficient rights — services + dirs already removed */ }
    }

    private static void TryDeleteRegistryKeyTree(string subKey)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Registry.LocalMachine.DeleteSubKeyTree(subKey, throwOnMissingSubKey: false); }
        catch { /* absent or insufficient rights — services already removed by StopServices */ }
    }

    // "3.77.0" / "v3.77.0-rc1" -> (3, 77) for ARP VersionMajor/Minor (cosmetic DWORDs).
    internal static (int Major, int Minor) ParseVersion(string version)
    {
        var parts = (version ?? "").TrimStart('v').Split('.', '-');
        int.TryParse(parts.ElementAtOrDefault(0), out var major);
        int.TryParse(parts.ElementAtOrDefault(1), out var minor);
        return (major, minor);
    }
}
