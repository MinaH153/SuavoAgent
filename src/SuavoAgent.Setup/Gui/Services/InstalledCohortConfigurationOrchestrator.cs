using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Verify;

namespace SuavoAgent.Setup.Gui.Services;

/// <summary>
/// Pairs and configures the five binaries already installed by MSI. Runtime
/// executables and SCM registrations are immutable inputs to this workflow.
/// </summary>
internal sealed class InstalledCohortConfigurationOrchestrator
{
    internal enum Phase { Validate, Brain, Configure, Activate, Verify, Done }
    internal sealed record PhaseEvent(Phase Phase, string Message, int? Percent = null);

    private readonly InstallContext _context;
    private readonly Release1InstallReceiptWriter? _release1ReceiptWriter;

    internal InstalledCohortConfigurationOrchestrator(
        InstallContext context,
        Release1InstallReceiptWriter? release1ReceiptWriter = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _release1ReceiptWriter = release1ReceiptWriter;
    }

    internal async Task RunAsync(
        IProgress<PhaseEvent> progress,
        CancellationToken cancellationToken)
    {
        using var transactionLock = InstallerTransactionLock.Acquire();
        ValidatePreconditions();

        progress.Report(new(Phase.Validate, "Verifying the installed signed cohort"));
        var maintenanceRoot = DefaultMaintenanceRoot();
        var coordinator = new NativeInstallCoordinator();
        var sidecars = await Release1TrustSidecarHydrator.HydrateAsync(
                _context.Config,
                _context.InstallDir,
                _context.DataDir,
                cancellationToken)
            .ConfigureAwait(false);
        if (!sidecars.Succeeded)
            throw new InstallException(
                "SuavoAgent could not retrieve and verify this install's signed release proof. " +
                "Check the internet connection and try again. Support code: " +
                sidecars.Code);
        if (!ValidateInstalledCohort(_context, requireCurrentInstalledHost: true))
            throw new InstallException(
                "The installed SuavoAgent files could not be proven authentic and complete.");

        cancellationToken.ThrowIfCancellationRequested();
        progress.Report(new(Phase.Brain, "Preparing the on-device brain", 0));
        await InstallBrainIfConfiguredAsync(progress, cancellationToken)
            .ConfigureAwait(false);

        var configWriter = new InstallOrchestrator(_context);
        string? provisioningId = null;
        VerifyOutcome? probationHealth = null;

        var transaction = new InstalledCohortConfigurationTransaction(
            _context.InstallDir,
            _context.DataDir,
            maintenanceRoot,
            new InstalledConfigurationCallbacks(
                ValidateCohort: () =>
                    ValidateInstalledCohort(
                        _context,
                        requireCurrentInstalledHost: true),
                Quiesce: () => coordinator.QuiesceAndRetireLegacyLifecycle(
                    _context.InstallDir,
                    _context.DataDir),
                ApplyConfigurationAndStageAuthority: () =>
                {
                    progress.Report(new(
                        Phase.Configure,
                        "Writing protected workstation configuration"));
                    configWriter.WriteConfigFiles(_context.InstallDir);
                    provisioningId = InitialCredentialPersister.Stage(
                        _context.DataDir,
                        _context.Config);
                },
                PreserveAuthorityForRecovery: () =>
                    DeviceKeyCutover.PreserveForRecovery(_context.Config),
                StartInstalledCohort: () => coordinator.StartInstalledCohort(
                    _context.InstallDir,
                    _context.DataDir),
                VerifyProbationHealth: () =>
                {
                    progress.Report(new(
                        Phase.Activate,
                        "Proving probation health and device authority"));
                    probationHealth = NativeInstallHealthMilestone.WaitAsync(
                            _context.InstallDir,
                            _context.DataDir,
                            TimeSpan.FromSeconds(90),
                            cancellationToken)
                        .GetAwaiter()
                        .GetResult();
                    return probationHealth.Passed;
                },
                PromoteAuthority: () => DeviceTokenConfirmation.ConfirmAsync(
                        _context.Config,
                        provisioningId ?? throw new InvalidOperationException(
                            "Provisioning identity was not staged."),
                        cancellationToken,
                        sqlServerCertificateSha256:
                            configWriter.EnrolledSqlServerCertificateDigest)
                    .GetAwaiter()
                    .GetResult(),
                FinalizeAuthority: () => FinalizeAuthority(_context),
                RestartPromotedCohort: () =>
                {
                    progress.Report(new(
                        Phase.Verify,
                        "Verifying active workstation health"));
                    return coordinator.RestartPromotedInstalledCohort(
                        _context.InstallDir,
                        _context.DataDir,
                        TimeSpan.FromSeconds(90));
                },
                CompleteAuthority: () => CompleteAuthority(
                    _context,
                    _release1ReceiptWriter ??
                    Release1InstallReceiptWriter.CreateProduction()),
                AbortAuthority: () => AbortAuthority(_context)));

        var result = transaction.Execute();
        if (!result.Succeeded)
        {
            throw new InstalledConfigurationException(
                result.Code,
                result.RecoveryRequired,
                result.RolledBack);
        }

        if (probationHealth is not null)
            WriteVerificationReceipt(_context.DataDir, probationHealth);
        configWriter.QueuePioneerRxHumanApproval();
        progress.Report(new(Phase.Done, "Workstation connected"));
    }

