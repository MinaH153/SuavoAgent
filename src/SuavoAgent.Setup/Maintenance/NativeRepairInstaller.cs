using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;

namespace SuavoAgent.Setup.Maintenance;

/// <summary>
/// Headless, fail-closed Windows service and lifecycle repair hosted by the staged signed Setup PE.
/// It never registers with the cloud, consumes a token, downloads code, deletes a
/// service, or stops the Watchdog that launched it.
/// </summary>
internal static class NativeRepairInstaller
{
    internal const int Success = 0;
    internal const int UnsupportedHost = 20;
    internal const int InvalidCohort = 21;
    internal const int AclRepairFailed = 22;
    internal const int ServiceStopFailed = 23;
    internal const int ServiceConfigFailed = 24;
    internal const int ServiceStartFailed = 25;
    internal const int CohortUnhealthy = 26;
    internal const int AuthorityRecoveryPending = 27;
    internal const int LegacyLifecycleMigrationFailed = 28;
    internal const int LifecycleRegistrationFailed = 29;

    public static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            ConsoleUI.WriteFail("Native service repair is supported only on Windows.");
            return UnsupportedHost;
        }

        var processPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(processPath) ||
            !string.Equals(
                Path.GetFileName(processPath),
                MaintenanceContract.ExecutableName,
                StringComparison.OrdinalIgnoreCase))
        {
            ConsoleUI.WriteFail(
                $"Repair must run from the staged {MaintenanceContract.ExecutableName} host.");
            return UnsupportedHost;
        }

        var reason = ReadReason(args);
        var installDir = Path.GetDirectoryName(processPath)!;
        var dataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent");
        var manifestPath = Path.Combine(dataDir, "binaries.manifest");

        ConsoleUI.WriteStep($"Native service repair ({MaintenanceContract.ToWireValue(reason)})");
        var services = new ScWindowsServiceControl();
        var reassertAcls = ServiceInstaller.ReassertMaintenanceAcls;
        using var installerTransactionLock = InstallerTransactionLock.Acquire();
        var coordinator = new NativeInstallCoordinator(
            services,
            reassertAcls,
            ServiceInstaller.KillCohortProcessesExceptCurrent);
        return RunCore(
            installDir,
            dataDir,
            manifestPath,
            services,
            reassertAcls,
            recoverAuthority: () => coordinator.RecoverIncomplete(
                installDir,
                dataDir),
            retireLegacyLifecycle: (installDirectory, dataDirectory) =>
                LegacyLifecycleMigration.Execute(
                    installDirectory,
                    dataDirectory).Succeeded,
            repairLifecycleRegistration: () =>
                RestoreLifecycleRegistration(installDir));
    }

    internal static int RunCore(
        string installDir,
        string dataDir,
        string manifestPath,
        IWindowsServiceControl services,
        Func<string, string, bool> reassertAcls,
        string? updatePublicKeyDerBase64 = null,
        Func<InstallTransactionResult>? recoverAuthority = null,
        Func<string, AuthenticodePublisherTrust>? verifyAuthenticode = null,
        Func<string, MaintenanceHostTrustResult>? verifyMaintenanceTrust = null,
        Func<string, string, bool>? retireLegacyLifecycle = null,
        Func<bool>? repairLifecycleRegistration = null)
    {
        if (recoverAuthority is not null)
        {
            var recovery = recoverAuthority();
            if (!recovery.Succeeded && !recovery.RolledBack)
            {
                ConsoleUI.WriteFail(
                    $"Native repair deferred: authority recovery is incomplete ({recovery.Code}).");
                return AuthorityRecoveryPending;
            }
        }

        // Absolute invariant: no Service Control Manager mutation is allowed until
        // the fixed host, managed-install marker, complete cohort, and every hash pass.
        var validation = MaintenanceCohortValidator.Validate(
            installDir,
            manifestPath,
            updatePublicKeyDerBase64,
            verifyAuthenticode,
            verifyMaintenanceTrust);
        if (!validation.IsValid)
        {
            ConsoleUI.WriteFail($"Native repair refused: {validation.Code}");
            return InvalidCohort;
        }

        // Broker first because it owns the interactive Helper; then Core. The
        // Watchdog is deliberately never stopped or deleted by its repair child.
        if (!services.StopAndWait(NativeServiceSpecs.Broker.Name, TimeSpan.FromSeconds(45)) ||
            !services.StopAndWait(NativeServiceSpecs.Core.Name, TimeSpan.FromSeconds(45)))
        {
            ConsoleUI.WriteFail("Native repair could not quiesce Core/Broker safely.");
            return ServiceStopFailed;
        }

        foreach (var spec in NativeServiceSpecs.All)
        {
            if (!services.EnsureConfigured(spec, installDir))
            {
                ConsoleUI.WriteFail($"Native repair could not configure {spec.Name}.");
                return ServiceConfigFailed;
            }
        }

        // Exact Core service-SID ACLs are safe only after the service exists and
        // SERVICE_SID_TYPE_UNRESTRICTED has been reasserted. This ordering repairs
        // old LocalService-wide installs without ever weakening an invalid cohort:
        // cohort validation above still precedes every SCM or ACL mutation.
        if (!reassertAcls(installDir, dataDir))
        {
            ConsoleUI.WriteFail("Native repair could not reassert the hardened install/data ACLs.");
            return AclRepairFailed;
        }

        // The directories are protected and the repair targets are quiesced.
        // Retire the old elevated script path before any service may restart.
        if (retireLegacyLifecycle is not null &&
            !retireLegacyLifecycle(installDir, dataDir))
        {
            ConsoleUI.WriteFail(
                "Native repair refused to restart while a legacy maintenance path remains. " +
                "Support code: SETUP-LEGACY-MIGRATION");
            return LegacyLifecycleMigrationFailed;
        }

        foreach (var spec in NativeServiceSpecs.All)
        {
            if (!services.StartAndWait(spec.Name, TimeSpan.FromSeconds(90)))
            {
                ConsoleUI.WriteFail($"Native repair could not start {spec.Name}.");
                return ServiceStartFailed;
            }
        }

        var unhealthy = NativeServiceSpecs.All
            .Where(spec => services.Query(spec.Name) != NativeServiceState.Running)
            .Select(spec => spec.Name)
            .ToArray();
        if (unhealthy.Length > 0)
        {
            ConsoleUI.WriteFail($"Native repair ended with unhealthy services: {string.Join(",", unhealthy)}");
            return CohortUnhealthy;
        }

        if (repairLifecycleRegistration is not null)
        {
            try
            {
                if (!repairLifecycleRegistration())
                {
                    ConsoleUI.WriteFail(
                        "Native repair could not restore the Windows Settings maintenance entry.");
                    return LifecycleRegistrationFailed;
                }
            }
            catch
            {
                ConsoleUI.WriteFail(
                    "Native repair could not restore the Windows Settings maintenance entry.");
                return LifecycleRegistrationFailed;
            }
        }

        ConsoleUI.WriteOk(
            "Native repair verified Core, Broker, Watchdog, and the Windows Settings maintenance entry.");
        return Success;
    }

    private static bool RestoreLifecycleRegistration(string installDirectory)
    {
        var identity = InstalledUpdateIdentityReader.TryRead(installDirectory);
        if (identity is null) return false;
        ServiceInstaller.RegisterUninstallEntry(installDirectory, identity.Version);
        return true;
    }

    internal static MaintenanceReason ReadReason(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], MaintenanceContract.ReasonSwitch, StringComparison.OrdinalIgnoreCase) &&
                MaintenanceContract.TryParseReason(args[i + 1], out var parsed))
                return parsed;
        }
        return MaintenanceReason.Unspecified;
    }
}
