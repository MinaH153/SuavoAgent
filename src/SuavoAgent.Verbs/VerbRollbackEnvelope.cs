namespace SuavoAgent.Verbs;

/// <summary>
/// The paired inverse action captured by a verb BEFORE its execution runs.
/// Per <c>docs/self-healing/action-grammar-v1.md §Rollback envelopes</c>.
/// Emitted as a <c>verb.rollback_captured</c> event so cloud has durable
/// record before any state changes.
/// </summary>
/// <param name="VerbInvocationId">Links to the <see cref="VerbContext.InvocationId"/>.</param>
/// <param name="InverseActionType">Human-readable label for the inverse action (for audit display).</param>
/// <param name="PreState">Snapshot of what is being mutated (registry value, file hash, service state).</param>
/// <param name="InverseFn">Code that reverts. Runs under the SAME CancellationToken as the forward action.</param>
/// <param name="MaxInverseDuration">Watchdog on the rollback itself — exceeded = escalate to operator.</param>
/// <param name="Evidence">SHA-256 of pre-state. Verifier can re-hash post-rollback to prove undo success.</param>
public sealed record VerbRollbackEnvelope(
    Guid VerbInvocationId,
    string InverseActionType,
    IReadOnlyDictionary<string, object?> PreState,
    Func<VerbContext, Task> InverseFn,
    TimeSpan MaxInverseDuration,
    string Evidence);
