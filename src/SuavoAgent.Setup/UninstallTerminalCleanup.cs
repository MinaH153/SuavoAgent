using System.Diagnostics;
using Microsoft.Win32;
using SuavoAgent.Contracts.Maintenance;
using System.Xml;
using System.Xml.Linq;

namespace SuavoAgent.Setup;

internal sealed record UninstallTerminalState(
    int ServicesRemaining,
    bool ScheduledUninstallTaskAbsent,
    bool ProtocolRegistrationAbsent,
    bool ArpRegistrationAbsent,
    bool RetainedEvidencePresent,
    bool OperationalCredentialsAbsent);

/// <summary>
/// Deletes legacy machine registrations and proves bounded, PHI-free terminal
/// predicates after the runtime directories are removed. No shell or script host
/// is used; scheduled-task work invokes schtasks.exe directly with argument tokens.
/// </summary>
internal static class UninstallTerminalCleanup
{
    // This is the only scheduled-task identity ever created by a SuavoAgent
    // installer in repository history (the retired Broker self-uninstaller).
    // Keep this as an exact scheduler path; product-name substrings are not
    // ownership evidence.
    internal const string LegacySelfUninstallTaskPath = @"\SuavoSelfUninstall";
    internal const int TaskNotRunningHResult = unchecked((int)0x8004130B);
    internal const string ProtocolKeyPath = @"SOFTWARE\Classes\suavoagent";
    internal const string ArpKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent";
    private const int MaxTaskCount = 128;
    private const int MaxProcessOutputChars = 1024 * 1024;
    private const int MaxTaskXmlChars = 64 * 1024;
    private static readonly string[] ServiceNames =
    [
        "SuavoAgent.Core",
        "SuavoAgent.Broker",
        "SuavoAgent.Watchdog",
    ];

    internal static UninstallTerminalState ExecuteAndProbe(string? retainedEvidencePath)
    {
        if (!OperatingSystem.IsWindows())
            return new(3, false, false, false, false, false);

        var scheduledTasksAbsent = RemoveAndProveScheduledTasksAbsent();
        RemoveMachineRegistration(ProtocolKeyPath);
        RemoveMachineRegistration(ArpKeyPath);
        var protocolAbsent = IsMachineRegistrationAbsent(ProtocolKeyPath);
        var arpAbsent = IsMachineRegistrationAbsent(ArpKeyPath);
        var retainedPresent = IsRetainedEvidencePresent(retainedEvidencePath);
        var credentialsAbsent = retainedPresent &&
                                AreOperationalCredentialsAbsent(retainedEvidencePath!);
        return new(
            CountRemainingServices(),
            scheduledTasksAbsent,
            protocolAbsent,
            arpAbsent,
            retainedPresent,
            credentialsAbsent);
    }

