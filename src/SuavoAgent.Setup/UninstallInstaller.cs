using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using System.Runtime.InteropServices;

namespace SuavoAgent.Setup;

/// <summary>
/// Native headless uninstall path used by signed quiet-maintenance requests.
/// Its native protected-copy handoff is also shared by the visible Windows
/// Settings flow so the installed maintenance executable never locks its own
/// directory during removal.
/// Removes all SuavoAgent services and binaries watchdog-first. Compliance evidence is moved to an
/// Admin+SYSTEM-only retention quarantine by default. Destructive data purge requires the explicit
/// local-admin <c>--purge-retained-data</c> switch.
/// Requires elevation (the installer manifest already requests administrator).
/// </summary>
internal static class UninstallInstaller
{
    private const string DefaultInstallDir = @"C:\Program Files\Suavo\Agent";
    private const string DefaultDataDir = @"C:\ProgramData\SuavoAgent";

    internal const string FromTempFlag = MaintenanceContract.ProtectedStagingSwitch;
    private const uint MoveFileDelayUntilReboot = 0x00000004;

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            // ARP launches the staged uninstaller from INSIDE the install dir, where it locks its
            // own exe against the dir delete. Re-launch from a random Admin/SYSTEM-only ProgramData
            // directory and exit so the child can remove the whole install directory safely.
            if (TryReExecFromTemp(args))
                return 0;
            ScheduleCurrentTempCleanup(args);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine();
            Console.WriteLine("  ╔═══════════════════════════════════════╗");
            Console.WriteLine("  ║   SuavoAgent — Uninstall              ║");
            Console.WriteLine("  ╚═══════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();

            var installDir = DiscoverInstallDir() ?? DefaultInstallDir;
            var dataDir = DefaultDataDir;
            var purgeRetainedData = ShouldPurgeRetainedData(args);
            var authenticatedClaim = ReadAuthenticatedClaimPath(args);
            if (authenticatedClaim is not null)
            {
                if (purgeRetainedData || !args.Any(argument => string.Equals(
                        argument,
                        SelfUninstallContract.PreserveDataSwitch,
                        StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        "Authenticated self-uninstall requires retained evidence policy.");
                var finalization = await SelfUninstallCompletionFinalizer.ExecuteProductionAsync(
                    authenticatedClaim,
                    installDir,
                    dataDir,
                    CancellationToken.None).ConfigureAwait(false);
                if (finalization.IsFinalized)
                {
                    ConsoleUI.WriteOk(
                        "SuavoAgent runtime removed and cloud completion finalized.");
                    return 0;
                }
                ConsoleUI.WriteWarn(
                    $"Self-uninstall is safely pending: {finalization.Code}. " +
                    "The signed completion evidence will replay before the next pairing.");
                return finalization.Cleanup is { FullyClean: false } ? 2 : 3;
            }
            // Local administrator rights prove control of Windows, not authority
            // to revoke a pharmacy device or close its immutable audit chain.
            // Only the authenticated branch above may invoke destructive cleanup.
            ConsoleUI.WriteWarn(
                purgeRetainedData
                    ? "Local evidence purge was refused because no signed cloud removal claim is present."
                    : "Removal is pending signed approval from the Suavo dashboard. No local state was changed.");
            return 3;
        }
        catch (Exception)
        {
            ConsoleUI.FatalError(
                "Uninstall could not complete safely. Retry or contact support. " +
                "Support code: SETUP-UNINSTALL-SAFE-FAIL");
            return 1;
        }
    }

    internal static bool ShouldPurgeRetainedData(string[] args)
    {
        var preserve = args.Any(argument => string.Equals(
            argument,
            SelfUninstallContract.PreserveDataSwitch,
            StringComparison.OrdinalIgnoreCase));
        var purge = args.Any(argument => string.Equals(
            argument,
            SelfUninstallContract.PurgeRetainedDataSwitch,
            StringComparison.OrdinalIgnoreCase));
        if (preserve && purge)
            throw new InvalidOperationException(
                "Conflicting uninstall data policies: preserve and purge were both requested.");
        return purge;
    }

    internal static string? ReadAuthenticatedClaimPath(string[] args)
    {
        var indexes = args
            .Select((argument, index) => (argument, index))
            .Where(item => string.Equals(
                item.argument,
                SelfUninstallContract.AuthenticatedRequestSwitch,
                StringComparison.OrdinalIgnoreCase))
            .Select(item => item.index)
            .ToArray();
        if (indexes.Length == 0) return null;
        if (indexes.Length != 1 || indexes[0] + 1 >= args.Length ||
            string.IsNullOrWhiteSpace(args[indexes[0] + 1]))
            throw new InvalidOperationException(
                "Authenticated self-uninstall claim argument is invalid.");
        return Path.GetFullPath(args[indexes[0] + 1]);
    }

    // Resolve the real install dir from the Core service's binPath; fall back to the default.
    private static string? DiscoverInstallDir()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(
                TrustedWindowsSystemBinary.Resolve("sc.exe"),
                "qc SuavoAgent.Core")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi);
            if (p == null) return null;
            var outputTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(5000))
            {
                try { p.Kill(entireProcessTree: true); } catch { }
                return null;
            }
            var outp = outputTask.GetAwaiter().GetResult();
            var m = System.Text.RegularExpressions.Regex.Match(
                outp, @"BINARY_PATH_NAME\s*:\s*""?([A-Za-z]:\\[^""\r\n]+?\.exe)");
            return m.Success ? Path.GetDirectoryName(m.Groups[1].Value) : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// If this is the installed maintenance host, copy it into a create-new,
    /// Admin/SYSTEM-only ProgramData directory and relaunch it with
    /// <see cref="FromTempFlag"/>. The final path, ACL, SHA-256, and MKM
    /// Authenticode identity are revalidated immediately before launch. Returns
    /// false only when handoff does not apply; a failed applicable handoff throws
    /// so uninstall cannot fall back to an unsafe in-place partial removal.
    /// </summary>
    internal static bool TryReExecFromTemp(string[] args)
    {
        if (!OperatingSystem.IsWindows()) return false;
        if (args.Any(a => string.Equals(a, FromTempFlag, StringComparison.OrdinalIgnoreCase))) return false;
        var self = Environment.ProcessPath;
        if (string.IsNullOrEmpty(self)) return false;
        // Trigger only for an installed maintenance/uninstall copy (by name), so
        // a downloaded SuavoSetup.exe can still run the removal path in place.
        var selfName = Path.GetFileName(self);
        if (!string.Equals(selfName, MaintenanceContract.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(selfName, ServiceInstaller.LegacyUninstallExeName, StringComparison.OrdinalIgnoreCase))
            return false;

        var sourceDirectory = Path.GetDirectoryName(self)
                              ?? throw new InvalidOperationException(
                                  "Installed maintenance path has no parent directory.");
        PrivilegedStagedExecutable? staged = null;
        try
        {
            staged = PrivilegedExecutableStaging.StageMkmExecutable(
                self,
                sourceDirectory,
                DefaultDataDir);
            var psi = new System.Diagnostics.ProcessStartInfo(staged.ExecutablePath)
            {
                UseShellExecute = false,
            };
            foreach (var argument in args) psi.ArgumentList.Add(argument);
            psi.ArgumentList.Add(FromTempFlag);

            // This is deliberately adjacent to Process.Start. The closed-file
            // interval is safe because neither the directory nor file is writable
            // by the unelevated same-SID token.
            if (!PrivilegedExecutableStaging.VerifyMkmExecutable(
                    staged.ExecutablePath,
                    staged.Sha256))
                throw new UnauthorizedAccessException(
                    "Staged maintenance executable changed before launch.");
            if (System.Diagnostics.Process.Start(psi) is null)
                throw new InvalidOperationException(
                    "Windows did not start the protected maintenance handoff.");
            return true;
        }
        catch (Exception exception)
        {
            if (staged is not null)
                PrivilegedExecutableStaging.TryCleanupDirectory(
                    staged.DirectoryPath,
                    staged.ExecutablePath);
            throw new InvalidOperationException(
                "Protected maintenance handoff failed; uninstall was not started.",
                exception);
        }
    }

    internal static void ScheduleCurrentTempCleanup(IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows() ||
            !args.Any(a => string.Equals(a, FromTempFlag, StringComparison.OrdinalIgnoreCase)))
            return;

        try
        {
            var processPath = Environment.ProcessPath;
            var commonData = Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData);
            if (string.IsNullOrWhiteSpace(processPath) ||
                !IsSafeTemporaryUninstallCopy(
                    processPath,
                    commonData,
                    Path.GetTempPath()) ||
                !PrivilegedExecutableStaging.VerifyMkmExecutable(
                    processPath,
                    PrivilegedExecutableStaging.ComputeSha256(processPath)))
                return;

            var directory = Path.GetDirectoryName(processPath)!;
            var fileScheduled = MoveFileEx(
                processPath,
                null,
                MoveFileDelayUntilReboot);
            var directoryScheduled = MoveFileEx(
                directory,
                null,
                MoveFileDelayUntilReboot);
            if (!fileScheduled || !directoryScheduled)
            {
                ConsoleUI.WriteWarn(
                    "Windows could not schedule the temporary uninstaller for cleanup. " +
                    "Support code: SETUP-UNINSTALL-TEMP-CLEANUP");
            }
        }
        catch
        {
            ConsoleUI.WriteWarn(
                "Windows could not schedule the temporary uninstaller for cleanup. " +
                "Support code: SETUP-UNINSTALL-TEMP-CLEANUP");
        }
    }

    internal static bool IsSafeTemporaryUninstallCopy(
        string processPath,
        string commonData,
        string tempRoot) =>
        PrivilegedExecutableStaging.IsApprovedStagedUninstallPath(
            processPath,
            commonData,
            tempRoot,
            DefaultInstallDir,
            DefaultDataDir);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string lpExistingFileName,
        string? lpNewFileName,
        uint dwFlags);
}
