using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Agentic.Replication;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic.EndToEnd;

/// <summary>
/// FULL WATCH-&-LEARN VERTICAL SLICE: observe → LEARN → replicate, end to end, with NO hand-built
/// template. The agent observes a repeated 3-step workflow (behavioral interactions), the RoutineDetector
/// detects the routine, the WorkflowTemplateExtractor LEARNS a grounded WorkflowTemplate from it, and the
/// real TemplatePlanCompiler + TemplateReplayer + VerbActuator replay that LEARNED template against a
/// SimulatedApp — driving it to Completed. This closes the loop the way-in promises: "observe any app,
/// gather workflow knowledge, and replicate." The only faked seam is the OS gateway (SimulatedApp stands
/// in for live UIA — that inch is the box, same as the other e2e slices).
/// </summary>
public sealed class WatchLearnReplicateEndToEndTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");
    private const string SessionId = "watch-learn-e2e";

    public WatchLearnReplicateEndToEndTests() => _db.CreateLearningSession(SessionId, "pharm-test-001");
    public void Dispose() => _db.Dispose();

    private void Interaction(int seq, string treeHash, string elementId, DateTimeOffset ts) =>
        _db.InsertBehavioralEvent(SessionId, seq, "interaction", "invoked",
            treeHash, elementId, "Button", "WinForms.Button", null, null, null, null, null, 1, ts.ToString("o"));

    [Fact]
    public async Task Observe_Learn_Replicate_DrivesTheApp_ToCompleted()
    {
        // 1) OBSERVE: six repetitions of a 3-step workflow (open → middle → approve), all Button clicks
        //    so each replays as a click that advances the app.
        var baseTime = DateTimeOffset.UtcNow;
        int seq = 1;
        for (int rep = 0; rep < 6; rep++)
        {
            var t0 = baseTime.AddMinutes(rep * 2);
            Interaction(seq++, "treeOpen", "btnOpen", t0);
            Interaction(seq++, "treeMid", "btnMid", t0.AddSeconds(5));
            Interaction(seq++, "treeApprove", "btnApprove", t0.AddSeconds(10));
        }
        new RoutineDetector(_db, SessionId).DetectAndPersist();
        Assert.NotEmpty(_db.GetLearnedRoutines(SessionId));

        // 2) LEARN: extract a grounded WorkflowTemplate from the observed routine (notepad-scoped so the
        //    actuation allowlist passes on replay).
        var extractor = new WorkflowTemplateExtractor(
            _db, SessionId, "learned", "notepad",
            () => new PmsVersionFingerprint("notepad", "schema", "dialect", "1.0"),
            WorkflowTemplateThresholds.Default);
        var learned = extractor.ExtractAndPersist();
        Assert.NotEmpty(learned);
        var template = learned[0];
        Assert.Equal(3, template.Steps.Count);
        Assert.Equal("btnOpen", template.Steps[0].Target.AutomationId);
        Assert.NotNull(template.RoutineHashOrigin); // this template was LEARNED from observation, not hand-built

        // 3) REPLICATE: a SimulatedApp whose screens advance through the learned steps, driven by the
        //    REAL compiler + replayer + actuation pipeline.
        var app = new SimulatedApp(
            initialVisible: new[] { SimulatedApp.Sig("Button", "btnOpen") },
            onClick: new()
            {
                ["btnOpen"] = new[] { SimulatedApp.Sig("Button", "btnMid") },
                ["btnMid"] = new[] { SimulatedApp.Sig("Button", "btnApprove") },
                ["btnApprove"] = new[] { SimulatedApp.Sig("Text", "done") },
            });

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

        var replayer = new TemplateReplayer(
            new SimulatedAppPerceiver(app), actuator, new FakeSafetyGate(), AgenticTestKit.NoDelay);

        var plan = TemplatePlanCompiler.Compile(template);

        var result = await replayer.ReplayAsync(
            plan,
            new AgentObjective("replicate the learned workflow", "task.watch-learn", "pharm-test-001"),
            new ActuationContext("pharm-test-001", "task.watch-learn", DryRun: false),
            new TemplateReplayOptions { SettleStableReads = 1 },
            CancellationToken.None);

        // The LEARNED template drove the real app through every step to completion.
        Assert.Equal(ReplayOutcome.Completed, result.Outcome);
        Assert.Equal(3, result.StepsCompleted);
        Assert.Contains(SimulatedApp.Sig("Text", "done"), app.VisibleSignatures);
    }
}