    internal static bool IsRetainedEvidencePresent(string? retainedEvidencePath)
    {
        if (string.IsNullOrWhiteSpace(retainedEvidencePath) ||
            !Path.IsPathFullyQualified(retainedEvidencePath))
            return false;
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(retainedEvidencePath));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            var marker = new FileInfo(Path.Combine(directory.FullName, "retention.json"));
            return marker.Exists &&
                   marker.Length is > 0 and <= 16 * 1024 &&
                   !marker.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool AreOperationalCredentialsAbsent(string retainedEvidencePath)
    {
        try
        {
            var directory = new DirectoryInfo(Path.GetFullPath(retainedEvidencePath));
            if (!directory.Exists || directory.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
            return !Directory.EnumerateFileSystemEntries(
                    directory.FullName,
                    "*",
                    SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Any(IsOperationalCredentialFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    internal static bool IsOperationalCredentialFileName(string? fileName) =>
        !string.IsNullOrEmpty(fileName) &&
        (string.Equals(fileName, "credentials.dat", StringComparison.OrdinalIgnoreCase) ||
         fileName.StartsWith(".credentials.dat.", StringComparison.OrdinalIgnoreCase) ||
         fileName.StartsWith("credentials.dat.tmp", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(fileName, "pipe.nonce", StringComparison.OrdinalIgnoreCase) ||
         fileName.StartsWith("pipe.nonce.tmp", StringComparison.OrdinalIgnoreCase));

    private static bool RemoveAndProveScheduledTasksAbsent()
    {
        var firstQuery = QuerySuavoTasks();
        if (!firstQuery.Success || firstQuery.TaskNames.Count > MaxTaskCount) return false;
        foreach (var taskName in firstQuery.TaskNames)
        {
            var disabled = RunProcess(
                "schtasks.exe",
                ["/Change", "/TN", taskName, "/Disable"]);
            if (!disabled.Started || disabled.ExitCode != 0) return false;

            var ended = RunProcess(
                "schtasks.exe",
                ["/End", "/TN", taskName]);
            if (!ended.Started || ended.OutputOverflow ||
                !IsSafeTaskEndResult(ended.ExitCode, ended.Output))
                return false;

            var deletion = RunProcess(
                "schtasks.exe",
                ["/Delete", "/TN", taskName, "/F"]);
            if (!deletion.Started || deletion.ExitCode != 0) return false;
        }
        var proof = QuerySuavoTasks();
        return proof.Success && proof.TaskNames.Count == 0;
    }

    private static (bool Success, IReadOnlyList<string> TaskNames) QuerySuavoTasks()
    {
        var query = RunProcess("schtasks.exe", ["/Query", "/FO", "CSV", "/V", "/NH"]);
        if (!query.Started || query.ExitCode != 0 || query.OutputOverflow)
            return (false, []);
        var candidates = ParseSuavoScheduledTaskNames(query.Output);
        if (candidates.Count > MaxTaskCount) return (false, []);

        var owned = new List<string>();
        string windowsDirectory;
        try { windowsDirectory = ReadTrustedWindowsDirectory(); }
        catch { return (false, []); }
        foreach (var candidate in candidates)
        {
            var xml = RunProcess(
                "schtasks.exe",
                ["/Query", "/TN", candidate, "/XML"]);
            if (!xml.Started || xml.ExitCode != 0 || xml.OutputOverflow)
                return (false, []);
            if (IsExactRetiredSelfUninstallTaskXml(xml.Output, windowsDirectory))
                owned.Add(candidate);
        }
        return (true, owned);
    }

    internal static IReadOnlyList<string> ParseSuavoScheduledTaskNames(string output) =>
        output
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(ReadFirstCsvField)
            .Where(IsExactOwnedScheduledTaskName)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxTaskCount + 1)
            .ToArray();

    internal static bool IsExactOwnedScheduledTaskName(string? taskName) =>
        string.Equals(
            taskName,
            LegacySelfUninstallTaskPath,
            StringComparison.OrdinalIgnoreCase) ||
        string.Equals(
            taskName,
            LegacySelfUninstallTaskPath.TrimStart('\\'),
            StringComparison.OrdinalIgnoreCase);

    internal static bool IsExactRetiredSelfUninstallTaskXml(
        string xml,
        string windowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(xml) ||
            xml.Length > MaxTaskXmlChars ||
            string.IsNullOrWhiteSpace(windowsDirectory))
            return false;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxTaskXmlChars,
            };
            using var text = new StringReader(xml);
            using var reader = XmlReader.Create(text, settings);
            var document = XDocument.Load(reader, LoadOptions.None);
            var actions = document.Descendants()
                .Where(element => element.Name.LocalName == "Actions")
                .ToArray();
            if (actions.Length != 1) return false;
            var actionElements = actions[0].Elements().ToArray();
            if (actionElements.Length != 1 ||
                actionElements[0].Name.LocalName != "Exec")
                return false;
            var exec = actionElements[0];
            var children = exec.Elements().ToArray();
            if (children.Length != 2 ||
                children.Count(element => element.Name.LocalName == "Command") != 1 ||
                children.Count(element => element.Name.LocalName == "Arguments") != 1)
                return false;
            var command = children.Single(
                element => element.Name.LocalName == "Command").Value.Trim();
            var arguments = children.Single(
                element => element.Name.LocalName == "Arguments").Value.Trim();
            return IsExactLegacyPowerShell(command) &&
                   IsExactLegacyCleanerArguments(arguments, windowsDirectory);
        }
        catch (Exception exception) when (exception is
            XmlException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // The retired Broker shipped exactly Command=powershell. Similar-looking
    // executable names or paths are not ownership evidence and must survive.
    private static bool IsExactLegacyPowerShell(string command) =>
        string.Equals(command, "powershell", StringComparison.Ordinal);

    private static bool IsExactLegacyCleanerArguments(
        string arguments,
        string windowsDirectory)
    {
        var tokens = arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 5 ||
            !string.Equals(tokens[0], "-NoProfile", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tokens[1], "-ExecutionPolicy", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tokens[2], "Bypass", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(tokens[3], "-File", StringComparison.OrdinalIgnoreCase))
            return false;
        var expectedPrefix = NormalizeWindowsPath(windowsDirectory) +
                             @"\Temp\suavo_selfuninstall_";
        var script = NormalizeWindowsPath(tokens[4]);
        if (!script.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase) ||
            !script.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ||
            script.Length != expectedPrefix.Length + 32 + 4)
            return false;
        return script.AsSpan(expectedPrefix.Length, 32).ToArray().All(character =>
            character is >= '0' and <= '9' or
                >= 'a' and <= 'f' or
                >= 'A' and <= 'F');
    }

    private static string NormalizeWindowsPath(string value) =>
        value.Replace('/', '\\').TrimEnd('\\');

    internal static string ReadTrustedWindowsDirectory()
    {
        var systemBinary = TrustedWindowsSystemBinary.Resolve("schtasks.exe");
        var systemDirectory = Path.GetDirectoryName(systemBinary)
                              ?? throw new InvalidDataException(
                                  "Trusted System32 directory is unavailable.");
        return Directory.GetParent(systemDirectory)?.FullName
               ?? throw new InvalidDataException(
                   "Trusted Windows directory is unavailable.");
    }

    internal static bool IsSafeTaskEndResult(int exitCode, string output) =>
        exitCode == 0 ||
        exitCode == TaskNotRunningHResult ||
        (exitCode == 1 && output.Contains(
            "cannot be stopped because it is not running",
            StringComparison.OrdinalIgnoreCase));

    internal static string? ReadFirstCsvField(string line)
    {
        line = line.TrimStart('\uFEFF');
        if (string.IsNullOrEmpty(line)) return null;
        if (line[0] != '"')
        {
            var comma = line.IndexOf(',');
            return comma < 0 ? line : line[..comma];
        }
        var builder = new System.Text.StringBuilder();
        for (var index = 1; index < line.Length; index++)
        {
            if (line[index] != '"')
            {
                builder.Append(line[index]);
                continue;
            }
            if (index + 1 < line.Length && line[index + 1] == '"')
            {
                builder.Append('"');
                index++;
                continue;
            }
            return builder.ToString();
        }
        return null;
    }

    private static void RemoveMachineRegistration(string path)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                root.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                root.Flush();
            }
            catch
            {
                // The proof below remains false if removal was denied or incomplete.
            }
        }
    }

