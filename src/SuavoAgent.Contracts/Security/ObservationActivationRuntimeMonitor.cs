namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Continuously revalidates the machine-wide observation lease. Once authority
/// is lost the token is cancelled permanently; a later lease cannot revive an
/// already-running observer process. The process must restart through the same
/// startup gate so every capability transition has one fail-closed path.
/// </summary>
public sealed class ObservationActivationRuntimeMonitor : IDisposable
{
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<ObservationActivationSnapshot> _refresh;
    private readonly ObservationActivationAuthority? _authority;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _authorityLost = new();
    private readonly object _sync = new();
    private bool _started;
    private bool _disposed;
    private string _stopCode = ObservationActivationCodes.Active;

    public ObservationActivationRuntimeMonitor(
        ObservationActivationAuthority authority,
        TimeSpan? pollInterval = null)
        : this(authority.Refresh, pollInterval)
    {
        ArgumentNullException.ThrowIfNull(authority);
        _authority = authority;
        _authority.AuthorityLost += SignalAuthorityLost;
    }

    internal ObservationActivationRuntimeMonitor(
        Func<ObservationActivationSnapshot> refresh,
        TimeSpan? pollInterval = null)
    {
        ArgumentNullException.ThrowIfNull(refresh);
        _refresh = refresh;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        if (_pollInterval < TimeSpan.FromMilliseconds(10) ||
            _pollInterval > TimeSpan.FromSeconds(1))
            throw new ArgumentOutOfRangeException(nameof(pollInterval));
    }

    public CancellationToken AuthorityLostToken => _authorityLost.Token;

    public string StopCode
    {
        get { lock (_sync) return _stopCode; }
    }

    public async Task RunAsync(CancellationToken lifetime)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) throw new InvalidOperationException("Activation monitor is already running.");
            _started = true;
        }

        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                ObservationActivationSnapshot snapshot;
                try
                {
                    snapshot = _refresh();
                }
                catch
                {
                    SignalAuthorityLost(ObservationActivationCodes.StateInvalid);
                    return;
                }

                if (!snapshot.ObservationEnabled)
                {
                    SignalAuthorityLost(snapshot.Code);
                    return;
                }

                await Task.Delay(_pollInterval, lifetime).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
        {
            // Normal process shutdown does not represent a lease failure.
        }
    }

    private void SignalAuthorityLost(string code)
    {
        lock (_sync) _stopCode = code;
        try { _authorityLost.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        if (_authority is not null)
            _authority.AuthorityLost -= SignalAuthorityLost;
        _authorityLost.Dispose();
    }
}
