using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using SuavoAgent.Contracts.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record LegacyLifecycleMigrationResult(
    bool Succeeded,
    string Code,
    int FilesRemoved,
    int ScheduledTasksRemoved,
    int RegistryEntriesRemoved,
    string? ReceiptPath)
{
    internal int ShortcutsRemoved { get; init; }
    internal int ProcessesStopped { get; init; }
    internal int UnclassifiedShortcutsPreserved { get; init; }

    internal static LegacyLifecycleMigrationResult Failed(string code) =>
        new(false, code, 0, 0, 0, null);
}

internal sealed record LegacyScheduledTaskCleanupResult(
    bool Succeeded,
    int Removed,
    bool RunnableLegacyTaskRemains);

internal sealed record LegacyRegistryCleanupResult(
    bool Succeeded,
    int Removed,
    bool RunnableLegacyCommandRemains);

/// <summary>
/// Retires the former customer script lifecycle after the runtime cohort is
/// quiesced. This boundary is intentionally narrow: it removes only exact known
/// artifacts and command registrations, never follows a reparse point, never
/// launches a command shell, and refuses to restart services while a privileged
/// legacy repair path remains runnable.
/// </summary>
internal static class LegacyLifecycleMigration
{
    internal const string ReceiptFileName = "legacy-lifecycle-migration.json";
    private const int MaxReceiptBytes = 8 * 1024;
    private const int MaxTaskCount = 128;
    private const int MaxTaskNameCharacters = 512;
    private const int MaxProcessOutputCharacters = 1024 * 1024;
    private const string LegacyProductKey = @"SOFTWARE\SuavoAgent";
    private const string ArpKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent";
    private const string ProtocolCommandKey =
        @"SOFTWARE\Classes\suavoagent\shell\open\command";

    private static readonly string[] LegacyFileNames =
    [
        "bootstrap.ps1",
        "install.ps1",
        "suavo-check.ps1",
        "quick-install.ps1",
        "upgrade.ps1",
    ];

    private static readonly string[] LegacyCommandValueNames =
    [
        "UninstallString",
        "QuietUninstallString",
        "ModifyPath",
        "RepairPath",
        "BootstrapPath",
    ];

    internal static LegacyLifecycleMigrationResult Execute(
        string installDirectory,
        string dataDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return LegacyLifecycleMigrationResult.Failed("unsupported_host");

        var systemDrive = Path.GetPathRoot(
            Environment.GetFolderPath(Environment.SpecialFolder.System));
        if (string.IsNullOrWhiteSpace(systemDrive))
            return LegacyLifecycleMigrationResult.Failed("system_drive_unavailable");

        return RunCore(
            installDirectory,
            dataDirectory,
            Path.Combine(systemDrive, "SuavoAgent"),
            RemoveAndProveLegacyScheduledTasksAbsent,
            RemoveAndProveLegacyRegistryEntriesAbsent,
            DateTimeOffset.UtcNow,
            LegacyInteractiveLaunchRetirement.Execute);
    }

