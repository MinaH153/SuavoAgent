using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.InstallerSupport;

internal enum MsiCleanHostPreflightExitCode
{
    Success = 0,
    InvalidArguments = 70,
    UnsupportedHost = 71,
    LegacyStatePresent = 72,
    ProbeFailed = 73,
}

/// <summary>
/// Read-only refusal boundary for a direct first-install MSI launch. A real
/// installed product skips this action through Installed; a related product
/// with the authored UpgradeCode is recognized here as a major upgrade.
/// </summary>
internal static class MsiCleanHostPreflightRunner
{
    internal const string Switch = "--msi-assert-clean-install-host";

    internal static bool IsRequested(IReadOnlyList<string>? arguments) =>
        arguments?.Any(argument => string.Equals(
            argument,
            Switch,
            StringComparison.OrdinalIgnoreCase)) == true;

    internal static int Run(IReadOnlyList<string>? arguments) => Run(
        arguments,
        OperatingSystem.IsWindows(),
        WindowsMsiCleanHostProbe.HasSingleInstalledRelatedProduct,
        WindowsMsiCleanHostProbe.AssertClean);

    internal static int Run(
        IReadOnlyList<string>? arguments,
        bool isWindows,
        Func<bool> hasRecognizedRelatedProduct,
        Action<bool> assertClean)
    {
        if (arguments is null ||
            arguments.Count != 1 ||
            !string.Equals(arguments[0], Switch, StringComparison.OrdinalIgnoreCase))
            return (int)MsiCleanHostPreflightExitCode.InvalidArguments;
        if (!isWindows)
            return (int)MsiCleanHostPreflightExitCode.UnsupportedHost;

        ArgumentNullException.ThrowIfNull(hasRecognizedRelatedProduct);
        ArgumentNullException.ThrowIfNull(assertClean);
        try
        {
            var recognizedRelatedProduct = hasRecognizedRelatedProduct();
            assertClean(recognizedRelatedProduct);
            return (int)MsiCleanHostPreflightExitCode.Success;
        }
        catch (MsiLegacyStatePresentException)
        {
            return (int)MsiCleanHostPreflightExitCode.LegacyStatePresent;
        }
        catch
        {
            // No path, product, process, account, or exception text crosses
            // the MSI boundary.
            return (int)MsiCleanHostPreflightExitCode.ProbeFailed;
        }
    }
}

internal sealed class MsiLegacyStatePresentException : Exception
{
}

internal static class WindowsMsiCleanHostProbe
{
    private const string UpgradeCode =
        "{32C06D4D-CFC3-49CB-A6C4-A52E6EFFFBCB}";
    private const uint ErrorSuccess = 0;
    private const uint ErrorNoMoreItems = 259;
    private const int InstallStateDefault = 5;
    private const int MaximumRegistryEntries = 4096;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int MaximumProcessPathCharacters = 32 * 1024;
    private const int MaximumShortcutTextCharacters = 4096;

