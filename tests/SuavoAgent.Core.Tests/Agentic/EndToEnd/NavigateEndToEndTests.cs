using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic.EndToEnd;

/// <summary>
/// FULL VERTICAL SLICE for the natural-language loop: the REAL AgenticLoopRunner driving the REAL
/// VerbActuator → REAL VerbDispatcher (7-step flow) → gateway → SimulatedApp, perceiving real state
/// each turn. Proves the perceive→reason→act→settle→verify loop actually drives an app to Done. The
/// only faked seam is the reasoner (the cloud brain isn't reachable in CI) — a scripted stand-in that
/// decides from the perceived screen exactly as the real TieredBrainReasoner would once it returns
/// grounded actions; and the OS gateway (SimulatedApp stands in for live UIA — that inch is the box).
/// </summary>
public sealed class NavigateEndToEndTests
{
    private const string Confirmed = "Text|confirmed";

    /// <summary>Decides from the perceived screen: not done → click Save; once "confirmed" is shown → Done.</summary>
    private sealed class ScriptedReasoner : IReasoner
    {
        public int ReasonCalls { get; private set; }

        public Task<ReasonResult> ReasonNextAsync(
            AgentObjective objective, WorkingMemory memory, IReadOnlySet<string> allowedActions, bool allowCloud, CancellationToken ct)
        {
            ReasonCalls++;
            var sigs = memory.LatestScreen?.Signatures ?? Array.Empty<string>();
            if (sigs.Contains(Confirmed, StringComparer.OrdinalIgnoreCase))
                return Task.FromResult(new ReasonResult(NextAction.Done, UsedCloud: false));

            var click = NextAction.Act("click_by_signature", new Dictionary<string, object?>
            {
                ["automationId"] = "saveBtn",
                ["controlType"] = "Button",
                ["process_name"] = "notepad",
            });
            return Task.FromResult(new ReasonResult(click, UsedCloud: false));
        }

        public Task<VerifyResult> VerifyPostconditionAsync(
            AgentObjective objective, NextAction lastAction, PerceivedScreen before, PerceivedScreen after, bool allowCloud, CancellationToken ct)
            // The app advanced (the after-screen now shows "confirmed"); the next reason will see it and finish.
            => Task.FromResult(new VerifyResult(PostconditionVerdict.Met, UsedCloud: false));
    }

    [Fact]
    public async Task NlObjective_DrivesTheApp_ToDone_ViaTheRealActuationPipeline()
    {
        var app = new SimulatedApp(
            initialVisible: new[] { SimulatedApp.Sig("Button", "saveBtn") },
            onClick: new() { ["saveBtn"] = new[] { SimulatedApp.Sig("Text", "confirmed") } });

        var services = new ServiceCollection();
        services.AddMissionLoopPhase1();
        services.AddSingleton<IActuationGateway>(new SimulatedAppGateway(app));
        using var provider = services.BuildServiceProvider();

        var actuator = new VerbActuator(
            VerbRegistry.Build(new[] { typeof(IVerb).Assembly }, NullLogger<VerbRegistry>.Instance),
            provider.GetRequiredService<VerbDispatcher>(),
            provider,
            _ => new ActuationEnvelope(MissionCharterLoader.BuildDefaultCharter("pharm-test-001"),
                provider.GetRequiredService<AuditChain>(), Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow.AddMinutes(2)));

        var reasoner = new ScriptedReasoner();
        var runner = new AgenticLoopRunner(
            new SimulatedAppPerceiver(app), reasoner, actuator, new FakeSafetyGate(),
            utcNow: null, delay: AgenticTestKit.NoDelay);

        var options = new AgenticLoopOptions
        {
            MaxSteps = 10,
            SettleStableReads = 1,
            AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey", "Log" },
        };

        var result = await runner.RunAsync(
            new AgentObjective("save the record", "task.nav", "pharm-test-001"), options, CancellationToken.None);

        Assert.Equal(TerminationReason.Done, result.Termination);
        Assert.Contains(SimulatedApp.Sig("Text", "confirmed"), app.VisibleSignatures); // the loop really advanced the app
        Assert.True(reasoner.ReasonCalls >= 2); // at least: decide-click, then see-confirmed-and-finish
    }

