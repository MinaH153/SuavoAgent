using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Helper.Tests.Reasoning;

/// <summary>
/// A REAL local LLM reasoning in CI — the Tier-2 path I had been ASSUMING couldn't run without the box.
/// LLamaLocalInference loads a real GGUF (llama.cpp via LLamaSharp, CPU) and produces a single action
/// proposal under a GBNF grammar that CONSTRAINS the output to the RuleActionSpec schema + the allowed
/// action set — so even a small model yields a valid, in-bounds action. This proves the on-device
/// reasoning tier executes end-to-end (model loads → grammar-constrained inference → parsed proposal),
/// without the production box. (Tier-3 cloud reasoning is Claude via /api/agent/reason — that one is
/// genuinely network/box-bound.)
///
/// Runs only when SUAVOAGENT_TEST_GGUF points at a downloaded model (the windows-uia-smoke CI job sets
/// it). No-op skip otherwise, so the cross-platform build/test gate is unaffected.
/// </summary>
public sealed class LocalLlmSmokeTests
{
    [Fact]
    public async Task RealLocalLlm_LoadsAndProducesAGrammarConstrainedAction()
    {
        var modelPath = Environment.GetEnvironmentVariable("SUAVOAGENT_TEST_GGUF");
        if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath)) return; // no model → no-op skip

        var agentOptions = Options.Create(new AgentOptions
        {
            Reasoning = new ReasoningOptions
            {
                Enabled = true,
                ModelId = "llama-3.2-1b-instruct-q4",
                ModelPath = modelPath,
                NativeLibraryPath = null, // null → LLamaSharp auto-loads the LLamaSharp.Backend.Cpu natives
                ContextSize = 2048,
                MaxOutputTokens = 96,
                IdleUnloadSeconds = 5,
            },
        });

        await using var inference = new LLamaLocalInference(
            agentOptions, modelPath, NullLogger<LLamaLocalInference>.Instance);

        var request = new InferenceRequest
        {
            Context = new RuleContext
            {
                SkillId = "navigate",
                VisibleElements = new HashSet<string> { "Button:Save", "Window:SuavoAgent UIA Harness" },
            },
            EscalationReason = "no Tier-1 rule matched; reason about the objective",
            AllowedActions = new HashSet<RuleActionType>
            {
                RuleActionType.Click,
                RuleActionType.Log,
                RuleActionType.Escalate,
            },
            Timeout = TimeSpan.FromSeconds(90), // CPU model load (~3-5s) + inference, generous for cold CI
        };

        var proposal = await inference.ProposeAsync(request, CancellationToken.None);

        // A real local LLM loaded and ran in CI, and the GBNF grammar kept its output a valid,
        // in-bounds RuleActionSpec — the on-device reasoning tier works without the box.
        Assert.NotNull(proposal);
        Assert.NotNull(proposal!.Action);
        Assert.Contains(proposal.Action.Type, request.AllowedActions);
        Assert.Equal("llama-3.2-1b-instruct-q4", proposal.ModelId);
    }
}
