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

    /// <summary>
    /// The user message — a compact JSON description of the current state.
    /// Smaller representation = fewer tokens. No prose beyond what the LLM
    /// needs to pick a next action.
    /// </summary>
    internal static string BuildUserMessage(InferenceRequest request)
    {
        var ctx = request.Context;
        // Cap visible elements to the 24 most recent to keep tokens bounded.
        var elements = ctx.VisibleElements.Take(24).ToList();

        var state = new
        {
            skill = ctx.SkillId,
            process = ctx.ProcessName,
            window = ctx.WindowTitle,
            visible_elements = elements,
            operator_idle_ms = ctx.OperatorIdleMs,
            flags = ctx.Flags,
        };
        var allowed = request.AllowedActions
            .OrderBy(a => a.ToString(), StringComparer.Ordinal)
            .Select(a => a.ToString())
            .ToList();

        var payload = new
        {
            state,
            allowed_actions = allowed,
            escalation_reason = request.EscalationReason,
        };

        return "STATE:\n" +
            JsonSerializer.Serialize(payload,
                new JsonSerializerOptions { WriteIndented = false });
    }
}
