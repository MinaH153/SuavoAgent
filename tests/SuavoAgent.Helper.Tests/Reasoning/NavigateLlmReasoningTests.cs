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
}