    internal static LegacyLifecycleMigrationResult RunCore(
        string installDirectory,
        string dataDirectory,
        string legacyRootDirectory,
        Func<LegacyScheduledTaskCleanupResult> scheduledTaskCleanup,
        Func<LegacyRegistryCleanupResult> registryCleanup,
        DateTimeOffset now,
        Func<LegacyInteractiveLaunchCleanupResult>? interactiveCleanup = null)
    {
        ArgumentNullException.ThrowIfNull(scheduledTaskCleanup);
        ArgumentNullException.ThrowIfNull(registryCleanup);

        if (!TryCanonicalRoot(installDirectory, out var installRoot) ||
            !TryCanonicalRoot(dataDirectory, out var dataRoot) ||
            !TryCanonicalRoot(legacyRootDirectory, out var legacyRoot))
            return LegacyLifecycleMigrationResult.Failed("path_boundary_invalid");

        if (!IsSafeRoot(installRoot) ||
            !IsSafeRoot(dataRoot) ||
            !IsSafeRoot(legacyRoot))
            return LegacyLifecycleMigrationResult.Failed("path_boundary_redirected");

        var filesRemoved = 0;
        foreach (var root in new[] { dataRoot, installRoot, legacyRoot })
        {
            foreach (var fileName in LegacyFileNames)
            {
                if (!RemoveExactFile(root, fileName, ref filesRemoved))
                    return WriteFailureReceiptIfPossible(
                        dataRoot,
                        "legacy_file_removal_failed",
                        filesRemoved,
                        0,
                        0,
                        now);
            }
        }

        // The deprecated quick installer also lived one level below the old
        // source checkout. Its parent must be a real directory, never a link.
        if (!RemoveExactFile(
                legacyRoot,
                Path.Combine("scripts", "quick-install.ps1"),
                ref filesRemoved))
            return WriteFailureReceiptIfPossible(
                dataRoot,
                "legacy_file_removal_failed",
                filesRemoved,
                0,
                0,
                now);

        LegacyScheduledTaskCleanupResult tasks;
        try
        {
            tasks = scheduledTaskCleanup();
        }
        catch
        {
            tasks = new(false, 0, true);
        }
        if (!tasks.Succeeded || tasks.RunnableLegacyTaskRemains)
            return WriteFailureReceiptIfPossible(
                dataRoot,
                tasks.RunnableLegacyTaskRemains
                    ? "legacy_scheduled_task_remains"
                    : "legacy_scheduled_task_cleanup_failed",
                filesRemoved,
                tasks.Removed,
                0,
                now);

        LegacyRegistryCleanupResult registry;
        try
        {
            registry = registryCleanup();
        }
        catch
        {
            registry = new(false, 0, true);
        }
        if (!registry.Succeeded || registry.RunnableLegacyCommandRemains)
            return WriteFailureReceiptIfPossible(
                dataRoot,
                registry.RunnableLegacyCommandRemains
                    ? "legacy_registry_command_remains"
                    : "legacy_registry_cleanup_failed",
                filesRemoved,
                tasks.Removed,
                registry.Removed,
                now);

        LegacyInteractiveLaunchCleanupResult interactive;
        try
        {
            interactive = interactiveCleanup?.Invoke() ??
                new(true, 0, 0, 0, false);
        }
        catch
        {
            interactive = new(false, 0, 0, 0, true);
        }
        if (!interactive.Succeeded || interactive.RunnableLegacyPathRemains)
            return WriteFailureReceiptIfPossible(
                dataRoot,
                "legacy_interactive_launch_remains",
                filesRemoved,
                tasks.Removed,
                registry.Removed,
                now,
                interactive);

        if (!ProveExactFilesAbsent(installRoot, dataRoot, legacyRoot))
            return WriteFailureReceiptIfPossible(
                dataRoot,
                "legacy_file_proof_failed",
                filesRemoved,
                tasks.Removed,
                registry.Removed,
                now,
                interactive);

        return WriteReceipt(
            dataRoot,
            succeeded: true,
            "legacy_script_lifecycle_retired",
            filesRemoved,
            tasks.Removed,
            registry.Removed,
            now,
            interactive);
    }

    internal static IReadOnlyList<string> ParseLegacyScheduledTaskNames(string output)
    {
        if (string.IsNullOrEmpty(output)) return [];
        return output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(line =>
                UninstallTerminalCleanup.ReadFirstCsvField(line) ?? string.Empty)
            .Where(UninstallTerminalCleanup.IsExactOwnedScheduledTaskName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTaskCount + 1)
            .ToArray();
    }

