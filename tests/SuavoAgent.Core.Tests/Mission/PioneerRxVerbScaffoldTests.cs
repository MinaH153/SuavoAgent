using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.PioneerRx;
using SuavoAgent.Core.Adapters;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

/// <summary>
/// Phase 5.3 PioneerRx verb scaffold — guarantees the gating semantics
/// hold. Live execution is gated on legal/contractual milestones; the
/// engineering side proves that:
///
///   - HIGH risk + sandbox-only charter is denied
///   - BAA amendment requirement gates dispatch even at lower risk
///   - The verbs ARE registered in the catalog so the cloud surface can
///     reference them
/// </summary>
public sealed class PioneerRxVerbScaffoldTests
{
    [Fact]
    public void Registry_DiscoversAllThreePioneerRxVerbs()
    {
        var registry = VerbRegistry.Build(
            new[] { typeof(IVerb).Assembly },
            NullLogger<VerbRegistry>.Instance);

        Assert.Contains(registry.All, e => e.Name == PioneerRxQueryVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == PioneerRxClickVerb.VerbName);
        Assert.Contains(registry.All, e => e.Name == PioneerRxWritebackRxDeliveryVerb.VerbName);
    }

    [Fact]
    public void WritebackVerb_DeclaredHighRiskAndAmendmentScope()
    {
        var meta = new PioneerRxWritebackRxDeliveryVerb().Metadata;
        Assert.Equal(VerbRiskTier.High, meta.RiskTier);
        var amendment = Assert.IsType<VerbBaaScope.BaaAmendment>(meta.BaaScope);
        Assert.Equal(PioneerRxWritebackRxDeliveryVerb.RequiredAmendmentId, amendment.AmendmentId);
        Assert.True(meta.IsMutation);
    }

