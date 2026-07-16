using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Core.Autonomy;

/// <summary>Every Core path capable of causing Helper-side mutation.</summary>
public enum AutopilotRunKind
{
    Pricing,
    Workflow,
    Navigation,
    DeliveryWriteback,
}

/// <summary>Local human controls. Stop is intentionally irreversible until restart.</summary>
public enum AutopilotControlAction
{
    Pause,
    Resume,
    Stop,
}

public sealed record AutopilotRuntimeState(
    long ControlGeneration,
    bool Paused,
    bool Stopped,
    int ActiveRunCount,
    IReadOnlyList<AutopilotRunKind> ActiveKinds);

public sealed record AutopilotControlReceipt(
    long ControlGeneration,
    AutopilotControlAction Action,
    bool Applied,
    string Code,
    string ReasonCode,
    bool Paused,
    bool Stopped,
    int SignalledRunCount,
    int CancellationSignalFailureCount,
    IReadOnlyList<AutopilotRunKind> SignalledKinds,
    DateTimeOffset AppliedAtUtc);

public sealed record AutopilotRunCancellationReceipt(
    AutopilotRunKind Kind,
    int SignalledRunCount,
    int CancellationSignalFailureCount);

public sealed record AutopilotAuthorityLossReceipt(
    int SignalledRunCount,
    int CancellationSignalFailureCount,
    IReadOnlyList<AutopilotRunKind> SignalledKinds);

/// <summary>
/// One process-wide cancellation and admission constitution for every
/// Autopilot execution path. The Helper remains the authoritative no-more-
/// mutations gate; this Core coordinator additionally stops perception,
/// reasoning, retries, and future task admission after a local human control.
/// </summary>
public sealed class AutopilotRunCoordinator
{
    private readonly object _sync = new();
    private readonly Dictionary<long, ActiveRun> _active = [];
    private readonly Func<DateTimeOffset> _clock;
    private readonly ObservationActivationIdentity? _observationIdentity;
    private readonly string? _observationControlPath;
    private readonly Action? _revokeObservationAuthority;
    private readonly Func<bool>? _isObservationAuthorized;
    private long _nextLeaseId;
    private long _controlGeneration;
    private bool _paused;
    private bool _stopped;

    public AutopilotRunCoordinator(Func<DateTimeOffset>? clock = null)
        : this(null, null, clock)
    { }

    public AutopilotRunCoordinator(
        ObservationActivationIdentity? observationIdentity,
        string? observationControlPath,
        Func<DateTimeOffset>? clock = null,
        Action? revokeObservationAuthority = null,
        Func<bool>? isObservationAuthorized = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _observationIdentity = observationIdentity;
        _observationControlPath = observationControlPath;
        _revokeObservationAuthority = revokeObservationAuthority;
        _isObservationAuthorized = isObservationAuthorized;
        if (observationIdentity is not null && !string.IsNullOrWhiteSpace(observationControlPath))
        {
            var persisted = ObservationControlStateStore.Load(
                observationControlPath,
                observationIdentity);
            _controlGeneration = persisted.Generation;
            _paused = persisted.Paused;
            _stopped = persisted.Stopped;
        }
    }

    public AutopilotRunLease Register(
        AutopilotRunKind kind,
        CancellationToken parentToken)
    {
        var linked = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        var observationAuthorized = false;
        try { observationAuthorized = _isObservationAuthorized?.Invoke() ?? true; }
        catch { }
        long leaseId;
        string? rejectionCode;

        lock (_sync)
        {
            leaseId = ++_nextLeaseId;
            rejectionCode = _stopped
                ? "autopilot_stopped"
                : _paused
                    ? "autopilot_paused"
                    : !observationAuthorized
                        ? ObservationActivationCodes.StateMissing
                    : null;
            if (rejectionCode is null)
                _active.Add(leaseId, new ActiveRun(kind, linked));
        }

        if (rejectionCode is not null)
            TryCancel(linked);

        return new AutopilotRunLease(
            this,
            leaseId,
            kind,
            linked,
            admitted: rejectionCode is null,
            rejectionCode);
    }