    internal static bool IsLegacyRunnableCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        // An oversized command in one of the exact legacy registration slots
        // cannot be safely classified, so it remains a blocking runnable path.
        if (command.Length > 32 * 1024) return true;
        return LegacyFileNames.Any(fileName =>
                   command.Contains(fileName, StringComparison.OrdinalIgnoreCase)) ||
               command.Contains("powershell", StringComparison.OrdinalIgnoreCase) ||
               command.Contains("pwsh", StringComparison.OrdinalIgnoreCase) ||
               command.Contains(
                   @"\SuavoAgent\scripts\quick-install.ps1",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static LegacyScheduledTaskCleanupResult RemoveAndProveLegacyScheduledTasksAbsent()
    {
        var first = QueryLegacyTasks();
        if (!first.Succeeded || first.Names.Count > MaxTaskCount)
            return new(false, 0, true);

        var removed = 0;
        foreach (var taskName in first.Names)
        {
            // Delete alone does not interrupt a running task. Disable first so
            // no trigger can race the cleanup, then prove the running instance
            // accepted termination before removing its registration.
            var disabled = RunTrustedProcess(
                "schtasks.exe",
                ["/Change", "/TN", taskName, "/Disable"]);
            if (!disabled.Started || disabled.ExitCode != 0 || disabled.OutputOverflow)
                return new(false, removed, true);

            var ended = RunTrustedProcess(
                "schtasks.exe",
                ["/End", "/TN", taskName]);
            if (!ended.Started || ended.OutputOverflow ||
                !UninstallTerminalCleanup.IsSafeTaskEndResult(
                    ended.ExitCode,
                    ended.Output))
                return new(false, removed, true);

            var deletion = RunTrustedProcess(
                "schtasks.exe",
                ["/Delete", "/TN", taskName, "/F"]);
            if (!deletion.Started || deletion.ExitCode != 0 || deletion.OutputOverflow)
                return new(false, removed, true);
            removed++;
        }

        var proof = QueryLegacyTasks();
        return new(
            proof.Succeeded && proof.Names.Count == 0,
            removed,
            proof.Names.Count != 0 || !proof.Succeeded);
    }

    private static (bool Succeeded, IReadOnlyList<string> Names) QueryLegacyTasks()
    {
        var query = RunTrustedProcess(
            "schtasks.exe",
            ["/Query", "/FO", "CSV", "/V", "/NH"]);
        if (!query.Started || query.ExitCode != 0 || query.OutputOverflow)
            return (false, []);
        var candidates = ParseLegacyScheduledTaskNames(query.Output);
        if (candidates.Count > MaxTaskCount)
            return (false, candidates);
        var names = new List<string>();
        string windowsDirectory;
        try { windowsDirectory = UninstallTerminalCleanup.ReadTrustedWindowsDirectory(); }
        catch { return (false, []); }
        foreach (var candidate in candidates)
        {
            var xml = RunTrustedProcess(
                "schtasks.exe",
                ["/Query", "/TN", candidate, "/XML"]);
            if (!xml.Started || xml.ExitCode != 0 || xml.OutputOverflow)
                return (false, []);
            if (UninstallTerminalCleanup.IsExactRetiredSelfUninstallTaskXml(
                    xml.Output,
                    windowsDirectory))
                names.Add(candidate);
        }
        return (
            names.Count <= MaxTaskCount && names.All(name =>
                !string.IsNullOrWhiteSpace(name) &&
                name.Length <= MaxTaskNameCharacters),
            names);
    }

    private static LegacyRegistryCleanupResult RemoveAndProveLegacyRegistryEntriesAbsent()
    {
        var removed = 0;
        var succeeded = true;
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                bool legacyProductKeyExists;
                using (var legacy = root.OpenSubKey(LegacyProductKey, writable: false))
                    legacyProductKeyExists = legacy is not null;
                if (legacyProductKeyExists)
                {
                    root.DeleteSubKeyTree(LegacyProductKey, throwOnMissingSubKey: false);
                    removed++;
                }

                removed += RemoveLegacyCommandValues(root, ArpKey);
                removed += RemoveLegacyCommandValues(root, ProtocolCommandKey);
                root.Flush();
            }
            catch (Exception exception) when (exception is
                UnauthorizedAccessException or IOException or System.Security.SecurityException)
            {
                succeeded = false;
            }
        }

