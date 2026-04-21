namespace SuavoAgent.Verbs;

/// <summary>
/// The typed-verb contract. Every state-changing action on the agent goes
/// through this interface. Per
/// <c>docs/self-healing/action-grammar-v1.md §Verb schema</c>.
///
/// Lifecycle (enforced by <see cref="VerbDispatcher"/>):
/// 1. Precondition check — prove diagnosis by reading state
/// 2. Capture rollback envelope — snapshot pre-state
/// 3. Execute — the forward action
/// 4. Verify postconditions — prove the action achieved its goal
/// 5. On postcondition failure, invoke rollback
///
/// Every step emits an audit event. No step is optional.
/// </summary>
public interface IVerb
{
    /// <summary>Self-describing metadata. Stable identity for the verb implementation.</summary>
    VerbMetadata Metadata { get; }

    /// <summary>Verify that the verb is safe + sensible to execute given current agent state.</summary>
    Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx);

    /// <summary>Capture the inverse-action envelope BEFORE executing.</summary>
    Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx);

    /// <summary>Run the forward action.</summary>
    Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx);

    /// <summary>Verify the verb achieved its intended post-state.</summary>
    Task<VerbPostconditionResult> VerifyPostconditionsAsync(VerbContext ctx);
}