    public AutopilotControlReceipt ApplyControl(
        AutopilotControlAction action,
        string reasonCode) =>
        ApplyControlInternal(action, reasonCode, expectedGeneration: null, requireResumeGeneration: false);

    /// <summary>
    /// Local UI controls use generation-bound resume so a delayed or replayed
    /// old Resume can never reopen Autopilot after a newer Pause or Stop.
    /// </summary>
    public AutopilotControlReceipt ApplyLocalControl(
        AutopilotControlAction action,
        string reasonCode,
        long? expectedGeneration) =>
        ApplyControlInternal(action, reasonCode, expectedGeneration, requireResumeGeneration: true);

    private AutopilotControlReceipt ApplyControlInternal(
        AutopilotControlAction action,
        string reasonCode,
        long? expectedGeneration,
        bool requireResumeGeneration)
    {
        var safeReasonCode = NormalizeReasonCode(reasonCode);
        List<ActiveRun> runsToCancel = [];
        bool applied;
        string code;
        long generation;
        bool paused;
        bool stopped;
        var mustRevokeObservationAuthority = false;

        lock (_sync)
        {
            if (action == AutopilotControlAction.Resume
                && requireResumeGeneration
                && expectedGeneration != _controlGeneration)
            {
                return new AutopilotControlReceipt(
                    _controlGeneration,
                    action,
                    false,
                    "control_generation_mismatch",
                    safeReasonCode,
                    _paused,
                    _stopped,
                    0,
                    0,
                    [],
                    _clock());
            }

            var previousGeneration = _controlGeneration;
            generation = ++_controlGeneration;
            switch (action)
            {
                case AutopilotControlAction.Pause:
                    _paused = true;
                    applied = true;
                    code = "paused";
                    runsToCancel.AddRange(_active.Values);
                    break;
                case AutopilotControlAction.Resume when _stopped:
                    applied = false;
                    code = "stop_latched";
                    break;
                case AutopilotControlAction.Resume:
                    _paused = false;
                    applied = true;
                    code = "resumed";
                    break;
                case AutopilotControlAction.Stop:
                    _paused = true;
                    _stopped = true;
                    applied = true;
                    code = "stopped";
                    runsToCancel.AddRange(_active.Values);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action));
            }

            if (_observationIdentity is not null &&
                !string.IsNullOrWhiteSpace(_observationControlPath) &&
                (!ObservationControlStateStore.TryTransition(
                     _observationControlPath,
                     _observationIdentity,
                     previousGeneration,
                     _paused,
                     _stopped,
                     _clock(),
                     out var persistedGeneration) ||
                 persistedGeneration != generation))
            {
                // Keep the in-memory generation coherent with the durable file.
                // Resume may never succeed only in memory. Pause/Stop failure
                // additionally trips the machine-wide emergency circuit below.
                _controlGeneration = previousGeneration;
                generation = previousGeneration;
                _paused = true;
                if (action == AutopilotControlAction.Stop) _stopped = true;
                applied = false;
                code = "observation_control_persistence_failed";
                mustRevokeObservationAuthority = action is
                    AutopilotControlAction.Pause or AutopilotControlAction.Stop;
            }

