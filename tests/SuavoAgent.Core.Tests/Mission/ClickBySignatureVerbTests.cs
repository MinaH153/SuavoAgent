using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Preconditions for the click_by_signature replay verb. The real UIA resolution is box-validated;
/// these pin the Core-side guards (required structural params + the sandbox allowlist + gateway present).
/// </summary>
public sealed class ClickBySignatureVerbTests
{
    private static Dictionary<string, object?> Params(
        string? automationId = "saveBtn", string? controlType = "Button", string? processName = "notepad") =>
        new() { ["automationId"] = automationId, ["controlType"] = controlType, ["process_name"] = processName };

    [Fact]
    public async Task CheckPreconditions_AllValid_Ok()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActuationGateway>(new StubGateway());
        using var provider = services.BuildServiceProvider();

        var ctx = new VerbContext(
            PharmacyId: "ph",
            Charter: MissionCharterLoader.BuildDefaultCharter("ph"),
            Audit: new AuditChain(),
            InvocationId: "inv-1",
            Actor: "test",
            Parameters: Params(),
            Services: provider,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await new ClickBySignatureVerb().CheckPreconditionsAsync(ctx, CancellationToken.None);

        Assert.True(result.Satisfied);
    }

    private sealed class StubGateway : IActuationGateway
    {
        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) =>
            Task.FromResult(new ActuationGateState(true, false, null, null, null));
        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(1, true, "x"));
        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(1, true, "x"));
    }

    [Fact]
    public async Task CheckPreconditions_MissingAutomationId_Fails()
    {
        using var harness = new MissionTestHarness();
        var verb = new ClickBySignatureVerb();
        var ctx = harness.Context(verb, Params(automationId: ""));

        var result = await verb.CheckPreconditionsAsync(ctx, CancellationToken.None);

        Assert.False(result.Satisfied);
        Assert.Equal("automation_id_non_empty", result.FailedPreconditionId);
    }

    [Fact]
    public async Task CheckPreconditions_MissingControlType_Fails()
    {
        using var harness = new MissionTestHarness();
        var verb = new ClickBySignatureVerb();
        var ctx = harness.Context(verb, Params(controlType: null));

        var result = await verb.CheckPreconditionsAsync(ctx, CancellationToken.None);

        Assert.False(result.Satisfied);
        Assert.Equal("control_type_non_empty", result.FailedPreconditionId);
    }

    [Fact]
    public async Task CheckPreconditions_ProcessNotAllowlisted_Fails()
    {
        using var harness = new MissionTestHarness();
        var verb = new ClickBySignatureVerb();
        var ctx = harness.Context(verb, Params(processName: "PioneerPharmacy"));

        var result = await verb.CheckPreconditionsAsync(ctx, CancellationToken.None);

        Assert.False(result.Satisfied);
        Assert.Equal("process_not_allowlisted", result.FailedPreconditionId);
    }
}
