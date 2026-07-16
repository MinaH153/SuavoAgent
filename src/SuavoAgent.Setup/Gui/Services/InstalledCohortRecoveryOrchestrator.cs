using SuavoAgent.Setup.Maintenance;

namespace SuavoAgent.Setup.Gui.Services;

internal static class InstalledCohortRecoveryOrchestrator
{
    internal const string DefaultInstallDirectory = @"C:\Program Files\Suavo\Agent";
    internal const string DefaultDataDirectory = @"C:\ProgramData\SuavoAgent";

    internal static bool HasPendingRecovery()
    {
        var path = Path.Combine(
            InstalledCohortConfigurationOrchestrator.DefaultMaintenanceRoot(),
            InstalledCohortConfigurationTransaction.JournalFileName);
        return File.Exists(path);
    }

    internal static InstalledConfigurationResult Recover()
    {
        using var transactionLock = InstallerTransactionLock.Acquire();
        var coordinator = new NativeInstallCoordinator();
        var transaction = new InstalledCohortConfigurationTransaction(
            DefaultInstallDirectory,
            DefaultDataDirectory,
            InstalledCohortConfigurationOrchestrator.DefaultMaintenanceRoot(),
            new InstalledConfigurationCallbacks(
                ValidateCohort: () => MaintenanceCohortValidator.Validate(
                    DefaultInstallDirectory,
                    Path.Combine(DefaultDataDirectory, "binaries.manifest")).IsValid,
                Quiesce: () => coordinator.Quiesce(),
                ApplyConfigurationAndStageAuthority: () => { },
                PreserveAuthorityForRecovery: () => { },
                StartInstalledCohort: () => coordinator.StartInstalledCohort(
                    DefaultInstallDirectory,
                    DefaultDataDirectory),
                VerifyProbationHealth: () => false,
                PromoteAuthority: () =>
                    InitialCredentialPersister.ReplayPendingAuthorityPromotion(
                        DefaultDataDirectory),
                FinalizeAuthority: () =>
                    InitialCredentialPersister.FinalizePendingAuthority(
                        DefaultDataDirectory),
                RestartPromotedCohort: () =>
                    coordinator.RestartPromotedInstalledCohort(
                        DefaultInstallDirectory,
                        DefaultDataDirectory,
                        TimeSpan.FromSeconds(90)),
                CompleteAuthority: CompleteRecoveredAuthority,
                AbortAuthority: () =>
                    InitialCredentialPersister.ReconcilePendingAuthorityWithoutTransaction(
                        DefaultDataDirectory)));
        return transaction.Recover();
    }

    private static bool CompleteRecoveredAuthority()
    {
        var identity = Release1InstalledReceiptIdentityReader.TryRead(
            DefaultInstallDirectory);
        if (identity is null) return false;
        var receipt = Release1InstallReceiptWriter.CreateProduction().Write(
            DefaultInstallDirectory,
            DefaultDataDirectory,
            identity.ReleaseTag,
            identity.MachineFingerprint,
            identity.MaintenanceKeyId);
        return receipt.Succeeded &&
               InitialCredentialPersister.CompleteRecoveredPendingAuthority(
                   DefaultDataDirectory);
    }
}