    private void ValidatePreconditions()
    {
        if (!_context.ConfigureInstalledCohort)
            throw new InvalidOperationException(
                "Configuration-only orchestration requires the installed entry mode.");
        if (_context.Consent is null)
            throw new InvalidOperationException("Consent must be captured before pairing.");
        if (_context.Pioneer is null)
            throw new InstallException("PioneerRx must be detected before pairing.");
        if (_context.SqlCredentials is null)
            throw new InstallException("PioneerRx SQL access must be verified before pairing.");
        if (string.IsNullOrWhiteSpace(_context.MachineFingerprint))
            throw new InstallException("The workstation identity is missing.");
    }

    private async Task InstallBrainIfConfiguredAsync(
        IProgress<PhaseEvent> progress,
        CancellationToken cancellationToken)
    {
        if (_context.Config.Reasoning is not { Enabled: true } reasoning)
            return;
        var brainProgress = new Progress<int>(percent => progress.Report(new(
            Phase.Brain,
            "Preparing the on-device brain",
            percent)));
        _context.BrainInstalled = await BrainInstaller.InstallAsync(
            reasoning,
            _context.DataDir,
            brainProgress,
            cancellationToken).ConfigureAwait(false);
        if (!_context.BrainInstalled)
            throw new InstallException(
                "The signed on-device brain package could not be verified.");
    }

    internal static bool ValidateInstalledCohort(
        InstallContext context,
        bool requireCurrentInstalledHost)
    {
        try
        {
            var maintenance = Path.Combine(
                context.InstallDir,
                MaintenanceContract.ExecutableName);
            if (requireCurrentInstalledHost &&
                !string.Equals(
                    Path.GetFullPath(Environment.ProcessPath ?? string.Empty),
                    Path.GetFullPath(maintenance),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                return false;
            var validation = MaintenanceCohortValidator.Validate(
                context.InstallDir,
                Path.Combine(context.DataDir, "binaries.manifest"));
            if (!validation.IsValid) return false;
            var statePath = Path.Combine(
                context.InstallDir,
                MaintenanceContract.InstallStateFileName);
            using var state = JsonDocument.Parse(
                BoundedFile.ReadBytes(statePath, 64 * 1024));
            if (!state.RootElement.TryGetProperty("version", out var version) ||
                version.ValueKind != JsonValueKind.String)
                return false;
            return string.Equals(
                version.GetString()?.TrimStart('v', 'V'),
                context.Config.ReleaseTag.TrimStart('v', 'V'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           JsonException or
                                           ArgumentException)
        {
            return false;
        }
    }

    private static bool FinalizeAuthority(InstallContext context)
    {
        InitialCredentialPersister.Commit(context.DataDir, context.Config);
        DeviceKeyCutover.Commit(
            context.Config,
            context.MachineFingerprint ?? throw new InvalidOperationException(
                "Machine fingerprint is missing."));
        return true;
    }

    private static bool CompleteAuthority(
        InstallContext context,
        Release1InstallReceiptWriter release1ReceiptWriter)
    {
        var receipt = release1ReceiptWriter.Write(
            context.InstallDir,
            context.DataDir,
            context.Config.ReleaseTag,
            context.MachineFingerprint ?? throw new InvalidOperationException(
                "Machine fingerprint is missing."),
            context.Config.MaintenanceKeyId ?? throw new InvalidOperationException(
                "Maintenance key identity is missing."));
        if (!receipt.Succeeded) return false;
        InitialCredentialPersister.Complete(context.DataDir, context.Config);
        return true;
    }

    private static bool AbortAuthority(InstallContext context)
    {
        var keyAborted = false;
        var credentialAborted = false;
        try
        {
            DeviceKeyCutover.Abort(
                context.Config,
                context.MachineFingerprint ?? throw new InvalidOperationException(
                    "Machine fingerprint is missing."));
            keyAborted = true;
        }
        finally
        {
            InitialCredentialPersister.Abort(context.DataDir, context.Config);
            credentialAborted = true;
        }
        return keyAborted && credentialAborted;
    }

    private static void WriteVerificationReceipt(
        string dataDirectory,
        VerifyOutcome outcome)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(dataDirectory, "install-verify.json"),
                PostInstallVerifier.ToJson(outcome));
        }
        catch
        {
            // Health already passed. The bounded receipt is diagnostic only.
        }
    }

    internal static string DefaultMaintenanceRoot() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent-Maintenance");
}

internal sealed class InstalledConfigurationException : Exception
{
    internal InstalledConfigurationException(
        string code,
        bool recoveryRequired,
        bool rolledBack)
        : base(code)
    {
        Code = code;
        RecoveryRequired = recoveryRequired;
        RolledBack = rolledBack;
    }

    internal string Code { get; }
    internal bool RecoveryRequired { get; }
    internal bool RolledBack { get; }
}
