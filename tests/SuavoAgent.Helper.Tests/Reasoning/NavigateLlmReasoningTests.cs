using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using LLama.Native;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Helper.Tests.Reasoning;

/// <summary>
/// The NL way-in, reason→ground, with the REAL on-device LLM (no box, no HIPAA capture gate): a real
/// TinyLlama GGUF, given a perceived screen + a natural-language objective and NO matching Tier-1 rule,
/// reasons an action and the GROUNDING LAYER binds it to the concrete on-screen element — yielding an
/// ACTUATABLE click_by_signature (controlType+automationId+process). This is the bridge that lets the
/// same brain act on any app: the model names intent, grounding resolves it to what's on screen.
///
/// Runs only when SUAVOAGENT_TEST_GGUF (model) + SUAVOAGENT_TEST_LLAMA_DYLIB (native) are set (the
/// windows-uia-smoke CI job / local repro). No-op skip otherwise, so the cross-platform gate is clean.
/// </summary>
public sealed class NavigateLlmReasoningTests
{
    [Fact]
    public async Task RealLlm_ReasonsAndGrounds_ToActuatableClick_OnThePerceivedElement()
    {
        var modelPath = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;
        var dylib = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_LLAMA_DYLIB");
        if (!string.IsNullOrEmpty(dylib) && File.Exists(dylib))
            NativeLibraryConfig.All.WithLibrary(dylib, null);

        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "tinyllama-1.1b-chat", // -> Zephyr template (the #182 fix)
                ModelPath = modelPath,
                NativeLibraryPath = null, // use the WithLibrary above (osx) / shipped native
                ContextSize = 2048,
                MaxOutputTokens = 256,
                IdleUnloadSeconds = 5,
            },
        });

        await using var llm = new LLamaLocalInference(options, modelPath, NullLogger<LLamaLocalInference>.Instance);

        var objective = new AgentObjective("Click the Save button", "task.nav.llm", "pharm-test-001");
        var screen = new PerceivedScreen(
            "screenhash", Scrubbed: true,
            ElementSummary: new[] { "Button|saveBtn" },
            WindowTitle: "notepad",
            Signatures: new[] { "Button|saveBtn" });
        var memory = ContextAccumulator.RecordObservation(ContextAccumulator.Start(objective), screen);

        // No Log: with Log allowed the tiny model prematurely emits the "complete" sentinel instead of
        // reasoning the action. The actuating verbs force it to choose a real action; Click is the only
        // sensible one for this objective, and grounding binds it to the on-screen element.
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey" };

        // The Tier-2 path: build the brain's context exactly as the loop does and ask the REAL LLM to
        // reason the next action — no Tier-1 rule, no demo to copy ("figure out the objective").
        var ctx = NavigateReasoning.BuildContext(objective, memory);
        var proposal = await llm.ProposeAsync(new InferenceRequest
        {
            Context = ctx,
            AllowedActions = NavigateReasoning.MapAllowedActions(allowed),
            EscalationReason = "no rule matched; reason about the objective",
            Timeout = TimeSpan.FromSeconds(60),
        }, CancellationToken.None);

        // (1) The real on-device LLM REASONED the correct action on the perceived element — a Click,
        //     referencing the on-screen Save button — purely from the NL objective + the screen.
        Assert.NotNull(proposal);
        Assert.Equal(RuleActionType.Click, proposal!.Action.Type);
        Assert.Contains(proposal.Action.Parameters.Values,
            v => v.Contains("save", StringComparison.OrdinalIgnoreCase) || v.Contains("saveBtn", StringComparison.OrdinalIgnoreCase));

        // (2) The GROUNDING LAYER binds that freeform intent to an ACTUATABLE click_by_signature on the
        //     concrete element + process — the bridge from "reasons about the objective" to a real action.
        var grounded = ActionGrounding.GroundClick(proposal.Action.Parameters, screen, "notepad");
        Assert.NotNull(grounded);
        Assert.Equal("saveBtn", grounded!["automationId"]);
        Assert.Equal("Button", grounded["controlType"]);
        Assert.Equal("notepad", grounded["process_name"]);

        // NOTE: autonomous ACTUATION beyond this is intentionally gated — the brain holds a Tier-2 LLM
        // action as OperatorRequired (needs approval) under the default autonomy level. Reasoning +
        // grounding are proven here; execution is the approval/autonomy layer (the "approve" path).
    }

    /// <summary>
    /// The 2026-06 brain upgrade: the REAL Phi-3.5 GGUF (MIT, ungated) under the PHI chat template
    /// reasons + grounds a valid action from an NL objective + a populated MULTI-STEP working-memory
    /// transcript (a prior attempt that didn't complete the goal). Proves (a) the new model + the new
    /// Phi template path produce valid JSON unassisted — the win-x64 grammar is a no-op, so this is the
    /// real test of "the template alone elicits valid JSON" — and (b) the loop's prior-actions memory is
    /// threaded into the reasoning context. Gated on SUAVOAGENT_TEST_GGUF_PHI (the windows-uia-smoke CI
    /// job / local repro); no-op skip otherwise.
    /// </summary>
    [Fact]
    public async Task RealPhiLlm_ReasonsAndGrounds_WithMultiStepMemory()
    {
        var modelPath = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_GGUF_PHI");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;
        var dylib = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_LLAMA_DYLIB");
        if (!string.IsNullOrEmpty(dylib) && File.Exists(dylib))
            NativeLibraryConfig.All.WithLibrary(dylib, null);

        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "phi-3.5-mini-instruct", // -> Phi template (ResolveFormat keys off "phi")
                ModelPath = modelPath,
                NativeLibraryPath = null,
                ContextSize = 4096,
                MaxOutputTokens = 512,
                IdleUnloadSeconds = 5,
            },
        });

        await using var llm = new LLamaLocalInference(options, modelPath, NullLogger<LLamaLocalInference>.Instance);

        var objective = new AgentObjective("Save the document", "task.nav.phi", "pharm-test-001");
        var screen = new PerceivedScreen(
            "screenhash", Scrubbed: true,
            ElementSummary: new[] { "Button|saveBtn" },
            WindowTitle: "notepad",
            Signatures: new[] { "Button|saveBtn" });

        // Build REAL multi-step working memory: a prior attempt (ctrl+s) that failed to complete the
        // goal, recorded through the production accumulator — so prior_actions is populated exactly as
        // the loop would present it on step 2.
        var m0 = ContextAccumulator.Start(objective);
        var m1 = ContextAccumulator.RecordObservation(m0, screen);
        var priorAttempt = NextAction.Act("press_keys",
            new Dictionary<string, object?> { ["keys"] = "ctrl+s" }, "tried the save shortcut");
        var m2 = ContextAccumulator.RecordStep(m1, "screenhash", priorAttempt,
            ActStatus.Failed, PostconditionVerdict.NotMet, memoryWindow: 8);
        var memory = ContextAccumulator.RecordObservation(m2, screen);

        var ctx = NavigateReasoning.BuildContext(objective, memory);

        // (1) The loop's multi-step working memory is threaded into the reasoning context.
        Assert.NotNull(ctx.LocalReasoningTranscript);
        Assert.Contains("press_keys", ctx.LocalReasoningTranscript);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey" };
        var proposal = await llm.ProposeAsync(new InferenceRequest
        {
            Context = ctx,
            AllowedActions = NavigateReasoning.MapAllowedActions(allowed),
            EscalationReason = "no rule matched; reason about the objective given prior attempts",
            Timeout = TimeSpan.FromSeconds(90), // a 3B model on a hosted CPU runner is slower
        }, CancellationToken.None);

        // (2) Phi-3.5, on the Phi template, emits valid JSON (no grammar backstop) and reasons a Click
        //     on the visible Save button — purely from the objective + screen + prior-actions history.
        Assert.NotNull(proposal);
        Assert.Equal(RuleActionType.Click, proposal!.Action.Type);
        Assert.Contains(proposal.Action.Parameters.Values,
            v => v.Contains("save", StringComparison.OrdinalIgnoreCase));

        // (3) Grounding binds the freeform intent to an actuatable click_by_signature on the element.
        var grounded = ActionGrounding.GroundClick(proposal.Action.Parameters, screen, "notepad");
        Assert.NotNull(grounded);
        Assert.Equal("saveBtn", grounded!["automationId"]);
        Assert.Equal("Button", grounded["controlType"]);
    }

    /// <summary>
    /// The 2026-06 runtime bump: the REAL Qwen3-1.7B GGUF (Apache-2.0) on the bumped LLamaSharp 0.24.0
    /// (first Qwen3-capable llama.cpp) reasons + grounds a valid action under the **Qwen3Thinkless**
    /// template — ChatML + the empty-&lt;think&gt; prefill that forces non-thinking, so the model emits the
    /// JSON directly instead of a chain-of-thought. Proves end-to-end: (a) Qwen3 architecture LOADS on the
    /// new runtime (it could not on 0.19.0 → "Architecture qwen3 not supported"), (b) the thinking-off
    /// template path yields clean JSON with no grammar backstop, (c) reason→ground works with multi-step
    /// memory. Gated on SUAVOAGENT_TEST_GGUF_QWEN3 (the windows-uia-smoke CI job); no-op skip otherwise.
    /// </summary>
    [Fact]
    public async Task RealQwen3Llm_ThinkingOff_ReasonsAndGrounds_WithMultiStepMemory()
    {
        var modelPath = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_GGUF_QWEN3");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return;
        var dylib = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_LLAMA_DYLIB");
        if (!string.IsNullOrEmpty(dylib) && File.Exists(dylib))
            NativeLibraryConfig.All.WithLibrary(dylib, null);

        var options = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "qwen3-1.7b", // -> Qwen3Thinkless template (ChatML + empty-<think> prefill)
                ModelPath = modelPath,
                NativeLibraryPath = null,
                ContextSize = 4096,
                MaxOutputTokens = 512,
                IdleUnloadSeconds = 5,
            },
        });

        await using var llm = new LLamaLocalInference(options, modelPath, NullLogger<LLamaLocalInference>.Instance);

        var objective = new AgentObjective("Save the document", "task.nav.qwen3", "pharm-test-001");
        var screen = new PerceivedScreen(
            "screenhash", Scrubbed: true,
            ElementSummary: new[] { "Button|saveBtn" },
            WindowTitle: "notepad",
            Signatures: new[] { "Button|saveBtn" });

        var m0 = ContextAccumulator.Start(objective);
        var m1 = ContextAccumulator.RecordObservation(m0, screen);
        var priorAttempt = NextAction.Act("press_keys",
            new Dictionary<string, object?> { ["keys"] = "ctrl+s" }, "tried the save shortcut");
        var m2 = ContextAccumulator.RecordStep(m1, "screenhash", priorAttempt,
            ActStatus.Failed, PostconditionVerdict.NotMet, memoryWindow: 8);
        var memory = ContextAccumulator.RecordObservation(m2, screen);

        var ctx = NavigateReasoning.BuildContext(objective, memory);
        Assert.NotNull(ctx.LocalReasoningTranscript);
        Assert.Contains("press_keys", ctx.LocalReasoningTranscript);

        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey" };
        var proposal = await llm.ProposeAsync(new InferenceRequest
        {
            Context = ctx,
            AllowedActions = NavigateReasoning.MapAllowedActions(allowed),
            EscalationReason = "no rule matched; reason about the objective given prior attempts",
            Timeout = TimeSpan.FromSeconds(90),
        }, CancellationToken.None);

        // Qwen3-1.7B, thinking forced OFF, emits valid JSON (no <think> pollution) and reasons a Click
        // on the visible Save button — on a runtime that literally could not load Qwen3 before this bump.
        Assert.NotNull(proposal);
        Assert.Equal(RuleActionType.Click, proposal!.Action.Type);
        Assert.Contains(proposal.Action.Parameters.Values,
            v => v.Contains("save", StringComparison.OrdinalIgnoreCase));

        var grounded = ActionGrounding.GroundClick(proposal.Action.Parameters, screen, "notepad");
        Assert.NotNull(grounded);
        Assert.Equal("saveBtn", grounded!["automationId"]);
        Assert.Equal("Button", grounded["controlType"]);
    }
}
