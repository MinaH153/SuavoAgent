namespace SuavoAgent.Verbs.Signing;

/// <summary>
/// Thread-safe current-fence-ID holder. Cloud-side kill switch updates fence
/// ID by rotating the accepted value; in-flight commands signed against the
/// old fence ID are rejected by <see cref="VerbDispatcher"/>.
///
/// See docs/self-healing/invariants.md §I.6 Kill switch + safety.
/// </summary>
public sealed class FenceProvider : IFenceProvider
{
    private readonly object _lock = new();
    private Guid _currentFenceId;

    public FenceProvider(Guid initialFenceId) => _currentFenceId = initialFenceId;

    public Guid CurrentFenceId
    {
        get { lock (_lock) return _currentFenceId; }
    }

    public void Rotate(Guid newFenceId)
    {
        lock (_lock) _currentFenceId = newFenceId;
    }
}