    [Fact]
    public async Task RealTieredBrainReasoner_RuleBrain_DrivesTheApp_ToDone()
    {
        // Same end-to-end loop, but the reasoner is the REAL TieredBrainReasoner over a REAL RuleEngine
        // (the agent's Tier-1 brain). The LLM tiers (Tier-2/3) are inherently not CI-runnable, but this
        // proves the real reasoning PATH — adapter → TieredBrain → RuleEngine → grounded action — drives
        // a real actuation pipeline to completion, not a scripted stand-in.
        var app = new SimulatedApp(
            initialVisible: new[] { SimulatedApp.Sig("Button", "saveBtn") },
            onClick: new() { ["saveBtn"] = new[] { SimulatedApp.Sig("Text", "confirmed") } });

        var services = new ServiceCollection();
        services.AddMissionLoopPhase1();
        services.AddSingleton<IActuationGateway>(new SimulatedAppGateway(app));
        using var provider = services.BuildServiceProvider();

        var actuator = new VerbActuator(
            VerbRegistry.Build(new[] { typeof(IVerb).Assembly }, NullLogger<VerbRegistry>.Instance),
            provider.GetRequiredService<VerbDispatcher>(),
            provider,
            _ => new ActuationEnvelope(MissionCharterLoader.BuildDefaultCharter("pharm-test-001"),
                provider.GetRequiredService<AuditChain>(), Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow.AddMinutes(2)));

        // Real Tier-1 rule brain for skill "navigate": click Save while it's shown; when "confirmed" is
        // shown, emit the completion sentinel (Log status=complete → the adapter maps it to Done).
        var rules = new[]
        {
            new Rule
            {
                Id = "navigate.click-save", SkillId = NavigateReasoning.NavigateSkillId,
                When = new RulePredicate { VisibleElements = new[] { "Button|saveBtn" } },
                Then = new[]
                {
                    new RuleActionSpec
                    {
                        Type = RuleActionType.Click,
                        Parameters = new Dictionary<string, string> { ["label"] = "saveBtn", ["process_name"] = "notepad" },
                    },
                },
            },
            new Rule
            {
                Id = "navigate.done", SkillId = NavigateReasoning.NavigateSkillId,
                When = new RulePredicate { VisibleElements = new[] { "Text|confirmed" } },
                Then = new[]
                {
                    new RuleActionSpec
                    {
                        Type = RuleActionType.Log,
                        Parameters = new Dictionary<string, string> { ["status"] = NavigateReasoning.CompleteStatusValue },
                    },
                },
            },
        };

        var reasoner = new TieredBrainReasoner(
            new RuleEngine(rules, NullLogger<RuleEngine>.Instance),
            new NullLocalInference(),
            new ActionVerifier(autoExecuteDestructive: true),
            new NullCloudReasoning(),
            NullLogger<TieredBrain>.Instance);

        var runner = new AgenticLoopRunner(
            new SimulatedAppPerceiver(app), reasoner, actuator, new FakeSafetyGate(),
            utcNow: null, delay: AgenticTestKit.NoDelay);

        var result = await runner.RunAsync(
            new AgentObjective("save the record", "task.nav", "pharm-test-001"),
            new AgenticLoopOptions
            {
                MaxSteps = 10,
                SettleStableReads = 1,
                AllowedActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Click", "Type", "PressKey", "Log" },
            },
            CancellationToken.None);

        Assert.Equal(TerminationReason.Done, result.Termination);
        Assert.Contains(SimulatedApp.Sig("Text", "confirmed"), app.VisibleSignatures); // real brain advanced the real app
    }
}
