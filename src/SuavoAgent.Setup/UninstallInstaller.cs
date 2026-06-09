namespace SuavoAgent.Setup;

/// <summary>
/// Console (headless) uninstall path — the symmetric counterpart to <see cref="ConsoleInstaller"/>.
/// Removes all SuavoAgent services + the install/data directories, watchdog-first, leaving zero
/// residue. Invoked via <c>SuavoSetup.exe --uninstall</c> (optionally with <c>--silent</c>).
/// Requires elevation (the installer manifest already requests administrator).
/// </summary>
internal static class UninstallInstaller
{
    private const string DefaultInstallDir = @"C:\Program Files\Suavo\Agent";
    private const string DefaultDataDir = @"C:\ProgramData\SuavoAgent";

    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  ╔═══════════════════════════════════════╗");
            Console.WriteLine("  ║   SuavoAgent — Uninstall              ║");
            Console.WriteLine("  ╚═══════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            var installDir = DiscoverInstallDir() ?? DefaultInstallDir;
            var dataDir = DefaultDataDir;
            ConsoleUI.WriteInfo($"Install dir: {installDir}");
            ConsoleUI.WriteInfo($"Data dir:    {dataDir}");

            ConsoleUI.WriteStep("Removing SuavoAgent (services, then directories)");
            var result = ServiceInstaller.Uninstall(installDir, dataDir);

            Console.WriteLine();
            if (result.FullyClean)
            {
                ConsoleUI.WriteOk("SuavoAgent fully removed — zero residue.");
            }
            else
            {
                ConsoleUI.WriteWarn(
                    $"Uninstall finished with residue: servicesRemaining={result.ServicesRemaining}, " +
                    $"dataDirRemoved={result.DataDirRemoved}, installDirRemoved={result.InstallDirRemoved}. " +
                    "Re-run after a reboot if a binary was still locked.");
            }

            ConsoleUI.WaitForExit();
            return Task.FromResult(result.FullyClean ? 0 : 2);
        }
        catch (Exception ex)
        {
            ConsoleUI.FatalError($"Uninstall error: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    // Resolve the real install dir from the Core service's binPath; fall back to the default.
    private static string? DiscoverInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("sc.exe", "qc SuavoAgent.Core")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var outp = p.StandardOutput.ReadToEnd();
            p.WaitForExit(5000);
            var m = System.Text.RegularExpressions.Regex.Match(
                outp, @"BINARY_PATH_NAME\s*:\s*""?([A-Za-z]:\\[^""\r\n]+?\.exe)");
            return m.Success ? Path.GetDirectoryName(m.Groups[1].Value) : null;
        }
        catch { return null; }
    }
}
