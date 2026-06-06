using System.Diagnostics;
using Serilog;
using SuavoAgent.Contracts.Ipc;

namespace SuavoAgent.Helper.Actuation;

/// <summary>
/// Centralised guard every actuation primitive consults BEFORE touching Win32.
/// Three independent state axes:
///
///   1. Enabled — master gate. Set false by config OR HotkeyKillSwitch
///      (Ctrl+Shift+Esc). Once tripped locally it stays tripped until the
///      Helper restarts — that's the point of a kill switch.
///   2. DryRun — when true, drivers log the would-be input and return
///      success without actually invoking SendInput.
///   3. PausedUntilUtc — bumped by UserInputObserver every time the
///      pharmacist touches the keyboard or mouse. Defaults to NOW+5 min on
///      any input. WorkflowExecutor checks this between every step.
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
            return new ActuationGateState(
                Enabled: _enabled,
                DryRun: _dryRun,
                PausedUntilUtc: _pausedUntilUtc,
                PauseReason: _pauseReason,
                KillSwitchTrippedUtc: _killSwitchTrippedUtc,
                CompromiseDetected: _compromiseDetected,
                CompromiseLevel: _compromiseLevel,
                CompromiseReasonLabel: _compromiseReasonLabel,
                CompromiseAtUtc: _compromiseAtUtc);
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
    /// it only records the level + PHI-safe reason label that Core reads via Snapshot()/IPC. Latches "up":
    /// never overwrites a higher recorded level (a late degrade can't mask an apoptosis); the trip time is
    /// stamped once. <paramref name="reasonLabel"/> MUST already be PHI-safe (HoneytokenCorroborator output).
    /// </summary>
    public void RecordHoneytokenCompromise(string level, string reasonLabel)
    {
        _lock.EnterWriteLock();
        try
        {
            if (Rank(level) >= Rank(_compromiseLevel))
            {
                _compromiseLevel = level;
                _compromiseReasonLabel = reasonLabel;
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
