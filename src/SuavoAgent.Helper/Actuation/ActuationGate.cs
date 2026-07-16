using System.Diagnostics;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Centralised guard every actuation primitive consults BEFORE touching Win32.
/// Five independent state axes:
///
///   1. Enabled — master gate. Set false by config OR HotkeyKillSwitch
///      (Ctrl+Shift+Esc). Once tripped locally it stays tripped until the
///      Helper restarts — that's the point of a kill switch.
///   2. DryRun — when true, drivers log the would-be input and return
///      success without actually invoking SendInput.
///   3. PausedUntilUtc — bumped by UserInputObserver every time the
///      pharmacist touches the keyboard or mouse. Defaults to NOW+5 min on
///      any input. WorkflowExecutor checks this between every step.
///   4. CompromiseDetected — latched by the honeytoken reflex.
///   5. DryRun — permitted for simulated sandbox commands, but never for a
///      workflow that claims to mutate a live PMS.
///
/// All methods are thread-safe. Read paths take the read lock; mutators
/// take the write lock briefly. The state is intentionally simple (no
/// async, no events) — this is on the safety-critical path and must
/// behave deterministically under load.
/// </summary>
public sealed class ActuationGate
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly TimeSpan _defaultPauseWindow;
    private readonly ILogger _logger;

    private bool _enabled;
    private bool _dryRun;
    private DateTimeOffset? _pausedUntilUtc;
    private string? _pauseReason;
    private DateTimeOffset? _killSwitchTrippedUtc;

    // Honeytoken immune-reflex state — stamped by the ApoptosisOrchestrator, read out via Snapshot() so
    // Core can emit the self-compromise heartbeat signal. Recording is separate from the gate change
    // (SetDryRun/TripKillSwitch) so each stays single-purpose; latches "up" (never downgrades the level).
    private bool _compromiseDetected;
    private string? _compromiseLevel;
    private string? _compromiseReasonLabel;
    private DateTimeOffset? _compromiseAtUtc;

    public ActuationGate(ActuationConfig config, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(logger);

        _enabled = config.Enabled;
        _dryRun = config.DryRun;
        _defaultPauseWindow = config.UserInputPauseWindow;
        _logger = logger.ForContext<ActuationGate>();

        _logger.Information(
            "ActuationGate constructed: Enabled={Enabled} DryRun={DryRun} PauseWindow={PauseWindowMinutes}min",
            _enabled,
            _dryRun,
            _defaultPauseWindow.TotalMinutes);
    }

    public ActuationGateState Snapshot()
    {
        _lock.EnterReadLock();
        try
        {
            return SnapshotUnsafe();
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Returns null if the gate is open and we may actuate; otherwise the
    /// rejection code/reason in an <see cref="ActuationResult"/> envelope.
    /// Pass the current dry-run flag back to the caller so a single read
    /// covers both decisions.
    /// </summary>
    public ActuationResult? CheckOrReject()
    {
        var now = DateTimeOffset.UtcNow;
        _lock.EnterReadLock();
        try
        {
            if (_killSwitchTrippedUtc is not null)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.KillSwitchTripped,
                    $"local kill switch tripped at {_killSwitchTrippedUtc:o}",
                    _dryRun);
            }
            if (_compromiseDetected)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.CompromiseDetected,
                    "local compromise reflex is active",
                    _dryRun);
            }
            if (!_enabled)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.GateDisabled,
                    "actuation gate is disabled (config or operator action)",
                    _dryRun);
            }
            if (_pausedUntilUtc is not null && _pausedUntilUtc.Value > now)
            {
                return ActuationResult.Reject(
                    ActuationRejectionCodes.GatePaused,
                    $"paused until {_pausedUntilUtc:o} ({_pauseReason ?? "user_input_detected"})",
                    _dryRun);
            }
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Atomic live-actuation check. Unlike <see cref="CheckOrReject"/>, this also
    /// rejects forced dry-run and a latched compromise. UIA workflows that cannot
    /// truthfully simulate their effects (notably PricingWorkflow) must call this
    /// immediately before every mutating UIA operation.
    /// </summary>
    public ActuationResult? CheckLiveOrReject()
    {
        _lock.EnterReadLock();
        try
        {
            return LiveRejectionUnsafe(DateTimeOffset.UtcNow);
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    /// <summary>
    /// Linearizable live-mutation boundary. The read lock is held from the
    /// five-axis decision through the one short UIA/keyboard mutation. A pause,
    /// kill, disable, dry-run transition, or compromise obtains the write lock,
    /// so it is ordered either wholly before this mutation (which is rejected)
    /// or wholly after it. There is no check-then-act window where a mutation can
    /// begin after the gate has closed.
    /// </summary>
    public ActuationResult? ExecuteLiveMutationOrReject(Action mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        _lock.EnterReadLock();
        try
        {
            var rejection = LiveRejectionUnsafe(DateTimeOffset.UtcNow);
            if (rejection is not null) return rejection;
            mutation();
            return null;
        }
        finally
        {
            _lock.ExitReadLock();
        }
    }

    private ActuationGateState SnapshotUnsafe() => new(
        Enabled: _enabled,
        DryRun: _dryRun,
        PausedUntilUtc: _pausedUntilUtc,
        PauseReason: _pauseReason,
        KillSwitchTrippedUtc: _killSwitchTrippedUtc,
        CompromiseDetected: _compromiseDetected,
        CompromiseLevel: _compromiseLevel,
        CompromiseReasonLabel: _compromiseReasonLabel,
        CompromiseAtUtc: _compromiseAtUtc);

    private ActuationResult? LiveRejectionUnsafe(DateTimeOffset now)
    {
        var code = LiveActuationGatePolicy.RejectionCode(SnapshotUnsafe(), now);
        if (code is null) return null;

        var reason = code switch
        {
            ActuationRejectionCodes.KillSwitchTripped =>
                $"local kill switch tripped at {_killSwitchTrippedUtc:o}",
            ActuationRejectionCodes.CompromiseDetected => "local compromise reflex is active",
            ActuationRejectionCodes.GateDisabled =>
                "actuation gate is disabled (config or operator action)",
            ActuationRejectionCodes.GatePaused =>
                $"paused until {_pausedUntilUtc:o} ({_pauseReason ?? "user_input_detected"})",
            ActuationRejectionCodes.GateDryRun =>
                "actuation gate requires dry-run; this workflow cannot simulate live UIA mutations",
            _ => "live actuation gate state is unavailable",
        };
        return ActuationResult.Reject(code, reason, _dryRun);
    }

    public bool IsDryRun
    {
        get
        {
            _lock.EnterReadLock();
            try { return _dryRun; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void SetDryRun(bool dryRun)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_dryRun == dryRun) return;
            _dryRun = dryRun;
            _logger.Warning("ActuationGate.DryRun = {DryRun}", dryRun);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void SetEnabled(bool enabled, string reason)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            _logger.Warning("ActuationGate.Enabled = {Enabled} reason={Reason}", enabled, reason);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Trip the local kill switch. Sets Enabled=false and records the
    /// trip timestamp. The audit/forensics layer reads
    /// <see cref="ActuationGateState.KillSwitchTrippedUtc"/> to distinguish
    /// "operator paused via cloud" from "local kill switch fired".
    /// </summary>
    public void TripKillSwitch(string reason)
    {
        var now = DateTimeOffset.UtcNow;
        _lock.EnterWriteLock();
        try
        {
            _enabled = false;
            _killSwitchTrippedUtc ??= now;
            _logger.Warning("KILL SWITCH TRIPPED at {When}: {Reason}", now, reason);
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Stamp a honeytoken-corroborated compromise for the heartbeat self-compromise signal. This does NOT
    /// change the gate (the ApoptosisOrchestrator applies SetDryRun/SetEnabled/TripKillSwitch separately) —
    /// it only records the level + fixed reason category that Core reads via Snapshot()/IPC. Latches "up":
    /// never overwrites a higher recorded level (a late degrade can't mask an apoptosis); the trip time is
    /// stamped once. Unknown labels normalize to <c>unknown_process</c>.
    /// </summary>
    public void RecordHoneytokenCompromise(string level, string reasonLabel)
    {
        _lock.EnterWriteLock();
        try
        {
            if (Rank(level) >= Rank(_compromiseLevel))
            {
                _compromiseLevel = level;
                _compromiseReasonLabel =
                    SuavoAgent.Contracts.Models.HoneytokenReasonLabels.Normalize(
                        reasonLabel);
            }
            _compromiseDetected = true;
            _compromiseAtUtc ??= DateTimeOffset.UtcNow;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    private static int Rank(string? level) => level switch
    {
        "apoptosis" => 2,
        "degrade" => 1,
        _ => 0,
    };

    public void NotifyUserInputDetected(string source)
    {
        var pausedUntil = DateTimeOffset.UtcNow + _defaultPauseWindow;
        _lock.EnterWriteLock();
        try
        {
            // We only extend; never shrink the pause window.
            if (_pausedUntilUtc is null || pausedUntil > _pausedUntilUtc.Value)
            {
                _pausedUntilUtc = pausedUntil;
            }
            _pauseReason = $"user_input:{source}";
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    /// <summary>
    /// Pause actuation until a local operator explicitly resumes it. Observation
    /// stays active. This is the companion UI's reversible control and is kept
    /// separate from <see cref="TripKillSwitch"/>, which intentionally cannot be
    /// reversed inside the running Helper process.
    /// </summary>
    public void PauseUntilResumed()
    {
        _lock.EnterWriteLock();
        try
        {
            _pausedUntilUtc = DateTimeOffset.MaxValue;
            _pauseReason = "operator:companion_control";
            _logger.Warning("ActuationGate paused until local operator resume");
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }

    public void ClearPause()
    {
        _lock.EnterWriteLock();
        try
        {
            _pausedUntilUtc = null;
            _pauseReason = null;
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
