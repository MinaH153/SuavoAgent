using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Setup.Maintenance;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.Views;
// DeviceCodeService, SetupConfig, DeviceCodePairing live in the root namespace.
using SuavoAgent.Setup;

namespace SuavoAgent.Setup.Gui.ViewModels;

/// <summary>
/// Six-step state machine for the installer: Welcome → SystemCheck →
/// Consent → Destination → Progress → Success. Any phase exception lands
/// on <see cref="GoToError"/> with a retry path back to the most recent
/// actionable step (usually Destination). All step classes share a single
/// <see cref="InstallContext"/> instance.
/// </summary>
internal sealed class MainWindowViewModel : ViewModelBase
{
    private const string DefaultCloudUrl = "https://suavollc.com";

    // _ctx is null until device-code pairing returns a probationary identity.
    private InstallContext? _ctx;
    private readonly Action<int>? _shutdown;
    private readonly bool _uninstallOnlyEntry;
    private readonly bool _repairOnlyEntry;
    private readonly bool _configureInstalledCohort;

    private UserControl _currentView;
    private string _stepLabel = "Welcome";

    public UserControl CurrentView
    {
        get => _currentView;
        private set => SetField(ref _currentView, value);
    }

    public string StepLabel
    {
        get => _stepLabel;
        private set => SetField(ref _stepLabel, value);
    }

    /// <summary>Design-time / XAML previewer constructor — no real install context.</summary>
    public MainWindowViewModel()
    {
        _currentView = new WelcomeView
        {
            DataContext = new WelcomeViewModel(() => { }),
        };
    }

    public MainWindowViewModel(
        Action<int> shutdown,
        bool startInUninstall = false,
        bool startInRepair = false,
        bool configureInstalledCohort = false,
        Func<ExistingInstallDisposition>? classifyExistingInstall = null)
    {
        if (new[] { startInUninstall, startInRepair, configureInstalledCohort }.Count(x => x) > 1)
            throw new ArgumentException("Only one native maintenance entry mode may be selected.");

        var existingInstall = startInUninstall || startInRepair
            ? ExistingInstallDisposition.NotInstalled
            : (classifyExistingInstall ?? ExistingInstallClassifier.ClassifyProduction)();

        _shutdown = shutdown;
        _uninstallOnlyEntry = startInUninstall;
        _repairOnlyEntry = startInRepair ||
                           existingInstall is ExistingInstallDisposition.InstalledConfigured or
                               ExistingInstallDisposition.RecoveryRequired;
        _configureInstalledCohort = configureInstalledCohort ||
                                    existingInstall is ExistingInstallDisposition.InstalledUnconfigured or
                                        ExistingInstallDisposition.InstalledRecoveryPending;

        if (startInUninstall)
        {
            // Windows Settings must land directly on a visible native
            // confirmation/progress flow. Uninstall never requires pairing or
            // cloud credentials and must not strand behind onboarding.
            _currentView = BuildUninstallConfirm();
        }
        else if (startInRepair)
        {
            // Add/Remove Programs Modify must be a real, visible native flow.
            // It never consumes a pairing code or launches a script host.
            _currentView = BuildRepairConfirm();
        }
        else if (existingInstall == ExistingInstallDisposition.RecoveryRequired)
        {
            // An ARP/service/install-directory footprint with no valid signed
            // cohort is never authorization to rotate device authority. Keep
            // the old identity untouched and require package-level recovery.
            _currentView = BuildExistingInstallRecoveryError();
        }
        else if (existingInstall == ExistingInstallDisposition.InstalledConfigured)
        {
            // Re-running Setup on an already configured workstation is a
            // maintenance action, not a new pairing. This prevents the cloud
            // from rotating authority before the local credential transaction.
            _currentView = BuildRepairConfirm();
        }
        else if (existingInstall == ExistingInstallDisposition.InstalledRecoveryPending ||
                 (_configureInstalledCohort &&
                  InstalledCohortRecoveryOrchestrator.HasPendingRecovery()))
        {
            _currentView = BuildInstalledRecovery();
        }
        else
        {
            // Native onboarding has one production path: device-code approval.
            // Human-visible codes never carry credentials and the agent-only poll
            // secret remains in memory until the DPAPI probation transaction.
            _currentView = BuildDeviceCodePairing();
        }
    }

