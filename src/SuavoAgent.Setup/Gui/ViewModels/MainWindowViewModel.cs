using Avalonia.Controls;
using Avalonia.Threading;
using SuavoAgent.Setup.Gui.Services;
using SuavoAgent.Setup.Gui.Views;

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
    private readonly InstallContext? _ctx;
    private readonly Action<int>? _shutdown;

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

    public MainWindowViewModel(InstallContext? ctx, Action<int> shutdown)
    {
        _ctx = ctx;
        _shutdown = shutdown;

        _currentView = ctx == null
            ? BuildNoConfigError()
            : BuildWelcome();
    }

    // ── Step transitions ───────────────────────────────────────────────

    private UserControl BuildWelcome()
    {
        StepLabel = "Welcome";
        return new WelcomeView
        {
            DataContext = new WelcomeViewModel(GoToSystemCheck),
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
        StepLabel = "Step 3 of 5 · Install destination";
        CurrentView = new DestinationView
        {
            DataContext = new DestinationViewModel(_ctx, GoToProgress),
        };
    }

    private void GoToProgress()
    {
        if (_ctx == null) return;
        StepLabel = "Step 4 of 5 · Installing";

        var cts = new CancellationTokenSource();
        var vm = new ProgressViewModel(cts.Cancel);
        CurrentView = new ProgressView { DataContext = vm };

        ConsoleUI.SetReporter(new GuiInstallReporter(vm));

        _ = Task.Run(async () =>
        {
            try
            {
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
                    "Installation cancelled",
                    "The operator cancelled before the services started. No binaries are active on this machine.",
                    retry: () => GoToDestination()));
            }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => GoToError(
                    "Installation failed",
                    ex.Message + "\n\n" + BuildLogTail(vm),
                    retry: () => GoToDestination()));
            }
        });
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
        StepLabel = "Setup not configured";
        return new ErrorView
        {
            DataContext = new ErrorViewModel(
                "No setup.json found",
                "SuavoSetup expects a setup.json file next to the installer (written by your "
                + "pharmacy dashboard), or --pharmacy-id / --api-key command-line arguments.\n\n"
                + "Download the configured installer from https://suavollc.com and run it again.",
                onRetry: null,
                onClose: () => _shutdown?.Invoke(1)),
        };
    }

    private static string BuildLogTail(ProgressViewModel vm)
    {
        var tail = vm.LogLines.Skip(Math.Max(0, vm.LogLines.Count - 6));
        return "Last events:\n" + string.Join("\n", tail.Select(l => l.Text));
    }
}
