using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;

namespace SuavoAgent.Setup.Maintenance;

internal sealed record LegacyInteractiveLaunchCleanupResult(
    bool Succeeded,
    int ShortcutsRemoved,
    int ProcessesStopped,
    int UnclassifiedShortcutsPreserved,
    bool RunnableLegacyPathRemains);

/// <summary>
/// Retires only the exact former developer-publish Broker launch. A same-named
/// user shortcut whose target cannot be proven is preserved, and no source
/// checkout or other user file is deleted.
/// </summary>
internal static partial class LegacyInteractiveLaunchRetirement
{
    private const int MaxShortcutCharacters = 32 * 1024;
    private static readonly Guid ShellLinkClassId =
        new("00021401-0000-0000-C000-000000000046");

    internal static LegacyInteractiveLaunchCleanupResult Execute()
    {
        if (!OperatingSystem.IsWindows())
            return new(false, 0, 0, 0, true);

        var removed = 0;
        var preserved = 0;
        var stopped = 0;
        var failed = false;
        foreach (var shortcut in CandidateShortcutPaths())
        {
            if (!File.Exists(shortcut)) continue;
            if (!TryReadShortcut(shortcut, out var target, out var arguments))
            {
                preserved++;
                continue;
            }
            if (!IsExactLegacyLaunch(target, arguments))
            {
                preserved++;
                continue;
            }
            try
            {
                File.Delete(shortcut);
                if (Path.Exists(shortcut)) failed = true;
                else removed++;
            }
            catch (Exception exception) when (exception is
                IOException or UnauthorizedAccessException or NotSupportedException)
            {
                failed = true;
            }
        }

        Process[] processes;
        try { processes = Process.GetProcessesByName("SuavoAgent.Broker"); }
        catch
        {
            processes = [];
            failed = true;
        }
        foreach (var process in processes)
        {
            using (process)
            {
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { continue; }
                if (!IsExactLegacyBrokerPath(path)) continue;
                try
                {
                    process.Kill(entireProcessTree: true);
                    if (!process.WaitForExit(5_000)) failed = true;
                    else stopped++;
                }
                catch
                {
                    try { if (!process.HasExited) failed = true; }
                    catch { failed = true; }
                }
            }
        }

        var remains = failed || ProveExactLegacyLaunchAbsent();
        return new(
            !remains,
            removed,
            stopped,
            preserved,
            remains);
    }

    internal static bool IsExactLegacyLaunch(string? target, string? arguments)
    {
        if (IsExactLegacyBrokerPath(target)) return true;
        string windowsDirectory;
        try
        {
            windowsDirectory = UninstallTerminalCleanup.ReadTrustedWindowsDirectory();
        }
        catch
        {
            return false;
        }
        return IsExactLegacyCommandHostLaunch(
            target,
            arguments,
            windowsDirectory);
    }

    internal static bool IsExactLegacyCommandHostLaunch(
        string? target,
        string? arguments,
        string trustedWindowsDirectory)
    {
        if (!IsTrustedCommandHost(target, trustedWindowsDirectory) ||
            string.IsNullOrWhiteSpace(arguments) ||
            arguments.Length > MaxShortcutCharacters)
            return false;
        return ExecutablePathRegex().Matches(arguments)
            .Select(match => match.Groups["path"].Value.Trim('"'))
            .Any(IsExactLegacyBrokerPath);
    }

    internal static bool IsExactLegacyBrokerPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            path.Length > 2_048 ||
            path.Any(char.IsControl))
            return false;
        var normalized = path.Trim().Trim('"').Replace('/', '\\');
        if (!DrivePathRegex().IsMatch(normalized) ||
            normalized.Split('\\').Any(segment => segment is "." or ".."))
            return false;
        var segments = normalized.Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 6 ||
            !string.Equals(segments[1], "Users", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[^1],
                "SuavoAgent.Broker.exe",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[^2], "Broker", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[^3],
                "suavo-publish",
                StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    private static bool ProveExactLegacyLaunchAbsent()
    {
        foreach (var shortcut in CandidateShortcutPaths())
        {
            if (!File.Exists(shortcut)) continue;
            if (TryReadShortcut(shortcut, out var target, out var arguments) &&
                IsExactLegacyLaunch(target, arguments))
                return true;
        }
        Process[] processes;
        try { processes = Process.GetProcessesByName("SuavoAgent.Broker"); }
        catch { return true; }
        foreach (var process in processes)
        {
            using (process)
            {
                try
                {
                    if (IsExactLegacyBrokerPath(process.MainModule?.FileName))
                        return true;
                }
                catch
                {
                    // An unreadable process is not proven to be the owned
                    // developer-publish path and is never terminated by guess.
                }
            }
        }
        return false;
    }

    private static IReadOnlyList<string> CandidateShortcutPaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .SelectMany(root => new[]
            {
                Path.Combine(root, "Suavo.lnk"),
                Path.Combine(root, "Suavo", "Suavo.lnk"),
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryReadShortcut(
        string path,
        out string target,
        out string arguments)
    {
        target = string.Empty;
        arguments = string.Empty;
        object? shellLink = null;
        try
        {
            shellLink = Activator.CreateInstance(
                Type.GetTypeFromCLSID(ShellLinkClassId, throwOnError: true)!);
            var persistence = (IPersistFile)shellLink!;
            persistence.Load(path, 0);
            var link = (IShellLinkW)shellLink!;
            var targetBuffer = new StringBuilder(MaxShortcutCharacters);
            var argumentsBuffer = new StringBuilder(MaxShortcutCharacters);
            if (link.GetPath(
                    targetBuffer,
                    targetBuffer.Capacity,
                    out _,
                    0) != 0 ||
                link.GetArguments(
                    argumentsBuffer,
                    argumentsBuffer.Capacity) != 0)
                return false;
            target = targetBuffer.ToString();
            arguments = argumentsBuffer.ToString();
            return !string.IsNullOrWhiteSpace(target);
        }
        catch (Exception exception) when (exception is COMException or
                                           InvalidCastException or
                                           UnauthorizedAccessException or
                                           IOException)
        {
            return false;
        }
        finally
        {
            if (shellLink is not null && Marshal.IsComObject(shellLink))
                Marshal.FinalReleaseComObject(shellLink);
        }
    }

    private static bool IsTrustedCommandHost(
        string? path,
        string trustedWindowsDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            string.IsNullOrWhiteSpace(trustedWindowsDirectory))
            return false;
        var normalized = path.Trim().Trim('"').Replace('/', '\\');
        var windows = trustedWindowsDirectory.Trim().Trim('"')
            .Replace('/', '\\').TrimEnd('\\');
        if (!DrivePathRegex().IsMatch(normalized) ||
            !DrivePathRegex().IsMatch(windows) ||
            normalized.Split('\\').Any(segment => segment is "." or "..") ||
            windows.Split('\\').Any(segment => segment is "." or ".."))
            return false;
        return string.Equals(
                   normalized,
                   windows + @"\System32\cmd.exe",
                   StringComparison.OrdinalIgnoreCase) ||
               string.Equals(
                   normalized,
                   windows + @"\Sysnative\cmd.exe",
                   StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(
        "^[A-Za-z]:\\\\",
        RegexOptions.CultureInvariant)]
    private static partial Regex DrivePathRegex();

    [GeneratedRegex(
        "(?<path>\"?[A-Za-z]:\\\\[^\"\\r\\n]*?SuavoAgent\\.Broker\\.exe\"?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ExecutablePathRegex();

    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        [PreserveSig]
        int GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file,
            int maximumCharacters,
            out Win32FindData findData,
            uint flags);
        [PreserveSig] int GetIdList(out IntPtr itemIdList);
        [PreserveSig] int SetIdList(IntPtr itemIdList);
        [PreserveSig] int GetDescription(StringBuilder value, int maximumCharacters);
        [PreserveSig] int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetWorkingDirectory(StringBuilder value, int maximumCharacters);
        [PreserveSig] int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig]
        int GetArguments(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder value,
            int maximumCharacters);
        [PreserveSig] int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string value);
        [PreserveSig] int GetHotkey(out short hotkey);
        [PreserveSig] int SetHotkey(short hotkey);
        [PreserveSig] int GetShowCommand(out int showCommand);
        [PreserveSig] int SetShowCommand(int showCommand);
        [PreserveSig] int GetIconLocation(StringBuilder path, int maximumCharacters, out int index);
        [PreserveSig] int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        [PreserveSig] int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        [PreserveSig] int Resolve(IntPtr window, uint flags);
        [PreserveSig] int SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string FileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] public string AlternateFileName;
    }
}
