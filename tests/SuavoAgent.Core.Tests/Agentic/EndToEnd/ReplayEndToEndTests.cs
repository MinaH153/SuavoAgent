using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Agentic.Replication;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic.EndToEnd;

/// <summary>
/// FULL VERTICAL SLICE for watch-&-learn replay: a learned template compiled by the real
/// TemplatePlanCompiler, executed by the real TemplateReplayer through the real VerbActuator →
/// real VerbDispatcher (7-step fail-closed flow) → gateway → a SimulatedApp, with checks read back
/// through a real perceiver. Proves the replication pipeline actually drives an app to completion
/// (not just that the units pass in isolation). The only seam not real is the OS gateway (the
/// SimulatedApp stands in for live UIA — that last inch is the box).
/// </summary>
public sealed class ReplayEndToEndTests
{
    private static ElementSignature Sig(string control, string auto) => new(control, auto, null);

    private static TemplateStep ClickStep(int ord, ElementSignature target, ElementSignature[] expectedAfter) =>
        new(ord, TemplateStepKind.Click, target, new[] { target }, 1, expectedAfter, IsWrite: true,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.95, Hint: null);

    private static TemplateStep VerifyStep(int ord, ElementSignature expect) =>
        new(ord, TemplateStepKind.VerifyElement, expect, new[] { expect }, 1, null, IsWrite: false,
            CorrelatedQueryShapeHash: null, StepConfidence: 0.95, Hint: null);

    [Fact]
    public async Task LearnedTemplate_ReplaysEndToEnd_DrivingTheApp_ToCompleted()
    {
        // A 2-screen app: clicking the Save button reveals a "confirmed" text.
        var savedSig = Sig("Text", "confirmed"); // ElementSignature for the template's expected-after
        var app = new SimulatedApp(
            initialVisible: new[] { SimulatedApp.Sig("Button", "saveBtn") },
            onClick: new() { ["saveBtn"] = new[] { SimulatedApp.Sig("Text", "confirmed") } });

        // Real container: verbs + dispatcher + policy + audit, with the gateway driving the SimulatedApp.
        var services = new ServiceCollection();
        services.AddMissionLoopPhase1();
        services.AddSingleton<IActuationGateway>(new SimulatedAppGateway(app));
        using var provider = services.BuildServiceProvider();

        var charter = MissionCharterLoader.BuildDefaultCharter("pharm-test-001");
        var audit = provider.GetRequiredService<AuditChain>();

        var actuator = new VerbActuator(
            VerbRegistry.Build(new[] { typeof(IVerb).Assembly }, NullLogger<VerbRegistry>.Instance),
            provider.GetRequiredService<VerbDispatcher>(),
            provider,
            envelope: _ => new ActuationEnvelope(charter, audit, Guid.NewGuid().ToString("n"), DateTimeOffset.UtcNow.AddMinutes(2)));

        var perceiver = new SimulatedAppPerceiver(app);
        var safety = new FakeSafetyGate(); // safety logic is unit-tested separately; here we prove the drive path
        var replayer = new TemplateReplayer(perceiver, actuator, safety, AgenticTestKit.NoDelay);

        // Learned template: click Save (writeback expecting "confirmed"), then verify "confirmed" is shown.
        var template = TemplatePlanCompilerTestTemplate(
            ClickStep(0, Sig("Button", "saveBtn"), new[] { savedSig }),
            VerifyStep(1, savedSig));

        var plan = TemplatePlanCompiler.Compile(template);

        var result = await replayer.ReplayAsync(
            plan,
            new AgentObjective("file the delivery", "task.replay", "pharm-test-001"),
            new ActuationContext("pharm-test-001", "task.replay", DryRun: false),
            new TemplateReplayOptions { SettleStableReads = 1 },
            CancellationToken.None);

        Assert.Equal(ReplayOutcome.Completed, result.Outcome);
        Assert.Equal(2, result.StepsCompleted);
        Assert.Contains(SimulatedApp.Sig("Text", "confirmed"), app.VisibleSignatures); // the app really advanced
    }

    [Fact]
    public async Task Replay_StopsFailClosed_WhenWritebackPostconditionNeverAppears()
    {
        // Clicking Save does NOT reveal "confirmed" (a stale selector / broken step) → fail-closed at the writeback.
        var app = new SimulatedApp(
            initialVisible: new[] { SimulatedApp.Sig("Button", "saveBtn") },
            onClick: new() { ["saveBtn"] = new[] { SimulatedApp.Sig("Button", "saveBtn") } }); // no change

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

        var replayer = new TemplateReplayer(new SimulatedAppPerceiver(app), actuator, new FakeSafetyGate(), AgenticTestKit.NoDelay);

        var plan = TemplatePlanCompiler.Compile(
            TemplatePlanCompilerTestTemplate(ClickStep(0, Sig("Button", "saveBtn"), new[] { Sig("Text", "confirmed") })));

        var result = await replayer.ReplayAsync(
            plan,
            new AgentObjective("file", "task.replay", "pharm-test-001"),
            new ActuationContext("pharm-test-001", "task.replay", DryRun: false),
            new TemplateReplayOptions { SettleStableReads = 1 },
            CancellationToken.None);

        Assert.Equal(ReplayOutcome.PostconditionFailed, result.Outcome);
        Assert.Equal(0, result.FailedOrdinal);
    }

    /// <summary>Builds a minimal valid WorkflowTemplate around the given steps (Calculator-scoped so the verb allowlist passes).</summary>
    private static WorkflowTemplate TemplatePlanCompilerTestTemplate(params TemplateStep[] steps)
    {
        var entry = steps[0].ExpectedVisible;
        var screenSig = WorkflowTemplate.ComputeScreenSignature(entry);
        var stepsHash = WorkflowTemplate.ComputeStepsHash(steps);
        return new WorkflowTemplate(
            TemplateId: WorkflowTemplate.ComputeTemplateId(screenSig, stepsHash),
            TemplateVersion: "1",
            SkillId: "navigate",
            ProcessNameGlob: "calc.exe*",
            PmsVersionRange: Array.Empty<PmsVersionFingerprint>(),
            ScreenSignatureV1: screenSig,
            StepsHash: stepsHash,
            RoutineHashOrigin: null,
            Steps: steps,
            AggregateConfidence: 0.95,
            ObservationCount: 5,
            HasWriteback: steps.Any(s => s.IsWrite),
            ExtractedAt: "2026-06-04T00:00:00Z",
            ExtractedBy: "test",
            RetiredAt: null,
            RetirementReason: null);
    }
}
