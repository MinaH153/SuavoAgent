namespace SuavoAgent.Verbs;

/// <summary>
/// Outcome of <see cref="IVerb.CheckPreconditions"/>.
/// </summary>
/// <param name="Satisfied">true = proceed to execute; false = abort.</param>
/// <param name="Reason">Human-readable why (populated only when !Satisfied).</param>
/// <param name="Evidence">Key-value evidence hashes supporting the decision, logged to audit chain.</param>
public sealed record VerbPreconditionResult(
    bool Satisfied,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Evidence = null)
{
    public static VerbPreconditionResult Ok(IReadOnlyDictionary<string, string>? evidence = null) =>
        new(true, null, evidence);

    public static VerbPreconditionResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Outcome of <see cref="IVerb.Execute"/>. Contains structured output matching
/// the verb's <see cref="VerbOutputSchema"/>.
/// </summary>
public sealed record VerbExecutionResult(
    bool Success,
    IReadOnlyDictionary<string, object?>? Output = null,
    string? Error = null,
    long DurationMs = 0)
{
    public static VerbExecutionResult Ok(IReadOnlyDictionary<string, object?> output, long durationMs) =>
        new(true, output, null, durationMs);

    public static VerbExecutionResult Fail(string error, long durationMs) =>
        new(false, null, error, durationMs);
}

/// <summary>
/// Outcome of <see cref="IVerb.VerifyPostconditions"/>. If Satisfied=false,
/// the dispatcher invokes the rollback envelope.
/// </summary>
public sealed record VerbPostconditionResult(
    bool Satisfied,
    string? Reason = null,
    IReadOnlyDictionary<string, string>? Evidence = null)
{
    public static VerbPostconditionResult Ok(IReadOnlyDictionary<string, string>? evidence = null) =>
        new(true, null, evidence);

    public static VerbPostconditionResult Fail(string reason) => new(false, reason);
}
