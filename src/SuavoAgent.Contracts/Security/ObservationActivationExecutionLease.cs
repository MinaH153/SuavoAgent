namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Cancellation boundary for one Core observation operation. Revocation,
/// expiry, pause, corruption, or a failed cloud refresh cancels active work;
/// a later valid lease can authorize a new operation without reviving the old.
/// </summary>
public sealed class ObservationActivationExecutionLease : IDisposable
{
    private readonly ObservationActivationAuthority _authority;
    private readonly CancellationTokenSource _cancellation;
    private int _disposed;

    internal ObservationActivationExecutionLease(
        ObservationActivationAuthority authority,
        CancellationToken parent)
    {
        _authority = authority;
        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(parent);
        _authority.AuthorityLost += OnAuthorityLost;
    }

    public CancellationToken Token => _cancellation.Token;

    private void OnAuthorityLost(string _) => _cancellation.Cancel();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _authority.AuthorityLost -= OnAuthorityLost;
        _cancellation.Dispose();
    }
}
