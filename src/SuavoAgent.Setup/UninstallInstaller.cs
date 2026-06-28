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

    private const string FromTempFlag = "--from-temp";

    public static Task<int> RunAsync(string[] args)
    {
        try
        {
            // ARP launches the staged uninstaller from INSIDE the install dir, where it locks its
            // own exe against the dir delete. Re-launch a throwaway copy from %TEMP% and exit, so the
            // real uninstall (running from temp) can remove the whole install dir → zero residue.
            if (TryReExecFromTemp(args))
                return Task.FromResult(0);

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

    /// <summary>
    /// If we are the staged uninstaller (SuavoAgent.Uninstall.exe, run from inside the install dir
    /// by Add/Remove Programs), copy ourselves to %TEMP% and relaunch there with <see cref="FromTempFlag"/>
    /// so the copy doesn't recurse, then return true so the caller exits and releases the lock on the
    /// install-dir exe — letting the real uninstall (from temp) delete the whole install dir. Returns
    /// false (run in place) when launched from elsewhere, e.g. the Downloads SuavoSetup.exe. The parent
    /// is already elevated (ARP honors the requireAdministrator manifest), so the temp child inherits
    /// the elevated token — no second UAC.
    /// </summary>
    private static bool TryReExecFromTemp(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (args.Any(a => string.Equals(a, FromTempFlag, StringComparison.OrdinalIgnoreCase))) return false;
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self)) return false;
            // Trigger only for the staged copy (by name), so it's robust to a custom install dir.
            if (!string.Equals(Path.GetFileName(self), ServiceInstaller.UninstallExeName, StringComparison.OrdinalIgnoreCase))
                return false;

            var tempExe = Path.Combine(Path.GetTempPath(), $"suavo-uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(self, tempExe, overwrite: true);

            var psi = new System.Diagnostics.ProcessStartInfo(tempExe) { UseShellExecute = false };
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add(FromTempFlag);
            return System.Diagnostics.Process.Start(psi) != null;
        }
        catch
        {
            // Fall through to an in-place uninstall: everything but our own exe is removed (acceptable).
            return false;
        }
    }
}