            paused = _paused;
            stopped = _stopped;
        }

        if (mustRevokeObservationAuthority)
        {
            ObservationEmergencyStop.Latch();
            try { _revokeObservationAuthority?.Invoke(); }
            catch
            {
                // The named emergency circuit is already latched. Never turn a
                // failed durable Pause into a successful acknowledgement.
            }
        }

        var failures = 0;
        foreach (var run in runsToCancel)
        {
            if (!TryCancel(run.Cancellation)) failures++;
        }

        return new AutopilotControlReceipt(
            generation,
            action,
            applied,
            code,
            safeReasonCode,
            paused,
            stopped,
            runsToCancel.Count,
            failures,
            runsToCancel.Select(run => run.Kind).Distinct().Order().ToArray(),
            _clock());
    }

    /// <summary>
    /// Cancels every active run and closes admission when the signed machine
    /// lease disappears or expires. This is deliberately in-memory only: a
    /// control-plane lease expiry is not an operator-authored persistent Pause.
    /// </summary>
    public AutopilotAuthorityLossReceipt EnforceObservationAuthorityLost()
    {
        List<ActiveRun> runs;
        lock (_sync)
        {
            runs = _active.Values.ToList();
        }

        var failures = runs.Count(run => !TryCancel(run.Cancellation));
        return new(
            runs.Count,
            failures,
            runs.Select(run => run.Kind).Distinct().Order().ToArray());
    }

    public AutopilotRuntimeState Snapshot()
    {
        lock (_sync)
        {
            return new AutopilotRuntimeState(
                _controlGeneration,
                _paused,
                _stopped,
                _active.Count,
                _active.Values.Select(run => run.Kind).Distinct().Order().ToArray());
        }
    }

    /// <summary>
    /// Cancels one authority class without changing the operator's global
    /// Pause/Stop state. Used when an exact signed grant is revoked while a
    /// detached run is active.
    /// </summary>
    public AutopilotRunCancellationReceipt CancelRuns(
        AutopilotRunKind kind)
    {
        List<ActiveRun> matching;
        lock (_sync)
        {
            matching = _active.Values
                .Where(run => run.Kind == kind)
                .ToList();
        }

        var failures = matching.Count(run => !TryCancel(run.Cancellation));
        return new AutopilotRunCancellationReceipt(
            kind,
            matching.Count,
            failures);
    }

    private void Release(long leaseId, CancellationTokenSource cancellation)
    {
        lock (_sync)
        {
            if (_active.TryGetValue(leaseId, out var active) &&
                ReferenceEquals(active.Cancellation, cancellation))
            {
                _active.Remove(leaseId);
            }
        }
        cancellation.Dispose();
    }

    private static bool TryCancel(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel(throwOnFirstException: false);
            return true;
        }
        catch
        {
            // A hostile/broken callback cannot prevent other active paths from
            // receiving cancellation. The structural receipt counts failures.
            return false;
        }
    }

    internal static string NormalizeReasonCode(string? reasonCode)
    {
        if (string.IsNullOrWhiteSpace(reasonCode) || reasonCode.Length > 64)
            return "local_operator_control";
        foreach (var ch in reasonCode)
        {
            if (ch is not (>= 'a' and <= 'z') and not (>= '0' and <= '9') and not '_')
                return "local_operator_control";
        }
        return reasonCode;
    }

    private sealed record ActiveRun(
        AutopilotRunKind Kind,
        CancellationTokenSource Cancellation);

    public sealed class AutopilotRunLease : IDisposable
    {
        private readonly AutopilotRunCoordinator _owner;
        private readonly long _leaseId;
        private CancellationTokenSource? _cancellation;

        internal AutopilotRunLease(
            AutopilotRunCoordinator owner,
            long leaseId,
            AutopilotRunKind kind,
            CancellationTokenSource cancellation,
            bool admitted,
            string? rejectionCode)
        {
            _owner = owner;
            _leaseId = leaseId;
            Kind = kind;
            _cancellation = cancellation;
            Admitted = admitted;
            RejectionCode = rejectionCode;
        }

        public AutopilotRunKind Kind { get; }
        public bool Admitted { get; }
        public string? RejectionCode { get; }
        public CancellationToken Token =>
            _cancellation?.Token ?? new CancellationToken(canceled: true);

        public void Dispose()
        {
            var cancellation = Interlocked.Exchange(ref _cancellation, null);
            if (cancellation is not null)
                _owner.Release(_leaseId, cancellation);
        }
    }
}