    private static bool IsMachineRegistrationAbsent(string path)
    {
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var key = root.OpenSubKey(path, writable: false);
                if (key is not null) return false;
            }
            catch
            {
                return false;
            }
        }
        return true;
    }

    private static int CountRemainingServices()
    {
        var remaining = 0;
        foreach (var serviceName in ServiceNames)
        {
            var result = RunProcess("sc.exe", ["query", serviceName]);
            if (!result.Started || result.OutputOverflow ||
                !result.Output.Contains("1060", StringComparison.Ordinal))
                remaining++;
        }
        return remaining;
    }

    private static ProcessResult RunProcess(string executable, IReadOnlyList<string> arguments)
    {
        if (!IsApprovedCleanupExecutable(executable))
            return new(false, -1, string.Empty, false);
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
            var output = new System.Text.StringBuilder();
            var overflow = false;
            var gate = new object();
            void Append(string? value)
            {
                if (value is null) return;
                lock (gate)
                {
                    if (output.Length + value.Length + 1 > MaxProcessOutputChars)
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

    internal static bool IsApprovedCleanupExecutable(string? executable) =>
        string.Equals(executable, "schtasks.exe", StringComparison.Ordinal) ||
        string.Equals(executable, "sc.exe", StringComparison.Ordinal);

    private sealed record ProcessResult(
        bool Started,
        int ExitCode,
        string Output,
        bool OutputOverflow);
}
