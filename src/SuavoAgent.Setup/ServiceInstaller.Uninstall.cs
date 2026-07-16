using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup;

/// <summary>
/// Owns native uninstall, retained-evidence quarantine, and secure removal of
/// operational credentials.
/// </summary>
internal static partial class ServiceInstaller
{
    /// <summary>
    /// Symmetric uninstall: stop + delete all three services (watchdog-first, kills the
    /// orphan Helper that locks binaries), preserve retained compliance evidence by default,
    /// then remove the install directory. A local administrator must explicitly request purge.
    /// </summary>
    public static UninstallResult Uninstall(
        string installDir,
        string dataDir,
        bool purgeRetainedData = false)
    {
        var result = new UninstallResult();

        ConsoleUI.WriteInfo("Stopping and removing SuavoAgent services (watchdog-first)...");
        StopServices(); // watchdog -> broker -> core + KillOrphanProcesses
        result.ServicesRemoved = true;

        // Data directory: state.db, state.key, configs, logs, learned templates, audit.
        // Remote/ARP uninstall preserves this evidence in an Admin+SYSTEM-only quarantine.
        // Destructive purge is a separate explicit local-admin choice.
        if (Directory.Exists(dataDir))
        {
            if (purgeRetainedData)
            {
                result.DataDirRemoved = SafeDeleteDirectory(dataDir);
                result.DataPurged = result.DataDirRemoved;
                if (result.DataDirRemoved)
                    ConsoleUI.WriteOk($"Explicit local purge removed retained data: {dataDir}");
                else
                    ConsoleUI.WriteWarn($"Could not fully purge retained data: {dataDir}");
            }
            else
            {
                var retentionRoot = DefaultRetentionRoot(dataDir);
                var preserved = PreserveDataDirectory(
                    dataDir,
                    retentionRoot,
                    DateTimeOffset.UtcNow,
                    LockdownRetainedEvidenceAcl);
                result.DataDirRemoved = preserved.IsPreserved;
                result.DataPreserved = preserved.IsPreserved;
                result.RetainedDataPath = preserved.RetainedPath;
                if (preserved.IsPreserved)
                    ConsoleUI.WriteOk($"Compliance evidence retained at: {preserved.RetainedPath}");
                else
                    ConsoleUI.WriteWarn("Could not quarantine retained compliance evidence; source data was not deleted");
            }
        }
        else { result.DataDirRemoved = true; }

        // Install directory: service binaries + immutable appsettings (no cloud auth;
        // optional SQL passwords are DPAPI-sealed by elevated Setup before staging).
        var appsettings = Path.Combine(installDir, "appsettings.json");
        _ = RemoveOperationalSecret(appsettings); // removed with the directory below if overwrite/delete fails
        if (Directory.Exists(installDir))
        {
            result.InstallDirRemoved = SafeDeleteDirectory(installDir);
            if (result.InstallDirRemoved) ConsoleUI.WriteOk($"Removed install dir: {installDir}");
            else ConsoleUI.WriteWarn($"Could not fully remove install dir: {installDir} (a file may still be locked)");
        }
        else { result.InstallDirRemoved = true; }

        // Add/Remove Programs entry (written on install) + defensive cleanup of any older
        // SOFTWARE\SuavoAgent key. Services were already removed above. The staged uninstaller
        // exe lives in installDir and is removed with the dir delete above (re-exec-from-temp in
        // UninstallInstaller means our own exe is NOT the one in installDir, so the delete is clean).
        RemoveUninstallEntry();
        TryDeleteRegistryKeyTree(@"SOFTWARE\SuavoAgent");

        var terminal = UninstallTerminalCleanup.ExecuteAndProbe(result.RetainedDataPath);
        result.ServicesRemaining = terminal.ServicesRemaining;
        result.ScheduledUninstallTaskAbsent = terminal.ScheduledUninstallTaskAbsent;
        result.ProtocolRegistrationAbsent = terminal.ProtocolRegistrationAbsent;
        result.ArpRegistrationAbsent = terminal.ArpRegistrationAbsent;
        result.RetainedEvidencePresent = terminal.RetainedEvidencePresent;
        result.OperationalCredentialsAbsent = terminal.OperationalCredentialsAbsent;
        return result;
    }

    /// <summary>Outcome of <see cref="Uninstall"/>, used for zero-residue verification.</summary>
    public sealed class UninstallResult
    {
        public bool ServicesRemoved { get; set; }
        public bool DataDirRemoved { get; set; }
        public bool DataPreserved { get; set; }
        public bool DataPurged { get; set; }
        public string? RetainedDataPath { get; set; }
        public bool InstallDirRemoved { get; set; }
        public int ServicesRemaining { get; set; }
        public bool ScheduledUninstallTaskAbsent { get; set; }
        public bool ProtocolRegistrationAbsent { get; set; }
        public bool ArpRegistrationAbsent { get; set; }
        public bool RetainedEvidencePresent { get; set; }
        public bool OperationalCredentialsAbsent { get; set; }
        public bool FullyClean =>
            ServicesRemaining == 0 &&
            DataDirRemoved &&
            InstallDirRemoved &&
            ScheduledUninstallTaskAbsent &&
            ProtocolRegistrationAbsent &&
            ArpRegistrationAbsent &&
            (!DataPreserved ||
             (RetainedEvidencePresent && OperationalCredentialsAbsent));
    }

