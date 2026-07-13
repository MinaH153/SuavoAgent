namespace SuavoAgent.Contracts.Maintenance;

/// <summary>
/// Stable command contract shared by Setup, Broker, and Watchdog. The maintenance
/// host is an Authenticode-signed native executable staged beside the service
/// binaries; production callers must never substitute a script path.
/// </summary>
public static class MaintenanceContract
{
    public const string ExecutableName = "SuavoAgent.Maintenance.exe";
    public const string InstallStateFileName = "install-state.json";
    public const string SignedSetupArtifactName = "SuavoSetup.exe";
    public const string ReleaseChecksumsFileName = "checksums.sha256";
    public const string ReleaseChecksumsSignatureFileName = "checksums.sha256.sig";
    public const string FieldReleaseReceiptFileName = "field-release-receipt.json";
    public const string CurrentOtaManifestFileName = "current-update-manifest.txt";
    public const string CurrentOtaManifestSignatureFileName = "current-update-manifest.sig";
    public const string RepairServicesSwitch = "--repair-services";
    public const string UninstallSwitch = "--uninstall";
    public const string ProtectedStagingSwitch = "--from-protected-staging";
    public const string ReasonSwitch = "--reason";

    public static string BuildRepairArguments(MaintenanceReason reason) =>
        $"{RepairServicesSwitch} {ReasonSwitch} {ToWireValue(reason)}";

    public static string ToWireValue(MaintenanceReason reason) => reason switch
    {
        MaintenanceReason.WatchdogServiceMissing => "watchdog-service-missing",
        MaintenanceReason.ServiceRestartFailed => "service-restart-failed",
        MaintenanceReason.HelperLaunchFailed => "helper-launch-failed",
        MaintenanceReason.RemoteRepairRequested => "remote-repair-requested",
        MaintenanceReason.SelfUninstallRequested => "self-uninstall-requested",
        MaintenanceReason.ManualRepairRequested => "manual-repair-requested",
        _ => "unspecified",
    };

    public static bool TryParseReason(string? value, out MaintenanceReason reason)
    {
        reason = value?.Trim().ToLowerInvariant() switch
        {
            "watchdog-service-missing" => MaintenanceReason.WatchdogServiceMissing,
            "service-restart-failed" => MaintenanceReason.ServiceRestartFailed,
            "helper-launch-failed" => MaintenanceReason.HelperLaunchFailed,
            "remote-repair-requested" => MaintenanceReason.RemoteRepairRequested,
            "self-uninstall-requested" => MaintenanceReason.SelfUninstallRequested,
            "manual-repair-requested" => MaintenanceReason.ManualRepairRequested,
            _ => MaintenanceReason.Unspecified,
        };

        return reason != MaintenanceReason.Unspecified;
    }
}

public enum MaintenanceReason
{
    Unspecified = 0,
    WatchdogServiceMissing,
    ServiceRestartFailed,
    HelperLaunchFailed,
    RemoteRepairRequested,
    SelfUninstallRequested,
    ManualRepairRequested,
}
