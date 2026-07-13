using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.ActionGrammarV1.Workflows;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Mission;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatWorkflowHandlerBoundaryTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(
        Path.GetTempPath(), "suavo-heartbeat-workflow-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly AgentStateDb _db;
    private readonly ServiceProvider _services;
    private readonly RecordingGateway _gateway = new();
    private readonly RecordingWorkflowAudit _workflowAudit = new();
    private readonly HeartbeatWorker _worker;
    private readonly MethodInfo _handler;
    private readonly MethodInfo _replayHandler;

    public HeartbeatWorkflowHandlerBoundaryTests()
    {
        _db = new AgentStateDb(_dbPath);
        var registry = VerbRegistry.Build(
            [typeof(IVerb).Assembly], NullLogger<VerbRegistry>.Instance);
        var charter = new MissionCharter(
            Guid.NewGuid(), "pharmacy-workflow", 1, DateTimeOffset.UtcNow,
            [new MissionObjective("workflow", "execute signed workflow", 1)],
            Array.Empty<MissionConstraint>(),
            new MissionPriorityOrdering(["workflow"]),
            new MissionToleranceThresholds(60, 3, 0.5),
            "operator", DateTimeOffset.UtcNow);
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(registry);
        services.AddSingleton<IActuationGateway>(_gateway);
        services.AddSingleton<IAuthzPolicy>(new CharterDrivenAuthzPolicy());
        services.AddSingleton<VerbDispatcher>();
        services.AddSingleton<IWorkflowAuditClient>(_workflowAudit);
        services.AddSingleton<Microsoft.Extensions.Logging.ILogger<WorkflowExecutor>>(
            NullLogger<WorkflowExecutor>.Instance);
        services.AddSingleton<WorkflowExecutor>();
        services.AddSingleton(charter);
        services.AddSingleton(new AutopilotRunCoordinator());
        _services = services.BuildServiceProvider();
        _worker = new HeartbeatWorker(
            NullLogger<HeartbeatWorker>.Instance,
            Options.Create(new AgentOptions
            {
                AgentId = "agent-workflow",
                PharmacyId = "pharmacy-workflow",
            }),
            _services,
            _db);
        _handler = typeof(HeartbeatWorker).GetMethod(
            "HandleRunWorkflowAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Workflow command handler was not found.");
        _replayHandler = typeof(HeartbeatWorker).GetMethod(
            "HandleReplayTemplateAsync", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Replay command handler was not found.");
    }

    [Fact]
    public async Task ValidDryRun_ExecutesThroughRegistryGatewayAndAuditThenClearsActiveRun()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            workflow_run_id = "run-handler-success",
            workflow_id = "workflow-1",
            workflow_name = "SANDBOX_CHECK",
            workflow_version = "1.0.0",
            manifest_signature = "signed-envelope-is-verified-upstream",
            dry_run = true,
            tier = "sandbox",
            steps = new[]
            {
                new
                {
                    verb = "launch_sandbox_app",
                    verb_version = "1.0.0",
                    manifest_hash = (string?)null,
                    @params = new { app_key = "calculator" },
                    description = "dry-run calculator launch",
                    step_id = "launch",
                },
            },
        });

        await InvokeAsync(payload);

        var request = Assert.Single(_gateway.LaunchRequests);
        Assert.Equal("calculator", request.AppKey);
        Assert.True(request.DryRun);
        Assert.Equal(1, _workflowAudit.StepCount);
        Assert.Equal(WorkflowRunOutcome.Completed, _workflowAudit.Outcome);
        Assert.Equal("run-handler-success", _workflowAudit.RunId);
        Assert.True(_db.GetAuditEntryCount() >= 1);
        Assert.Null(ReadField<CancellationTokenSource>("_activeWorkflowCts"));
        Assert.Null(ReadField<string>("_activeWorkflowRunId"));
    }

    [Fact]
    public async Task InvalidDefinition_IsRejectedBeforeAuditOrGateway()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            workflow_run_id = "run-invalid",
            workflow_id = "workflow-1",
            workflow_name = "",
            workflow_version = "1.0.0",
            manifest_signature = "signature",
            dry_run = true,
            tier = "sandbox",
            steps = Array.Empty<object>(),
        });

        await InvokeAsync(payload);

        Assert.Empty(_gateway.LaunchRequests);
        Assert.Equal(0, _workflowAudit.StepCount);
        Assert.Equal(0, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task MalformedStepsShape_IsContainedAndDoesNotPoisonSemaphore()
    {
        var malformed = JsonSerializer.Deserialize<JsonElement>("""
            {"workflow_run_id":"run-malformed","workflow_name":"BAD","steps":17}
            """);

        await InvokeAsync(malformed);

        var semaphore = ReadField<SemaphoreSlim>("_workflowSemaphore");
        Assert.NotNull(semaphore);
        Assert.Equal(1, semaphore!.CurrentCount);
        Assert.Empty(_gateway.LaunchRequests);
    }

    [Fact]
    public async Task ConcurrentWorkflow_IsRejectedWithoutDisturbingCurrentLease()
    {
        var semaphore = ReadField<SemaphoreSlim>("_workflowSemaphore");
        Assert.NotNull(semaphore);
        Assert.True(await semaphore!.WaitAsync(0));
        try
        {
            await InvokeAsync(JsonSerializer.SerializeToElement(new
            {
                workflow_run_id = "run-busy",
                workflow_name = "BUSY",
                steps = new[] { new { verb = "launch_sandbox_app" } },
            }));

            Assert.Equal(0, semaphore.CurrentCount);
            Assert.Empty(_gateway.LaunchRequests);
            Assert.Equal(0, _db.GetAuditEntryCount());
        }
        finally
        {
            semaphore.Release();
        }
    }

    [Fact]
    public async Task ReplayTemplate_MissingTaskKeyRejectsBeforeTemplateExecution()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            commandId = "replay-missing-task",
            template = Template(),
        });

        await InvokeReplayAsync(payload);

        Assert.Equal(0, _db.GetAuditEntryCount());
        Assert.Empty(_gateway.LaunchRequests);
    }

    [Fact]
    public async Task ReplayTemplate_MalformedTemplateRejectsWithoutTakingNavigationLease()
    {
        var payload = JsonSerializer.SerializeToElement(new
        {
            commandId = "replay-malformed",
            taskKey = "task.replay",
            template = new { templateId = "incomplete" },
        });

        await InvokeReplayAsync(payload);

        var semaphore = ReadField<SemaphoreSlim>("_navigationSemaphore");
        Assert.NotNull(semaphore);
        Assert.Equal(1, semaphore!.CurrentCount);
        Assert.Equal(0, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task ReplayTemplate_LegacyLiveRequestIsForcedDryRunAndContainedByRuntimeGates()
    {
        var template = Template();
        var payload = JsonSerializer.SerializeToElement(new
        {
            commandId = "replay-valid",
            taskKey = "task.replay",
            runId = "run-replay-handler",
            deadlineSeconds = 2,
            dryRun = false,
            template,
        });

        await InvokeReplayAsync(payload);

        Assert.True(_db.GetAuditEntryCount() >= 1);
        Assert.Null(ReadField<CancellationTokenSource>("_activeNavigationCts"));
        Assert.Null(ReadField<string>("_activeNavigationRunId"));
        Assert.Equal(1, ReadField<SemaphoreSlim>("_navigationSemaphore")!.CurrentCount);
    }

    public void Dispose()
    {
        _services.Dispose();
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private async Task InvokeAsync(JsonElement payload)
    {
        var command = new SuavoAgent.Core.Cloud.SignedCommand(
            "run_workflow", "agent-workflow", "fingerprint-workflow",
            DateTimeOffset.UtcNow.ToString("O"), Guid.NewGuid().ToString("D"),
            "test-key", "test-signature", "test-data-hash");
        var task = Assert.IsAssignableFrom<Task>(
            _handler.Invoke(_worker, [payload, command, CancellationToken.None]));
        await task;
    }

    private async Task InvokeReplayAsync(JsonElement payload)
    {
        var command = Command("replay_template");
        var task = Assert.IsAssignableFrom<Task>(
            _replayHandler.Invoke(_worker, [payload, command, CancellationToken.None]));
        await task;
    }

    private static SuavoAgent.Core.Cloud.SignedCommand Command(string command) => new(
        command, "agent-workflow", "fingerprint-workflow",
        DateTimeOffset.UtcNow.ToString("O"), Guid.NewGuid().ToString("D"),
        "test-key", "test-signature", "test-data-hash");

    private static WorkflowTemplate Template()
    {
        var target = new ElementSignature("Button", "buttonOpen", "ButtonClass");
        var step = new TemplateStep(
            0, TemplateStepKind.Click, target, [target], 1, null, false, null, 0.95, null);
        var steps = new[] { step };
        var screen = WorkflowTemplate.ComputeScreenSignature(step.ExpectedVisible);
        var hash = WorkflowTemplate.ComputeStepsHash(steps);
        return new WorkflowTemplate(
            WorkflowTemplate.ComputeTemplateId(screen, hash), "1.0.0", "replay-skill",
            "calc.exe*", Array.Empty<PmsVersionFingerprint>(), screen, hash, null,
            steps, 0.95, 5, false, "2026-07-12T00:00:00Z", "test", null, null);
    }

    private T? ReadField<T>(string name) where T : class
    {
        var field = typeof(HeartbeatWorker).GetField(
            name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Missing field {name}.");
        return field.GetValue(_worker) as T;
    }

    private sealed class RecordingWorkflowAudit : IWorkflowAuditClient
    {
        internal int StepCount { get; private set; }
        internal string? RunId { get; private set; }
        internal WorkflowRunOutcome? Outcome { get; private set; }

        public Task PostStepAuditAsync(
            WorkflowStepAuditEntry entry,
            CancellationToken ct)
        {
            StepCount++;
            return Task.CompletedTask;
        }

        public Task PostRunCompletedAsync(
            string workflowRunId,
            WorkflowRunOutcome outcome,
            string? abortReason,
            CancellationToken ct)
        {
            RunId = workflowRunId;
            Outcome = outcome;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGateway : IActuationGateway
    {
        internal List<LaunchSandboxAppRequest> LaunchRequests { get; } = [];
        internal List<ClickBySignatureRequest> SignatureClickRequests { get; } = [];
        private static ActuationResult Success(bool dryRun, string evidence) =>
            ActuationResult.Success(1, dryRun, evidence);

        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new ActuationGateState(
                true, true, null, null, null));

        public Task<ActuationResult> LaunchSandboxAppAsync(
            LaunchSandboxAppRequest req,
            CancellationToken ct)
        {
            LaunchRequests.Add(req);
            return Task.FromResult(Success(req.DryRun, "launch-evidence"));
        }

        public Task<ActuationResult> ClickByLabelAsync(
            ClickByLabelRequest req, CancellationToken ct) =>
            Task.FromResult(Success(req.DryRun, "click-label"));
        public Task<ActuationResult> ClickBySignatureAsync(
            ClickBySignatureRequest req, CancellationToken ct)
        {
            SignatureClickRequests.Add(req);
            return Task.FromResult(Success(req.DryRun, "click-signature"));
        }
        public Task<ActuationResult> TypeTextAsync(
            TypeTextRequest req, CancellationToken ct) =>
            Task.FromResult(Success(req.DryRun, "type"));
        public Task<ActuationResult> PressKeysAsync(
            PressKeysRequest req, CancellationToken ct) =>
            Task.FromResult(Success(req.DryRun, "keys"));
        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
            Task.FromResult(Success(true, "reload"));
        public Task<ActuationResult> AssertElementAsync(
            AssertElementRequest req, CancellationToken ct) =>
            Task.FromResult(Success(req.DryRun, "assert"));
        public Task<ActuationResult> DiscoverElementsAsync(
            DiscoverElementsRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.SuccessWithPayload(
                0, req.DryRun, "discover", "[]"));
    }
}
