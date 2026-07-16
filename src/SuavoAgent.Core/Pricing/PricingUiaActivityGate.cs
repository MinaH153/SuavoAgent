namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Serializes all Core-side use of the live PioneerRx pricing UI. A package
/// approval probe is read-only, but it still shares the Helper command pipe
/// and visible window with an active pricing run, so the two may never overlap.
/// </summary>
internal sealed class PricingUiaActivityGate
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    internal async ValueTask<IDisposable> EnterExecutionAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_gate);
    }

    internal IDisposable? TryEnterBootstrap()
        => _gate.Wait(0) ? new Lease(_gate) : null;

    private sealed class Lease : IDisposable
    {
        private SemaphoreSlim? _gate;

        internal Lease(SemaphoreSlim gate) => _gate = gate;

        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
