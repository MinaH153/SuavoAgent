// Namespace deliberately differs from the folder name: the folder is
// `ActionGrammar/` (matches docs/self-healing/action-grammar-v1.md), but the
// existing `SuavoAgent.Core.Reasoning.ActionGrammar` static class would be
// shadowed by a namespace of the same short name. Keeping namespace and
// folder decoupled avoids ambiguity without renaming the folder the spec
// pins.
namespace SuavoAgent.Core.ActionGrammarV1;

/// <summary>
/// Action-grammar v1 scaffolding — the typed-verb contract every state-changing
/// code path on a pharmacy workstation will route through.
///
/// Spec: docs/self-healing/action-grammar-v1.md (locked 2026-04-21).
///
/// Scaffolding only. No verbs are registered or dispatched yet. Concrete verbs
/// land under <c>SuavoAgent.Core/ActionGrammar/Verbs/</c> post-Nadim pilot.
/// </summary>
public abstract record VerbDefinition(
    string VerbId,
    string Category,
    IReadOnlyList<VerbPrecondition> Preconditions,
    IReadOnlyList<VerbPostcondition> Postconditions,
    bool RequiresApproval
);

public abstract record VerbPrecondition(string Id, string Expression, string FailureMessage);

public abstract record VerbPostcondition(string Id, string Expression);

/// <summary>
/// Raised by the dispatcher when a verb's precondition fails structural
/// evaluation. Carries the specific precondition id so audit events can pin
/// the refusal to a named rule.
/// </summary>
public sealed class VerbPreconditionNotMetException : Exception
{
    public string PreconditionId { get; }

    public VerbPreconditionNotMetException(string id, string msg) : base(msg)
    {
        PreconditionId = id;
    }

    public VerbPreconditionNotMetException(string id, string msg, Exception inner) : base(msg, inner)
    {
        PreconditionId = id;
    }
}
