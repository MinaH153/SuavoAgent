using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Text;

namespace SuavoAgent.Contracts.Maintenance;

/// <summary>
/// Resolves the small, fixed set of Windows maintenance utilities from the
/// kernel-reported System32 directory. Elevated services must never give
/// CreateProcess a bare executable name because its search order includes
/// attacker-influenced locations such as the current directory and PATH.
/// </summary>
public static class TrustedWindowsSystemBinary
{
    private const int MaximumDirectoryCharacters = 32_768;
    private static readonly FrozenSet<string> AllowedExecutables = new[]
        {
            "manage-bde.exe",
            "sc.exe",
            "schtasks.exe",
        }
        .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public static string Resolve(string executableName)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(
                "Trusted Windows system binaries are available only on Windows.");

        return ResolveFromTrustedDirectories(
            executableName,
            ReadSystemDirectory(),
            ReadWindowsDirectory(),
            File.Exists,
            File.GetAttributes);
    }

    internal static string ResolveFromTrustedDirectories(
        string executableName,
        string systemDirectory,
        string windowsDirectory,
        Func<string, bool> fileExists,
        Func<string, FileAttributes> getAttributes)
    {
        ArgumentNullException.ThrowIfNull(fileExists);
        ArgumentNullException.ThrowIfNull(getAttributes);
        if (!AllowedExecutables.Contains(executableName) ||
            !string.Equals(
                Path.GetFileName(executableName),
                executableName,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The requested executable is not an approved Windows maintenance utility.");

        var system = NormalizeDirectory(systemDirectory);
        var windows = NormalizeDirectory(windowsDirectory);
        var expectedSystem = NormalizeDirectory(Path.Combine(windows, "System32"));
        if (!string.Equals(system, expectedSystem, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The kernel-reported system directory is outside the Windows root.");

        AssertTrustedDirectory(windows, getAttributes);
        AssertTrustedDirectory(system, getAttributes);

        var candidate = Path.GetFullPath(Path.Combine(system, executableName));
        if (!string.Equals(
                Path.GetDirectoryName(candidate)?.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                system,
                StringComparison.OrdinalIgnoreCase) ||
            !fileExists(candidate))
            throw new FileNotFoundException(
                "The trusted Windows maintenance utility is unavailable.",
                candidate);

        var attributes = getAttributes(candidate);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            throw new InvalidDataException(
                "The trusted Windows maintenance utility is not a regular file.");
        return candidate;
    }

    private static string NormalizeDirectory(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Path.IsPathFullyQualified(value))
            throw new InvalidDataException("A fully-qualified Windows directory is required.");
        return Path.GetFullPath(value).TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }

    private static void AssertTrustedDirectory(
        string path,
        Func<string, FileAttributes> getAttributes)
    {
        var attributes = getAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0 ||
            (attributes & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                "A trusted Windows system directory is missing or redirected.");
    }

    private static string ReadSystemDirectory() =>
        ReadKernelDirectory(GetSystemDirectory, "system");

    private static string ReadWindowsDirectory() =>
        ReadKernelDirectory(GetWindowsDirectory, "Windows");

    private static string ReadKernelDirectory(
        KernelDirectoryReader reader,
        string label)
    {
        var buffer = new StringBuilder(MaximumDirectoryCharacters);
        var length = reader(buffer, (uint)buffer.Capacity);
        if (length == 0 || length >= buffer.Capacity)
            throw new InvalidOperationException(
                $"The kernel did not return a bounded {label} directory.");
        return buffer.ToString();
    }

    private delegate uint KernelDirectoryReader(StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetSystemDirectory(StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetWindowsDirectory(StringBuilder buffer, uint size);
}
