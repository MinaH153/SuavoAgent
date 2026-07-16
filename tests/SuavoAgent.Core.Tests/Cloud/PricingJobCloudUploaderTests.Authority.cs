using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Tests.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public sealed partial class PricingJobCloudUploaderTests
{
    [Fact]
    public async Task FlushPending_ExactSignedRevocation_IsTerminalAndNeverSent()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(
            signer,
            _db,
            new AuthorityTimeProvider(now.AddMinutes(2)));
        var (spec, grant) = StageAuthorizedCompletedPayload(
            uploader,
            "outbox-authority-revoked",
            now,
            now.AddDays(7));
        var revocation = PricingTestAuthority.InstallRevocation(
            _db,
            PricingTestAuthority.Revocation(grant, now.AddMinutes(1)),
            now.AddMinutes(1));
        Assert.True(revocation.Succeeded, revocation.Code);

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);

        Assert.Null(signer.Payload);
        AssertAuthorityQuarantine(
            spec.JobId,
            "pricing_cost_basis_approval_revoked");
        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);
        Assert.Null(signer.Payload);
    }

    [Fact]
    public async Task FlushPending_ExpiredExactGrant_IsTerminalAndNeverSent()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(
            signer,
            _db,
            new AuthorityTimeProvider(now.AddMinutes(2)));
        var (spec, _) = StageAuthorizedCompletedPayload(
            uploader,
            "outbox-authority-expired",
            now,
            now.AddMinutes(1));

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);

        Assert.Null(signer.Payload);
        AssertAuthorityQuarantine(
            spec.JobId,
            "pricing_cost_basis_approval_expired");
    }

    [Fact]
    public async Task FlushPending_MissingImmutableAuthorityBinding_IsTerminalAndNeverSent()
    {
        var now = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.True(_db.RecordPricingCloudAuthorityHeartbeat(
            now,
            now,
            out var leaseCode), leaseCode);
        var signer = new RecordingPostSigner();
        var uploader = CreateUploader(
            signer,
            _db,
            new AuthorityTimeProvider(now.AddMinutes(1)));
        var spec = new PricingJobSpec(
            "outbox-authority-binding-missing",
            @"C:\Pricing.xlsx",
            "NDC",
            "Supplier",
            "Cost",
            "77777777-7777-4777-8777-777777777777",
            new string('7', 64));
        const string commandId = "77777777-7777-4777-8777-777777777777";
        RegisterPricingCommandBinding(_db, commandId, spec);
        uploader.PrepareDelivery(
            spec,
            commandId,
            null,
            PricingExecutorMode.SqlFirst);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "55111064501",
            true,
            "McKesson",
            1.25m,
            null));
        _db.UpsertPricingJob(
            spec,
            PricingJobStatus.Completed,
            1,
            1,
            0);
        var payload = PricingJobCloudUploader.BuildPersistedPayloadEnvelope(
            spec.JobId,
            commandId,
            PricingJobStatus.Completed,
            "sql",
            1,
            1,
            0,
            _db.GetPricingResults(spec.JobId),
            spec.ApprovalId,
            spec.GrantDigest);
        _db.StagePricingResultPayload(
            spec.JobId,
            commandId,
            null,
            payload.Json,
            payload.ItemCount,
            executionOk: true);

        await uploader.FlushPendingAsync(
            CancellationToken.None,
            includeDeferred: true);

        Assert.Null(signer.Payload);
        AssertAuthorityQuarantine(
            spec.JobId,
            "pricing_job_authority_binding_missing");
    }

    private (PricingJobSpec Spec, PricingApprovalGrant Grant)
        StageAuthorizedCompletedPayload(
            PricingJobCloudUploader uploader,
            string jobId,
            DateTimeOffset now,
            DateTimeOffset expiresAt)
    {
        var contract = PricingTestAuthority.Contract();
        var grant = PricingTestAuthority.InstallApproval(
            _db,
            contract,
            now,
            expiresAt);
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
        var spec = new PricingJobSpec(
            jobId,
            @"C:\Pricing.xlsx",
            "NDC",
            "Supplier",
            "Cost",
            authority!.ApprovalId,
            authority.ApprovalDigest);
        _db.UpsertPricingJob(spec, PricingJobStatus.Pending, 0, 0, 0);
        const string commandId = "88888888-8888-4888-8888-888888888888";
        RegisterPricingCommandBinding(_db, commandId, spec);
        uploader.PrepareDelivery(
            spec,
            commandId,
            null,
            PricingExecutorMode.SqlFirst);
        Assert.True(_db.TryBindPricingInputIdentity(
            spec.JobId,
            new string('a', 64),
            new string('b', 64),
            contract,
            authority!,
            now,
            out var bindCode), bindCode);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "55111064501",
            true,
            "McKesson",
            1.25m,
            null));
        _db.UpsertPricingJob(
            spec,
            PricingJobStatus.Completed,
            1,
            1,
            0);
        return (spec, grant);
    }

    private void AssertAuthorityQuarantine(string jobId, string expectedCode)
    {
        var quarantine = Assert.IsType<
            AgentStateDb.PricingResultOutboxQuarantineEntry>(
            _db.GetPricingResultOutboxQuarantine(jobId));
        Assert.Equal(expectedCode, quarantine.ReasonCode);
        Assert.Null(quarantine.HttpStatus);
        Assert.Null(quarantine.ResponseJson);
        Assert.Empty(_db.GetAllPendingPricingResultPayloads(20));
        var retained = Assert.IsType<AgentStateDb.PricingResultOutboxEntry>(
            _db.GetPricingResultOutbox(jobId));
        Assert.Equal("pending", retained.State);
        Assert.Equal(0, retained.AttemptCount);
    }

    private sealed class AuthorityTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
