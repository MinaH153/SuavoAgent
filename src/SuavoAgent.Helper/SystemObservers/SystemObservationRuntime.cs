using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Owns the always-on system observer lifetime, including the authenticated
/// browser native-host relay. Keeping this composition out of Program also
/// makes shutdown order and readiness emission independently testable.
/// </summary>
internal sealed class SystemObservationRuntime : IAsyncDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly ForegroundTracker _foreground;
    private readonly StationProfiler _station;
    private readonly UserSessionObserver _session;
    private readonly BrowserDomainObserver _browser;
    private readonly PrintEventObserver _print;
    private readonly SpreadsheetStructureObserver _spreadsheet;
    private readonly MultiAppUiaObserver _multiAppUia;
    private readonly BrowserObservationRelayServer? _browserRelay;
    private readonly Action<bool> _setObservationActive;
    private readonly Func<CancellationToken, Task> _foregroundRunner;
    private readonly Func<CancellationToken, Task> _printRunner;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _shutdown;
    private readonly object _healthLock = new();
    private Task? _foregroundTask;
    private Task? _printTask;
    private Task? _livenessTask;
    private Task? _taskSupervisor;
    private bool _observationClosed;
    private int _started;
    private int _disposed;

    public SystemObservationRuntime(
        BehavioralEventBuffer buffer,
        string observationKey,
        Func<string, string?> domainClassifier,
        Func<ObservationKeyLease?> currentLease,
        Action<bool> setObservationActive,
        ILogger logger,
        CancellationToken parentToken,
        Func<CancellationToken, Task>? foregroundRunner = null,
        Func<CancellationToken, Task>? printRunner = null)
    {
        _buffer = buffer ?? throw new ArgumentNullException(nameof(buffer));
        ArgumentException.ThrowIfNullOrWhiteSpace(observationKey);
        ArgumentNullException.ThrowIfNull(domainClassifier);
        ArgumentNullException.ThrowIfNull(currentLease);
        _setObservationActive = setObservationActive ?? throw new ArgumentNullException(nameof(setObservationActive));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _shutdown = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        _foreground = new ForegroundTracker(buffer, observationKey, logger);
        _station = new StationProfiler(buffer, observationKey, logger);
        _session = new UserSessionObserver(buffer, observationKey, logger);
        _browser = new BrowserDomainObserver(buffer, observationKey, domainClassifier, logger);
        _print = new PrintEventObserver(buffer, observationKey, logger);
        _foregroundRunner = foregroundRunner ?? _foreground.RunAsync;
        _printRunner = printRunner ?? _print.RunAsync;
        _spreadsheet = new SpreadsheetStructureObserver(buffer, observationKey, logger);
        _multiAppUia = new MultiAppUiaObserver(buffer, observationKey, logger);
        _browserRelay = OperatingSystem.IsWindows()
            ? BrowserObservationRelayServer.CreateProduction(_browser, currentLease, logger)
            : null;

        _foreground.OnAppFocusChanged(context =>
        {
            if (BrowserDomainObserver.IsBrowserProcess(context.ProcessName))
                _browser.OnBrowserFocusedWithoutConnector();
            if (SpreadsheetStructureObserver.IsSpreadsheetProcess(context.ProcessName) &&
                !string.IsNullOrEmpty(context.WindowTitle))
                _spreadsheet.OnSpreadsheetFocused(context.WindowTitle);
            _multiAppUia.OnAppFocused(context.ProcessName, context.WindowHandle);
        });
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("system_observers_already_started");
        _station.CaptureProfile();
        _browserRelay?.Start(_shutdown.Token);
        _foregroundTask = Task.Run(
            () => _foregroundRunner(_shutdown.Token),
            CancellationToken.None);
        _printTask = Task.Run(
            () => _printRunner(_shutdown.Token),
            CancellationToken.None);
        _livenessTask = Task.Run(EmitLivenessAsync, CancellationToken.None);
        _taskSupervisor = Task.WhenAll(
            ObserveTaskAsync(_foregroundTask, "foreground", unexpectedCompletionIsFailure: true),
            ObserveTaskAsync(
                _printTask,
                "print",
                unexpectedCompletionIsFailure: OperatingSystem.IsWindows()),
            ObserveTaskAsync(_livenessTask, "liveness", unexpectedCompletionIsFailure: true));
        RenewObservationHealth();
        _logger.Information(
            "System observers started (foreground, session, browser relay, Winspool, spreadsheet, UIA)");
    }

    private async Task EmitLivenessAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
            do
            {
                // Watching is a renewable health lease, not a startup latch.
                // PandaCompanionHost expires it if this loop stops renewing.
                RenewObservationHealth();
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("system_liveness", "ready"));
                _buffer.Enqueue(BehavioralEvent.ObserverStatus(
                    "browser_domain",
                    _browser.ConnectorStatus));
                _buffer.Enqueue(BehavioralEvent.ObserverStatus("print", _print.CurrentStatus));
                _buffer.Enqueue(BehavioralEvent.ObserverStatus(
                    "user_session",
                    _session.IsAvailable ? "ready" : "unavailable"));
                _buffer.Enqueue(BehavioralEvent.ObserverStatus(
                    "multi_app_uia",
                    _multiAppUia.CurrentStatus));
            }
            while (await timer.WaitForNextTickAsync(_shutdown.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    private async Task ObserveTaskAsync(
        Task task,
        string component,
        bool unexpectedCompletionIsFailure)
    {
        try
        {
            await task.ConfigureAwait(false);
            if (!_shutdown.IsCancellationRequested && unexpectedCompletionIsFailure)
            {
                _logger.Error(
                    "System observation component stopped unexpectedly component={Component}",
                    component);
                FailObservationHealth();
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.Error(
                "System observation component failed closed component={Component} errorType={ErrorType}",
                component,
                exception.GetType().Name);
            FailObservationHealth();
        }
    }

    private void RenewObservationHealth()
    {
        lock (_healthLock)
        {
            if (!_observationClosed)
                _setObservationActive(true);
        }
    }

    private void FailObservationHealth()
    {
        try { _shutdown.Cancel(); } catch { }
        CloseObservationHealth();
    }

    private void CloseObservationHealth()
    {
        lock (_healthLock)
        {
            if (_observationClosed) return;
            _observationClosed = true;
            _setObservationActive(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CloseObservationHealth();
        _shutdown.Cancel();
        _foreground.Dispose();
        _session.Dispose();
        _print.Dispose();
        if (_browserRelay is not null)
            await _browserRelay.DisposeAsync().ConfigureAwait(false);
        await AwaitStoppedAsync(_foregroundTask).ConfigureAwait(false);
        await AwaitStoppedAsync(_printTask).ConfigureAwait(false);
        await AwaitStoppedAsync(_livenessTask).ConfigureAwait(false);
        await AwaitStoppedAsync(_taskSupervisor).ConfigureAwait(false);
        await _buffer.DisposeAsync().ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private static async Task AwaitStoppedAsync(Task? task)
    {
        if (task is null)
            return;
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
        catch
        {
            // Faults are converted to an inactive health edge by the task
            // supervisor; disposal must still complete every cleanup step.
        }
    }
}
