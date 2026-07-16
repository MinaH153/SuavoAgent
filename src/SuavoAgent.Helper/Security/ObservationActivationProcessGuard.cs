using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Helper.Security;

/// <summary>
/// Starts only from a valid machine-bound signed lease and permanently cancels
/// the process lifetime when that authority is removed, expires, or becomes
/// unreadable. No observer entry point may run before this guard exists.
/// </summary>
internal sealed class ObservationActivationProcessGuard : IAsyncDisposable
{
    internal const int AuthorityRequiredExitCode = 78;
    internal const int ScopeDeniedExitCode = 79;

    private readonly ObservationActivationRuntimeMonitor _monitor;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _monitorTask;

    private ObservationActivationProcessGuard(ObservationActivationAuthority authority)
    {
        _monitor = new ObservationActivationRuntimeMonitor(authority);
        _monitorTask = Task.Run(() => _monitor.RunAsync(_lifetime.Token));
    }

    public CancellationToken AuthorityLostToken => _monitor.AuthorityLostToken;

    public static bool TryStartProduction(
        out ObservationActivationProcessGuard? guard,
        out string code)
    {
        var identity = ObservationActivationIdentityStore.LoadProduction();
        var authority = new ObservationActivationAuthority(identity: identity);
        return TryStart(authority, out guard, out code);
    }

    internal static bool TryStart(
        ObservationActivationAuthority authority,
        out ObservationActivationProcessGuard? guard,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(authority);
        var snapshot = authority.Refresh();
        code = snapshot.Code;
        if (!snapshot.ObservationEnabled)
        {
            guard = null;
            return false;
        }

        guard = new ObservationActivationProcessGuard(authority);
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        try { await _monitorTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        _lifetime.Dispose();
        _monitor.Dispose();
    }
}
