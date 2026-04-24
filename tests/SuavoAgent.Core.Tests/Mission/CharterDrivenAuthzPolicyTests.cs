using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.ActionGrammarV1.Policy;
using SuavoAgent.Core.ActionGrammarV1.Verbs.LookupPatient;
using SuavoAgent.Core.Audit;
using SuavoAgent.Core.Mission;
using Xunit;

namespace SuavoAgent.Core.Tests.Mission;

public sealed class CharterDrivenAuthzPolicyTests
{
    private static VerbContext MakeCtx(IVerb verb, MissionCharter? charter = null) => new(
        PharmacyId: "pharm-test",
        Charter: charter ?? MissionCharterLoader.BuildDefaultCharter("pharm-test"),
        Audit: new AuditChain(),
        InvocationId: Guid.NewGuid().ToString("D"),
        Actor: "test",
        Parameters: new Dictionary<string, object?>(),
        Services: new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
        DeadlineUtc: DateTimeOffset.UtcNow.AddMinutes(5));

    [Fact]
    public void LowRiskNonMutating_Allowed()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var verb = new LookupPatientVerb();
        var decision = policy.Evaluate(MakeCtx(verb), verb);
        Assert.True(decision.Allowed, decision.Reason);
    }

    [Fact]
    public void HighRiskVerb_DeniedUntilCedarLands()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var high = new StubVerb(new VerbMetadata(
            Name: "pioneerrx_writeback_rx_delivery",
            Version: "1.0.0",
            Description: "High risk test",
            RiskTier: VerbRiskTier.High,
            BaaScope: new VerbBaaScope.AgentBaa(),
            IsMutation: true,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(10),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, 0, 0, "test")));

        var decision = policy.Evaluate(MakeCtx(high), high);
        Assert.False(decision.Allowed);
        Assert.Contains("HIGH", decision.Reason);
    }

    [Fact]
    public void UnknownRisk_AlwaysDenied()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var verb = new StubVerb(VerbMetadataFixtures.Unknown());

        var decision = policy.Evaluate(MakeCtx(verb), verb);

        Assert.False(decision.Allowed);
        Assert.Contains("Unknown", decision.Reason);
    }

    [Fact]
    public void PhiExposingVerb_Requires_AgentBaaOrAmendment()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var leaking = new StubVerb(new VerbMetadata(
            Name: "leaky",
            Version: "1.0.0",
            Description: "PHI-exposing with BaaScope=None — should be denied",
            RiskTier: VerbRiskTier.Low,
            BaaScope: new VerbBaaScope.None(),
            IsMutation: false,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(1),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 10, 0, 0, "PHI boundary test")));

        var decision = policy.Evaluate(MakeCtx(leaking), leaking);
        Assert.False(decision.Allowed);
        Assert.Contains("BAA scope required", decision.Reason);
    }

    [Fact]
    public void AmendmentRequired_DeniedIfCharterMissingConstraint()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var verb = new StubVerb(new VerbMetadata(
            Name: "needs_amendment",
            Version: "1.0.0",
            Description: "test",
            RiskTier: VerbRiskTier.Low,
            BaaScope: new VerbBaaScope.BaaAmendment("writeback-v1"),
            IsMutation: true,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(1),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, 0, 0, "test")));

        var charter = MissionCharterLoader.BuildDefaultCharter("pharm-X"); // default has no baa-amendment constraints
        var decision = policy.Evaluate(MakeCtx(verb, charter), verb);
        Assert.False(decision.Allowed);
        Assert.Contains("writeback-v1", decision.Reason);
    }

    [Fact]
    public void Forbidden_StructurallyDenied()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var verb = new StubVerb(new VerbMetadata(
            Name: "forbidden",
            Version: "1.0.0",
            Description: "test",
            RiskTier: VerbRiskTier.Low,
            BaaScope: new VerbBaaScope.Forbidden(),
            IsMutation: false,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(1),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, 0, 0, "test")));

        var decision = policy.Evaluate(MakeCtx(verb), verb);
        Assert.False(decision.Allowed);
        Assert.Contains("Forbidden", decision.Reason);
    }

    [Fact]
    public void DowntimeBlastRadius_ExceedsToleranceBudget_Denied()
    {
        var policy = new CharterDrivenAuthzPolicy();
        var charter = MissionCharterLoader.BuildDefaultCharter("pharm-test"); // MaxDowntimeSecondsPerShift = 120
        var verb = new StubVerb(new VerbMetadata(
            Name: "long_restart",
            Version: "1.0.0",
            Description: "test",
            RiskTier: VerbRiskTier.Low,
            BaaScope: new VerbBaaScope.None(),
            IsMutation: true,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(600),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, DowntimeSeconds: 600, RecoverableWithinSeconds: 0, "over budget")));

        var decision = policy.Evaluate(MakeCtx(verb, charter), verb);
        Assert.False(decision.Allowed);
        Assert.Contains("DowntimeSeconds=600", decision.Reason);
    }

    private sealed class StubVerb : IVerb
    {
        public StubVerb(VerbMetadata metadata) => Metadata = metadata;
        public VerbMetadata Metadata { get; }
        public Task<VerbPreconditionResult> CheckPreconditionsAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbPreconditionResult.Ok());
        public Task<VerbRollbackEnvelope> CaptureRollbackAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbRollbackEnvelope.None(ctx.InvocationId));
        public Task<VerbExecutionResult> ExecuteAsync(VerbContext ctx, CancellationToken ct) =>
            Task.FromResult(VerbExecutionResult.Ok(new Dictionary<string, object?>()));
        public Task<VerbPostconditionResult> VerifyPostconditionsAsync(VerbContext ctx, VerbExecutionResult result, CancellationToken ct) =>
            Task.FromResult(VerbPostconditionResult.Ok());
    }

    private static class VerbMetadataFixtures
    {
        public static VerbMetadata Unknown() => new(
            Name: "unknown",
            Version: "1.0.0",
            Description: "",
            RiskTier: VerbRiskTier.Unknown,
            BaaScope: new VerbBaaScope.AgentBaa(),
            IsMutation: false,
            IsDestructive: false,
            MaxExecutionTime: TimeSpan.FromSeconds(1),
            Params: new VerbParameterSchema(Array.Empty<VerbParameterSpec>()),
            Output: new VerbOutputSchema(Array.Empty<VerbOutputSpec>()),
            BlastRadius: new VerbBlastRadius(0m, 0, 0, 0, ""));
    }
}
