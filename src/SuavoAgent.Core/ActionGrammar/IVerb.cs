namespace SuavoAgent.Core.ActionGrammarV1;

/// <summary>
/// The typed-verb contract. Every state-reading or state-changing action the
/// agent can take is an IVerb. Raw shell, ad-hoc SQL, untyped config writes
/// are structurally impossible because nothing else passes through the
/// dispatcher.
///
/// Contract from docs/self-healing/action-grammar-v1.md.
/// </summary>
public interface IVerb
{
    VerbMetadata Metadata { get; }

    Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct);

    Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct);

    Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct);

    Task<VerbPostconditionResult> VerifyPostconditionsAsync(
        VerbContext ctx,
        VerbExecutionResult executionResult,
        CancellationToken ct);
}
