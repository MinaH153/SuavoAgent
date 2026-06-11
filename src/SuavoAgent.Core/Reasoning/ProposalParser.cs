using System.Text.Json;
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
///   "rationale": "Save button visible and skill expects to save."
/// }
/// </code>
/// </summary>
public static class ProposalParser
{
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
            if (root.ValueKind != JsonValueKind.Object) return null;

            // --- action ---------------------------------------------------------
            if (!root.TryGetProperty("action", out var actionEl) ||
                actionEl.ValueKind != JsonValueKind.Object)
                return null;

            if (!actionEl.TryGetProperty("type", out var typeEl) ||
                typeEl.ValueKind != JsonValueKind.String ||
                !Enum.TryParse<RuleActionType>(typeEl.GetString(), out var actionType))
                return null;

            var parameters = new Dictionary<string, string>();
            if (actionEl.TryGetProperty("parameters", out var paramsEl) &&
                paramsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in paramsEl.EnumerateObject())
                {
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        parameters[kv.Name] = kv.Value.GetString() ?? "";
                }
            }

            // --- confidence ----------------------------------------------------
            // Guard ValueKind first: TryGetDouble THROWS (not returns false) on a
            // non-Number element, e.g. a model emitting "confidence":"high".
            if (!root.TryGetProperty("confidence", out var confEl) ||
                confEl.ValueKind != JsonValueKind.Number ||
                !confEl.TryGetDouble(out var confidence))
                return null;

            if (confidence < 0.0 || confidence > 1.0) return null;

            // --- rationale (optional) ------------------------------------------
            string? rationale = null;
            if (root.TryGetProperty("rationale", out var ratEl) &&
                ratEl.ValueKind == JsonValueKind.String)
            {
                rationale = ratEl.GetString();
                // Cap rationale so a runaway model can't blow audit log size.
                if (rationale != null && rationale.Length > 500)
                    rationale = rationale[..500];
            }

            return new InferenceProposal
            {
                Action = new RuleActionSpec
                {
                    Type = actionType,
                    Parameters = parameters,
                },
                Confidence = confidence,
                ModelId = modelId,
                Rationale = rationale,
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