    internal sealed record RetainedDataResult(bool IsPreserved, string? RetainedPath)
    {
        public static RetainedDataResult Preserved(string path) => new(true, path);
        public static RetainedDataResult Failed() => new(false, null);
    }

    internal static string DefaultRetentionRoot(string dataDir)
    {
        var fullPath = Path.GetFullPath(dataDir).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(fullPath)
                     ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        return Path.Combine(parent, "SuavoAgent-Retained");
    }

    /// <summary>
    /// Moves evidence out of the active runtime path and recursively restricts it to
    /// Administrators + SYSTEM. Operational credential blobs are removed first; the
    /// DPAPI database key remains because it is required to read the retained audit DB.
    /// </summary>
    internal static RetainedDataResult PreserveDataDirectory(
        string dataDir,
        string retentionRoot,
        DateTimeOffset now,
        Func<string, bool> lockdown)
    {
        if (!Directory.Exists(dataDir)) return RetainedDataResult.Failed();
        try
        {
            Directory.CreateDirectory(retentionRoot);
            if (!lockdown(retentionRoot)) return RetainedDataResult.Failed();
            // Services are already stopped. Remove the Core-service/interactive
            // grants before the move so there is no access window at the destination.
            if (!lockdown(dataDir)) return RetainedDataResult.Failed();

            if (!RemoveOperationalSecrets(dataDir))
                return RetainedDataResult.Failed();

            var retainedPath = Path.Combine(
                retentionRoot,
                $"retained-{now.UtcDateTime:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}");
            Directory.Move(dataDir, retainedPath);
            File.WriteAllText(
                Path.Combine(retainedPath, "retention.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    retainedAtUtc = now.ToString("O"),
                    reason = "software_uninstalled_evidence_preserved",
                    purgeRequiresExplicitLocalAdmin = true,
                }));
            // The metadata file was created after the move. Re-protect and
            // verify the complete destination tree so it cannot retain an
            // inherited DACL or the elevated administrator as owner.
            if (!lockdown(retainedPath))
            {
                try { Directory.Move(retainedPath, dataDir); } catch { }
                return RetainedDataResult.Failed();
            }
            return RetainedDataResult.Preserved(retainedPath);
        }
        catch
        {
            return RetainedDataResult.Failed();
        }
    }

    private static bool RemoveOperationalSecret(string path)
    {
        try
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                if (attributes.HasFlag(FileAttributes.Directory)) Directory.Delete(path);
                else File.Delete(path);
                return !Path.Exists(path);
            }
            if (attributes.HasFlag(FileAttributes.Directory)) return false;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var zeros = new byte[Math.Min(81920, (int)Math.Min(stream.Length, int.MaxValue))];
                long remaining = stream.Length;
                while (remaining > 0)
                {
                    var count = (int)Math.Min(zeros.Length, remaining);
                    stream.Write(zeros, 0, count);
                    remaining -= count;
                }
                stream.Flush(flushToDisk: true);
            }
            File.Delete(path);
            return !File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    private static bool RemoveOperationalSecrets(string dataDirectory)
    {
        try
        {
            var candidates = Directory.EnumerateFileSystemEntries(
                    dataDirectory,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Where(path => UninstallTerminalCleanup.IsOperationalCredentialFileName(
                    Path.GetFileName(path)))
                .Take(65)
                .ToArray();
            if (candidates.Length > 64) return false;
            return candidates.All(RemoveOperationalSecret);
        }
        catch
        {
            return false;
        }
    }

    private static bool LockdownRetainedEvidenceAcl(string path)
    {
        try
        {
            new SuavoAgent.Contracts.Security.HandleBoundAcl().ApplyTree(
                path,
                BuildProtectedAclPolicy(
                    ProtectedDirectoryKind.Maintenance,
                    directory: true,
                    inherit: true),
                BuildProtectedAclPolicy(
                    ProtectedDirectoryKind.Maintenance,
                    directory: false,
                    inherit: false),
                BuildProtectedAclPolicy(
                    ProtectedDirectoryKind.Maintenance,
                    directory: true,
                    inherit: false));
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Recursive delete that tolerates a briefly-held handle from a just-killed process.
    private static bool SafeDeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch (DirectoryNotFoundException) { return true; }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                try { ClearReadOnlyRecursive(path); } catch { /* best effort */ }
                Thread.Sleep(1000);
            }
            catch { break; }
        }
        return !Directory.Exists(path);
    }

    private static void ClearReadOnlyRecursive(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { var fi = new FileInfo(file); if (fi.IsReadOnly) fi.IsReadOnly = false; } catch { }
        }
    }

    private static int CountRemainingServices()
    {
        var remaining = 0;
        foreach (var name in new[] { CoreServiceName, BrokerServiceName, WatchdogServiceName })
            if (IsServicePresent(name)) remaining++;
        return remaining;
    }

    // Present = sc.exe query did NOT return "FAILED 1060" (the service-does-not-exist code).
    private static bool IsServicePresent(string serviceName)
    {
        try
        {
            var outp = RunSc($"query {serviceName}", expectSuccess: false);
            return !outp.Contains("FAILED 1060", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }
}
