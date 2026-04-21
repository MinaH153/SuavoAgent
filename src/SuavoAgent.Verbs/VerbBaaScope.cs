namespace SuavoAgent.Verbs;

/// <summary>
/// BAA coverage requirement for a verb. Enforced at the verb registry level
/// per <c>invariants.md §I.2 BAA scope enforcement</c>. Structurally rejected
/// BEFORE the operator approval gate sees a verb with insufficient BAA scope.
/// </summary>
public abstract record VerbBaaScope
{
    /// <summary>Verb touches only infrastructure — no BAA interaction required.</summary>
    public sealed record None : VerbBaaScope;

    /// <summary>Verb operates under the standard SuavoAgent BAA with the pharmacy.</summary>
    public sealed record AgentBaa : VerbBaaScope;

    /// <summary>Verb requires a specific BAA amendment to be in force.</summary>
    public sealed record BaaAmendment(string AmendmentId) : VerbBaaScope;

    /// <summary>
    /// Verb is forbidden against this pharmacy (contractual or regulatory block).
    /// </summary>
    public sealed record Forbidden(string Reason) : VerbBaaScope;

    public static readonly VerbBaaScope NoneInstance = new None();
    public static readonly VerbBaaScope AgentBaaInstance = new AgentBaa();
}
