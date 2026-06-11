using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Core.Reasoning;

/// <summary>Chat templates for the on-device models we run. Must match the model's training format.</summary>
public enum ChatPromptFormat
{
    Llama3,
    Zephyr,
    /// <summary>Phi-3 / Phi-3.5.</summary>
    Phi,
    /// <summary>ChatML — Qwen2.5 / SmolLM2 / Qwen3-*-Instruct-2507 (non-thinking by design).</summary>
    ChatML,
    /// <summary>Qwen3 HYBRID (e.g. 1.7B) forced NON-thinking: ChatML + trailing /no_think.</summary>
    Qwen3Thinkless,
    Plain,
}

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
    // The schema is SPELLED OUT here because the GBNF grammar is a no-op on win-x64 — the template alone
    // must elicit the EXACT nested shape ProposalParser expects ({"action":{"type",...,"parameters"},...}).
    // A capable model left to guess (real Qwen3-1.7B, 2026-06-05) emits a plausible-but-wrong
    // {"action":"Click","target":"..."} that fails to parse; spelling out the shape fixes it grammar-free.
    private const string SystemPrompt =
        "You are a pharmacy automation reasoner. Given the current UI state, propose ONE next action.\n" +
        "Respond with ONLY a JSON object of EXACTLY this shape — no prose, no markdown, no extra keys:\n" +
        "{\"action\":{\"type\":\"<one value from allowed_actions>\",\"parameters\":{\"name\":\"<a visible element>\"}}," +
        "\"confidence\":0.0,\"rationale\":\"<short reason>\"}\n" +
        "Only propose actions whose target elements are visible in the state. " +
        "Set confidence to your certainty: 1.0 if sure, lower if uncertain.";

    /// <summary>
    /// Resolves the chat template from the model id. CRITICAL: the template MUST match the model,
    /// otherwise the model is fed special tokens that aren't in its vocabulary, gets confused, and
    /// emits its end-of-turn token first — yielding ZERO output. (Live on Mina's box 2026-06-05: a
    /// Llama-3 template fed to TinyLlama produced "0 chars". A correct grammar would mask the EOS and
    /// force valid JSON, but we must not RELY on grammar-masking — it was a no-op on the box's win-x64
    /// llama.cpp build, so the template alone has to make the model emit valid JSON.)
    /// </summary>
    public static ChatPromptFormat ResolveFormat(string? modelId)
    {
        var id = (modelId ?? string.Empty).ToLowerInvariant();
        if (id.Contains("tinyllama") || id.Contains("zephyr") || id.Contains("stablelm")) return ChatPromptFormat.Zephyr;
        if (id.Contains("llama-3") || id.Contains("llama3") || id.Contains("llama_3")) return ChatPromptFormat.Llama3;
        // 2026-06 brain upgrade. The TEMPLATE alone must elicit valid JSON (GBNF grammar-masking is a
        // no-op on win-x64). Phi-3.5 = Phi. Qwen3 is the committed family:
        //  - Qwen3 HYBRID (e.g. 1.7B) is thinking-ON by default → Qwen3Thinkless = ChatML + a trailing
        //    /no_think switch, else <think> pollutes the JSON.
        //  - Qwen3-*-Instruct-2507 is non-thinking BY DESIGN → plain ChatML.
        //  - Qwen2.5 / SmolLM2 → plain ChatML.
        if (id.Contains("phi")) return ChatPromptFormat.Phi;
        if (id.Contains("qwen3"))
            return (id.Contains("instruct") || id.Contains("2507"))
                ? ChatPromptFormat.ChatML
                : ChatPromptFormat.Qwen3Thinkless;
        if (id.Contains("qwen") || id.Contains("smollm")) return ChatPromptFormat.ChatML;
        // Default to Zephyr: it's the template used by most small instruct GGUFs we ship, and it
        // degrades gracefully (plain <|role|> markers) on models that don't recognize it.
        return ChatPromptFormat.Zephyr;
    }

    /// <summary>The model-native antiprompts (stop strings) for a given format.</summary>
    public static string[] AntiPromptsFor(ChatPromptFormat format) => format switch
    {
        ChatPromptFormat.Llama3 => new[] { "<|eot_id|>", "<|end_of_text|>" },
        ChatPromptFormat.Zephyr => new[] { "</s>", "<|user|>" },
        ChatPromptFormat.Phi => new[] { "<|end|>", "<|endoftext|>" },
        // <|im_start|> stops a runaway small model that begins hallucinating a fresh turn after its
        // answer (terminates faster on a slow box, too). <|im_end|> is the normal end-of-turn.
        ChatPromptFormat.ChatML or ChatPromptFormat.Qwen3Thinkless => new[] { "<|im_end|>", "<|im_start|>", "<|endoftext|>" },
        _ => new[] { "</s>", "\n\n\n" },
    };

    /// <summary>
    /// Builds a chat-templated prompt. The template MUST match the model (see <see cref="ResolveFormat"/>).
    /// </summary>
    public static string Build(InferenceRequest request, ChatPromptFormat format = ChatPromptFormat.Llama3)
    {
        var user = BuildUserMessage(request);
        var sb = new StringBuilder(512);
        switch (format)
        {
            case ChatPromptFormat.Llama3:
                sb.Append("<|begin_of_text|><|start_header_id|>system<|end_header_id|>\n\n");
                sb.Append(SystemPrompt);
                sb.Append("<|eot_id|><|start_header_id|>user<|end_header_id|>\n\n");
                sb.Append(user);
                sb.Append("<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n");
                break;
            case ChatPromptFormat.Zephyr:
                // TinyLlama / Zephyr template: <|system|>…</s>\n<|user|>…</s>\n<|assistant|>\n
                sb.Append("<|system|>\n").Append(SystemPrompt).Append("</s>\n");
                sb.Append("<|user|>\n").Append(user).Append("</s>\n");
                sb.Append("<|assistant|>\n");
                break;
            case ChatPromptFormat.Phi:
                // Phi-3 / Phi-3.5 template: <|system|>…<|end|>\n<|user|>…<|end|>\n<|assistant|>\n
                sb.Append("<|system|>\n").Append(SystemPrompt).Append("<|end|>\n");
                sb.Append("<|user|>\n").Append(user).Append("<|end|>\n");
                sb.Append("<|assistant|>\n");
                break;
            case ChatPromptFormat.ChatML:
                // ChatML (Qwen2.5 / SmolLM2 / Qwen3-Instruct-2507): <|im_start|>role\n…<|im_end|>\n
                sb.Append("<|im_start|>system\n").Append(SystemPrompt).Append("<|im_end|>\n");
                sb.Append("<|im_start|>user\n").Append(user).Append("<|im_end|>\n");
                sb.Append("<|im_start|>assistant\n");
                break;
            case ChatPromptFormat.Qwen3Thinkless:
                // Qwen3's OFFICIAL enable_thinking=false rendering: plain ChatML turns + an EMPTY
                // <think></think> prefill on the assistant turn. The model is aligned to exactly this form
                // (it's what tokenizer_config.json emits), so after </think> it goes straight to the answer
                // — the empty block does NOT cost an early end-of-turn. It also generates FEWER tokens than
                // the /no_think soft-switch, which a hybrid model can ignore and still emit a <think> block
                // that wastes the whole time budget on a slow NOAVX box. (2026-06-10 Fable review.)
                sb.Append("<|im_start|>system\n").Append(SystemPrompt).Append("<|im_end|>\n");
                sb.Append("<|im_start|>user\n").Append(user).Append("<|im_end|>\n");
                sb.Append("<|im_start|>assistant\n<think>\n\n</think>\n\n");
                break;
            default: // Plain
                sb.Append(SystemPrompt).Append("\n\n").Append(user).Append("\n\nRespond with the JSON action now:\n");
                break;
        }
        return sb.ToString();
    }

    // Per-field length caps — defense-in-depth against unscrubbed callers or
    // runaway state. PHI scrubbing happens at the extraction boundary; these
    // caps bound token usage and limit exposure if a PHI-adjacent value slips
    // through (Codex M-3).
    private const int MaxFieldLen = 200;
    private const int MaxEscalationLen = 500;
    // prior_actions is the multi-step working-memory transcript ("tried X → outcome; …",
    // oldest→newest). It gets a dedicated, larger budget than a generic flag AND keeps the
    // TAIL (most-recent steps) — the generic head-truncation at 200 was clipping away exactly
    // the latest steps that matter most for the next decision (2026-06 brain upgrade).
    internal const string PriorActionsFlag = "prior_actions";
    private const int PriorActionsMaxLen = 800;
    private const int MaxElementNameLen = 100;
    private const int MaxElements = 24;
    private const int MaxFlags = 16;
    private const int MaxScreenTextRegions = 16;
    private const int MaxScreenTextLen = 120;
    private const int MaxScreenElements = 24;
    // Aggregate char budget across screen_text + screen_elements so vision can't blow the
    // Tier-2 small-token discipline regardless of how many regions/elements are present.
    private const int MaxScreenCharBudget = 1000;

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
                kv => kv.Key == PriorActionsFlag
                    ? TruncateTail(kv.Value, PriorActionsMaxLen)
                    : Truncate(kv.Value, MaxFieldLen));

        // General navigate mode: the NL objective. Null in scoped mode ⇒ omitted from the
        // JSON (WhenWritingNull) so the legacy prompt is byte-identical when unset.
        var objective = string.IsNullOrEmpty(ctx.UserObjective)
            ? null
            : Truncate(ctx.UserObjective, MaxFieldLen);

        // Vision grounding: when a ScreenFrame has been merged into the context (vision on),
        // surface the on-screen text + elements so the LLM reasons over what's literally shown.
        // When vision is off (both empty) we emit the EXACT legacy object — byte-identical output.
        object state;
        if (ctx.ScreenText.Count == 0 && ctx.ScreenElements.Count == 0)
        {
            state = new
            {
                skill = Truncate(ctx.SkillId, MaxFieldLen),
                process = Truncate(ctx.ProcessName, MaxFieldLen),
                window = Truncate(ctx.WindowTitle, MaxFieldLen),
                visible_elements = elements,
                operator_idle_ms = ctx.OperatorIdleMs,
                flags = cappedFlags,
                objective,
            };
        }
        else
        {
            // Highest-confidence-first, bounded by both a count cap AND a shared char budget.
            var budget = MaxScreenCharBudget;
            var screenText = new List<string>();
            foreach (var t in ctx.ScreenText
                .OrderByDescending(t => t.Confidence)
                .ThenByDescending(t => t.Bounds.Area))
            {
                if (screenText.Count >= MaxScreenTextRegions || budget <= 0) break;
                var s = Truncate(t.Text, Math.Min(MaxScreenTextLen, budget));
                if (s.Length == 0) continue;
                screenText.Add(s);
                budget -= s.Length;
            }

            var screenElements = new List<object>();
            foreach (var e in ctx.ScreenElements.OrderByDescending(e => e.Confidence))
            {
                if (screenElements.Count >= MaxScreenElements || budget <= 0) break;
                var role = Truncate(e.Role, MaxFieldLen);
                var name = Truncate(e.Name, Math.Min(MaxElementNameLen, budget));
                screenElements.Add(new { role, name });
                budget -= role.Length + name.Length;
            }
            state = new
            {
                skill = Truncate(ctx.SkillId, MaxFieldLen),
                process = Truncate(ctx.ProcessName, MaxFieldLen),
                window = Truncate(ctx.WindowTitle, MaxFieldLen),
                visible_elements = elements,
                operator_idle_ms = ctx.OperatorIdleMs,
                flags = cappedFlags,
                screen_text = screenText,
                screen_elements = screenElements,
                objective,
            };
        }
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
                new JsonSerializerOptions
                {
                    WriteIndented = false,
                    // Omit the objective key entirely in scoped mode (null) so the legacy
                    // prompt is byte-identical when UserObjective is unset.
                    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                });
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max];
    }

    /// <summary>Keep the LAST <paramref name="max"/> chars — for tail-relevant values like the
    /// prior-actions transcript where the most-recent entries are the most important.</summary>
    private static string TruncateTail(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[^max..];
    }
}
