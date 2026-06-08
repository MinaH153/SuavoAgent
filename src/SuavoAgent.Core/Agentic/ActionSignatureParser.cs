using System;
using System.Collections.Generic;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Inverse of <see cref="NextAction.Signature()"/> — reconstructs an executable <see cref="NextAction"/>
/// from a banked signature string (<c>verb(k=v,k2=v2)</c>). Used by <see cref="VerifiedSkillReplayer"/> to
/// turn a stored <see cref="VerifiedStep.ActionSignature"/> back into something the actuator can run.
///
/// <para>FAIL-CLOSED + lossless-or-nothing: a signature whose value contains a delimiter (<c>,</c> or
/// <c>=</c>) cannot round-trip exactly, so the caller MUST verify <c>TryParse(sig)?.Signature() == sig</c>
/// before acting — a misparse then reconstructs a DIFFERENT signature and is rejected rather than executed.
/// Only <c>Act</c> signatures parse; terminal Done/Escalate (no parens) return null.</para>
/// </summary>
public static class ActionSignatureParser
{
    public static NextAction? TryParse(string? signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;

        var open = signature.IndexOf('(');
        if (open <= 0 || signature[^1] != ')') return null; // must be verb(...) — not a terminal kind
        var verb = signature[..open];
        var inner = signature[(open + 1)..^1];

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (inner.Length > 0)
        {
            foreach (var pair in inner.Split(','))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) return null;                 // malformed token
                var key = pair[..eq];
                var value = pair[(eq + 1)..];
                if (!parameters.TryAdd(key, value)) return null; // duplicate key — ambiguous
            }
        }

        return NextAction.Act(verb, parameters);
    }
}
