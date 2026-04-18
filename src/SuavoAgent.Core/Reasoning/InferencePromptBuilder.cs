using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Builds prompts for Llama-3.2 instruct format. Output is constrained by
/// GBNF grammar (see ActionGrammar), so the prompt only needs to explain
/// WHAT to decide, not HOW to format the response.
///
/// Prompt budget discipline: under 500 tokens total. No per-call few-shots
/// (they inflate token count and shouldn't be needed with a constrained
/// grammar). All context passes through PhiScrubber at the extraction
/// boundary in the Helper before reaching this builder — we never emit
/// patient names, Rx numbers, or medication text.
/// </summary>
public static class InferencePromptBuilder
{
    private const string SystemPrompt =
        "You are a pharmacy automation reasoner. Given the current UI state, " +
        "propose ONE next action as a JSON object. You may only propose actions " +
        "from the allowed list. Set confidence to your certainty: 1.0 if sure, " +
        "lower if uncertain. Explain your reasoning in the rationale field. " +
        "Only propose actions whose target elements are visible in the state.";

    /// <summary>
    /// Builds a Llama-3.2-instruct formatted prompt from an InferenceRequest.
    /// </summary>
    public static string Build(InferenceRequest request)
    {
        var user = BuildUserMessage(request);

        // Llama-3.2 instruct chat template — see the model card.
        var sb = new StringBuilder(512);
        sb.Append("<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n");
        sb.Append(SystemPrompt);
        sb.Append("<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n");
        sb.Append(user);
        sb.Append("<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n");
        return sb.ToString();
    }

    // Per-field length caps — defense-in-depth against unscrubbed callers or
    // runaway state. PHI scrubbing happens at the extraction boundary; these
    // caps bound token usage and limit exposure if a PHI-adjacent value slips
    // through (Codex M-3).
    private const int MaxFieldLen = 200;
    private const int MaxEscalationLen = 500;
    private const int MaxElementNameLen = 100;
    private const int MaxElements = 24;
    private const int MaxFlags = 16;

    /// <summary>
    /// The user message — a compact JSON description of the current state.
    /// Smaller representation = fewer tokens. No prose beyond what the LLM
    /// needs to pick a next action. Every field is length-capped as a HIPAA
    /// trust-boundary defense (Codex M-3).
    /// </summary>
    internal static string BuildUserMessage(InferenceRequest request)
    {
        var ctx = request.Context;

        var elements = ctx.VisibleElements
            .Take(MaxElements)
            .Select(e => Truncate(e, MaxElementNameLen))
            .ToList();

        var cappedFlags = ctx.Flags
            .Take(MaxFlags)
            .ToDictionary(
                kv => Truncate(kv.Key, MaxFieldLen),
                kv => Truncate(kv.Value, MaxFieldLen));

        var state = new
        {
            skill = Truncate(ctx.SkillId, MaxFieldLen),
            process = Truncate(ctx.ProcessName, MaxFieldLen),
            window = Truncate(ctx.WindowTitle, MaxFieldLen),
            visible_elements = elements,
            operator_idle_ms = ctx.OperatorIdleMs,
            flags = cappedFlags,
        };
        var allowed = request.AllowedActions
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .Select(a => a.ToString())
            .ToList();

        var payload = new
        {
            state,
            allowed_actions = allowed,
            escalation_reason = Truncate(request.EscalationReason, MaxEscalationLen),
        };

        return "STATE:\n" +
            JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = false });
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }
}
