using System.Text;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Builds GBNF (GGML BNF) grammar strings that constrain llama.cpp output to
/// the RuleActionSpec JSON schema. Grammar-constrained decoding makes it
/// IMPOSSIBLE for the model to emit tokens outside the schema — the sampler
/// masks off-grammar candidates. This is the single strongest hallucination
/// defense available for local models.
///
/// Grammar spec: https://github.com/ggerganov/llama.cpp/blob/master/grammars/README.md
/// </summary>
public static class ActionGrammar
{
    /// <summary>
    /// Builds a grammar that allows exactly ONE JSON object matching:
    /// <code>
    /// {
    ///   "action": { "type": "...", "parameters": {...} },
    ///   "confidence": 0.0-1.0,
    ///   "rationale": "..."
    /// }
    /// </code>
    /// Action types are restricted to <paramref name="allowedActions"/>.
    /// </summary>
    public static string BuildProposalGrammar(IReadOnlySet<RuleActionType> allowedActions)
    {
        if (allowedActions.Count == 0)
            throw new ArgumentException("At least one action type must be allowed", nameof(allowedActions));

        var sb = new StringBuilder();

        // Root: one object, no preamble/epilogue. llama.cpp stops at root end.
        sb.AppendLine("root ::= proposal");
        sb.AppendLine();

        // proposal := { "action": <action>, "confidence": <num>, "rationale": <string> }
        sb.AppendLine("proposal ::= \"{\" ws \"\\\"action\\\":\" ws action ws \",\" ws \"\\\"confidence\\\":\" ws number ws \",\" ws \"\\\"rationale\\\":\" ws string ws \"}\"");
        sb.AppendLine();

        // action := { "type": <type>, "parameters": <params> }
        sb.AppendLine("action ::= \"{\" ws \"\\\"type\\\":\" ws type ws \",\" ws \"\\\"parameters\\\":\" ws params ws \"}\"");
        sb.AppendLine();

        // type := one of the allowed literal strings
        sb.Append("type ::= ");
        var first = true;
        foreach (var allowed in allowedActions.OrderBy(a => a.ToString(), StringComparer.Ordinal))
        {
            if (!first) sb.Append(" | ");
            sb.Append($"\"\\\"{allowed}\\\"\"");
            first = false;
        }
        sb.AppendLine();
        sb.AppendLine();

        // params := {} | { "key": string (, "key": string)* }  (max 6 entries — hard cap)
        sb.AppendLine("params ::= \"{\" ws (kv (ws \",\" ws kv){0,5})? ws \"}\"");
        sb.AppendLine("kv ::= string ws \":\" ws string");
        sb.AppendLine();

        // JSON primitives
        sb.AppendLine("string ::= \"\\\"\" char* \"\\\"\"");
        sb.AppendLine("char ::= [^\"\\\\\\x00-\\x1f] | \"\\\\\" escape");
        sb.AppendLine("escape ::= [\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4}");
        sb.AppendLine();

        // Confidence: 0.NN or 1.0
        sb.AppendLine("number ::= \"0\" (\".\" [0-9]{1,3})? | \"1\" (\".0\" \"0\"?)?");
        sb.AppendLine();

        // Whitespace
        sb.AppendLine("ws ::= [ \\t\\n]*");

        return sb.ToString();
    }

    /// <summary>
    /// Lightweight syntax check — not a full GBNF parser, but catches the
    /// obvious breakages (unmatched quotes, empty rules) so tests don't have
    /// to shell out to llama.cpp to validate every grammar build.
    /// </summary>
    public static bool LooksWellFormed(string grammar)
    {
        if (string.IsNullOrWhiteSpace(grammar)) return false;
        if (!grammar.Contains("root ::=")) return false;
        if (grammar.Count(c => c == '"') % 2 != 0) return false;

        // Every `::=` must have a non-empty LHS + RHS on the same line
        foreach (var line in grammar.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.Contains("::=")) continue;
            var parts = trimmed.Split("::=", 2, StringSplitOptions.None);
            if (parts.Length != 2) return false;
            if (string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                return false;
        }

        return true;
    }
}