    private UserControl BuildExistingInstallRecoveryError()
    {
        StepLabel = "Existing installation needs recovery";
        return new ErrorView
        {
            DataContext = new ErrorViewModel(
                "SuavoAgent is already installed but cannot be verified",
                "Setup found existing program files, Windows services, or an Installed Apps entry, but could not prove a complete signed installation. No pairing code was created and the existing device authority was not changed.\n\nUse the original signed SuavoAgent MSI to repair or remove this installation, then run Setup again.\n\nSupport code: SETUP-EXISTING-INSTALL-RECOVERY",
                onRetry: null,
                onClose: () => _shutdown?.Invoke(1)),
        };
    }

    // ── Device-code pairing ────────────────────────────────────────────────

    private UserControl BuildDeviceCodePairing()
    {
        StepLabel = "Connect workstation";

        var fingerprint = MachineFingerprint.Get();
        var version = InstallerVersion();
        var service = new DeviceCodeService(DefaultCloudUrl);

        var vm = new DeviceCodePairingViewModel(
            service,
            DefaultCloudUrl,
            fingerprint,
            version,
            onPaired: config => OnPaired(config, fingerprint, version),
            onCancelled: () => CurrentView = BuildNoConfigError());

        var view = new DeviceCodePairingView { DataContext = vm };
        _ = vm.StartAsync();
        return view;
    }

    private void OnPaired(SetupConfig config, string fingerprint, string version)
    {
        // Pairing returns an empty ReleaseTag — the installer and the agent
        // binaries ship under the same tag, so use the installer's own version
        // as the release to download.
        var tagged = config with { ReleaseTag = $"v{version}" };
        DeviceKeyCutover.Track(tagged, fingerprint);
        _ctx = new InstallContext(tagged, _configureInstalledCohort)
        {
            MachineFingerprint = fingerprint,
        };

        // We're already on the UI thread (pairing awaited on the captured
        // context), but post defensively to be safe across hosts.
        Dispatcher.UIThread.Post(() =>
        {
            if (_configureInstalledCohort)
                GoToSystemCheck();
            else
                CurrentView = BuildWelcome();
        });
    }

    private static string InstallerVersion() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    // ── Step transitions ───────────────────────────────────────────────

    private UserControl BuildWelcome()
    {
        StepLabel = "Welcome";
        return new WelcomeView
        {
            DataContext = new WelcomeViewModel(GoToSystemCheck, GoToUninstallConfirm),
        };
    }

    private void GoToSystemCheck()
    {
        if (_ctx == null) return;
        StepLabel = "Step 1 of 5 · System check";
        var vm = new SystemCheckViewModel(_ctx, GoToConsent);
        CurrentView = new SystemCheckView { DataContext = vm };
        _ = vm.RunChecksAsync();
    }

    private void GoToConsent()
    {
        if (_ctx == null) return;
        StepLabel = "Step 2 of 5 · Terms & consent";
        CurrentView = new ConsentView
        {
            DataContext = new ConsentViewModel(_ctx, GoToDestination),
        };
    }

    private void GoToDestination()
    {
        if (_ctx == null) return;
        StepLabel = _configureInstalledCohort
            ? "Step 3 of 5 · Workstation settings"
            : "Step 3 of 5 · Install destination";
        CurrentView = new DestinationView
        {
            DataContext = new DestinationViewModel(_ctx, GoToProgress),
        };
    }