    private static readonly Regex ExactLegacyBrokerPath = new(
        @"^[A-Za-z]:\\Users\\[^\\]+\\suavo-publish\\Broker\\SuavoAgent\.Broker\.exe$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));
    private static readonly Regex ExactLegacyBrokerArgument = new(
        "\"?[A-Za-z]:\\\\Users\\\\[^\"\\\\\\r\\n]+\\\\suavo-publish\\\\Broker\\\\SuavoAgent\\.Broker\\.exe\"?",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        TimeSpan.FromMilliseconds(50));

    private static readonly string[] ServiceNames =
    [
        "SuavoAgent.Core",
        "SuavoAgent.Broker",
        "SuavoAgent.Watchdog",
    ];

    internal static bool HasSingleInstalledRelatedProduct()
    {
        var installed = 0;
        var enumerated = 0;
        for (uint index = 0; index <= 16; index++)
        {
            var productCode = new StringBuilder(39);
            var result = MsiEnumRelatedProducts(
                UpgradeCode,
                0,
                index,
                productCode);
            if (result == ErrorNoMoreItems)
                return enumerated == 1 && installed == 1;
            if (result != ErrorSuccess)
                throw new InvalidOperationException(
                    "Related-product enumeration failed.");
            enumerated++;
            if (enumerated > 1)
                throw new InvalidDataException(
                    "Multiple related products require manual recovery.");
            if (MsiQueryProductState(productCode.ToString()) == InstallStateDefault)
                installed++;
        }
        throw new InvalidDataException(
            "Related-product enumeration exceeded its bound.");
    }

    internal static void AssertClean(bool hasRecognizedRelatedProduct)
    {
        var programFiles = Environment.GetFolderPath(
            Environment.SpecialFolder.ProgramFiles);
        var canonicalBrokerPath = Path.Combine(
            programFiles,
            "Suavo",
            "Agent",
            "SuavoAgent.Broker.exe");
        AssertNoConflictingBrokerProcess(
            hasRecognizedRelatedProduct,
            canonicalBrokerPath);
        AssertNoExactLegacyShortcut();

        // A related MSI legitimately owns the exact services, registration,
        // and canonical Program Files directory below. Exempt only that state;
        // the independent process/shortcut checks above always run.
        if (hasRecognizedRelatedProduct)
            return;

        foreach (var serviceName in ServiceNames)
            AssertRegistryKeyAbsent(
                RegistryHive.LocalMachine,
                RegistryView.Registry64,
                $@"SYSTEM\CurrentControlSet\Services\{serviceName}");

        AssertMachineProductStateAbsent(RegistryView.Registry64);
        AssertMachineProductStateAbsent(RegistryView.Registry32);
        AssertCurrentUserProductStateAbsent();

        var systemRoot = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        AssertPathAbsent(Path.Combine(programFiles, "Suavo", "Agent"));
        AssertPathAbsent(Path.Combine(programFiles, "SuavoAgent"));
        AssertPathAbsent(Path.Combine(Path.GetPathRoot(systemRoot)!, "SuavoAgent"));
        AssertPathAbsent(Path.Combine(
            systemRoot,
            "System32",
            "Tasks",
            "SuavoSelfUninstall"));
        AssertPathAbsent(Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.CommonApplicationData),
            Release1ConvergenceContract.InstallProofRootDirectoryName,
            Release1MsiInstallMarkerTransaction.JournalFileName));
    }

    private static void AssertNoConflictingBrokerProcess(
        bool hasRecognizedRelatedProduct,
        string canonicalBrokerPath)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName("SuavoAgent.Broker"); }
        catch { throw new InvalidOperationException("Process probe failed."); }
        try
        {
            foreach (var process in processes)
            {
                var path = QueryProcessImagePath(process.Id);
                if (!IsAllowedBrokerProcessPath(
                        path,
                        hasRecognizedRelatedProduct,
                        canonicalBrokerPath))
                    throw new MsiLegacyStatePresentException();
            }
        }
        finally
        {
            foreach (var process in processes)
                process.Dispose();
        }
    }

    internal static bool IsAllowedBrokerProcessPath(
        string? processPath,
        bool hasRecognizedRelatedProduct,
        string canonicalBrokerPath)
    {
        var observed = NormalizeWindowsPath(processPath);
        var canonical = NormalizeWindowsPath(canonicalBrokerPath);
        return hasRecognizedRelatedProduct &&
            observed is not null &&
            canonical is not null &&
            string.Equals(observed, canonical, StringComparison.OrdinalIgnoreCase);
    }

    private static string QueryProcessImagePath(int processId)
    {
        var handle = OpenProcess(
            ProcessQueryLimitedInformation,
            inheritHandle: false,
            processId);
        if (handle == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var path = new StringBuilder(MaximumProcessPathCharacters);
            var characters = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref characters) ||
                characters is <= 0 or >= MaximumProcessPathCharacters)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        }
        finally
        {
            _ = CloseHandle(handle);
        }
    }

    private static void AssertNoExactLegacyShortcut()
    {
        var windowsDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.Windows);
        foreach (var root in ShortcutRoots())
        {
            foreach (var path in new[]
                     {
                         Path.Combine(root, "Suavo.lnk"),
                         Path.Combine(root, "Suavo", "Suavo.lnk"),
                     })
            {
                if (!RegularLocalFileExists(path))
                    continue;
                if (ReadExactLegacyShortcutTarget(path, windowsDirectory))
                    throw new MsiLegacyStatePresentException();
            }
        }
    }

    private static bool ReadExactLegacyShortcutTarget(
        string path,
        string windowsDirectory)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID(
                "WScript.Shell",
                throwOnError: true)
                ?? throw new InvalidOperationException(
                    "The shortcut metadata reader is unavailable.");
            shell = Activator.CreateInstance(shellType)
                ?? throw new InvalidOperationException(
                    "The shortcut metadata reader is unavailable.");
            dynamic dynamicShell = shell;
            shortcut = dynamicShell.CreateShortcut(path);
            dynamic dynamicShortcut = shortcut;
            return IsExactLegacyShortcutTarget(
                (string?)dynamicShortcut.TargetPath,
                (string?)dynamicShortcut.Arguments,
                windowsDirectory);
        }
        catch (MsiLegacyStatePresentException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Shortcut metadata inspection failed.",
                exception);
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    internal static bool IsExactLegacyShortcutTarget(
        string? targetPath,
        string? arguments,
        string windowsDirectory)
    {
        if (IsExactLegacyBrokerPath(targetPath))
            return true;
        var target = NormalizeWindowsPath(targetPath);
        var windows = NormalizeWindowsPath(windowsDirectory);
        if (target is null || windows is null)
            return false;
        var trustedCommandHosts = new[]
        {
            windows + @"\System32\cmd.exe",
            windows + @"\Sysnative\cmd.exe",
        };
        if (!trustedCommandHosts.Any(candidate => string.Equals(
                target,
                candidate,
                StringComparison.OrdinalIgnoreCase)) ||
            string.IsNullOrWhiteSpace(arguments) ||
            arguments.Length > MaximumShortcutTextCharacters ||
            arguments.IndexOfAny(['\r', '\n']) >= 0)
            return false;
        return ExactLegacyBrokerArgument.IsMatch(arguments);
    }

    internal static bool IsExactLegacyBrokerPath(string? value)
    {
        var path = NormalizeWindowsPath(value);
        return path is not null && ExactLegacyBrokerPath.IsMatch(path);
    }

    private static bool RegularLocalFileExists(string path)
    {
        try
        {
            if (PathContainsReparsePoint(path))
                throw new InvalidDataException(
                    "The shortcut path crosses a reparse point.");
            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The shortcut candidate is not a regular local file.");
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            _ = Marshal.FinalReleaseComObject(value);
    }

    private static void AssertMachineProductStateAbsent(RegistryView view)
    {
        AssertRegistryKeyAbsent(
            RegistryHive.LocalMachine,
            view,
            @"SOFTWARE\SuavoAgent");
        AssertRegistryKeyAbsent(
            RegistryHive.LocalMachine,
            view,
            @"SOFTWARE\MKM Technologies LLC\SuavoAgent");
        AssertRegistryKeyAbsent(
            RegistryHive.LocalMachine,
            view,
            @"SOFTWARE\Classes\suavoagent");
        AssertNoSuavoAgentUninstallEntry(
            RegistryHive.LocalMachine,
            view,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private static void AssertCurrentUserProductStateAbsent()
    {
        AssertRegistryKeyAbsent(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"SOFTWARE\Classes\suavoagent");
        AssertNoSuavoAgentUninstallEntry(
            RegistryHive.CurrentUser,
            RegistryView.Default,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
    }

    private static void AssertRegistryKeyAbsent(
        RegistryHive hive,
        RegistryView view,
        string path)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var key = root.OpenSubKey(path, writable: false);
        if (key is not null)
            throw new MsiLegacyStatePresentException();
    }

    private static void AssertNoSuavoAgentUninstallEntry(
        RegistryHive hive,
        RegistryView view,
        string path)
    {
        using var root = RegistryKey.OpenBaseKey(hive, view);
        using var uninstall = root.OpenSubKey(path, writable: false);
        if (uninstall is null)
            return;
        var names = uninstall.GetSubKeyNames();
        if (names.Length > MaximumRegistryEntries)
            throw new InvalidDataException(
                "Uninstall registry enumeration exceeded its bound.");
        foreach (var name in names)
        {
            using var product = uninstall.OpenSubKey(name, writable: false);
            if (product?.GetValue("DisplayName") is string displayName &&
                (displayName.Equals("SuavoAgent", StringComparison.OrdinalIgnoreCase) ||
                 displayName.StartsWith(
                     "SuavoAgent ",
                     StringComparison.OrdinalIgnoreCase)))
                throw new MsiLegacyStatePresentException();
        }
    }

    private static void AssertPathAbsent(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("A clean-host probe path is invalid.");
        try
        {
            _ = File.GetAttributes(path);
            throw new MsiLegacyStatePresentException();
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static IEnumerable<string> ShortcutRoots() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        }
        .Where(IsBoundedLocalShortcutRoot)
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static bool IsBoundedLocalShortcutRoot(string? path)
    {
        var candidate = NormalizeWindowsPath(path);
        if (!HasBoundedDrivePathSyntax(candidate))
            return false;

        var driveType = new DriveInfo(candidate![..3]).DriveType;
        return IsBoundedLocalShortcutRoot(
            candidate,
            driveType,
            PathContainsReparsePoint(candidate));
    }

    internal static bool IsBoundedLocalShortcutRoot(
        string? path,
        DriveType driveType,
        bool pathContainsReparsePoint)
    {
        var candidate = NormalizeWindowsPath(path);
        return HasBoundedDrivePathSyntax(candidate) &&
            driveType == DriveType.Fixed &&
            !pathContainsReparsePoint;
    }

    private static bool HasBoundedDrivePathSyntax(string? path)
    {
        if (path is not { Length: >= 3 } ||
            !char.IsAsciiLetter(path[0]) ||
            path[1] != ':' ||
            path[2] != '\\')
            return false;
        var segments = path[3..].Split(
            '\\',
            StringSplitOptions.RemoveEmptyEntries);
        return segments.Length <= 64 &&
            segments.All(segment =>
                segment is not "." and not ".." &&
                !segment.Contains(':'));
    }

    private static bool PathContainsReparsePoint(string path)
    {
        var current = path[..3];
        foreach (var segment in path[3..].Split(
                     '\\',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                if (File.GetAttributes(current).HasFlag(
                        FileAttributes.ReparsePoint))
                    return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                return false;
            }
        }
        return false;
    }

    private static string? NormalizeWindowsPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > MaximumProcessPathCharacters ||
            value.IndexOfAny(['\r', '\n', '\0']) >= 0)
            return null;
        var normalized = value.Trim().Trim('"').Replace('/', '\\');
        while (normalized.Length > 3 && normalized.EndsWith('\\'))
            normalized = normalized[..^1];
        return normalized;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern uint MsiEnumRelatedProducts(
        string upgradeCode,
        uint reserved,
        uint productIndex,
        StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode)]
    private static extern int MsiQueryProductState(string productCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [DllImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(
        IntPtr process,
        uint flags,
        StringBuilder executableName,
        ref int size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
