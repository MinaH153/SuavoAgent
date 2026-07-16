using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SuavoAgent.Setup.Preflight;

internal sealed record WindowsVolumeEncryptionStatus(
    string VolumeRoot,
    uint ReturnCode,
    uint ProtectionStatus,
    string? ProviderDiagnostic = null)
{
    internal bool IsProtected => ReturnCode == 0 && ProtectionStatus == 1;
}

internal sealed record PhiVolumeEncryptionResult(
    bool IsProtected,
    string Detail,
    IReadOnlyList<WindowsVolumeEncryptionStatus> Volumes);

/// <summary>
/// Proves BitLocker state through Win32_EncryptableVolume numeric API fields.
/// No localized command output is parsed and no shell host is involved.
/// </summary>
internal static class WindowsVolumeEncryptionProbe
{
    internal static PhiVolumeEncryptionResult Evaluate(
        IEnumerable<string> phiBearingPaths,
        Func<string, WindowsVolumeEncryptionStatus> probe)
    {
        ArgumentNullException.ThrowIfNull(phiBearingPaths);
        ArgumentNullException.ThrowIfNull(probe);
        var roots = phiBearingPaths
            .Select(path => Path.GetPathRoot(Path.GetFullPath(path)))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => NormalizeVolumeRoot(root!))
            .Distinct(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal)
            .ToArray();
        if (roots.Length == 0)
            return new(false, "The PHI storage volume could not be identified.", []);

        var results = roots.Select(probe).ToArray();
        var failed = results.Where(result => !result.IsProtected).ToArray();
        if (failed.Length > 0)
        {
            var volumes = string.Join(", ", failed.Select(result => result.VolumeRoot));
            return new(
                false,
                $"PHI storage is not protected on {volumes}. Enable BitLocker on every listed volume, then retry.",
                results);
        }
        return new(
            true,
            $"BitLocker protection verified on {string.Join(", ", roots)}",
            results);
    }

    internal static WindowsVolumeEncryptionStatus ProbeProduction(string volumeRoot)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("BitLocker status is available only on Windows.");
        return ProbeWindows(volumeRoot);
    }

    private static string NormalizeVolumeRoot(string root)
    {
        var full = Path.GetFullPath(root);
        if (OperatingSystem.IsWindows())
        {
            if (full.Length < 3 || !char.IsAsciiLetter(full[0]) ||
                full[1] != ':' || full[2] != Path.DirectorySeparatorChar)
                throw new InvalidDataException(
                    "PHI storage must use a local drive-letter volume for BitLocker proof.");
            return char.ToUpperInvariant(full[0]) + @":\";
        }
        return full;
    }

    [SupportedOSPlatform("windows")]
    private static WindowsVolumeEncryptionStatus ProbeWindows(string volumeRoot)
    {
        var driveLetter = NormalizeVolumeRoot(volumeRoot).TrimEnd('\\');
        object? locator = null;
        object? service = null;
        object? volumes = null;
        try
        {
            var locatorType = Type.GetTypeFromProgID("WbemScripting.SWbemLocator", throwOnError: true)
                ?? throw new InvalidOperationException("Windows Management Instrumentation is unavailable.");
            locator = Activator.CreateInstance(locatorType)
                ?? throw new InvalidOperationException("Windows Management Instrumentation is unavailable.");
            dynamic dynamicLocator = locator;
            service = dynamicLocator.ConnectServer(
                ".",
                @"root\CIMV2\Security\MicrosoftVolumeEncryption");
            dynamic dynamicService = service;
            volumes = dynamicService.ExecQuery(
                $"SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter = '{driveLetter}'");
            foreach (dynamic volume in (dynamic)volumes)
            {
                dynamic output = volume.ExecMethod_("GetProtectionStatus");
                var returnCode = Convert.ToUInt32(
                    output.Properties_.Item("ReturnValue").Value,
                    CultureInfo.InvariantCulture);
                var protection = Convert.ToUInt32(
                    output.Properties_.Item("ProtectionStatus").Value,
                    CultureInfo.InvariantCulture);
                return new(volumeRoot, returnCode, protection);
            }
            return new(volumeRoot, uint.MaxValue, 2, "volume_not_found");
        }
        catch (COMException exception)
        {
            return new(
                volumeRoot,
                unchecked((uint)exception.HResult),
                2,
                "wmi_error");
        }
        finally
        {
            ReleaseCom(volumes);
            ReleaseCom(service);
            ReleaseCom(locator);
        }
    }

    private static void ReleaseCom(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); } catch { }
        }
    }
}