    [Fact]
    public async Task CharterDrivenPolicy_DeniesHighRiskUnconditionally()
    {
        var harness = BuildHarness();
        var verb = new PioneerRxWritebackRxDeliveryVerb();
        var ctx = harness.Context(verb, new Dictionary<string, object?>
        {
            ["rx_number"] = "RX-1",
            ["delivery_status"] = "delivered",
            ["delivered_at"] = DateTimeOffset.UtcNow.ToString("O"),
        });

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);
        Assert.Equal(VerbDispatchOutcome.Rejected, result.Outcome);
        Assert.StartsWith("authz_denied", result.FailureReason);
    }

    [Fact]
    public async Task CharterDrivenPolicy_DeniesBaaAmendmentScope_WhenCharterMissingConstraint()
    {
        var harness = BuildHarness();
        // Click verb is MED + AgentBaa, so the HIGH/UNKNOWN bypass doesn't
        // apply. Use an AgentBaa-scoped fake to exercise BAA amendment
        // denial path via the registry. The PioneerRxClickVerb itself is
        // AgentBaa (not BaaAmendment), so we directly invoke the policy
        // against a synthetic verb whose scope is BaaAmendment.
        var verb = new SyntheticAmendmentScopedVerb();
        var ctx = harness.Context(verb, new Dictionary<string, object?>());

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);
        Assert.Equal(VerbDispatchOutcome.Rejected, result.Outcome);
        Assert.Contains("requires BAA amendment", result.FailureReason);
    }

    [Fact]
    public async Task QueryVerb_IsExplicitlyUnavailable_NotSuccessfulNoOp()
    {
        var harness = BuildHarness();
        var verb = new PioneerRxQueryVerb();
        var ctx = harness.Context(verb, new Dictionary<string, object?>
        {
            ["query_kind"] = "ndc",
            ["query_arg"] = "0093-0123-45",
        });

        var result = await harness.Dispatcher.DispatchAsync(verb, ctx, CancellationToken.None);
        Assert.NotEqual(VerbDispatchOutcome.Success, result.Outcome);
        Assert.Contains("capability_unavailable", result.FailureReason);
    }

    [Fact]
    public async Task AllPioneerRxVerbExecuteAndPostconditionPathsFailClosed()
    {
        var harness = BuildHarness();
        foreach (var verb in new IVerb[]
        {
            new PioneerRxClickVerb(),
            new PioneerRxQueryVerb(),
            new PioneerRxWritebackRxDeliveryVerb(),
        })
        {
            var ctx = harness.Context(verb, new Dictionary<string, object?>());
            var execution = await verb.ExecuteAsync(ctx, CancellationToken.None);
            var postcondition = await verb.VerifyPostconditionsAsync(ctx, execution, CancellationToken.None);
            Assert.False(execution.Succeeded);
            Assert.Equal("capability_unavailable", execution.FailureReason);
            Assert.False(postcondition.Satisfied);
        }
    }

    private static Harness BuildHarness()
    {
        var harness = new Harness();
        return harness;
    }

    private sealed class Harness
    {
        public ServiceProvider Provider { get; }
        public VerbDispatcher Dispatcher { get; }
        public AuditChain Audit { get; } = new();
        public MissionCharter Charter { get; }

        public Harness()
        {
            var services = new ServiceCollection();
            services.AddSingleton<IAuthzPolicy>(new CharterDrivenAuthzPolicy());
            services.AddSingleton<VerbDispatcher>();
            services.AddSingleton<IPharmacyReadAdapter>(new StubReadAdapter());
            Provider = services.BuildServiceProvider();
            Dispatcher = Provider.GetRequiredService<VerbDispatcher>();

            Charter = new MissionCharter(
                CharterId: Guid.Empty,
                PharmacyId: "ph-1",
                Version: 1,
                EffectiveFrom: DateTimeOffset.UtcNow,
                Objectives: new List<MissionObjective> { new("o1", "obj", 1) },
                Constraints: Array.Empty<MissionConstraint>(),
                PriorityOrdering: new MissionPriorityOrdering(new[] { "o1" }),
                Tolerance: new MissionToleranceThresholds(60, 3, 0.5),
                SignedByOperator: "test",
                SignedAt: DateTimeOffset.UtcNow);
        }

        public VerbContext Context(IVerb verb, IReadOnlyDictionary<string, object?> parameters) =>
            new(
                PharmacyId: "ph-1",
                Charter: Charter,
                Audit: Audit,
                InvocationId: Guid.NewGuid().ToString("D"),
                Actor: "test",
                Parameters: parameters,
                Services: Provider,
                DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(2));
        }

    private sealed class StubReadAdapter : IPharmacyReadAdapter
    {
        public string AdapterType => "stub-test";

        public Task<PatientRecord?> LookupPatientAsync(string identifier, CancellationToken ct) =>
            Task.FromResult<PatientRecord?>(new PatientRecord(
                PatientId: "P-1",
                DisplayNameHash: "abc",
                LastActivityUtc: DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<RxHistoryRecord>> GetTopNdcsForPatientAsync(
            string patientId, int topN, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<RxHistoryRecord>>(Array.Empty<RxHistoryRecord>());
    }

    private sealed class SyntheticAmendmentScopedVerb : IVerb
    {
        public VerbMetadata Metadata { get; } = new(
            Name: "synthetic_amendment_only",
            Version: "1.0.0",
            Description: "Test fixture",
            RiskTier: VerbRiskTier.Low,
            BaaScope: new VerbBaaScope.BaaAmendment("test-only-v1"),
            IsMutation: false,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(5),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, 0, 0, "test"));

        public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbPreconditionResult.Ok());
        public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));
        public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbExecutionResult.Ok(new Dictionary<string, object?>()));
        public Task<VerbPostconditionResult> VerifyPostconditionsAsync(VerbContext ctx, VerbExecutionResult r, CancellationToken ct) =>
            Task.FromResult(VerbPostconditionResult.Ok());
    }
}
