namespace SuavoAgent.Events;

/// <summary>
/// Publishes structured events to the cloud audit-chain ingest endpoint.
/// Implementations handle HMAC signing + retry + batching + offline buffering.
/// </summary>
public interface IEventPublisher
{
    /// <summary>
    /// Enqueue an event for publication. Returns immediately — actual
    /// network call is async and retried. Events survive process restart
    /// via <see cref="LocalEventQueue"/>.
    /// </summary>
    void Publish(StructuredEvent evt);

    /// <summary>
    /// Drain as many queued events as possible to the cloud. Called by the
    /// heartbeat worker each cycle. Returns the number of events accepted
    /// by cloud ingest.
    /// </summary>
    Task<int> FlushAsync(CancellationToken cancellationToken = default);

    /// <summary>Current queue depth (for diagnostic heartbeat payload).</summary>
    int QueueDepth { get; }
}
