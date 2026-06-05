using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Integration of the REAL TieredBrainReasoner with the pure loop (fakes only at the screen/act/
/// safety boundaries the live box owns). Proves the brain↔loop seam works without a RuleEngine
/// catalog, local model, cloud, or live UI — the wiring the navigate_app command depends on.
/// </summary>
public sealed class TieredBrainReasonerIntegrationTests
{
    private static TieredBrainReasoner RealReasoner()
    {
        var rules = new RuleEngine(Array.Empty<Rule>(), NullLogger<RuleEngine>.Instance);
        var verifier = new ActionVerifier(autoExecuteDestructive: false);
        return new TieredBrainReasoner(
            rules, new NullLocalInference(), verifier, new NullCloudReasoning(),
            NullLogger<TieredBrain>.Instance);
    }

    [Fact]
    public async Task RealBrain_NoRulesNoInferenceNoCloud_EscalatesThroughLoop_NeverActsNeverHangs()
    {
        var reasoner = RealReasoner();
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("home"));
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate();
        var runner = new AgenticLoopRunner(perceiver, reasoner, actuator, safety, null, AgenticTestKit.NoDelay);

        var result = await runner.RunAsync(
            new AgentObjective("open notepad and type hi", "task.demo", "ph.test"),
            new AgenticLoopOptions { MaxSteps = 5 },
            CancellationToken.None);

        // Undecidable (no rules/model/cloud) ⇒ the brain returns OperatorRequired ⇒ the reasoner maps
        // to Escalate ⇒ the loop terminates cleanly, escalates, and NEVER actuates.
        Assert.Equal(TerminationReason.Escalated, result.Termination);
        Assert.True(result.EscalationEmitted);
        Assert.Empty(actuator.Calls);
    }

    [Fact]
    public async Task RealBrain_PreflightDenied_HaltsBeforeReasoning()
    {
        var reasoner = RealReasoner();
        var perceiver = new FakePerceiver(AgenticTestKit.Screen("home"));
        var actuator = new FakeActuator();
        var safety = new FakeSafetyGate { PreflightFn = _ => SafetyVerdict.Denied("kill_switch") };
        var runner = new AgenticLoopRunner(perceiver, reasoner, actuator, safety, null, AgenticTestKit.NoDelay);

        var result = await runner.RunAsync(
            new AgentObjective("anything", "task.x", "ph.x"),
            new AgenticLoopOptions { MaxSteps = 5 },
            CancellationToken.None);

        Assert.Equal(TerminationReason.GateDenied, result.Termination);
        Assert.Equal(0, perceiver.Calls);
        Assert.Empty(actuator.Calls);
    }
}