    private void GoToProgress()
    {
        if (_ctx == null) return;
        StepLabel = _configureInstalledCohort
            ? "Step 4 of 5 · Connecting"
            : "Step 4 of 5 · Installing";

        var cts = new CancellationTokenSource();
        var vm = _configureInstalledCohort
            ? new ProgressViewModel(
                cts.Cancel,
                new[]
                {
                    "Verify installed SuavoAgent",
                    "Prepare the on-device brain",
                    "Write protected configuration",
                    "Confirm device authority",
                    "Verify active workstation",
                })
            {
                Title = "Connecting SuavoAgent",
                CancelHint = "Cancellation is safe before cloud authority is confirmed.",
            }
            : new ProgressViewModel(cts.Cancel);
        CurrentView = new ProgressView { DataContext = vm };

        ConsoleUI.SetReporter(new GuiInstallReporter(vm));

        _ = Task.Run(async () =>
        {
            try
            {
                if (_configureInstalledCohort)
                {
                    await RunInstalledConfigurationAsync(vm, cts.Token);
                    Dispatcher.UIThread.Post(() =>
                    {
                        foreach (var phase in vm.Phases)
                            phase.State = PhaseState.Done;
                        GoToSuccess();
                    });
                    return;
                }

                var orchestrator = new InstallOrchestrator(_ctx);
                vm.MarkPhase(0, PhaseState.Running);

                await orchestrator.RunAsync(new Progress<InstallOrchestrator.PhaseEvent>(evt =>
                {
                    var index = (int)evt.Phase;
                    if (index >= vm.Phases.Count) return;

                    // Previous phase → done
                    for (int i = 0; i < index; i++)
                        if (vm.Phases[i].State != PhaseState.Done)
                            vm.Phases[i].State = PhaseState.Done;
                    // Current phase → running (unless Done event)
                    if (evt.Phase == InstallOrchestrator.Phase.Done) return;
                    vm.MarkPhase(index, PhaseState.Running);
                    // Live percent (the brain download streams 0-100 + captions).
                    if (evt.Percent is int p)
                        vm.UpdatePhaseProgress(evt.Message, p);
                }), cts.Token);

                // All phases completed
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var phase in vm.Phases)
                        phase.State = PhaseState.Done;
                    GoToSuccess();
                });
            }
            catch (OperationCanceledException)
            {
                Dispatcher.UIThread.Post(() => GoToError(
                    _configureInstalledCohort
                        ? "Connection paused safely"
                        : "Installation cancelled",
                    _configureInstalledCohort
                        ? "SuavoAgent preserved the installed cohort and will recover any pending protected configuration before another pairing attempt."
                        : "The operator cancelled before the services started. No binaries are active on this machine.",
                    retry: () => GoToDestination()));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => GoToError(
                    _configureInstalledCohort
                        ? "Connection could not complete"
                        : "Installation failed",
                    BuildSafeFailureDetail(
                        _configureInstalledCohort ? "configure" : "install",
                        ex),
                    retry: () => GoToDestination()));
            }
        });
    }

    private async Task RunInstalledConfigurationAsync(
        ProgressViewModel viewModel,
        CancellationToken cancellationToken)
    {
        if (_ctx is null)
            throw new InvalidOperationException("Pairing context is unavailable.");
        if (viewModel.Phases.Count != 5)
            throw new InvalidOperationException(
                "Installed configuration requires the exact five-phase UI.");
        var orchestrator = new InstalledCohortConfigurationOrchestrator(_ctx);
        viewModel.MarkPhase(0, PhaseState.Running);
        await orchestrator.RunAsync(
            new Progress<InstalledCohortConfigurationOrchestrator.PhaseEvent>(evt =>
            {
                var index = (int)evt.Phase;
                if (index >= viewModel.Phases.Count) return;
                for (var previous = 0; previous < index; previous++)
                {
                    if (viewModel.Phases[previous].State != PhaseState.Done)
                        viewModel.Phases[previous].State = PhaseState.Done;
                }
                if (evt.Phase == InstalledCohortConfigurationOrchestrator.Phase.Done)
                    return;
                viewModel.MarkPhase(index, PhaseState.Running);
                if (evt.Percent is int percent)
                    viewModel.UpdatePhaseProgress(evt.Message, percent);
            }),
            cancellationToken);
    }

    private UserControl BuildInstalledRecovery()
    {
        StepLabel = "Recover secure connection";
        var vm = new ProgressViewModel(
            () => { },
            new[]
            {
                "Verify installed SuavoAgent",
                "Resume device authority",
                "Verify active workstation",
            },
            canCancel: false)
        {
            Title = "Recovering SuavoAgent",
            CancelHint = "The prior authority decision must be reconciled before a new pairing.",
        };
        vm.MarkPhase(0, PhaseState.Running);
        var view = new ProgressView { DataContext = vm };
        _ = Task.Run(() =>
        {
            var result = InstalledCohortRecoveryOrchestrator.Recover();
            Dispatcher.UIThread.Post(() =>
            {
                if (result.Succeeded)
                {
                    foreach (var phase in vm.Phases)
                        phase.State = PhaseState.Done;
                    StepLabel = "Done";
                    CurrentView = new UninstallSuccessView
                    {
                        DataContext = new UninstallSuccessViewModel(
                            () => _shutdown?.Invoke(0),
                            headline: "SuavoAgent connection recovered",
                            message: "Device authority and active workstation health are confirmed."),
                    };
                    return;
                }
                if (result.RolledBack)
                {
                    CurrentView = BuildDeviceCodePairing();
                    return;
                }
                GoToError(
                    "Secure recovery is still pending",
                    "SuavoAgent could not yet prove the prior device-authority result. No new pairing was started and the protected recovery journal was preserved.\n\nSupport code: SETUP-CONNECT-RECOVERY",
                    retry: () => CurrentView = BuildInstalledRecovery());
            });
        });
        return view;
    }

    private void GoToSuccess()
    {
        if (_ctx == null) return;
        StepLabel = "Done";
        CurrentView = new SuccessView
        {
            DataContext = new SuccessViewModel(_ctx, () => _shutdown?.Invoke(0)),
        };
    }

    // ── Uninstall flow (Welcome → Confirm → Progress → Success/Error) ──────

    private void GoToUninstallConfirm()
    {
        CurrentView = BuildUninstallConfirm();
    }

    private UserControl BuildUninstallConfirm()
    {
        StepLabel = "Uninstall · Confirm";
        return new UninstallConfirmView
        {
            DataContext = new UninstallConfirmViewModel(
                onConfirm: GoToUninstallProgress,
                onCancel: () =>
                {
                    if (_uninstallOnlyEntry)
                        _shutdown?.Invoke(0);
                    else
                        CurrentView = BuildWelcome();
                }),
        };
    }

    private void GoToUninstallProgress()
    {
        StepLabel = "Uninstall · Removing";

        // Uninstall is not cancellable mid-flight (ServiceInstaller.Uninstall is a
        // single blocking call), so the cancel affordance is a no-op pass-through.
        var cts = new CancellationTokenSource();
        var vm = new ProgressViewModel(
            () => { },
            new[]
            {
                "Confirm dashboard authority",
                "Remove authorized runtime",
                "Preserve compliance evidence",
                "Confirm cloud completion",
            },
            canCancel: false)
        {
            Title = "Uninstalling SuavoAgent",
            CancelHint = "Removing runtime components while retaining compliance evidence.",
        };
        CurrentView = new ProgressView { DataContext = vm };

        ConsoleUI.SetReporter(new GuiInstallReporter(vm));

        _ = Task.Run(async () =>
        {
            try
            {
                var orchestrator = new UninstallOrchestrator();
                vm.MarkPhase(0, PhaseState.Running);

                var result = await orchestrator.RunAsync(
                    new Progress<UninstallOrchestrator.PhaseEvent>(evt =>
                    {
                        var index = (int)evt.Phase;
                        if (index >= vm.Phases.Count) return;

                        for (int i = 0; i < index; i++)
                            if (vm.Phases[i].State != PhaseState.Done)
                                vm.Phases[i].State = PhaseState.Done;

                        if (evt.Phase == UninstallOrchestrator.Phase.Done) return;
                        vm.MarkPhase(index, PhaseState.Running);
                    }), cts.Token);

                Dispatcher.UIThread.Post(() =>
                {
                    if (result.IsFinalized && result.Cleanup is { FullyClean: true })
                    {
                        foreach (var phase in vm.Phases)
                            phase.State = PhaseState.Done;
                        GoToUninstallSuccess();
                        return;
                    }

                    var active = vm.Phases
                        .Select((phase, index) => (phase, index))
                        .LastOrDefault(item => item.phase.State == PhaseState.Running);
                    if (active.phase is not null)
                        vm.MarkPhase(active.index, PhaseState.Failed);
                    GoToError(
                        "Removal awaiting secure approval",
                        BuildUninstallPendingDetail(result),
                        retry: GoToUninstallProgress);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => GoToError(
                    "Uninstall failed",
                    BuildSafeFailureDetail("uninstall", ex),
                    retry: GoToUninstallProgress));
            }
        });
    }

    private void GoToUninstallSuccess()
    {
        StepLabel = "Done";
        CurrentView = new UninstallSuccessView
        {
            DataContext = new UninstallSuccessViewModel(() => _shutdown?.Invoke(0)),
        };
    }

    // ── Repair flow (Settings Modify → Confirm → Progress → Success/Error) ──

    private UserControl BuildRepairConfirm()
    {
        StepLabel = "Repair services · Confirm";
        return new RepairConfirmView
        {
            DataContext = new RepairConfirmViewModel(
                onConfirm: GoToRepairProgress,
                onCancel: () =>
                {
                    if (_repairOnlyEntry)
                        _shutdown?.Invoke(0);
                    else
                        CurrentView = BuildWelcome();
                }),
        };
    }

    private void GoToRepairProgress()
    {
        StepLabel = "Repair services · Verifying";
        var vm = new ProgressViewModel(
            () => { },
            new[]
            {
                "Verify existing signed files",
                "Repair protected service settings",
                "Confirm Windows services running",
                "Restore Windows Settings maintenance",
            },
            canCancel: false)
        {
            Title = "Repairing SuavoAgent services",
            CancelHint = "Service repair cannot be interrupted while Windows protections are being reasserted.",
        };
        CurrentView = new ProgressView { DataContext = vm };
        ConsoleUI.SetReporter(new GuiInstallReporter(vm));

        _ = Task.Run(() =>
        {
            try
            {
                vm.MarkPhase(0, PhaseState.Running);
                var repairArgs = new[]
                {
                    MaintenanceContract.RepairServicesSwitch,
                    MaintenanceContract.ReasonSwitch,
                    MaintenanceContract.ToWireValue(MaintenanceReason.ManualRepairRequested),
                };
                var exitCode = NativeRepairInstaller.Run(repairArgs);
                Dispatcher.UIThread.Post(() =>
                {
                    if (exitCode == NativeRepairInstaller.Success)
                    {
                        foreach (var phase in vm.Phases)
                            phase.State = PhaseState.Done;
                        GoToRepairSuccess();
                        return;
                    }

                    vm.MarkPhase(0, PhaseState.Failed);
                    GoToError(
                        "Repair could not complete",
                        BuildRepairFailureDetail(exitCode),
                        retry: GoToRepairProgress);
                });
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => GoToError(
                    "Repair could not complete",
                    BuildSafeFailureDetail("repair", ex),
                    retry: GoToRepairProgress));
            }
        });
    }

    private void GoToRepairSuccess()
    {
        StepLabel = "Done";
        CurrentView = new UninstallSuccessView
        {
            DataContext = new UninstallSuccessViewModel(
                () => _shutdown?.Invoke(0),
                headline: "SuavoAgent services repaired",
                message: "The existing signed files were verified, protected service settings and the Windows Settings maintenance entry were restored, and Core, Broker, and Watchdog are running. This repair does not replace missing or modified program files."),
        };
    }

    private static string BuildResidueSummary(ServiceInstaller.UninstallResult r)
    {
        var residue = new List<string>();
        if (r.ServicesRemaining > 0)
            residue.Add($"{r.ServicesRemaining} Windows service(s) still registered");
        if (!r.DataDirRemoved)
            residue.Add($"compliance evidence could not be moved into protected retention ({UninstallOrchestrator.DefaultDataDir})");
        if (!r.InstallDirRemoved)
            residue.Add($"install dir could not be fully removed ({UninstallOrchestrator.DefaultInstallDir}) — a file may still be locked");

        var detail = residue.Count > 0
            ? "Residue remaining:\n• " + string.Join("\n• ", residue)
            : "Some items could not be removed.";

        return detail + "\n\nReboot to release any locked files, then run the uninstaller again.";
    }

    internal static string BuildUninstallPendingDetail(
        SelfUninstallFinalizationResult result)
    {
        if (string.Equals(
                result.Code,
                "signed_cloud_authority_required",
                StringComparison.Ordinal))
        {
            return "For HIPAA audit integrity, removal can begin only after an authorized pharmacy administrator approves it in the Suavo dashboard. Nothing on this PC was changed. Approve removal in the dashboard, then retry.\n\nSupport code: SETUP-UNINSTALL-AUTHORITY-REQUIRED";
        }

        if (result.Cleanup is { FullyClean: false } cleanup)
        {
            return BuildResidueSummary(cleanup) +
                   "\n\nCloud completion remains pending and SuavoAgent has not reported this workstation removed.\n\nSupport code: SETUP-UNINSTALL-RESIDUE";
        }

        return "The signed removal is safely pending cloud confirmation. SuavoAgent has not reported this workstation removed. Connect to the internet and retry; the exact signed completion ticket is preserved for replay.\n\nSupport code: SETUP-UNINSTALL-FINALIZATION-PENDING";
    }

    private void GoToError(string title, string detail, Action? retry)
    {
        StepLabel = "Something went wrong";
        CurrentView = new ErrorView
        {
            DataContext = new ErrorViewModel(title, detail, retry, () => _shutdown?.Invoke(1)),
        };
    }

    private UserControl BuildNoConfigError()
    {
        StepLabel = "Not connected to a pharmacy";
        return new ErrorView
        {
            DataContext = new ErrorViewModel(
                "Let's connect this installer to your dashboard",
                "This installer isn't linked to a pharmacy yet.\n\n"
                + "Download SuavoAgent from your dashboard at https://suavollc.com and run "
                + "that installer — it connects to your dashboard automatically.\n\n"
                + "Already have an 8-character pairing code? Run the installer normally and enter "
                + "it on the connect screen.",
                onRetry: null,
                onClose: () => _shutdown?.Invoke(1)),
        };
    }

    internal static string BuildSafeFailureDetail(string operation, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var guidance = exception switch
        {
            InstalledConfigurationException { RecoveryRequired: true } =>
                "SuavoAgent preserved a protected recovery journal because the cloud authority result or active health could not be proven. Retry this screen; a new device pairing will not start until recovery finishes.",
            InstalledConfigurationException { RolledBack: true } =>
                "SuavoAgent could not prove a safe workstation connection. The prior protected configuration was restored.",
            HttpRequestException =>
                "SuavoAgent could not reach the secure service. Check the internet connection, then retry.",
            UnauthorizedAccessException =>
                "Windows blocked access to a protected SuavoAgent component. Approve the administrator prompt, then retry.",
            IOException =>
                "A required SuavoAgent file is unavailable or still in use. Close other setup windows, then retry.",
            _ when string.Equals(operation, "repair", StringComparison.Ordinal) =>
                "SuavoAgent could not prove a safe Windows service and lifecycle repair. No complete repair was reported.",
            _ when string.Equals(operation, "uninstall", StringComparison.Ordinal) =>
                "SuavoAgent could not prove a complete removal. Existing evidence was preserved and no result was reported as clean.",
            _ when string.Equals(operation, "configure", StringComparison.Ordinal) =>
                "SuavoAgent could not prove a safe workstation connection. The installed runtime was not replaced.",
            _ =>
                "SuavoAgent could not prove a safe installation. The prior working version was preserved when rollback was possible.",
        };

        var code = exception switch
        {
            InstalledConfigurationException { RecoveryRequired: true } =>
                "SETUP-CONNECT-RECOVERY",
            InstalledConfigurationException { RolledBack: true } =>
                "SETUP-CONNECT-ROLLED-BACK",
            HttpRequestException => "SETUP-NETWORK",
            UnauthorizedAccessException => "SETUP-ACCESS",
            IOException => "SETUP-FILE-IO",
            _ when string.Equals(operation, "uninstall", StringComparison.Ordinal) => "SETUP-UNINSTALL-SAFE-FAIL",
            _ when string.Equals(operation, "repair", StringComparison.Ordinal) => "SETUP-REPAIR-SAFE-FAIL",
            _ when string.Equals(operation, "configure", StringComparison.Ordinal) => "SETUP-CONNECT-SAFE-FAIL",
            _ => "SETUP-INSTALL-SAFE-FAIL",
        };

        return guidance
            + $"\n\nSupport code: {code}"
            + $"\nProtected setup log: {SetupLog.LogPath}";
    }

    internal static string BuildRepairFailureDetail(int exitCode)
    {
        var guidance = exitCode switch
        {
            NativeRepairInstaller.UnsupportedHost =>
                "Repair must run from the installed, signed SuavoAgent maintenance app on Windows.",
            NativeRepairInstaller.InvalidCohort =>
                "The installed files could not be proven authentic and complete. Download a fresh installer from the Suavo dashboard.",
            NativeRepairInstaller.AuthorityRecoveryPending =>
                "A previous protected update is still recovering. Restart Windows, then try Repair again.",
            NativeRepairInstaller.AclRepairFailed =>
                "Windows did not accept the required protected-folder permissions. Restart Windows, then try Repair again.",
            NativeRepairInstaller.ServiceStopFailed =>
                "Windows could not safely pause the agent services. Restart Windows, then try Repair again.",
            NativeRepairInstaller.ServiceConfigFailed or
            NativeRepairInstaller.ServiceStartFailed or
            NativeRepairInstaller.CohortUnhealthy =>
                "Windows could not return every SuavoAgent service to a healthy state. Restart Windows, then try Repair again.",
            NativeRepairInstaller.LifecycleRegistrationFailed =>
                "Windows services are running, but the signed Repair/Uninstall entry could not be restored in Windows Settings. Retry Repair; no complete repair was reported.",
            _ =>
                "SuavoAgent could not prove a healthy repair. No success was reported.",
        };

        return guidance
            + $"\n\nSupport code: SETUP-REPAIR-{exitCode}"
            + $"\nProtected setup log: {SetupLog.LogPath}";
    }
}
