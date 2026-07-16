using System.Text.Json;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public sealed class PackageCostLaneTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"suavo-package-cost-{Guid.NewGuid():N}.db");
    private readonly AgentStateDb _db;

    public PackageCostLaneTests() => _db = new AgentStateDb(_path);

    [Fact]
    public void Package_cost_round_trips_without_populating_cost_per_unit()
    {
        var spec = new PricingJobSpec(
            "package-job",
            "C:\\pricing.xlsx",
            "NDC",
            "Cheapest Supplier",
            "Cost",
            CostBasis: PricingApprovalContract.PackageCostBasis);
        _db.UpsertPricingJob(spec, PricingJobStatus.Running, 1, 0, 0);
        _db.SavePricingResult(new SupplierPriceResult(
            spec.JobId,
            2,
            "00093505698",
            true,
            "ParMed",
            null,
            null,
            PackageCost: 2.6000m,
            CostBasis: PricingApprovalContract.PackageCostBasis));

        var persisted = Assert.Single(_db.GetPricingResults(spec.JobId));
        Assert.Equal(PricingApprovalContract.PackageCostBasis, persisted.CostBasis);
        Assert.Equal(2.6000m, persisted.PackageCost);
        Assert.Null(persisted.CostPerUnit);
        Assert.Null(persisted.BaselineCostPerUnit);
        Assert.Null(persisted.Quantity);
    }

    [Fact]
    public void Package_cost_policy_can_receive_a_separate_pic_approval()
    {
        var now = DateTimeOffset.UtcNow;
        var contract = PricingTestAuthority.Contract(
            modality: "uia",
            costBasis: PricingApprovalContract.PackageCostBasis);

        var grant = PricingTestAuthority.InstallApproval(_db, contract, now);
        var authority = PricingObservationPolicy.TryAdmitAuthority(
            grant,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            contract,
            now,
            PricingTestAuthority.TrustedPublicKeys,
            out var code);

        Assert.NotNull(authority);
        Assert.Equal("pricing_cost_basis_approval_admitted", code);
        Assert.Equal(PricingApprovalContract.PackageSchemaVersion, grant.SchemaVersion);
        Assert.Equal(
            PricingApprovalContract.PackageSnapshotContractV2,
            grant.SnapshotContract);
        Assert.Equal(PricingApprovalContract.PackageCostBasis, authority!.CostBasis);
    }

    [Fact]
    public async Task Verified_read_only_surface_bootstraps_pic_grant_before_package_run()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(_db.RecordPricingCloudAuthorityHeartbeat(now, now, out var leaseCode), leaseCode);
        var readiness = new ActuationReadinessTracker();
        readiness.Record(new ActuationReadinessSnapshot(
            CommandPipeResponsive: true,
            IsConsoleInteractive: true,
            HelperSessionId: 1,
            ActiveConsoleSessionId: 1,
            HelperPid: 42,
            FailureCode: null,
            FailureReason: null,
            LastConclusiveCheckAtUtc: now,
            LastProbeAttemptAtUtc: now,
            SkippedReason: null,
            ConsecutiveStrandFailures: 0));
        var surface = new BootstrapSurfaceIpcClient(new string('b', 64));
        var activityGate = new PricingUiaActivityGate();
        var bootstrapper = new PackageCostApprovalBootstrapper(
            Options.Create(new AgentOptions
            {
                PharmacyId = PricingTestAuthority.PharmacyId,
                AgentId = PricingTestAuthority.AgentId,
                MachineFingerprint = PricingTestAuthority.MachineFingerprint,
                PricingExecutor = PricingExecutorMode.UiaFirst,
            }),
            _db,
            surface,
            activityGate,
            readiness,
            new StaticPmsIdentityProvider(),
            NullLogger<PackageCostApprovalBootstrapper>.Instance,
            PricingTestAuthority.TrustedPublicKeys);

        var awaitingPic = await bootstrapper.TryStageAsync(now, CancellationToken.None);

        Assert.Equal("pricing_cost_basis_approval_pending", awaitingPic.Code);
        Assert.NotNull(awaitingPic.Observation);
        Assert.Null(awaitingPic.Authority);
        Assert.Equal(PricingApprovalContract.PackageCostBasis, awaitingPic.Observation!.CostBasis);
        Assert.Equal("uia", awaitingPic.Observation.Modality);
        Assert.Equal([IpcCommands.PricingObservationContext], surface.Commands);
        var proposal = Assert.Single(_db.GetPendingPricingApprovalProposals(
            20,
            now,
            PricingTestAuthority.TrustedPublicKeys));
        Assert.Equal(PricingApprovalContract.PackageSchemaVersion, proposal.SchemaVersion);
        Assert.Equal(PricingApprovalContract.PackageCostBasis, proposal.CostBasis);

        var grant = PricingTestAuthority.Grant(proposal, now.AddSeconds(1));
        var installed = PricingTestAuthority.InstallGrant(_db, grant, now.AddSeconds(1));
        Assert.True(installed.Succeeded, installed.Code);

        var admitted = await bootstrapper.TryStageAsync(
            now.AddSeconds(2),
            CancellationToken.None);
        var authority = Assert.IsType<PricingCostBasisAuthority>(admitted.Authority);
        Assert.Equal("pricing_cost_basis_approval_admitted", admitted.Code);
        Assert.Equal(grant.ApprovalId, authority.ApprovalId);

        var workbookPath = CreatePackageWorkbook();
        try
        {
            var spec = new PricingJobSpec(
                $"package-bootstrap-{Guid.NewGuid():N}",
                workbookPath,
                "NDC",
                PricingJobDefaults.PackageSupplierColumn,
                PricingJobDefaults.PackageCostColumn,
                CostBasis: PricingApprovalContract.PackageCostBasis);
            Assert.Equal(
                authority.ApprovalDigest,
                PricingApprovalContract.ComputeGrantDigest(grant));
            Assert.True(
                PricingObservationPolicy.TryMatchJobAuthority(
                    spec,
                    authority,
                    out var bindingCode),
                bindingCode);
            var runner = new PricingJobRunner(
                new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
                new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
                _db,
                NullLogger<PricingJobRunner>.Instance,
                interLookupDelay: TimeSpan.Zero,
                trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);

            var progress = await runner.RunAsync(
                spec,
                new PackageCostIpcClient(),
                admitted.Observation!,
                authority,
                Array.Empty<SuavoAgent.Contracts.Learning.SelectorPatch>(),
                null,
                null,
                CancellationToken.None);

            Assert.True(
                progress.Status == PricingJobStatus.Completed,
                $"status={progress.Status} reason={progress.HaltReason}");
            Assert.Equal(1, progress.CompletedItems);
            Assert.Equal(1, progress.FailedItems);
            foreach (var output in Directory.GetFiles(
                         Path.GetDirectoryName(workbookPath)!,
                         $"{Path.GetFileNameWithoutExtension(workbookPath)}-priced-*.xlsx"))
                File.Delete(output);
        }
        finally
        {
            File.Delete(workbookPath);
        }
    }

    [Fact]
    public async Task Package_bootstrap_never_competes_with_an_active_pricing_run()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(_db.RecordPricingCloudAuthorityHeartbeat(now, now, out var leaseCode), leaseCode);
        var readiness = new ActuationReadinessTracker();
        readiness.Record(new ActuationReadinessSnapshot(
            CommandPipeResponsive: true,
            IsConsoleInteractive: true,
            HelperSessionId: 1,
            ActiveConsoleSessionId: 1,
            HelperPid: 42,
            FailureCode: null,
            FailureReason: null,
            LastConclusiveCheckAtUtc: now,
            LastProbeAttemptAtUtc: now,
            SkippedReason: null,
            ConsecutiveStrandFailures: 0));
        var surface = new BootstrapSurfaceIpcClient(new string('b', 64));
        var activityGate = new PricingUiaActivityGate();
        var bootstrapper = new PackageCostApprovalBootstrapper(
            Options.Create(new AgentOptions
            {
                PharmacyId = PricingTestAuthority.PharmacyId,
                AgentId = PricingTestAuthority.AgentId,
                MachineFingerprint = PricingTestAuthority.MachineFingerprint,
                PricingExecutor = PricingExecutorMode.UiaFirst,
            }),
            _db,
            surface,
            activityGate,
            readiness,
            new StaticPmsIdentityProvider(),
            NullLogger<PackageCostApprovalBootstrapper>.Instance,
            PricingTestAuthority.TrustedPublicKeys);

        using var activeRun = await activityGate.EnterExecutionAsync(CancellationToken.None);
        var result = await bootstrapper.TryStageAsync(now, CancellationToken.None);

        Assert.Equal("pricing_package_bootstrap_run_active", result.Code);
        Assert.Empty(surface.Commands);
        Assert.Empty(_db.GetPendingPricingApprovalProposals(
            20,
            now,
            PricingTestAuthority.TrustedPublicKeys));
    }

    [Fact]
    public void Signed_cpu_grant_cannot_authorize_a_package_cost_run()
    {
        var now = DateTimeOffset.UtcNow;
        var cpuContract = PricingTestAuthority.Contract(modality: "uia");
        var packageContract = PricingTestAuthority.Contract(
            modality: "uia",
            costBasis: PricingApprovalContract.PackageCostBasis);
        var cpuGrant = PricingTestAuthority.InstallApproval(_db, cpuContract, now);

        var authority = PricingObservationPolicy.TryAdmitAuthority(
            cpuGrant,
            PricingTestAuthority.PharmacyId,
            PricingTestAuthority.AgentId,
            PricingTestAuthority.MachineFingerprint,
            packageContract,
            now,
            PricingTestAuthority.TrustedPublicKeys,
            out var code);

        Assert.Null(authority);
        Assert.Equal("pricing_cost_basis_approval_required", code);
        Assert.Equal(PricingApprovalContract.SchemaVersion, cpuGrant.SchemaVersion);
        Assert.Equal(PricingApprovalContract.CostPerUnitBasis, cpuGrant.CostBasis);
        Assert.Equal(
            PricingApprovalContract.PackageSnapshotContractV2,
            packageContract.SnapshotContract);
    }

    [Fact]
    public void Result_integrity_rejects_cross_basis_relabeling()
    {
        var input = ReadResult.Ok(
            [new NdcRow(2, "00093505698", "00093505698")],
            [],
            4,
            1);
        Assert.True(PricingRunIntegrity.TryCreateManifest(input, out var manifest));
        var row = input.Rows[0];
        var relabeled = new SupplierPriceResult(
            "job",
            2,
            "00093505698",
            true,
            "ParMed",
            2.6000m,
            null,
            CostBasis: PricingApprovalContract.PackageCostBasis);

        Assert.False(PricingRunIntegrity.TryValidateLookupResult(
            "job",
            row,
            relabeled,
            PricingApprovalContract.PackageCostBasis,
            out var code));
        Assert.Equal("pricing_result_outcome_invalid", code);
    }

    [Fact]
    public void Package_surface_failures_halt_but_no_eligible_row_remains_reviewable()
    {
        SupplierPriceResult Failure(string error) => new(
            "job", 2, "00093505698", false, null, null, error,
            CostBasis: PricingApprovalContract.PackageCostBasis);

        Assert.True(PricingJobRunner.IsPackageCostInfrastructureFailure(
            Failure("Pricing grid package-cost schema not recognized")));
        Assert.True(PricingJobRunner.IsPackageCostInfrastructureFailure(
            Failure("pricing_screen_identity_changed")));
        Assert.False(PricingJobRunner.IsPackageCostInfrastructureFailure(
            Failure("No eligible package-cost supplier rows")));
        Assert.False(PricingJobRunner.IsPackageCostInfrastructureFailure(
            Failure("Loaded item did not match the requested identifier")));
    }

    [Fact]
    public async Task Package_run_completes_with_legitimate_needs_review_rows()
    {
        var workbookPath = CreatePackageWorkbook();
        try
        {
            var contract = PricingTestAuthority.Contract(
                modality: "uia",
                costBasis: PricingApprovalContract.PackageCostBasis);
            var authority = PricingTestAuthority.InstallAuthority(_db, contract);
            var spec = new PricingJobSpec(
                "package-review-job",
                workbookPath,
                "NDC",
                "Cheapest Supplier",
                "Cost",
                CostBasis: PricingApprovalContract.PackageCostBasis);
            var runner = new PricingJobRunner(
                new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
                new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
                _db,
                NullLogger<PricingJobRunner>.Instance,
                interLookupDelay: TimeSpan.Zero,
                trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
            var updates = new List<PricingJobLocalProgress>();
            string? deliverablePath = null;

            var progress = await runner.RunAsync(
                spec,
                new PackageCostIpcClient(),
                contract,
                authority,
                Array.Empty<SuavoAgent.Contracts.Learning.SelectorPatch>(),
                null,
                null,
                CancellationToken.None,
                (update, _) =>
                {
                    updates.Add(update);
                    return ValueTask.CompletedTask;
                },
                path => deliverablePath = path);

            Assert.True(
                progress.Status == PricingJobStatus.Completed,
                $"status={progress.Status} reason={progress.HaltReason}");
            Assert.Equal(1, progress.CompletedItems);
            Assert.Equal(1, progress.FailedItems);
            var outputPath = Assert.Single(Directory.GetFiles(
                Path.GetDirectoryName(workbookPath)!,
                $"{Path.GetFileNameWithoutExtension(workbookPath)}-priced-*.xlsx"));
            Assert.Equal(outputPath, deliverablePath);
            Assert.Equal(PricingJobLocalPhase.PricingItems, updates[0].Phase);
            Assert.Equal(PricingJobLocalPhase.CreatingSpreadsheet, updates[^2].Phase);
            Assert.Equal(PricingJobLocalPhase.VerifyingResults, updates[^1].Phase);
            Assert.All(updates, update =>
            {
                Assert.InRange(update.ProcessedItems, 0, update.TotalItems);
                Assert.InRange(update.NeedsReviewItems, 0, update.ProcessedItems);
            });
            using var output = new XLWorkbook(outputPath);
            Assert.Equal("ParMed", output.Worksheet(1).Cell(2, 5).GetString());
            Assert.Equal("Needs review", output.Worksheet(1).Cell(3, 5).GetString());
            File.Delete(outputPath);
        }
        finally
        {
            File.Delete(workbookPath);
        }
    }

    private static string CreatePackageWorkbook()
    {
        var path = Path.Combine(
            Path.GetTempPath(), $"package-source-{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.AddWorksheet("Top 500");
        var headers = new[] { "Rank", "Drug", "Strength", "NDC" };
        for (var index = 0; index < headers.Length; index++)
            sheet.Cell(1, index + 1).Value = headers[index];
        sheet.Cell(2, 1).Value = 1;
        sheet.Cell(2, 2).Value = "Example Drug";
        sheet.Cell(2, 3).Value = "10 mg";
        sheet.Cell(2, 4).SetValue("00093505698");
        sheet.Cell(3, 1).Value = 2;
        sheet.Cell(3, 2).Value = "Omeprazole";
        sheet.Cell(3, 3).Value = "40 mg";
        sheet.Cell(3, 4).SetValue("55111064501");
        workbook.SaveAs(path);
        return path;
    }

    private sealed class PackageCostIpcClient : IIpcCommandClient
    {
        public bool IsConnected => true;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct)
        {
            var pricing = request.Data!.Value.Deserialize<NdcPricingRequest>()!;
            if (pricing.CostBasis != PricingApprovalContract.PackageCostBasis)
                throw new InvalidOperationException("package_cost_basis_not_propagated");
            var found = pricing.RowIndex == 2;
            var result = new SupplierPriceResult(
                pricing.JobId,
                pricing.RowIndex,
                pricing.Ndc,
                found,
                found ? "ParMed" : null,
                null,
                found ? null : "No eligible package-cost supplier rows",
                PackageCost: found ? 2.6000m : null,
                CostBasis: PricingApprovalContract.PackageCostBasis);
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null));
        }
    }

    private sealed class BootstrapSurfaceIpcClient(string screenSignature) : IIpcCommandClient
    {
        internal List<string> Commands { get; } = [];
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request,
            TimeSpan timeout,
            CancellationToken ct)
        {
            Commands.Add(request.Command);
            Assert.Equal(IpcCommands.PricingObservationContext, request.Command);
            Assert.Null(request.Data);
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(new PricingScreenObservationContext(
                    42,
                    screenSignature)),
                null));
        }
    }

    private sealed class StaticPmsIdentityProvider : IPioneerRxAutonomyIdentityProvider
    {
        public PioneerRxAutonomyIdentity? Current(DateTimeOffset now) => new(
            "1.0.0.0",
            new string('1', 64),
            new string('2', 64),
            new string('3', 64),
            new string('4', 64),
            7);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_path); } catch { }
    }
}
