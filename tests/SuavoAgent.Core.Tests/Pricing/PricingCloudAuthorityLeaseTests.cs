using System.Net;
using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PricingCloudAuthorityLeaseTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"suavo-pricing-cloud-lease-{Guid.NewGuid():N}");
    private readonly string _path;

    public PricingCloudAuthorityLeaseTests()
    {
        Directory.CreateDirectory(_directory);
        _path = Path.Combine(_directory, "state.db");
    }

    [Fact]
    public void MissingLease_IsFailClosed()
    {
        using var db = new AgentStateDb(_path);

        Assert.False(db.TryAdmitPricingCloudAuthority(
            DateTimeOffset.UtcNow,
            out var code));
        Assert.Equal("pricing_cloud_authority_lease_unavailable", code);
    }

    [Fact]
    public void LeaseExpiryAndHighWater_SurviveRestartAndCannotBeRewound()
    {
        var issuedAt = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using (var initial = new AgentStateDb(_path))
        {
            Assert.True(initial.RecordPricingCloudAuthorityHeartbeat(
                issuedAt,
                issuedAt,
                out var renewedCode));
            Assert.Equal("pricing_cloud_authority_lease_renewed", renewedCode);
            Assert.True(initial.TryAdmitPricingCloudAuthority(
                issuedAt.AddMinutes(14),
                out var activeCode));
            Assert.Equal("pricing_cloud_authority_lease_active", activeCode);
        }

        using (var restarted = new AgentStateDb(_path))
        {
            Assert.False(restarted.TryAdmitPricingCloudAuthority(
                issuedAt.AddMinutes(1),
                out var rollbackCode));
            Assert.Equal("pricing_cloud_authority_clock_rollback", rollbackCode);

            Assert.False(restarted.TryAdmitPricingCloudAuthority(
                issuedAt.AddMinutes(16),
                out var expiredCode));
            Assert.Equal("pricing_cloud_authority_lease_expired", expiredCode);

            Assert.False(restarted.TryAdmitPricingCloudAuthority(
                issuedAt.AddMinutes(14),
                out var rewindCode));
            Assert.Equal("pricing_cloud_authority_lease_expired", rewindCode);
        }
    }

    [Fact]
    public void TerminalInactiveResponse_IsDurableAndCannotBeRenewed()
    {
        var issuedAt = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        using (var initial = new AgentStateDb(_path))
        {
            Assert.True(initial.RecordPricingCloudAuthorityHeartbeat(
                issuedAt,
                issuedAt,
                out _));
            initial.LatchPricingCloudAuthorityRevocation(
                issuedAt.AddMinutes(1));
            Assert.False(initial.RecordPricingCloudAuthorityHeartbeat(
                issuedAt.AddMinutes(2),
                issuedAt.AddMinutes(2),
                out var renewalCode));
            Assert.Equal("pricing_cloud_authority_revoked", renewalCode);
        }

        using var restarted = new AgentStateDb(_path);
        Assert.False(restarted.TryAdmitPricingCloudAuthority(
            issuedAt.AddMinutes(2),
            out var admissionCode));
        Assert.Equal("pricing_cloud_authority_revoked", admissionCode);
    }

    [Fact]
    public void TerminalInactivePersistenceFailure_CancelsRunAndDeniesFutureAdmission()
    {
        var now = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var db = new AgentStateDb(_path);
        Assert.True(db.RecordPricingCloudAuthorityHeartbeat(now, now, out _));
        var runs = new AutopilotRunCoordinator();
        using var pricing = runs.Register(
            AutopilotRunKind.Pricing,
            CancellationToken.None);
        db.Dispose();

        var failure = Assert.Throws<InvalidOperationException>(() =>
            HeartbeatWorker.RevokePricingAuthorityAndCancelRuns(
                db,
                runs,
                now.AddMinutes(1)));

        Assert.Equal(
            "pricing_cloud_authority_revocation_persist_failed",
            failure.Message);
        Assert.True(pricing.Token.IsCancellationRequested);
        Assert.False(db.TryAdmitPricingCloudAuthority(
            now.AddMinutes(1),
            out var admissionCode));
        Assert.Equal("pricing_cloud_authority_revoked", admissionCode);
        Assert.False(db.RecordPricingCloudAuthorityHeartbeat(
            now.AddMinutes(2),
            now.AddMinutes(2),
            out var renewalCode));
        Assert.Equal("pricing_cloud_authority_revoked", renewalCode);
    }

    [Fact]
    public void ResolverRejectsAStillValidPicGrantAfterCloudLeaseExpires()
    {
        var issuedAt = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var contract = PricingTestAuthority.Contract();
        using var db = new AgentStateDb(_path);
        PricingTestAuthority.InstallApproval(
            db,
            contract,
            issuedAt,
            issuedAt.AddDays(365));

        Assert.Null(PricingApprovalAuthorityResolver.ResolveOrStageProposal(
            db,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            issuedAt.AddMinutes(16),
            PricingTestAuthority.TrustedPublicKeys,
            out var code));
        Assert.Equal("pricing_cloud_authority_lease_expired", code);
    }

    [Fact]
    public async Task SqlRunnerStopsMidBatchAndDiscardsTheRowWhenLeaseExpires()
    {
        var issuedAt = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(issuedAt.AddMinutes(14));
        var contract = PricingTestAuthority.Contract();
        var workbookPath = Path.Combine(_directory, "pricing.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Sheet1");
            sheet.Cell(1, 1).Value = PricingJobDefaults.NdcColumn;
            sheet.Cell(2, 1).Value = "00093-5124-01";
            workbook.SaveAs(workbookPath);
        }

        using var db = new AgentStateDb(_path);
        Assert.True(db.RecordPricingCloudAuthorityHeartbeat(
            issuedAt,
            issuedAt,
            out _));
        var authority = PricingTestAuthority.InstallAuthority(
            db,
            contract,
            issuedAt,
            issuedAt.AddDays(365));
        var lookup = new AdvancingLookup(clock, issuedAt.AddMinutes(16));
        var runner = new SqlPricingJobRunner(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
            db,
            lookup,
            NullLogger<SqlPricingJobRunner>.Instance,
            contract,
            authority,
            clock: clock,
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
        var spec = new PricingJobSpec(
            "cloud-lease-mid-batch",
            workbookPath,
            PricingJobDefaults.NdcColumn,
            PricingJobDefaults.SupplierColumn,
            PricingJobDefaults.CostColumn);

        var result = await runner.RunAsync(spec, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Halted, result.Status);
        Assert.Equal("pricing_cloud_authority_lease_expired", result.HaltReason);
        Assert.Empty(db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task SqlRunner_RevocationAfterTempFlush_NeverPublishesSibling()
    {
        var issuedAt = new DateTimeOffset(
            2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(issuedAt.AddMinutes(1));
        var contract = PricingTestAuthority.Contract();
        var workbookPath = Path.Combine(_directory, "sql-revoke-publication.xlsx");
        using (var workbook = new XLWorkbook())
        {
            var sheet = workbook.Worksheets.Add("Sheet1");
            sheet.Cell(1, 1).Value = PricingJobDefaults.NdcColumn;
            sheet.Cell(2, 1).Value = "00093-5124-01";
            workbook.SaveAs(workbookPath);
        }
        var sourceBytes = File.ReadAllBytes(workbookPath);
        var outputPath = Path.Combine(
            _directory, "sql-revoke-publication-priced.xlsx");

        using var db = new AgentStateDb(_path);
        var grant = PricingTestAuthority.InstallApproval(
            db,
            contract,
            issuedAt,
            issuedAt.AddDays(365));
        var authority = PricingObservationPolicy.TryAdmitAuthority(
            grant,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            issuedAt,
            PricingTestAuthority.TrustedPublicKeys,
            out var authorityCode);
        Assert.NotNull(authority);
        Assert.Equal("pricing_cost_basis_approval_admitted", authorityCode);
        AgentStateDb.PricingApprovalLedgerResult? revocationResult = null;
        var writer = new ExcelPricingWriter(
            NullLogger<ExcelPricingWriter>.Instance,
            _ => outputPath,
            () =>
            {
                var revokedAt = issuedAt.AddMinutes(2);
                clock.SetUtcNow(revokedAt);
                revocationResult = PricingTestAuthority.InstallRevocation(
                    db,
                    PricingTestAuthority.Revocation(grant, revokedAt),
                    revokedAt);
            });
        var runner = new SqlPricingJobRunner(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            writer,
            db,
            new AdvancingLookup(clock, issuedAt.AddMinutes(1)),
            NullLogger<SqlPricingJobRunner>.Instance,
            contract,
            authority!,
            clock: clock,
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
        var spec = new PricingJobSpec(
            "sql-revoke-before-publication",
            workbookPath,
            PricingJobDefaults.NdcColumn,
            PricingJobDefaults.SupplierColumn,
            PricingJobDefaults.CostColumn);

        var result = await runner.RunAsync(spec, CancellationToken.None);

        Assert.NotNull(revocationResult);
        Assert.True(revocationResult!.Succeeded, revocationResult.Code);
        Assert.Equal(PricingJobStatus.Halted, result.Status);
        Assert.Equal("pricing_cost_basis_approval_revoked", result.HaltReason);
        Assert.False(File.Exists(outputPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(workbookPath));
        Assert.Empty(Directory.EnumerateFiles(_directory, ".suavo-priced-*"));
    }

    [Fact]
    public void HeartbeatProtocolRequiresAuthenticatedServerTimeAndExactInactiveSignal()
    {
        using var success = JsonDocument.Parse(
            """{"success":true,"data":{"serverTime":"2026-07-13T12:00:00.123Z"}}""");
        Assert.True(HeartbeatWorker.TryReadAuthenticatedServerTime(
            success.RootElement,
            out var serverTime));
        Assert.Equal(TimeSpan.Zero, serverTime.Offset);

        using var missingTime = JsonDocument.Parse(
            """{"success":true,"data":{}}""");
        Assert.False(HeartbeatWorker.TryReadAuthenticatedServerTime(
            missingTime.RootElement,
            out _));

        var exact = CloudErrorResponse.Create(
            "safe",
            HttpStatusCode.Unauthorized,
            """{"success":false,"error":"Agent binding inactive"}""");
        Assert.True(HeartbeatWorker.IsTerminalInactiveAgentResponse(exact));

        var wrongStatus = CloudErrorResponse.Create(
            "safe",
            HttpStatusCode.Forbidden,
            """{"success":false,"error":"Agent binding inactive"}""");
        Assert.False(HeartbeatWorker.IsTerminalInactiveAgentResponse(wrongStatus));

        var wrongError = CloudErrorResponse.Create(
            "safe",
            HttpStatusCode.Unauthorized,
            """{"success":false,"error":"Invalid agent credentials"}""");
        Assert.False(HeartbeatWorker.IsTerminalInactiveAgentResponse(wrongError));
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        internal void SetUtcNow(DateTimeOffset value) =>
            _now = value.ToUniversalTime();
    }

    private sealed class AdvancingLookup(
        MutableTimeProvider clock,
        DateTimeOffset afterLookup) : ISupplierPriceLookup
    {
        public Task<SupplierPriceResult> FindCheapestSupplierAsync(
            string jobId,
            int rowIndex,
            string ndc11,
            CancellationToken ct)
        {
            clock.SetUtcNow(afterLookup);
            return Task.FromResult(new SupplierPriceResult(
                jobId,
                rowIndex,
                ndc11,
                true,
                "supplier",
                0.01m,
                null));
        }
    }
}
