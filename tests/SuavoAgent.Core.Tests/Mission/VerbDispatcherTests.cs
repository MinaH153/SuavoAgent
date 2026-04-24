using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class VerbDispatcherTests
{
    [Fact]
    public async Task SuccessfulVerb_RecordsRollbackThenExecuted_AuditChain()
    {
        using var harness = new MissionTestHarness();
        harness.Adapter.SeedPatient(
            identifier: "IDX-001",
            patientId: "PID-001",
            displayNameHash: "aaaaaa",
            lastActivityUtc: DateTimeOffset.UtcNow.AddDays(-3));

        var verb = harness.Verb<LookupPatientVerb>();
        var ctx = harness.Context(verb, new Dictionary<string, object?>
        {
            ["patient_identifier"] = "IDX-001",
        });

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);

        Assert.Equal(VerbDispatchOutcome.Success, result.Outcome);
        Assert.Equal("PID-001", result.Output["patient_id"]);
        Assert.NotNull(result.Authz);
        Assert.True(result.Authz!.Allowed);

        var snap = harness.Audit.Snapshot();
        Assert.Contains(snap, e => e.EventType == "verb.rollback_captured" && (string?)e.Metadata["verb"] == LookupPatientVerb.VerbName);
        Assert.Contains(snap, e => e.EventType == "verb.executed" && (string?)e.Metadata["verb"] == LookupPatientVerb.VerbName);
        Assert.True(harness.Audit.VerifyChain());
    }

    [Fact]
    public async Task MissingRequiredParameter_Rejected_BeforeAuthz()
    {
        using var harness = new MissionTestHarness();
        var verb = harness.Verb<LookupPatientVerb>();
        var ctx = harness.Context(verb, new Dictionary<string, object?>());

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);

        Assert.Equal(VerbDispatchOutcome.Rejected, result.Outcome);
        Assert.StartsWith("parameter_validation_failed", result.FailureReason);
        Assert.Null(result.Authz);
        Assert.Contains(harness.Audit.Snapshot(), e =>
            e.EventType == "verb.rejected" && (string?)e.Metadata["code"] == "parameter_validation_failed");
    }

    [Fact]
    public async Task WrongParameterType_Rejected()
    {
        using var harness = new MissionTestHarness();
        var verb = harness.Verb<LookupPatientVerb>();
        var ctx = harness.Context(verb, new Dictionary<string, object?>
        {
            ["patient_identifier"] = 42, // expected string
        });

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);

        Assert.Equal(VerbDispatchOutcome.Rejected, result.Outcome);
        Assert.Contains("expected String", result.FailureReason);
    }

    [Fact]
    public async Task AdapterMissing_PreconditionFails_Rejected()
    {
        using var harness = new MissionTestHarness();
        // Swap the adapter out by rebuilding a plain container missing the adapter
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        services.AddMissionLoopPhase1();
        using var emptyProvider = services.BuildServiceProvider();

        var verb = harness.Verb<LookupPatientVerb>();
        var ctx = new VerbContext(
            PharmacyId: "pharm-test-001",
            Charter: harness.Charter(),
            Audit: harness.Audit,
            InvocationId: Guid.NewGuid().ToString("D"),
            Actor: "test-operator",
            Parameters: new Dictionary<string, object?> { ["patient_identifier"] = "IDX-001" },
            Services: emptyProvider,
            DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);

        Assert.Equal(VerbDispatchOutcome.Rejected, result.Outcome);
        Assert.Contains("adapter_missing", result.FailureReason);
    }

    [Fact]
    public async Task PatientNotFound_ExecutionFails_FailureAudited()
    {
        using var harness = new MissionTestHarness(); // empty mock
        var verb = harness.Verb<LookupPatientVerb>();
        var ctx = harness.Context(verb, new Dictionary<string, object?>
        {
            ["patient_identifier"] = "UNKNOWN",
        });

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);

        Assert.Equal(VerbDispatchOutcome.Failed, result.Outcome);
        Assert.StartsWith("execution_failed", result.FailureReason);
        Assert.Contains(harness.Audit.Snapshot(), e => e.EventType == "verb.failed");
        // rollback_captured still emitted even though inverse is a no-op
        Assert.Contains(harness.Audit.Snapshot(), e => e.EventType == "verb.rollback_captured");
    }

    [Fact]
    public async Task AuditChainRemainsValid_AfterMixedSuccessAndFailure()
    {
        using var harness = new MissionTestHarness();
        harness.Adapter.SeedPatient(
            identifier: "IDX-OK",
            patientId: "PID-OK",
            displayNameHash: "hhhhhh",
            lastActivityUtc: DateTimeOffset.UtcNow);

        var verb = harness.Verb<LookupPatientVerb>();

        var ok = await harness.Dispatcher.DispatchAsync(verb,
            harness.Context(verb, new Dictionary<string, object?> { ["patient_identifier"] = "IDX-OK" }),
            CancellationToken.None);
        Assert.Equal(VerbDispatchOutcome.Success, ok.Outcome);

        var fail = await harness.Dispatcher.DispatchAsync(verb,
            harness.Context(verb, new Dictionary<string, object?> { ["patient_identifier"] = "IDX-MISSING" }),
            CancellationToken.None);
        Assert.Equal(VerbDispatchOutcome.Failed, fail.Outcome);

        Assert.True(harness.Audit.VerifyChain());
    }
}