        var remains = !ProveLegacyRegistryEntriesAbsent();
        return new(succeeded && !remains, removed, remains);
    }

    private static int RemoveLegacyCommandValues(RegistryKey root, string subKeyPath)
    {
        using var key = root.OpenSubKey(subKeyPath, writable: true);
        if (key is null) return 0;
        var removed = 0;
        foreach (var valueName in LegacyCommandValueNames.Append(string.Empty))
        {
            if (key.GetValue(valueName) is not string value ||
                !IsLegacyRunnableCommand(value))
                continue;
            key.DeleteValue(valueName, throwOnMissingValue: false);
            removed++;
        }
        key.Flush();
        return removed;
    }

    private static bool ProveLegacyRegistryEntriesAbsent()
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using (var legacy = root.OpenSubKey(LegacyProductKey, writable: false))
                    if (legacy is not null) return false;
                if (HasLegacyCommandValue(root, ArpKey) ||
                    HasLegacyCommandValue(root, ProtocolCommandKey))
                    return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static bool HasLegacyCommandValue(RegistryKey root, string subKeyPath)
    {
        using var key = root.OpenSubKey(subKeyPath, writable: false);
        if (key is null) return false;
        return LegacyCommandValueNames
            .Append(string.Empty)
            .Select(valueName => key.GetValue(valueName))
            .OfType<string>()
            .Any(IsLegacyRunnableCommand);
    }

    private static bool RemoveExactFile(
        string root,
        string relativePath,
        ref int removed)
    {
        if (!TryBoundedPath(root, relativePath, out var target) ||
            !AreParentDirectoriesSafe(root, target))
            return false;
        if (!TryGetAttributes(target, out var attributes, out var exists))
            return false;
        if (!exists) return true;
        if ((attributes & FileAttributes.Directory) != 0) return false;
        try
        {
            // File.Delete removes a file reparse point itself. Parent reparse
            // points were rejected above, so the target cannot escape the root.
            File.Delete(target);
            if (Path.Exists(target)) return false;
            removed++;
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool ProveExactFilesAbsent(
        string installRoot,
        string dataRoot,
        string legacyRoot)
    {
        foreach (var root in new[] { installRoot, dataRoot, legacyRoot })
        {
            foreach (var fileName in LegacyFileNames)
            {
                if (!TryBoundedPath(root, fileName, out var path) ||
                    !AreParentDirectoriesSafe(root, path) ||
                    Path.Exists(path))
                    return false;
            }
        }
        return TryBoundedPath(legacyRoot, Path.Combine("scripts", "quick-install.ps1"), out var nested) &&
               AreParentDirectoriesSafe(legacyRoot, nested) &&
               !Path.Exists(nested);
    }

    private static bool TryCanonicalRoot(string path, out string canonical)
    {
        canonical = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
                return false;
            canonical = Path.GetFullPath(path).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);
            return !string.IsNullOrWhiteSpace(canonical);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsSafeRoot(string root)
    {
        if (!TryGetAttributes(root, out var attributes, out var exists)) return false;
        return !exists ||
               ((attributes & FileAttributes.Directory) != 0 &&
                (attributes & FileAttributes.ReparsePoint) == 0);
    }

    private static bool TryBoundedPath(
        string root,
        string relativePath,
        out string target)
    {
        target = string.Empty;
        try
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathFullyQualified(relativePath))
                return false;
            target = Path.GetFullPath(Path.Combine(root, relativePath));
            var prefix = root + Path.DirectorySeparatorChar;
            return target.StartsWith(prefix, PathComparison) &&
                   !string.Equals(target, root, PathComparison);
        }
        catch (Exception exception) when (exception is
            ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool AreParentDirectoriesSafe(string root, string target)
    {
        var parent = Path.GetDirectoryName(target);
        if (parent is null) return false;
        var relative = Path.GetRelativePath(root, parent);
        if (relative == ".") return IsSafeRoot(root);
        var current = root;
        if (!IsSafeRoot(current)) return false;
        foreach (var segment in relative.Split(
                     new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..") return false;
            current = Path.Combine(current, segment);
            if (!TryGetAttributes(current, out var attributes, out var exists))
                return false;
            if (!exists) return true;
            if ((attributes & FileAttributes.Directory) == 0 ||
                (attributes & FileAttributes.ReparsePoint) != 0)
                return false;
        }
        return true;
    }

    private static bool TryGetAttributes(
        string path,
        out FileAttributes attributes,
        out bool exists)
    {
        attributes = default;
        exists = false;
        try
        {
            attributes = File.GetAttributes(path);
            exists = true;
            return true;
        }
        catch (FileNotFoundException)
        {
            return true;
        }
        catch (DirectoryNotFoundException)
        {
            return true;
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    private static LegacyLifecycleMigrationResult WriteFailureReceiptIfPossible(
        string dataRoot,
        string code,
        int filesRemoved,
        int tasksRemoved,
        int registryRemoved,
        DateTimeOffset now,
        LegacyInteractiveLaunchCleanupResult? interactive = null)
    {
        var receipt = WriteReceipt(
            dataRoot,
            succeeded: false,
            code,
            filesRemoved,
            tasksRemoved,
            registryRemoved,
            now,
            interactive);
        return receipt with { Succeeded = false, Code = code };
    }

    private static LegacyLifecycleMigrationResult WriteReceipt(
        string dataRoot,
        bool succeeded,
        string code,
        int filesRemoved,
        int tasksRemoved,
        int registryRemoved,
        DateTimeOffset now,
        LegacyInteractiveLaunchCleanupResult? interactive = null)
    {
        var receiptPath = Path.Combine(dataRoot, ReceiptFileName);
        var tempPath = receiptPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            if (!IsSafeRoot(dataRoot) ||
                !AreParentDirectoriesSafe(dataRoot, receiptPath))
                return new(false, "receipt_path_unsafe", filesRemoved, tasksRemoved, registryRemoved, null);
            Directory.CreateDirectory(dataRoot);
            if (Path.Exists(receiptPath))
            {
                var attributes = File.GetAttributes(receiptPath);
                if ((attributes & FileAttributes.Directory) != 0)
                    return new(false, "receipt_path_invalid", filesRemoved, tasksRemoved, registryRemoved, null);
                File.Delete(receiptPath);
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 2,
                status = succeeded ? "completed" : "failed",
                code,
                completedAtUtc = now.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                filesRemoved,
                scheduledTasksRemoved = tasksRemoved,
                registryEntriesRemoved = registryRemoved,
                shortcutsRemoved = interactive?.ShortcutsRemoved ?? 0,
                legacyProcessesStopped = interactive?.ProcessesStopped ?? 0,
                unclassifiedShortcutsPreserved =
                    interactive?.UnclassifiedShortcutsPreserved ?? 0,
                runnableLegacyPathsRemaining = !succeeded,
            });
            if (bytes.Length is <= 0 or > MaxReceiptBytes)
                return new(false, "receipt_size_invalid", filesRemoved, tasksRemoved, registryRemoved, null);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(tempPath, receiptPath, overwrite: true);
            using var committed = new FileStream(
                receiptPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                1,
                FileOptions.SequentialScan);
            if (committed.Length is <= 0 or > MaxReceiptBytes)
                return new(false, "receipt_commit_invalid", filesRemoved, tasksRemoved, registryRemoved, null);
            return new(
                succeeded,
                code,
                filesRemoved,
                tasksRemoved,
                registryRemoved,
                receiptPath)
            {
                ShortcutsRemoved = interactive?.ShortcutsRemoved ?? 0,
                ProcessesStopped = interactive?.ProcessesStopped ?? 0,
                UnclassifiedShortcutsPreserved =
                    interactive?.UnclassifiedShortcutsPreserved ?? 0,
            };
        }
        catch (Exception exception) when (exception is
            IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new(false, "receipt_write_failed", filesRemoved, tasksRemoved, registryRemoved, null);
        }
        finally
        {
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    private static ProcessResult RunTrustedProcess(
        string executable,
        IReadOnlyList<string> arguments)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = TrustedWindowsSystemBinary.Resolve(executable),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var overflow = false;
            var gate = new object();
            void Append(string? value)
            {
                if (value is null) return;
                lock (gate)
                {
                    if (output.Length + value.Length + 1 > MaxProcessOutputCharacters)
                    {
                        overflow = true;
                        return;
                    }
                    output.AppendLine(value);
                }
            }

            process.OutputDataReceived += (_, eventArgs) => Append(eventArgs.Data);
            process.ErrorDataReceived += (_, eventArgs) => Append(eventArgs.Data);
            if (!process.Start()) return new(false, -1, string.Empty, false);
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return new(true, -1, output.ToString(), overflow);
            }
            process.WaitForExit();
            return new(true, process.ExitCode, output.ToString(), overflow);
        }
        catch
        {
            return new(false, -1, string.Empty, false);
        }
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private sealed record ProcessResult(
        bool Started,
        int ExitCode,
        string Output,
        bool OutputOverflow);
}
