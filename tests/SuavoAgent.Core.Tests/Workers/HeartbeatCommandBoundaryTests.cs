using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public partial class HeartbeatWorkerTests
{
    [Theory]
    [MemberData(nameof(SynchronousSignedDispatchCommands))]
    public async Task SignedDispatcher_RoutesEverySynchronousCommandToItsFailClosedHandler(
        string command)
    {
        var data = BindLiveCommandExpiry(command, new { });
        var signed = Sign(command, JsonSerializer.Serialize(data));
        var response = BuildResponseJson(signed, data);

        await InvokeProcessAsync(response);

        Assert.False(_db.TryRecordNonce(signed.Nonce));
    }

    [Theory]
    [InlineData("run_pricing_job")]
    [InlineData("find_and_run_pricing_job")]
    [InlineData("run_workflow")]
    [InlineData("navigate_app")]
    [InlineData("navigate_pricing")]
    [InlineData("replay_template")]
    [InlineData("run_learned_template")]
    [InlineData("explore_sandbox")]
    [InlineData("replay_skill")]
    public async Task SignedDispatcher_BackgroundCommandsRejectEmptyPayloadWithoutEscapingTask(
        string command)
    {
        await InvokeProcessAsync(BuildResponseJson(command, new { }));

        await Task.Delay(50);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("17")]
    [InlineData("\"string\"")]
    [InlineData("[]")]
    public async Task SignedDispatcher_NonObjectDataShapeIsContained(string dataJson)
    {
        var response = JsonSerializer.Deserialize<JsonElement>("{\"data\":" + dataJson + "}");

        await InvokeProcessAsync(response);
    }

    [Fact]
    public async Task SelfUninstall_RetryableFailureDoesNotConsumePersistentOrMemoryNonce()
    {
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(4).ToString("O");
        var data = new
        {
            commandId = "33333333-3333-4333-8333-333333333333",
            expiresAt,
        };
        var signed = Sign("self_uninstall", JsonSerializer.Serialize(data));
        var response = BuildResponseJson(signed, data);

        // This fixture has no cloud client. Both deliveries must reach the
        // retryable handler and neither may burn the exact signed nonce.
        await InvokeProcessAsync(response);
        await InvokeProcessAsync(response);

        using var connection = new SqliteConnection($"Data Source={_dbPath};Mode=ReadOnly");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT count(*) FROM command_nonces WHERE nonce = @nonce";
        command.Parameters.AddWithValue("@nonce", signed.Nonce);
        Assert.Equal(0L, (long)command.ExecuteScalar()!);
    }

    [Theory]
    [MemberData(nameof(MalformedCommandPayloads))]
    public async Task CommandHandler_MalformedOrUnauthorizedPayloadFailsClosed(
        string handler,
        string command,
        string dataJson)
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(handler, command, dataJson);

        Assert.Equal(before, _db.GetAuditEntryCount());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"ndc\":17}")]
    [InlineData("{\"ndc\":\"\"}")]
    [InlineData("{\"ndc\":\"123\"}")]
    [InlineData("{\"ndc\":\"1234567890x\"}")]
    public async Task NavigatePricing_RejectsEveryNonNdcShapeWithoutAudit(string dataJson)
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleNavigatePricingAsync",
            "navigate_pricing",
            dataJson);

        Assert.Equal(before, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task NavigatePricing_ValidNdcDelegatesThroughSingleNavigatePipeline()
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleNavigatePricingAsync",
            "navigate_pricing",
            """
            {
              "commandId":"cmd_nav_price",
              "ndc":"12345678901",
              "runId":"run_nav_price",
              "maxSteps":2,
              "deadlineSeconds":1,
              "dryRun":true
            }
            """);

        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Fact]
    public async Task NavigateApp_ValidDryRunReachesAuditedPipelineAndFailsWithoutRuntimePorts()
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleNavigateAppAsync",
            "navigate_app",
            """
            {
              "commandId":"cmd_nav",
              "objective":"inspect the synthetic calculator surface",
              "taskKey":"coverage.synthetic.navigation",
              "runId":"run_nav",
              "maxSteps":1,
              "deadlineSeconds":1,
              "dryRun":true
            }
            """);

        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Theory]
    [InlineData("calculator")]
    [InlineData("calc.exe")]
    [InlineData("CALCULATOR")]
    public async Task SandboxExplore_AllowlistedIdentityReachesAuditedPipelineOnly(string app)
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleSandboxExploreAsync",
            "explore_sandbox",
            JsonSerializer.Serialize(new
            {
                commandId = "cmd_explore",
                objective = "inspect the synthetic calculator surface",
                taskKey = "coverage.synthetic.explore",
                app,
                runId = "run_explore",
                maxSteps = 1,
                deadlineSeconds = 1,
            }));

        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Fact]
    public async Task ReplaySkill_MissingMismatchedUnreadableAndEmptySkillsFailBeforeActuation()
    {
        await InvokePrivateHandlerAsync(
            "HandleReplaySkillAsync",
            "replay_skill",
            "{\"skillId\":\"missing-skill\"}");

        _db.UpsertVerifiedSkill(
            "wrong-pharmacy-skill",
            "other-pharmacy",
            "task",
            "calculator",
            "[]",
            new string('a', 64));
        await InvokePrivateHandlerAsync(
            "HandleReplaySkillAsync",
            "replay_skill",
            "{\"skillId\":\"wrong-pharmacy-skill\"}");

        _db.UpsertVerifiedSkill(
            "unreadable-skill",
            TestPharmacyId,
            "task",
            "calculator",
            "not-json",
            new string('b', 64));
        await InvokePrivateHandlerAsync(
            "HandleReplaySkillAsync",
            "replay_skill",
            "{\"skillId\":\"unreadable-skill\"}");

        _db.UpsertVerifiedSkill(
            "empty-skill",
            TestPharmacyId,
            "task",
            "calculator",
            "[]",
            new string('c', 64));
        await InvokePrivateHandlerAsync(
            "HandleReplaySkillAsync",
            "replay_skill",
            "{\"skillId\":\"empty-skill\"}");

        await InvokePrivateHandlerAsync(
            "HandleReplaySkillAsync",
            "replay_skill",
            "{\"taskKey\":\"missing-task\",\"app\":\"calculator\"}");
    }

    [Fact]
    public async Task AbortNavigation_MatchingRunCancelsOnlyExactActiveLease()
    {
        using var active = new CancellationTokenSource();
        SetPrivateField("_activeNavigationCts", active);
        SetPrivateField("_activeNavigationRunId", "run-active");

        await InvokePrivateHandlerAsync(
            "HandleAbortNavigationAsync",
            "abort_navigation",
            "{\"runId\":\"run-other\",\"reason\":\"operator_requested\"}");
        Assert.False(active.IsCancellationRequested);

        await InvokePrivateHandlerAsync(
            "HandleAbortNavigationAsync",
            "abort_navigation",
            "{\"runId\":\"run-active\",\"reason\":\"operator_requested\"}");
        Assert.True(active.IsCancellationRequested);
    }

    [Fact]
    public async Task AbortWorkflow_MatchingRunCancelsAndAuditsExactActiveLease()
    {
        var activeRunId = Guid.NewGuid().ToString("D");
        var otherRunId = Guid.NewGuid().ToString("D");
        using var active = new CancellationTokenSource();
        SetPrivateField("_activeWorkflowCts", active);
        SetPrivateField("_activeWorkflowRunId", activeRunId);
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleAbortWorkflowAsync",
            "abort_workflow",
            AbortWorkflowPayload(otherRunId));
        Assert.False(active.IsCancellationRequested);
        Assert.Equal(before, _db.GetAuditEntryCount());

        await InvokePrivateHandlerAsync(
            "HandleAbortWorkflowAsync",
            "abort_workflow",
            AbortWorkflowPayload(activeRunId));
        Assert.True(active.IsCancellationRequested);
        Assert.True(_db.GetAuditEntryCount() > before);
    }

    [Theory]
    [InlineData("{\"workflow_run_id\":\"11111111-1111-4111-8111-111111111111\",\"reason\":\"dashboard_abort\"}")]
    [InlineData("{\"schemaVersion\":1,\"workflowRunId\":\"11111111-1111-4111-8111-111111111111\",\"reasonCode\":\"operator_requested\",\"commandId\":\"22222222-2222-4222-8222-222222222222\",\"expiresAt\":\"2099-01-01T00:00:00.0000000+00:00\"}")]
    [InlineData("{\"schemaVersion\":1,\"workflowRunId\":\"11111111-1111-4111-8111-111111111111\",\"reasonCode\":\"dashboard_abort\",\"commandId\":\"22222222-2222-4222-8222-222222222222\",\"expiresAt\":\"2099-01-01T00:00:00.0000000+00:00\",\"unexpected\":true}")]
    [InlineData("{\"schemaVersion\":2,\"workflowRunId\":\"11111111-1111-4111-8111-111111111111\",\"reasonCode\":\"dashboard_abort\",\"commandId\":\"22222222-2222-4222-8222-222222222222\",\"expiresAt\":\"2099-01-01T00:00:00.0000000+00:00\"}")]
    public async Task AbortWorkflow_RejectsEveryNonExactPayload(string payload)
    {
        using var active = new CancellationTokenSource();
        SetPrivateField("_activeWorkflowCts", active);
        SetPrivateField(
            "_activeWorkflowRunId",
            "11111111-1111-4111-8111-111111111111");
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleAbortWorkflowAsync", "abort_workflow", payload);

        Assert.False(active.IsCancellationRequested);
        Assert.Equal(before, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task ConfigurationHandlers_RefuseMissingDependenciesAndMalformedDataWithoutMutation()
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleInstallPioneerRxApprovalAsync",
            "install_pioneerrx_approval",
            "{}");
        await InvokePrivateHandlerAsync(
            "HandleSetVisionConfigAsync",
            "set_vision_config",
            "{}");
        await InvokePrivateHandlerAsync(
            "HandleDiscoverElementsAsync",
            "discover_elements",
            "{\"process\":\"calculator\",\"max\":10}");
        await InvokePrivateHandlerAsync(
            "HandleSetReasoningConfigAsync",
            "set_reasoning_config",
            "{}");
        await InvokePrivateHandlerAsync(
            "HandleChatAsync",
            "chat",
            "{\"prompt\":\"Give a structural health summary\"}");
        await InvokePrivateHandlerAsync(
            "HandleExtendAppAllowlistAsync",
            "extend_app_allowlist",
            "{\"apps\":{\"unsafe\":\"unsafe.exe\"}}");
        await InvokePrivateHandlerAsync(
            "HandleUpdateSelectorCommandAsync",
            "update_selector",
            "{}");

        Assert.Equal(before, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task SelfUninstall_MissingDataAndInvalidCommandIdNeverQueueRemoval()
    {
        var before = _db.GetAuditEntryCount();

        await InvokePrivateHandlerAsync(
            "HandleSelfUninstallAsync",
            "self_uninstall",
            "{}");
        await InvokePrivateHandlerAsync(
            "HandleSelfUninstallAsync",
            "self_uninstall",
            "{\"commandId\":\"not-canonical\"}");

        Assert.Equal(before, _db.GetAuditEntryCount());
    }

    public static IEnumerable<object[]> MalformedCommandPayloads()
    {
        yield return ["HandleNavigateAppAsync", "navigate_app", "{}"];
        yield return ["HandleNavigateAppAsync", "navigate_app", "{\"objective\":\"x\"}"];
        yield return ["HandleNavigateAppAsync", "navigate_app", "{\"taskKey\":\"x\"}"];
        yield return ["HandleNavigateAppAsync", "navigate_app", "{\"objective\":1,\"taskKey\":\"x\"}"];
        yield return ["HandleNavigateAppAsync", "navigate_app", "{\"objective\":\"x\",\"taskKey\":1}"];
        yield return ["HandleSandboxExploreAsync", "explore_sandbox", "{}"];
        yield return ["HandleSandboxExploreAsync", "explore_sandbox", "{\"objective\":\"x\",\"taskKey\":\"x\"}"];
        yield return ["HandleSandboxExploreAsync", "explore_sandbox", "{\"objective\":\"x\",\"taskKey\":\"x\",\"app\":\"pioneerrx\"}"];
        yield return ["HandleSandboxExploreAsync", "explore_sandbox", "{\"objective\":\"x\",\"taskKey\":\"x\",\"app\":1}"];
        yield return ["HandleReplayTemplateAsync", "replay_template", "{}"];
        yield return ["HandleReplayTemplateAsync", "replay_template", "{\"taskKey\":1}"];
        yield return ["HandleReplayTemplateAsync", "replay_template", "{\"taskKey\":\"x\"}"];
        yield return ["HandleReplayTemplateAsync", "replay_template", "{\"taskKey\":\"x\",\"template\":{}}"];
        yield return ["HandleReplaySkillAsync", "replay_skill", "{}"];
        yield return ["HandleReplaySkillAsync", "replay_skill", "{\"skillId\":1}"];
        yield return ["HandleRunLearnedTemplateAsync", "run_learned_template", "{}"];
        yield return ["HandleRunLearnedTemplateAsync", "run_learned_template", "{\"commandId\":\"bad\"}"];
        yield return ["HandleAbortWorkflowAsync", "abort_workflow", "{}"];
    }

    public static TheoryData<string> SynchronousSignedDispatchCommands => new()
    {
        "decommission",
        "repair",
        "repair_agent",
        "collect_health_probe",
        "fetch_diagnostics",
        "export_pioneerrx_shadow_fixture",
        "update",
        "approve_pom",
        "acknowledge_drift",
        "approve_candidate",
        "reject_candidate",
        "reapprove_candidate",
        "force_relearn",
        "adjust_window",
        "acknowledge_stale",
        "show_cursor",
        "show_intent_cursor",
        "computer_use_observe",
        "computer_use_propose",
        "transition_auto_rule_approval",
        "abort_workflow",
        "update_selector",
        "abort_navigation",
        "force_learning_phase",
        "extend_app_allowlist",
        "discover_elements",
        "chat",
        "set_reasoning_config",
    };

    private async Task InvokePrivateHandlerAsync(
        string handlerName,
        string commandName,
        string dataJson)
    {
        var commandElement = JsonSerializer.Deserialize<JsonElement>(
            "{\"data\":" + dataJson + "}");
        var handler = typeof(HeartbeatWorker).GetMethod(
            handlerName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(handler);

        var arguments = handler!.GetParameters().Length == 2
            ? new object?[] { commandElement, CancellationToken.None }
            : new object?[] { commandElement, Sign(commandName), CancellationToken.None };
        var task = Assert.IsAssignableFrom<Task>(handler.Invoke(_worker, arguments));
        await task;
    }

    private void SetPrivateField(string fieldName, object? value)
    {
        var field = typeof(HeartbeatWorker).GetField(
            fieldName,
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        field!.SetValue(_worker, value);
    }

    private static string AbortWorkflowPayload(string workflowRunId) =>
        JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            workflowRunId,
            reasonCode = "dashboard_abort",
            commandId = Guid.NewGuid().ToString("D"),
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(4).ToString("O"),
        });
}
