using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Parses grammar-constrained JSON output from the local LLM into an
/// InferenceProposal. The grammar guarantees schema conformance at the
/// sampler level, but this parser is a second line of defense — any parse
/// failure returns null and the caller cleanly escalates.
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
            if (!root.TryGetProperty("confidence", out var confEl) ||
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
}
