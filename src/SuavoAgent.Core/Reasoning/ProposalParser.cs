using System.Collections.Frozen;
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Parses the local LLM's JSON output into an InferenceProposal.
///
/// LOAD-BEARING DEFENSE: the GBNF grammar is a sampler-level constraint that is
/// a NO-OP on the win-x64 llama.cpp build (documented 2026-06-05). So the JSON
/// guarantee actually rests on (1) a model-matched chat template that makes the
/// model emit valid JSON unassisted, and (2) THIS parser, which schema-validates
/// every field and returns null on any mismatch so the caller cleanly escalates
/// — a bad proposal never executes. Treat this parser as the real guard, not the
/// grammar.
///
/// Expected shape:
/// <code>
/// {
///   "action": {
///     "type": "Click",
///     "parameters": { "name": "Save" }
///   },
///   "confidence": 0.95,
///   "rationaleCode": "target_present"
/// }
/// </code>
/// </summary>
public static class ProposalParser
{
    private static readonly Regex ParameterKeyPattern = new(
        @"^[A-Za-z][A-Za-z0-9_]{0,31}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static InferenceProposal? TryParse(string json, string modelId, long latencyMs)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        // The grammar allows trailing whitespace but some samplers also emit
        // stop tokens or extra newlines after the close brace; trim proactively.
        json = json.Trim();

        // Qwen3 hybrid can still emit a leading reasoning block when the runtime
        // ignores the empty-think prefill. Strip only that model-native wrapper;
        // prose-wrapped JSON still fails strict parsing below.
        json = StripLeadingThinkBlock(json);

        // Instruct models (Phi-3.5 / Qwen2.5) sometimes wrap the object in a
        // markdown code fence (```json … ```). Strip a single enclosing fence so
        // the schema validation below still runs. We do NOT mine JSON out of free
        // prose — that stays a parse failure (escalate, never guess).
        json = StripCodeFence(json);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!HasExactKeys(root, "action", "confidence", "rationaleCode"))
                return null;

            // --- action ---------------------------------------------------------
            if (!root.TryGetProperty("action", out var actionEl) ||
                !HasExactKeys(actionEl, "type", "parameters"))
                return null;

            if (!actionEl.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<RuleActionType>(typeEl.GetString(), out var actionType) ||
                Enum.GetName(actionType) != typeEl.GetString())
                return null;

            if (!actionEl.TryGetProperty("parameters", out var paramsEl) ||
                paramsEl.ValueKind != JsonValueKind.Object)
                return null;
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in paramsEl.EnumerateObject())
            {
                var value = kv.Value.ValueKind == JsonValueKind.String
                    ? kv.Value.GetString()
                    : null;
                if (parameters.Count >= 16 ||
                    !ParameterKeyPattern.IsMatch(kv.Name) ||
                    kv.Value.ValueKind != JsonValueKind.String ||
                    value is null || value.Length > 200 ||
                    !parameters.TryAdd(kv.Name, value))
                    return null;
            }
            if (!InferenceActionParameterContract.IsExact(actionType, parameters))
                return null;

            // --- confidence ----------------------------------------------------
            // Guard ValueKind first: TryGetDouble THROWS (not returns false) on a
            // non-Number element, e.g. a model emitting "confidence":"high".
            if (!root.TryGetProperty("confidence", out var confEl) ||
                confEl.ValueKind != JsonValueKind.Number ||
                !confEl.TryGetDouble(out var confidence))
                return null;

            if (!double.IsFinite(confidence) || confidence < 0.0 || confidence > 1.0)
                return null;

            // --- fixed rationale code (required) -------------------------------
            if (!root.TryGetProperty("rationaleCode", out var rationaleEl) ||
                rationaleEl.ValueKind != JsonValueKind.String ||
                !InferenceRationaleCodeCodec.TryParseWireValue(
                    rationaleEl.GetString(), out var rationaleCode))
                return null;

            return new InferenceProposal
            {
                Action = new RuleActionSpec
                {
                    Type = actionType,
                    Parameters = parameters.ToFrozenDictionary(
                        StringComparer.Ordinal),
                },
                Confidence = confidence,
                ModelId = modelId,
                RationaleCode = rationaleCode,
                LatencyMs = latencyMs,
            };
        }
        catch (JsonException)
        {
            // Malformed JSON despite grammar constraint — return null so the
            // caller can escalate rather than crash.
            return null;
        }
    }

    private static bool HasExactKeys(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        var names = element.EnumerateObject()
            .Select(property => property.Name)
            .ToArray();
        return names.Length == expected.Length &&
            names.Distinct(StringComparer.Ordinal).Count() == names.Length &&
            names.ToHashSet(StringComparer.Ordinal).SetEquals(expected);
    }

    /// <summary>
    /// Strips a single enclosing markdown code fence (```json … ``` or ``` … ```)
    /// if present. Returns the input unchanged when there is no leading fence, so
    /// prose-wrapped output still fails the strict JSON parse and escalates.
    /// </summary>
    private static string StripCodeFence(string s)
    {
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;

        // Drop the opening fence line (``` or ```json + newline).
        var firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        var body = s[(firstNewline + 1)..];

        // Drop a trailing closing fence if one remains.
        var lastFence = body.LastIndexOf("```", StringComparison.Ordinal);
        if (lastFence >= 0) body = body[..lastFence];

        return body.Trim();
    }

    private static string StripLeadingThinkBlock(string s)
    {
        if (!s.StartsWith("<think>", StringComparison.OrdinalIgnoreCase)) return s;

        var end = s.IndexOf("</think>", StringComparison.OrdinalIgnoreCase);
        if (end < 0) return s;

        return s[(end + "</think>".Length)..].TrimStart();
    }
}
