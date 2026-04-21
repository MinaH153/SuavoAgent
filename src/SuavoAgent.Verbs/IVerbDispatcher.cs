namespace SuavoAgent.Verbs;

/// <summary>
/// Executes a signed verb invocation through the full lifecycle:
/// signature → schema → fence → precondition → rollback capture → execute →
/// postcondition → (rollback on failure) → audit.
/// </summary>
public interface IVerbDispatcher
{
    Task<VerbDispatchResult> DispatchAsync(SignedVerbInvocation invocation, CancellationToken cancellationToken);
}

/// <summary>Provides the current fence ID (for kill-switch enforcement).</summary>
public interface IFenceProvider
{
    Guid CurrentFenceId { get; }
}

/// <summary>Verifies HMAC signatures on inbound signed messages.</summary>
public interface ISignatureVerifier
{
    bool Verify(SignedVerbInvocation invocation);
}
