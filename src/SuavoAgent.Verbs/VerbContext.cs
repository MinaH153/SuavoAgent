namespace SuavoAgent.Verbs;

/// <summary>
/// Runtime context passed to verb lifecycle methods. Provides parameters
/// + access to execution services + correlation tracking for audit chain.
/// </summary>
public sealed record VerbContext(
    Guid InvocationId,
    string VerbName,
    string VerbVersion,
    IReadOnlyDictionary<string, object?> Parameters,
    DateTimeOffset ReceivedAt,
    /// <summary>
    /// Cancellation token respecting <see cref="VerbMetadata.MaxExecutionTime"/>.
    /// Execution code MUST honor this.
    /// </summary>
    CancellationToken CancellationToken,
    /// <summary>Service provider for DI-resolved execution primitives (sc.exe, SQL, etc.).</summary>
    IServiceProvider Services);
