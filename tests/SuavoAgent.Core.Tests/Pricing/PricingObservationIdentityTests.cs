using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingObservationIdentityTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"suavo-pricing-policy-{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public PricingObservationIdentityTests() => _db = new AgentStateDb(_path);

    [Fact]
    public void ProductionApproval_DefaultsBlocked_AndRequiresExactTenantRoleBasisPolicyAndExpiry()
    {
        var contract = PricingObservationPolicy.CreateUia("uia");
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        Assert.Null(PricingObservationPolicy.TryAdmitAuthority(
            null,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            now,
            PricingTestAuthority.TrustedPublicKeys,
            out var defaultCode));
        Assert.Equal("pricing_cost_basis_approval_required", defaultCode);

        var proposal = PricingTestAuthority.Proposal(contract, now);
        var valid = PricingTestAuthority.Grant(
            proposal,
            now,
            now.AddDays(1));
        Assert.NotNull(PricingObservationPolicy.TryAdmitAuthority(
            valid,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            now,
            PricingTestAuthority.TrustedPublicKeys,
            out _));

        Assert.Null(PricingObservationPolicy.TryAdmitAuthority(
            valid with { ApprovedByRole = "operator" }, PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId, PricingTestAuthority.MachineFingerprint, contract, now,
            PricingTestAuthority.TrustedPublicKeys, out _));
        Assert.Null(PricingObservationPolicy.TryAdmitAuthority(
            valid with { PharmacyId = "other-pharmacy" }, PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId, PricingTestAuthority.MachineFingerprint, contract, now,
            PricingTestAuthority.TrustedPublicKeys, out _));
        Assert.Null(PricingObservationPolicy.TryAdmitAuthority(
            valid with { PolicyDigest = new string('f', 64) }, PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId, PricingTestAuthority.MachineFingerprint, contract, now,
            PricingTestAuthority.TrustedPublicKeys, out _));
    }

    [Theory]
    [InlineData("modality")]
    [InlineData("schema")]
    [InlineData("status")]
    [InlineData("cost_basis")]
    public void Resume_RejectsAnyObservationSemanticChange(string change)
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var original = change == "cost_basis"
            ? PricingTestAuthority.Contract(modality: "uia")
            : PricingTestAuthority.Contract();
        var originalAuthority = PricingTestAuthority.Authority(original);
        var job = NewJob($"job-{change}");
        Assert.True(Bind(job, original, originalAuthority, now, out _));

        var changed = change switch
        {
            "modality" => PricingTestAuthority.Contract(modality: "uia"),
            "schema" => PricingTestAuthority.Contract(schemaMarker: "schema-v2"),
            "status" => PricingTestAuthority.Contract(statusMarker: "status-v2"),
            "cost_basis" => PricingTestAuthority.Contract(
                modality: "uia",
                costBasis: PricingObservationPolicy.PackageCostBasis),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

        Assert.False(Bind(
            job,
            changed,
            PricingTestAuthority.Authority(changed),
            now.AddMinutes(1),
            out var code));
        Assert.Contains(code, new[]
        {
            "pricing_observation_contract_conflict",
            "pricing_observation_contract_invalid",
        });
    }

    [Fact]
    public void Resume_RejectsStaleSnapshot_EvenWhenEveryDigestStillMatches()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract(freshness: TimeSpan.FromHours(1));
        var authority = PricingTestAuthority.Authority(contract);
        var job = NewJob("job-stale");
        Assert.True(Bind(job, contract, authority, now, out _));

        Assert.False(Bind(job, contract, authority, now.AddHours(2), out var code));
        Assert.Equal("pricing_observation_snapshot_stale", code);
    }

    [Fact]
    public void Recovery_RejectsApprovalForDifferentConfiguredPharmacy()
    {
        var now = DateTimeOffset.UtcNow;
        var contract = PricingTestAuthority.Contract();
        var approval = PricingTestAuthority.InstallApproval(
            _db,
            contract,
            now);
        var authority = Assert.IsType<PricingCostBasisAuthority>(
            PricingObservationPolicy.TryAdmitAuthority(
                approval,
                PricingTestAuthority.PharmacyId,
                PricingTestAuthority.AgentId,
                PricingTestAuthority.MachineFingerprint,
                contract,
                now,
                PricingTestAuthority.TrustedPublicKeys,
                out _));
        var job = NewJob("job-tenant-recovery");
        Assert.True(Bind(job, contract, authority, now, out _));
        _db.UpsertPricingJob(job, PricingJobStatus.Running, 1, 0, 0);

        Assert.Null(_db.GetRecoverablePricingJob(
            "sql",
            "different-pharmacy",
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            now.AddMinutes(1),
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys));
        Assert.Equal(job, _db.GetRecoverablePricingJob(
            "sql",
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            now.AddMinutes(1),
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys));
    }

    [Fact]
    public void UiaObservationIdentity_BindsLiveScreenAndExactSelectorSnapshot()
    {
        var pms = new string('1', 64);
        var screen = new string('2', 64);
        var patch = new SelectorPatch(
            "patch-a",
            "pricing",
            SelectorStepId.PricingTab,
            pms,
            screen,
            new ElementSignature("TabItem", "pricing-tab", null),
            Array.Empty<ElementSignature>(),
            0.99,
            new string('3', 64),
            1);
        var original = PricingObservationPolicy.CreateUia(
            "uia", pms, screen, new[] { patch });
        var changedScreen = PricingObservationPolicy.CreateUia(
            "uia", pms, new string('4', 64), new[] { patch });
        var changedSelector = PricingObservationPolicy.CreateUia(
            "uia", pms, screen, new[] { patch with { Version = 2 } });

        Assert.NotEqual(original.SchemaDigest, changedScreen.SchemaDigest);
        Assert.NotEqual(original.PolicyDigest, changedScreen.PolicyDigest);
        Assert.NotEqual(original.SchemaDigest, changedSelector.SchemaDigest);
        Assert.NotEqual(original.PolicyDigest, changedSelector.PolicyDigest);
    }

    private PricingJobSpec NewJob(string id)
    {
        var spec = new PricingJobSpec(id, "/tmp/workbook.xlsx", "NDC", "Supplier", "Cost Per Unit");
        _db.UpsertPricingJob(spec, PricingJobStatus.Pending, 0, 0, 0);
        return spec;
    }

    private bool Bind(
        PricingJobSpec job,
        PricingObservationContract contract,
        PricingCostBasisAuthority authority,
        DateTimeOffset now,
        out string code) => _db.TryBindPricingInputIdentity(
            job.JobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority,
            now,
            out code);

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
