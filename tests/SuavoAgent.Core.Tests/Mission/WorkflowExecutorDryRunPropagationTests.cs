using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Bug 21 (MinaH153/SuavoAgent#63): the workflow definition's <c>dry_run</c>
/// flag must reach the Helper's actuation gate via <see cref="VerbContext"/>
/// and the IPC request DTOs. Before Bug 21, <c>WorkflowExecutor</c> read
/// <c>definition.DryRun</c> only for log lines and audit metadata — it never
/// flowed into <c>VerbDispatcher</c>, the verbs, the gateway requests, or
/// any safety enforcement path. Real input could fire on a workflow
/// dispatched as <c>dry_run=true</c>.
///
/// These tests pin the propagation contract end-to-end through the
/// production verbs (LaunchSandboxAppVerb, PressKeysVerb, TypeIntoFieldVerb,
/// ClickByLabelVerb) and the audit row.
/// </summary>
public sealed class WorkflowExecutorDryRunPropagationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaunchSandboxAppVerb_PropagatesDefinitionDryRun(bool dryRun)
    {
        var harness = new RecordingHarness();
        var def = Def("run-launch", dryRun, Step("launch_sandbox_app", "{\"app_key\":\"calculator\"}"));
        var result = await harness.Run(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.NotNull(harness.Gateway.LastLaunchSandboxApp);
        Assert.Equal(dryRun, harness.Gateway.LastLaunchSandboxApp!.DryRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PressKeysVerb_PropagatesDefinitionDryRun(bool dryRun)
    {
        var harness = new RecordingHarness();
        var def = Def("run-press", dryRun, Step("press_keys", "{\"chords\":[\"Enter\"],\"process_name\":\"calc.exe\"}"));
        var result = await harness.Run(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.NotNull(harness.Gateway.LastPressKeys);
        Assert.Equal(dryRun, harness.Gateway.LastPressKeys!.DryRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TypeIntoFieldVerb_PropagatesDefinitionDryRun(bool dryRun)
    {
        var harness = new RecordingHarness();
        var def = Def("run-type", dryRun, Step("type_into_field", "{\"text\":\"hello\",\"process_name\":\"calc.exe\"}"));
        var result = await harness.Run(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.NotNull(harness.Gateway.LastTypeText);
        Assert.Equal(dryRun, harness.Gateway.LastTypeText!.DryRun);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ClickByLabelVerb_PropagatesDefinitionDryRun(bool dryRun)
    {
        var harness = new RecordingHarness();
        var def = Def("run-click", dryRun,
            Step("click_by_label", "{\"label\":\"Save\",\"process_name\":\"calc.exe\"}"));
        var result = await harness.Run(def);
        Assert.Equal(WorkflowRunOutcome.Completed, result.Outcome);
        Assert.NotNull(harness.Gateway.LastClickByLabel);
        Assert.Equal(dryRun, harness.Gateway.LastClickByLabel!.DryRun);
    }

    [Fact]
    public async Task AuditRow_RecordsRequestedAndEffectiveDryRun()
    {
        // Workflow requests dry_run=false; gateway forces effective dry_run=true
        // (e.g. local gate's ActuationGate.IsDryRun was true). The audit row
        // must reflect both — requested=false (what the workflow asked for)
        // and effective=true (what actually executed). Joining these is how
        // operators reconstruct chain of custody.
        var harness = new RecordingHarness();
        harness.Gateway.NextResult = ActuationResult.Success(5, dryRun: true, evidenceHash: "evidence");
        var def = Def("run-audit", dryRun: false, Step("press_keys", "{\"chords\":[\"Enter\"],\"process_name\":\"calc.exe\"}"));

        await harness.Run(def);

        Assert.Single(harness.Audit.StepEntries);
        var entry = harness.Audit.StepEntries[0];
        Assert.False(entry.DryRun);             // requested (definition-level)
        Assert.True(entry.EffectiveDryRun);     // effective (from gateway result)
    }

    [Fact]
    public async Task AuditRow_GatewayReject_PreservesEffectiveDryRunFromResult()
    {
        // Codex review of #67 (HIGH-2): the previous behavior asserted
        // EffectiveDryRun=null on any verb that returned Fail. But the
        // Helper KNOWS the effective dry-run state at PHI / gate / parse
        // rejection — it returns it inside ActuationResult.DryRun. The
        // verb must preserve that in its Fail-output so the audit row
        // captures the truth instead of an audit blind spot
        // ("indistinguishable from no-actuation-attempted").
        var harness = new RecordingHarness();
        harness.Gateway.NextResult = ActuationResult.Reject("gate_paused", "test", dryRun: true);
        var def = Def("run-reject", dryRun: false, Step("press_keys", "{\"chords\":[\"Enter\"],\"process_name\":\"calc.exe\"}"));

        await harness.Run(def);

        Assert.Single(harness.Audit.StepEntries);
        var entry = harness.Audit.StepEntries[0];
        Assert.False(entry.DryRun);             // requested = workflow definition
        Assert.True(entry.EffectiveDryRun);     // effective = gate forced dry-run
    }

    [Fact]
    public async Task AuditRow_ManifestResolutionFailure_RecordsRequestedDryRun()
    {
        // Codex review of #67 (HIGH-1): manifest-resolution failures
        // (unknown verb) previously hardcoded DryRun=false in the audit
        // entry. A dry-run workflow that hit an unresolved manifest then
        // wrote an audit row saying "requested live" — chain of custody
        // lies before any actuation surface is even reached.
        var harness = new RecordingHarness();
        var def = Def("run-manifest-miss", dryRun: true, Step("verb_that_does_not_exist", "{}"));

        await harness.Run(def);

        // The manifest-resolution-failed path writes one audit row.
        Assert.Single(harness.Audit.StepEntries);
        var entry = harness.Audit.StepEntries[0];
        Assert.True(entry.DryRun);              // requested = workflow definition (was hardcoded false)
        Assert.Null(entry.EffectiveDryRun);     // no actuation surface reached — null is honest
        Assert.Equal("rejected", entry.Outcome);
    }

    [Fact]
    public async Task AuditRow_ActuatingVersionMismatch_RecordsGateEffectiveDryRun()
    {
        var harness = new RecordingHarness();
        harness.Gateway.GateState = new ActuationGateState(
            Enabled: true,
            DryRun: true,
            PausedUntilUtc: null,
            PauseReason: null,
            KillSwitchTrippedUtc: null);
        var step = Step(
            "press_keys",
            "{\"chords\":[\"Enter\"],\"process_name\":\"calc.exe\"}") with
        {
            VerbVersion = "999.0.0",
        };
        var def = Def("run-version-miss", dryRun: false, step);

        await harness.Run(def);

        var entry = Assert.Single(harness.Audit.StepEntries);
        Assert.False(entry.RequestedDryRun);
        Assert.True(entry.EffectiveDryRun);
        Assert.Equal("manifest_resolution_failed", entry.ErrorKind);
        Assert.Null(harness.Gateway.LastPressKeys);
    }

    [Fact]
    public async Task AuditRow_SkippedActuatingVerb_RecordsGateEffectiveDryRun()
    {
        var harness = new RecordingHarness();
        harness.Gateway.GateState = new ActuationGateState(
            Enabled: true,
            DryRun: true,
            PausedUntilUtc: null,
            PauseReason: null,
            KillSwitchTrippedUtc: null);
        var step = Step(
            "press_keys",
            "{\"chords\":[\"Enter\"],\"process_name\":\"calc.exe\"}") with
        {
            Condition = new WorkflowConditionDto("never", null, null, null),
        };
        var def = Def("run-skip", dryRun: false, step);

        await harness.Run(def);

        var entry = Assert.Single(harness.Audit.StepEntries);
        Assert.Equal("skipped", entry.Outcome);
        Assert.False(entry.RequestedDryRun);
        Assert.True(entry.EffectiveDryRun);
        Assert.Null(harness.Gateway.LastPressKeys);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    private static WorkflowStepDto Step(string verb, string paramsJson) =>
        new(verb, "1.0.0", null, JsonDocument.Parse(paramsJson).RootElement, null);

    private static WorkflowDefinitionDto Def(string runId, bool dryRun, params WorkflowStepDto[] steps) =>
        new(runId, "wf", "DEMO", "1.0.0", "abc", dryRun, "sandbox", steps);

    private sealed class RecordingHarness
    {
        public RecordingGateway Gateway { get; } = new();
        public RecordingAuditClient Audit { get; } = new();
        public ServiceProvider Provider { get; }
        public WorkflowExecutor Executor { get; }
        public AuditChain AuditChain { get; } = new();
        public MissionCharter Charter { get; }

        public RecordingHarness()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IActuationGateway>(Gateway);
            services.AddSingleton<IAuthzPolicy>(new CharterDrivenAuthzPolicy());
            services.AddSingleton<VerbDispatcher>();
            services.AddSingleton(NullLogger<WorkflowExecutor>.Instance);
            Provider = services.BuildServiceProvider();

            var registry = VerbRegistry.Build(
                new[] { typeof(IVerb).Assembly },
                NullLogger<VerbRegistry>.Instance);

            Executor = new WorkflowExecutor(
                registry,
                Provider.GetRequiredService<VerbDispatcher>(),
                Gateway,
                Audit,
                NullLogger<WorkflowExecutor>.Instance);

            Charter = new MissionCharter(
                CharterId: Guid.Empty,
                PharmacyId: "ph-1",
                Version: 1,
                EffectiveFrom: DateTimeOffset.UtcNow,
                Objectives: new List<MissionObjective> { new("o1", "test", 1) },
                Constraints: Array.Empty<MissionConstraint>(),
                PriorityOrdering: new MissionPriorityOrdering(new[] { "o1" }),
                Tolerance: new MissionToleranceThresholds(60, 3, 0.5),
                SignedByOperator: "test",
                SignedAt: DateTimeOffset.UtcNow);
        }

        public Task<WorkflowExecutor.WorkflowExecutionResult> Run(WorkflowDefinitionDto def) =>
            Executor.ExecuteAsync(def, Provider, AuditChain, Charter, "ph-1", "test", CancellationToken.None);
    }

    private sealed class RecordingGateway : IActuationGateway
    {
        public ActuationGateState GateState { get; set; } =
            new(Enabled: true, DryRun: false, PausedUntilUtc: null, PauseReason: null, KillSwitchTrippedUtc: null);

        public ActuationResult? NextResult { get; set; }

        public LaunchSandboxAppRequest? LastLaunchSandboxApp { get; private set; }
        public PressKeysRequest? LastPressKeys { get; private set; }
        public TypeTextRequest? LastTypeText { get; private set; }
        public ClickByLabelRequest? LastClickByLabel { get; private set; }

        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) => Task.FromResult(GateState);

        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct)
        {
            LastClickByLabel = req;
            return Task.FromResult(NextResult ?? ActuationResult.Success(1, req.DryRun, "ok"));
        }

        public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct)
            => Task.FromResult(NextResult ?? ActuationResult.Success(1, req.DryRun, "ok"));

        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct)
        {
            LastTypeText = req;
            return Task.FromResult(NextResult ?? ActuationResult.Success(1, req.DryRun, "ok"));
        }

        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct)
        {
            LastPressKeys = req;
            return Task.FromResult(NextResult ?? ActuationResult.Success(1, req.DryRun, "ok"));
        }

        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct)
        {
            LastLaunchSandboxApp = req;
            return Task.FromResult(NextResult ?? ActuationResult.Success(1, req.DryRun, "ok"));
        }

        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(0, true, "reload"));
        public Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct) =>
            Task.FromResult(NextResult ?? ActuationResult.Success(0, req.DryRun, "assert"));

        public Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct) =>
            Task.FromResult(NextResult ?? ActuationResult.SuccessWithPayload(0, req.DryRun, "discover", "[]"));
    }

    private sealed class RecordingAuditClient : IWorkflowAuditClient
    {
        public List<WorkflowStepAuditEntry> StepEntries { get; } = new();
        public WorkflowRunOutcome? LastRunOutcome { get; private set; }
        public string? LastAbortReason { get; private set; }

        public Task PostStepAuditAsync(WorkflowStepAuditEntry entry, CancellationToken ct)
        {
            StepEntries.Add(entry);
            return Task.CompletedTask;
        }

        public Task PostRunCompletedAsync(string workflowRunId, WorkflowRunOutcome outcome, string? abortReason, CancellationToken ct)
        {
            LastRunOutcome = outcome;
            LastAbortReason = abortReason;
            return Task.CompletedTask;
        }
    }
}
