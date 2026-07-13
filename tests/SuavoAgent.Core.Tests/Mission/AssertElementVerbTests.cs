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
/// Core-side guards for the assert_element verification verb. The real UIA read + compare is
/// box-validated (Helper-side); these pin the precondition contract: process allowlist, a non-empty
/// expected, and at least one locator (automation_id | name | control_type).
/// </summary>
public sealed class AssertElementVerbTests
{
    private static VerbContext Ctx(Dictionary<string, object?> parameters)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IActuationGateway>(new StubGateway());
        var provider = services.BuildServiceProvider();
        return new VerbContext(
            PharmacyId: "ph",
            Charter: MissionCharterLoader.BuildDefaultCharter("ph"),
            Audit: new AuditChain(),
            InvocationId: "inv-1",
            Actor: "test",
            Parameters: parameters,
            Services: provider,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private static Dictionary<string, object?> Params(
        string? processName = "calc.exe",
        string? expected = "12",
        string? automationId = "CalculatorResults",
        string? name = null,
        string? controlType = null) =>
        new()
        {
            ["process_name"] = processName,
            ["expected"] = expected,
            ["automation_id"] = automationId,
            ["name"] = name,
            ["control_type"] = controlType,
        };

    [Fact]
    public async Task CheckPreconditions_AllValid_Ok()
    {
        var result = await new AssertElementVerb().CheckPreconditionsAsync(Ctx(Params()), CancellationToken.None);
        Assert.True(result.Satisfied);
    }

    [Fact]
    public async Task CheckPreconditions_EmptyExpected_Fails()
    {
        var result = await new AssertElementVerb().CheckPreconditionsAsync(Ctx(Params(expected: "")), CancellationToken.None);
        Assert.False(result.Satisfied);
        Assert.Equal("expected_non_empty", result.FailedPreconditionId);
    }

    [Fact]
    public async Task CheckPreconditions_NoLocator_Fails()
    {
        var result = await new AssertElementVerb().CheckPreconditionsAsync(
            Ctx(Params(automationId: null, name: null, controlType: null)), CancellationToken.None);
        Assert.False(result.Satisfied);
        Assert.Equal("locator_required", result.FailedPreconditionId);
    }

    [Fact]
    public async Task CheckPreconditions_LocatorByControlTypeOnly_Ok()
    {
        var result = await new AssertElementVerb().CheckPreconditionsAsync(
            Ctx(Params(automationId: null, controlType: "Document")), CancellationToken.None);
        Assert.True(result.Satisfied);
    }

    [Fact]
    public async Task CheckPreconditions_ProcessNotAllowlisted_Fails()
    {
        var result = await new AssertElementVerb().CheckPreconditionsAsync(
            Ctx(Params(processName: "PioneerPharmacy")), CancellationToken.None);
        Assert.False(result.Satisfied);
        Assert.Equal("process_not_allowlisted", result.FailedPreconditionId);
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
        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(0, true, "reload"));
        public Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.Success(0, req.DryRun, "assert"));
        public Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct) =>
            Task.FromResult(ActuationResult.SuccessWithPayload(0, req.DryRun, "discover", "[]"));
    }
}
