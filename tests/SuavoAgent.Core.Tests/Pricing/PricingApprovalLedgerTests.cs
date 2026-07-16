using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingApprovalLedgerTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"suavo-pricing-approval-{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public PricingApprovalLedgerTests() => _db = new AgentStateDb(_path);

    [Fact]
    public void ExpiredGrant_StagesFreshProposal_AndAcceptsReplacementGrant()
    {
        var issuedAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var afterExpiry = issuedAt.AddMinutes(2);
        var contract = PricingTestAuthority.Contract();
        var expired = PricingTestAuthority.InstallApproval(
            _db,
            contract,
            issuedAt,
            issuedAt.AddMinutes(1));

        Assert.False(_db.TryGetInstalledPricingAuthority(
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            afterExpiry,
            PricingTestAuthority.TrustedPublicKeys,
            out _,
            out var expiredCode));
        Assert.Equal("pricing_cost_basis_approval_expired", expiredCode);

        Assert.Null(PricingApprovalAuthorityResolver.ResolveOrStageProposal(
            _db,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            afterExpiry,
            PricingTestAuthority.TrustedPublicKeys,
            out var pendingCode));
        Assert.Equal("pricing_cost_basis_approval_pending", pendingCode);

        var renewal = Assert.Single(_db.GetPendingPricingApprovalProposals(
            20,
            afterExpiry,
            PricingTestAuthority.TrustedPublicKeys));
        Assert.NotEqual(expired.ProposalId, renewal.ProposalId);

        var replacement = PricingTestAuthority.InstallApproval(
            _db,
            contract,
            afterExpiry);
        Assert.Equal(renewal.ProposalId, replacement.ProposalId);
        Assert.True(_db.TryGetInstalledPricingAuthority(
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            afterExpiry.AddMinutes(1),
            PricingTestAuthority.TrustedPublicKeys,
            out var authority,
            out var admittedCode));
        Assert.NotNull(authority);
        Assert.Equal("pricing_cost_basis_approval_admitted", admittedCode);
    }

    [Fact]
    public void SignedRevocation_BlocksGrant_AndStagesFreshProposal()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        var grant = PricingTestAuthority.InstallApproval(_db, contract, now);
        var revocation = PricingTestAuthority.Revocation(grant, now.AddMinutes(1));

        var result = PricingTestAuthority.InstallRevocation(
            _db,
            revocation,
            now.AddMinutes(1));
        Assert.True(result.Succeeded);
        Assert.False(_db.TryGetInstalledPricingAuthority(
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            now.AddMinutes(2),
            PricingTestAuthority.TrustedPublicKeys,
            out _,
            out var code));
        Assert.Equal("pricing_cost_basis_approval_revoked", code);

        Assert.Null(PricingApprovalAuthorityResolver.ResolveOrStageProposal(
            _db,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            now.AddMinutes(2),
            PricingTestAuthority.TrustedPublicKeys,
            out _));
        var renewal = Assert.Single(_db.GetPendingPricingApprovalProposals(
            20,
            now.AddMinutes(2),
            PricingTestAuthority.TrustedPublicKeys));
        Assert.NotEqual(grant.ProposalId, renewal.ProposalId);
    }

    [Fact]
    public void ExactBoundJobAuthority_IsActiveThenDeniedByItsSignedRevocation()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        var grant = PricingTestAuthority.InstallApproval(_db, contract, now);
        var authority = PricingObservationPolicy.TryAdmitAuthority(
                grant,
                PricingTestAuthority.PharmacyId,
                PricingTestAuthority.AgentId,
                PricingTestAuthority.MachineFingerprint,
                contract,
                now,
                PricingTestAuthority.TrustedPublicKeys,
                out var authorityCode);
        Assert.NotNull(authority);
        Assert.Equal("pricing_cost_basis_approval_admitted", authorityCode);
        const string jobId = "exact-bound-job-revocation";
        _db.UpsertPricingJob(
            new PricingJobSpec(
                jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"),
            PricingJobStatus.Running,
            0,
            0,
            0);
        Assert.True(_db.TryBindPricingInputIdentity(
            jobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority!,
            now,
            out var bindCode), bindCode);

        Assert.True(_db.TryAdmitPricingJobAuthority(
            jobId,
            now.AddMinutes(1),
            PricingTestAuthority.TrustedPublicKeys,
            out var activeCode), activeCode);
        Assert.Equal("pricing_job_authority_active", activeCode);

        var revokedAt = now.AddMinutes(2);
        var revoked = PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(grant, revokedAt),
            revokedAt);
        Assert.True(revoked.Succeeded, revoked.Code);
        Assert.False(_db.TryAdmitPricingJobAuthority(
            jobId,
            revokedAt,
            PricingTestAuthority.TrustedPublicKeys,
            out var revokedCode));
        Assert.Equal("pricing_cost_basis_approval_revoked", revokedCode);
    }

    [Fact]
    public async Task ArtifactPublicationAndSignedRevocation_AreLinearizedByOneLedgerLock()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        var grant = PricingTestAuthority.InstallApproval(_db, contract, now);
        var authority = PricingObservationPolicy.TryAdmitAuthority(
                grant,
                PricingTestAuthority.PharmacyId,
                PricingTestAuthority.AgentId,
                PricingTestAuthority.MachineFingerprint,
                contract,
                now,
                PricingTestAuthority.TrustedPublicKeys,
                out _);
        Assert.NotNull(authority);
        const string jobId = "linearized-publication-revocation";
        _db.UpsertPricingJob(
            new PricingJobSpec(
                jobId, @"C:\Pricing.xlsx", "NDC", "Supplier", "Cost"),
            PricingJobStatus.Running,
            0,
            0,
            0);
        Assert.True(_db.TryBindPricingInputIdentity(
            jobId,
            new string('c', 64),
            new string('d', 64),
            contract,
            authority!,
            now,
            out var bindCode), bindCode);

        using var publicationEntered = new ManualResetEventSlim();
        using var releasePublication = new ManualResetEventSlim();
        using var revocationStarted = new ManualResetEventSlim();
        var publication = Task.Run(() =>
        {
            var admitted = _db.TryPublishPricingArtifact(
                jobId,
                new FixedTimeProvider(now.AddMinutes(1)),
                PricingTestAuthority.TrustedPublicKeys,
                () =>
                {
                    publicationEntered.Set();
                    Assert.True(releasePublication.Wait(TimeSpan.FromSeconds(5)));
                },
                out var code);
            return (admitted, code);
        });
        Assert.True(publicationEntered.Wait(TimeSpan.FromSeconds(5)));

        var revocation = Task.Run(() =>
        {
            revocationStarted.Set();
            var revokedAt = now.AddMinutes(2);
            return PricingTestAuthority.InstallRevocation(
                _db,
                PricingTestAuthority.Revocation(grant, revokedAt),
                revokedAt);
        });
        Assert.True(revocationStarted.Wait(TimeSpan.FromSeconds(5)));
        var premature = await Task.WhenAny(
            revocation,
            Task.Delay(TimeSpan.FromMilliseconds(100)));
        Assert.NotSame(revocation, premature);

        releasePublication.Set();
        var published = await publication;
        var revoked = await revocation;
        Assert.True(published.admitted, published.code);
        Assert.True(revoked.Succeeded, revoked.Code);
        Assert.False(_db.TryAdmitPricingJobAuthority(
            jobId,
            now.AddMinutes(2),
            PricingTestAuthority.TrustedPublicKeys,
            out var finalCode));
        Assert.Equal("pricing_cost_basis_approval_revoked", finalCode);
    }

    [Fact]
    public void PreGrantRevocation_SurvivesRestart_AndDelayedInstallStaysInactive()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"suavo-pricing-pregrant-revoke-{Guid.NewGuid():N}.db");
        var observedAt = new DateTimeOffset(
            2026,
            7,
            13,
            12,
            0,
            0,
            TimeSpan.Zero);
        var issuedAt = observedAt.AddMinutes(1);
        var revokedAt = observedAt.AddMinutes(2);
        var contract = PricingTestAuthority.Contract();
        SuavoAgent.Contracts.Pricing.PricingApprovalGrant grant;

        try
        {
            using (var initial = new AgentStateDb(path))
            {
                var proposal = Assert.IsType<
                    SuavoAgent.Contracts.Pricing.PricingApprovalProposal>(
                    initial.StagePricingApprovalProposal(
                        PricingTestAuthority.PharmacyId,
                        PricingTestAuthority.AgentId,
                        PricingTestAuthority.MachineFingerprint,
                        contract,
                        observedAt,
                        out _));
                grant = PricingTestAuthority.Grant(proposal, issuedAt);
                var revocation = PricingTestAuthority.Revocation(grant, revokedAt);

                var revoked = PricingTestAuthority.InstallRevocation(
                    initial,
                    revocation,
                    revokedAt);
                Assert.Equal(
                    AgentStateDb.PricingApprovalLedgerKind.Applied,
                    revoked.Kind);
                Assert.Equal("pricing_approval_revoked_pregrant", revoked.Code);
                Assert.False(initial.TryGetInstalledPricingAuthority(
                    PricingTestAuthority.PharmacyId,
                    PricingTestAuthority.AgentId,
                    PricingTestAuthority.MachineFingerprint,
                    contract,
                    revokedAt,
                    PricingTestAuthority.TrustedPublicKeys,
                    out _,
                    out var initialCode));
                Assert.Equal("pricing_cost_basis_approval_revoked", initialCode);
            }

            using (var restarted = new AgentStateDb(path))
            {
                Assert.True(restarted.RecordPricingCloudAuthorityHeartbeat(
                    revokedAt.AddMinutes(1),
                    revokedAt.AddMinutes(1),
                    out _));
                var delayedInstall = PricingTestAuthority.InstallGrant(
                    restarted,
                    grant,
                    revokedAt.AddMinutes(1));
                Assert.Equal(
                    AgentStateDb.PricingApprovalLedgerKind.Idempotent,
                    delayedInstall.Kind);
                Assert.Equal(
                    "pricing_approval_already_revoked",
                    delayedInstall.Code);
                Assert.False(restarted.TryGetInstalledPricingAuthority(
                    PricingTestAuthority.PharmacyId,
                    PricingTestAuthority.AgentId,
                    PricingTestAuthority.MachineFingerprint,
                    contract,
                    revokedAt.AddMinutes(1),
                    PricingTestAuthority.TrustedPublicKeys,
                    out _,
                    out var restartedCode));
                Assert.Equal("pricing_cost_basis_approval_revoked", restartedCode);

                Assert.Null(PricingApprovalAuthorityResolver.ResolveOrStageProposal(
                    restarted,
                    PricingTestAuthority.PharmacyId,
                    PricingTestAuthority.AgentId,
                    PricingTestAuthority.MachineFingerprint,
                    contract,
                    revokedAt.AddMinutes(1),
                    PricingTestAuthority.TrustedPublicKeys,
                    out var pendingCode));
                Assert.Equal("pricing_cost_basis_approval_pending", pendingCode);
                var replacement = Assert.Single(
                    restarted.GetPendingPricingApprovalProposals(
                        20,
                        revokedAt.AddMinutes(1),
                        PricingTestAuthority.TrustedPublicKeys));
                Assert.NotEqual(grant.ProposalId, replacement.ProposalId);
            }
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public void Proposal_RetriesUntilExactSignedReceipt_IsDurablyRecorded()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        var proposal = Assert.IsType<SuavoAgent.Contracts.Pricing.PricingApprovalProposal>(
            _db.StagePricingApprovalProposal(
                PricingTestAuthority.PharmacyId,
                PricingTestAuthority.AgentId,
                PricingTestAuthority.MachineFingerprint,
                contract,
                now,
                out _));
        Assert.Single(_db.GetPendingPricingApprovalProposals(20, now));

        var receipt = PricingTestAuthority.Receipt(proposal, now.AddMinutes(1));
        Assert.False(_db.TryRecordPricingApprovalProposalReceipt(
            receipt with { Signature = Convert.ToBase64String(new byte[64]) },
            now.AddMinutes(1),
            PricingTestAuthority.TrustedPublicKeys,
            out var forgedCode));
        Assert.Equal(
            "pricing_approval_proposal_receipt_signature_invalid",
            forgedCode);
        Assert.Single(_db.GetPendingPricingApprovalProposals(20, now.AddMinutes(1)));

        Assert.True(_db.TryRecordPricingApprovalProposalReceipt(
            receipt,
            now.AddMinutes(1),
            PricingTestAuthority.TrustedPublicKeys,
            out var recordedCode));
        Assert.Equal("pricing_approval_proposal_acknowledged", recordedCode);
        Assert.Empty(_db.GetPendingPricingApprovalProposals(20, now.AddMinutes(1)));
    }

    [Fact]
    public void RejectedSignedRevocation_DoesNotCreateProcessOnlyAuthorityTombstone()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            $"suavo-pricing-rejected-revoke-{Guid.NewGuid():N}.db");
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        const string jobId = "rejected-revocation-authority";
        try
        {
            string approvalId;
            string grantDigest;
            using (var initial = new AgentStateDb(path))
            {
                var grant = PricingTestAuthority.InstallApproval(
                    initial,
                    contract,
                    now,
                    now.AddDays(7));
                var authority = Assert.IsType<PricingCostBasisAuthority>(
                    PricingObservationPolicy.TryAdmitAuthority(
                        grant,
                        PricingTestAuthority.PharmacyId,
                        PricingTestAuthority.AgentId,
                        PricingTestAuthority.MachineFingerprint,
                        contract,
                        now,
                        PricingTestAuthority.TrustedPublicKeys,
                        out _));
                approvalId = authority.ApprovalId;
                grantDigest = authority.ApprovalDigest;
                initial.UpsertPricingJob(
                    new PricingJobSpec(
                        jobId,
                        @"C:\Pricing.xlsx",
                        "NDC",
                        "Supplier",
                        "Cost",
                        approvalId,
                        grantDigest),
                    PricingJobStatus.Running,
                    0,
                    0,
                    0);
                Assert.True(initial.TryBindPricingInputIdentity(
                    jobId,
                    new string('a', 64),
                    new string('b', 64),
                    contract,
                    authority,
                    now,
                    out var bindCode), bindCode);

                var mismatchedGrant = grant with
                {
                    ProposalId = Guid.NewGuid().ToString("D"),
                };
                var rejected = PricingTestAuthority.InstallRevocation(
                    initial,
                    PricingTestAuthority.Revocation(
                        mismatchedGrant,
                        now.AddMinutes(1)),
                    now.AddMinutes(1));
                Assert.Equal(
                    AgentStateDb.PricingApprovalLedgerKind.Rejected,
                    rejected.Kind);
                Assert.Equal(
                    "pricing_approval_revocation_binding_invalid",
                    rejected.Code);
                Assert.True(initial.TryAdmitPricingJobAuthority(
                    jobId,
                    now.AddMinutes(2),
                    PricingTestAuthority.TrustedPublicKeys,
                    out var activeCode), activeCode);
            }

            using var restarted = new AgentStateDb(path);
            Assert.True(restarted.RecordPricingCloudAuthorityHeartbeat(
                now.AddMinutes(3),
                now.AddMinutes(3),
                out var leaseCode), leaseCode);
            Assert.True(restarted.TryAdmitPricingJobAuthority(
                jobId,
                now.AddMinutes(3),
                PricingTestAuthority.TrustedPublicKeys,
                out var restartCode), restartCode);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
