using System.Collections.Generic;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

// Regression for the 2026-06-05 "0 chars" bug: the prompt template MUST match the model. A Llama-3
// template fed to TinyLlama makes the model emit its end-of-turn token first -> zero output. The
// template is resolved from the model id so TinyLlama gets the Zephyr template, not Llama-3.
public sealed class InferencePromptBuilderFormatTests
{
    [Theory]
    [InlineData("tinyllama-1.1b-chat", ChatPromptFormat.Zephyr)]
    [InlineData("TinyLlama-1.1B-Chat-Q4", ChatPromptFormat.Zephyr)]
    [InlineData("llama-3.2-1b-q4_k_m", ChatPromptFormat.Llama3)]
    [InlineData("Llama3-8B", ChatPromptFormat.Llama3)]
    // The 2026-06 brain upgrade: Phi-3.5 (primary) + ChatML for Qwen2.5/SmolLM2 (fallback).
    [InlineData("phi-3.5-mini-instruct-q4_k_m", ChatPromptFormat.Phi)]
    [InlineData("Phi-3.5-mini", ChatPromptFormat.Phi)]
    [InlineData("qwen2.5-1.5b-instruct-q4_k_m", ChatPromptFormat.ChatML)]
    [InlineData("Qwen2.5-3B-Instruct", ChatPromptFormat.ChatML)]
    [InlineData("smollm2-1.7b-instruct", ChatPromptFormat.ChatML)]
    // Qwen3 HYBRID (e.g. 1.7B) is thinking-ON by default → force NON-thinking via Qwen3's official
    // empty <think></think> prefill, else <think> reasoning pollutes the JSON (no grammar backstop on win-x64).
    [InlineData("qwen3-1.7b", ChatPromptFormat.Qwen3Thinkless)]
    [InlineData("Qwen3-1.7B-Q4_K_M", ChatPromptFormat.Qwen3Thinkless)]
    // Qwen3-*-Instruct-2507 is non-thinking BY DESIGN → plain ChatML.
    [InlineData("qwen3-4b-instruct-2507", ChatPromptFormat.ChatML)]
    [InlineData("Qwen3-4B-Instruct-2507", ChatPromptFormat.ChatML)]
    [InlineData("some-unknown-gguf", ChatPromptFormat.Zephyr)] // safe default for small instruct GGUFs
    public void ResolveFormat_maps_model_id_to_template(string modelId, ChatPromptFormat expected)
        => Assert.Equal(expected, InferencePromptBuilder.ResolveFormat(modelId));

    [Fact]
    public void Build_Zephyr_uses_zephyr_markers_not_llama3()
    {
        var prompt = InferencePromptBuilder.Build(Req(), ChatPromptFormat.Zephyr);
        Assert.Contains("<|user|>", prompt);
        Assert.Contains("<|assistant|>", prompt);
        Assert.DoesNotContain("<|start_header_id|>", prompt); // no Llama-3 tokens for TinyLlama
        Assert.DoesNotContain("<|eot_id|>", prompt);
    }

    [Fact]
    public void Build_Llama3_uses_llama3_markers()
    {
        var prompt = InferencePromptBuilder.Build(Req(), ChatPromptFormat.Llama3);
        Assert.Contains("<|start_header_id|>", prompt);
        Assert.Contains("<|eot_id|>", prompt);
    }

    [Fact]
    public void Build_Phi_uses_phi_markers_not_zephyr_or_chatml()
    {
        var prompt = InferencePromptBuilder.Build(Req(), ChatPromptFormat.Phi);
        Assert.Contains("<|user|>", prompt);
        Assert.Contains("<|assistant|>", prompt);
        Assert.Contains("<|end|>", prompt);          // Phi turn terminator
        Assert.DoesNotContain("</s>", prompt);       // not Zephyr
        Assert.DoesNotContain("<|im_start|>", prompt); // not ChatML
        Assert.DoesNotContain("<|eot_id|>", prompt);  // not Llama3
    }

    [Fact]
    public void Build_ChatML_uses_im_markers()
    {
        var prompt = InferencePromptBuilder.Build(Req(), ChatPromptFormat.ChatML);
        Assert.Contains("<|im_start|>system", prompt);
        Assert.Contains("<|im_start|>user", prompt);
        Assert.Contains("<|im_start|>assistant", prompt);
        Assert.Contains("<|im_end|>", prompt);
        Assert.DoesNotContain("</s>", prompt);
        Assert.DoesNotContain("<|eot_id|>", prompt);
        Assert.DoesNotContain("<think>", prompt); // plain ChatML must NOT prefill a think block
    }

    [Fact]
    public void Build_Qwen3Thinkless_isChatML_with_empty_think_prefill()
    {
        // Qwen3 enable_thinking=false official rendering: plain ChatML turns (NO /no_think soft-switch)
        // + an EMPTY <think></think> block prefilled on the assistant turn. Training-matched + emits fewer
        // tokens than /no_think (which a hybrid model can ignore, still emitting <think>). (2026-06-10 Fable.)
        var prompt = InferencePromptBuilder.Build(Req(), ChatPromptFormat.Qwen3Thinkless);
        Assert.Contains("<|im_start|>assistant\n<think>\n\n</think>", prompt);
        Assert.Contains("<|im_start|>user", prompt);
        Assert.Contains("<|im_end|>", prompt);
        Assert.DoesNotContain("/no_think", prompt); // the soft-switch is gone; the empty-think prefill replaces it
        Assert.DoesNotContain("<|eot_id|>", prompt);
    }

    [Fact]
    public void Chat_Qwen3Thinkless_uses_empty_think_prefill_not_no_think_switch()
    {
        var prompt = LLamaLocalInference.BuildChatPrompt(
            "What is the capital of France? Answer in exactly one word.",
            "You are SuavoAgent.",
            ChatPromptFormat.Qwen3Thinkless);

        Assert.Contains("<|im_start|>assistant\n<think>\n\n</think>", prompt);
        Assert.DoesNotContain("/no_think", prompt);
    }

    [Fact]
    public void Qwen3Thinkless_disables_proposal_grammar()
    {
        // The Qwen3 hybrid path relies on the empty-think prefill + strict parser, NOT GBNF. On the live
        // win-x64 NOAVX box, adding GBNF before the model's first JSON token stalled generation (0 chars).
        Assert.False(LLamaLocalInference.UsesProposalGrammar(ChatPromptFormat.Qwen3Thinkless));
        Assert.True(LLamaLocalInference.UsesProposalGrammar(ChatPromptFormat.ChatML));
        Assert.True(LLamaLocalInference.UsesProposalGrammar(ChatPromptFormat.Phi));
    }

    [Fact]
    public void AntiPrompts_match_format()
    {
        Assert.Contains("</s>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.Zephyr));
        Assert.Contains("<|eot_id|>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.Llama3));
        Assert.Contains("<|end|>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.Phi));
        Assert.Contains("<|im_end|>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.ChatML));
        Assert.Contains("<|im_end|>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.Qwen3Thinkless));
        // <|im_start|> stops a runaway model that begins hallucinating a fresh turn after its answer.
        Assert.Contains("<|im_start|>", InferencePromptBuilder.AntiPromptsFor(ChatPromptFormat.Qwen3Thinkless));
    }

    private static InferenceRequest Req() => new()
    {
        Context = new RuleContext { SkillId = "navigate", VisibleElements = new HashSet<string> { "Button:Save" } },
        EscalationReason = "no rule matched",
        AllowedActions = new HashSet<RuleActionType> { RuleActionType.Click, RuleActionType.Log },
    };
}
