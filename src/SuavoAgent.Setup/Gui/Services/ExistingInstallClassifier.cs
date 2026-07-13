using Microsoft.Win32;
using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Closed-world classification performed before a device code may be created.
/// Any machine-wide SuavoAgent footprint is treated as an existing install
/// until the signed cohort and configured identity prove otherwise.
/// </summary>
internal enum ExistingInstallDisposition
{
    NotInstalled,
    InstalledUnconfigured,
    InstalledRecoveryPending,
    InstalledConfigured,
    RecoveryRequired,
}

internal static class ExistingInstallClassifier
{
    private enum ServiceFootprint
    {
        None,
        Complete,
        IncompleteOrUnknown,
    }

    private enum DirectoryFootprint
    {
        None,
        Ordinary,
        UntrustedOrUnknown,
    }

    private const string ArpKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\SuavoAgent";

    internal static ExistingInstallDisposition ClassifyProduction()
    {
        if (!OperatingSystem.IsWindows())
            return ExistingInstallDisposition.NotInstalled;
        try
        {
            return ClassifyWindowsProduction();
        }
        catch
        {
            return ExistingInstallDisposition.RecoveryRequired;
        }
    }

    private static ExistingInstallDisposition ClassifyWindowsProduction()
    {
        var installDirectory = UninstallOrchestrator.DefaultInstallDir;
        var dataDirectory = UninstallOrchestrator.DefaultDataDir;
        var installFootprint = ReadDirectoryFootprint(installDirectory);
        var dataFootprint = ReadDirectoryFootprint(dataDirectory);
        var installPresent = installFootprint == DirectoryFootprint.Ordinary;
        var dataPresent = dataFootprint != DirectoryFootprint.None;
        var arpPresent = HasArpRegistration();
        var serviceFootprint = ReadServiceFootprint();
        var servicePresent = serviceFootprint != ServiceFootprint.None;

        if (!installPresent)
        {
            return arpPresent || servicePresent || dataPresent ||
                   installFootprint != DirectoryFootprint.None
                ? ExistingInstallDisposition.RecoveryRequired
                : ExistingInstallDisposition.NotInstalled;
        }

        var cohort = MaintenanceCohortValidator.Validate(
            installDirectory,
            Path.Combine(dataDirectory, "binaries.manifest"));
        var configurationRecoveryPending = PathEntryExistsOrUnknown(Path.Combine(
            InstalledCohortConfigurationOrchestrator.DefaultMaintenanceRoot(),
            InstalledCohortConfigurationTransaction.JournalFileName));
        return Classify(
            installPresent: true,
            arpPresent,
            servicePresent,
            cohortValid: cohort.IsValid,
            configuredIdentityValid:
                InstalledUpdateIdentityReader.TryRead(installDirectory) is not null,
            dataPresent: dataPresent,
            serviceCohortComplete: serviceFootprint == ServiceFootprint.Complete,
            authorityStatePresent: PathEntryExistsOrUnknown(
                InitialCredentialPersister.CredentialPath(dataDirectory)),
            configurationRecoveryPending: configurationRecoveryPending);
    }

    internal static ExistingInstallDisposition Classify(
        bool installPresent,
        bool arpPresent,
        bool servicePresent,
        bool cohortValid,
        bool configuredIdentityValid,
        bool dataPresent = false,
        bool? serviceCohortComplete = null,
        bool authorityStatePresent = false,
        bool configurationRecoveryPending = false)
    {
        if (!installPresent && !arpPresent && !servicePresent && !dataPresent)
            return ExistingInstallDisposition.NotInstalled;
        if (!installPresent || !cohortValid)
            return ExistingInstallDisposition.RecoveryRequired;
        if (configurationRecoveryPending)
            return ExistingInstallDisposition.InstalledRecoveryPending;
        if (!configuredIdentityValid && authorityStatePresent)
            return ExistingInstallDisposition.RecoveryRequired;
        // MSI-installed, unconfigured binaries are safe to pair only when the
        // complete Windows lifecycle footprint is present. A partial service
        // cohort or missing ARP entry must be repaired before cloud authority
        // can rotate.
        if (!configuredIdentityValid &&
            (!arpPresent || !(serviceCohortComplete ?? servicePresent)))
            return ExistingInstallDisposition.RecoveryRequired;
        return configuredIdentityValid
            ? ExistingInstallDisposition.InstalledConfigured
            : ExistingInstallDisposition.InstalledUnconfigured;
    }

    private static DirectoryFootprint ReadDirectoryFootprint(string path)
    {
        try
        {
            var attributes = File.GetAttributes(Path.GetFullPath(path));
            return attributes.HasFlag(FileAttributes.Directory) &&
                   !attributes.HasFlag(FileAttributes.ReparsePoint)
                ? DirectoryFootprint.Ordinary
                : DirectoryFootprint.UntrustedOrUnknown;
        }
        catch (FileNotFoundException)
        {
            return DirectoryFootprint.None;
        }
        catch (DirectoryNotFoundException)
        {
            return DirectoryFootprint.None;
        }
        catch
        {
            return DirectoryFootprint.UntrustedOrUnknown;
        }
    }

    private static bool HasArpRegistration()
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                RegistryView.Registry64);
            using var key = baseKey.OpenSubKey(ArpKeyPath, writable: false);
            return key is not null;
        }
        catch
        {
            // A denied/failed machine-wide query is ambiguous, never proof of
            // absence. The caller also checks installed files and services.
            return true;
        }
    }

    private static bool PathEntryExistsOrUnknown(string path)
    {
        try
        {
            _ = File.GetAttributes(Path.GetFullPath(path));
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
        catch
        {
            // Access denial or malformed machine state is ambiguity, never
            // proof that old device authority is absent.
            return true;
        }
    }

    private static ServiceFootprint ReadServiceFootprint()
    {
        try
        {
            var services = new ScWindowsServiceControl();
            var states = NativeServiceSpecs.All
                .Select(spec => services.Query(spec.Name))
                .ToArray();
            if (states.Any(state => state == NativeServiceState.Unknown))
                return ServiceFootprint.IncompleteOrUnknown;
            if (states.All(state => state == NativeServiceState.NotInstalled))
                return ServiceFootprint.None;
            return states.All(state => state != NativeServiceState.NotInstalled)
                ? ServiceFootprint.Complete
                : ServiceFootprint.IncompleteOrUnknown;
        }
        catch
        {
            return ServiceFootprint.IncompleteOrUnknown;
        }
    }
}
