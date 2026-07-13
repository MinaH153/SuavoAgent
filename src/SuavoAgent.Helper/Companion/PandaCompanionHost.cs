using Serilog;
using SuavoAgent.Helper.Actuation;
using SuavoAgent.Helper.Presence;

namespace SuavoAgent.Helper.Companion;

/// <summary>
/// Binds the panda view to existing runtime truth and to the same safety gate
/// used by every actuation primitive. The view can pause/resume/stop Autopilot,
/// but it can never bypass a disabled config, compromise latch, or kill switch.
/// </summary>
public sealed class PandaCompanionHost : IDisposable
{
    private readonly IPandaCompanionView _view;
    private readonly ActuationGate _gate;
    private readonly Func<bool> _coreConnected;
    private readonly Func<PresenceMode> _presenceMode;
    private readonly IAutopilotControlClient _coreControl;
    private readonly Func<DateTimeOffset> _clock;
    private readonly ILogger _logger;
    private readonly object _refreshLock = new();
    private readonly TimeSpan _observationLeaseDuration;
    private Timer? _timer;
    private long _observationLeaseExpiresUtcTicks;
    private int _learningActive;
    private int _controlSynchronized = 1;
    private int _controlInFlight;
    private int _disposed;
    private CompanionPresentation? _lastPresentation;

    public PandaCompanionHost(
        IPandaCompanionView view,
        ActuationGate gate,
        Func<bool> coreConnected,
        Func<PresenceMode> presenceMode,
        IAutopilotControlClient coreControl,
        ILogger logger,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? observationLeaseDuration = null)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _coreConnected = coreConnected ?? throw new ArgumentNullException(nameof(coreConnected));
        _presenceMode = presenceMode ?? throw new ArgumentNullException(nameof(presenceMode));
        _coreControl = coreControl ?? throw new ArgumentNullException(nameof(coreControl));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<PandaCompanionHost>();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _observationLeaseDuration = observationLeaseDuration ?? TimeSpan.FromSeconds(45);
        if (_observationLeaseDuration <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(observationLeaseDuration));

        _view.PauseRequested += PauseAutopilot;
        _view.ResumeRequested += ResumeAutopilot;
        _view.StopRequested += StopAutopilot;
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed) != 0 || _timer is not null) return;
        _view.Start();
        Refresh();
        _timer = new Timer(_ => Refresh(), null, TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500));
        _logger.Information("Pharmacist panda companion started");
    }

    public void SetObservationActive(bool active)
    {
        var expires = active
            ? (_clock() + _observationLeaseDuration).UtcDateTime.Ticks
            : 0;
        Interlocked.Exchange(ref _observationLeaseExpiresUtcTicks, expires);
        Refresh();
    }

    public void SetLearningActive(bool active)
    {
        Interlocked.Exchange(ref _learningActive, active ? 1 : 0);
        Refresh();
    }

    internal CompanionPresentation Refresh()
    {
        lock (_refreshLock)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return _lastPresentation ?? CompanionStateLogic.Evaluate(new CompanionSignals(
                    CoreConnected: false,
                    ObservationActive: false,
                    LearningActive: false,
                    PresenceMode: PresenceMode.Idle,
                    ActuationEnabled: false,
                    DryRun: true,
                    ActuationPaused: true,
                    KillSwitchTripped: false,
                    CompromiseDetected: false));
            }

            var gate = _gate.Snapshot();
            var now = _clock();
            var paused = gate.PausedUntilUtc is { } until && until > now;
            var observationActive =
                Interlocked.Read(ref _observationLeaseExpiresUtcTicks) > now.UtcDateTime.Ticks;
            var presentation = CompanionStateLogic.Evaluate(new CompanionSignals(
                CoreConnected: SafeRead(_coreConnected),
                ObservationActive: observationActive,
                LearningActive: Volatile.Read(ref _learningActive) != 0,
                PresenceMode: SafeRead(_presenceMode, PresenceMode.Idle),
                ActuationEnabled: gate.Enabled,
                DryRun: gate.DryRun,
                ActuationPaused: paused,
                KillSwitchTripped: gate.KillSwitchTrippedUtc is not null,
                CompromiseDetected: gate.CompromiseDetected,
                ControlSynchronized: Volatile.Read(ref _controlSynchronized) != 0));

            if (presentation != _lastPresentation)
            {
                _view.Render(presentation);
                _lastPresentation = presentation;
                // State names are a fixed enum vocabulary. Never log the status
                // body or any workflow/window context.
                _logger.Information("Pharmacist panda state={State}", presentation.State);
            }

            return presentation;
        }
    }

    private async void PauseAutopilot()
    {
        if (Interlocked.CompareExchange(ref _controlInFlight, 1, 0) != 0)
            return;
        var snapshot = _gate.Snapshot();
        if (!snapshot.Enabled || snapshot.KillSwitchTrippedUtc is not null || snapshot.CompromiseDetected)
        {
            Volatile.Write(ref _controlInFlight, 0);
            return;
        }
        _gate.PauseUntilResumed();
        Volatile.Write(ref _controlSynchronized, 0);
        Refresh();
        try
        {
            var acknowledged = await _coreControl.PauseAsync(CancellationToken.None)
                .ConfigureAwait(false);
            Volatile.Write(ref _controlSynchronized, acknowledged ? 1 : 0);
        }
        catch (Exception ex)
        {
            _logger.Warning("Core Autopilot pause signal failed ({ErrorType})", ex.GetType().Name);
            Volatile.Write(ref _controlSynchronized, 0);
        }
        finally
        {
            Volatile.Write(ref _controlInFlight, 0);
            Refresh();
        }
    }

    private async void ResumeAutopilot()
    {
        if (Interlocked.CompareExchange(ref _controlInFlight, 1, 0) != 0)
            return;
        var snapshot = _gate.Snapshot();
        if (!snapshot.Enabled || snapshot.KillSwitchTrippedUtc is not null || snapshot.CompromiseDetected)
        {
            Volatile.Write(ref _controlInFlight, 0);
            return;
        }
        Volatile.Write(ref _controlSynchronized, 0);
        Refresh();
        try
        {
            if (await _coreControl.ResumeAsync(CancellationToken.None).ConfigureAwait(false))
            {
                _gate.ClearPause();
                Volatile.Write(ref _controlSynchronized, 1);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning("Core Autopilot resume signal failed ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _controlInFlight, 0);
            Refresh();
        }
    }

    private async void StopAutopilot()
    {
        if (Interlocked.CompareExchange(ref _controlInFlight, 1, 0) != 0)
            return;
        if (_gate.Snapshot().KillSwitchTrippedUtc is null)
            _gate.TripKillSwitch("companion_control");
        Refresh();
        try
        {
            _ = await _coreControl.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Warning("Core Autopilot stop signal failed ({ErrorType})", ex.GetType().Name);
        }
        finally
        {
            Volatile.Write(ref _controlInFlight, 0);
        }
    }

    private static bool SafeRead(Func<bool> read)
    {
        try { return read(); }
        catch { return false; }
    }

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        lock (_refreshLock)
        {
            _view.PauseRequested -= PauseAutopilot;
            _view.ResumeRequested -= ResumeAutopilot;
            _view.StopRequested -= StopAutopilot;
            _view.Dispose();
        }
    }
}
