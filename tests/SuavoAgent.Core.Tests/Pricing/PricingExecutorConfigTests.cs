using ClosedXML.Excel;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// Covers the new pricing executor / throttle configuration plumbing landed for the Nadim
/// UIA-first pilot. The actual UiaFirstPricingJobExecutor needs a live IpcCommandClient and
/// Helper process, so its end-to-end behavior is verified by the smoke test rather than here.
/// </summary>
public class PricingExecutorConfigTests : IDisposable
{
    private static readonly string TestScreenSignature = new('c', 64);
    private static readonly PioneerRxAutonomyIdentity TestPmsIdentity = new(
        "1.2.3",
        new string('a', 64),
        new string('b', 64),
        new string('d', 64),
        new string('e', 64),
        7);
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_exec_cfg_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingExecutorConfigTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
        Assert.True(_db.RecordPricingCloudAuthorityHeartbeat(
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            out _));
    }

    public void Dispose()
    {
        _db.Dispose();
        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AgentOptions_PricingExecutor_DefaultsToUiaFirst()
    {
        // Default flipped to UiaFirst (2026-06-20): stealth is the product model — a pharmacy install
        // must not have to expose the agent to PioneerRx/the vendor, and SqlFirst requires provisioning
        // SQL access (asking a DBA for a login does exactly that). UiaFirst drives PioneerRx's UI like a
        // pharmacist — no SQL, no credentials, invisible to the PMS. TRADEOFF: an existing pharmacy that
        // relied on the SqlFirst default switches to UIA on upgrade; it must opt back in explicitly via
        // the Agent.PricingExecutor cloud override (now allowlisted) once it has authorized SQL access.
        var options = new AgentOptions();
        Assert.Equal(PricingExecutorMode.UiaFirst, options.PricingExecutor);
    }

    [Fact]
    public void AgentOptions_PricingThrottleMs_Defaults_To_1500()
    {
        // 1500 ms is the UIA-safe default selected for the Nadim pilot — keeps a 500-NDC run
        // at ~12.5 min and stays below any anti-automation heuristic we suspect PioneerRx may
        // apply. SQL-first paths may safely lower this via config.
        var options = new AgentOptions();
        Assert.Equal(1500, options.PricingThrottleMs);
    }

    [Fact]
    public void PricingPayloadBudget_Enforces_Top500_Before_Actuation()
    {
        Assert.Equal(500, PricingResultPayloadBudget.MaximumRequiredRows);
        Assert.True(
            PricingResultPayloadBudget.MaximumTransportRows >
            PricingResultPayloadBudget.MaximumRequiredRows);
        Assert.True(PricingResultPayloadBudget.CanAdmitWorkload(500, 500));
        Assert.False(PricingResultPayloadBudget.CanAdmitWorkload(501, 501));
    }

    [Fact]
    public void PricingExecutorMode_HasBothValues()
    {
        // Defensive: detect accidental renames in the enum that would break appsettings
        // binding (Microsoft.Extensions.Configuration binds enums by case-insensitive name).
        Assert.True(Enum.IsDefined(typeof(PricingExecutorMode), "SqlFirst"));
        Assert.True(Enum.IsDefined(typeof(PricingExecutorMode), "UiaFirst"));
    }

    [Theory]
    [InlineData(-5000)]       // negative typo - clamp to zero
    [InlineData(0)]           // explicit zero - allowed
    [InlineData(1500)]        // recommended UIA default
    [InlineData(60_000)]      // 1 minute - over the 30s ceiling, clamp to 30000
    [InlineData(3_600_000)]   // 1 hour - clearly a typo, clamp to 30000
    public void PricingJobRunner_AnyThrottleValue_Constructs_Without_Throwing(int throttleMs)
    {
        // The runner's constructor must clamp absurd throttle inputs (negative, huge) without
        // raising. The clamp is what protects a misconfigured pharmacy from accidentally
        // stalling jobs forever.
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);

        var runner = new PricingJobRunner(
            reader,
            writer,
            _db,
            NullLogger<PricingJobRunner>.Instance,
            brainEvaluator: null,
            interLookupDelay: TimeSpan.FromMilliseconds(throttleMs));

        Assert.NotNull(runner);
    }

    [Fact]
    public async Task UiaFirstExecutor_BlindRunGate_RefusesToRun_WhenHelperNotInteractive()
    {
        // Executor invariant: the UIA-first executor must NOT touch the live screen unless the
        // Helper pre-flight passes (reachable + answering + SI=1). This gate now lives in the
        // EXECUTOR so any caller (not just heartbeat dispatch) is fail-closed. Here the Helper
        // pipe is unreachable → the executor must return Failed WITHOUT invoking the runner.
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);
        var runner = new PricingJobRunner(reader, writer, _db, NullLogger<PricingJobRunner>.Instance);

        var executor = new UiaFirstPricingJobExecutor(
            runner,
            new UnreachableIpcCommandClient(),
            _db,
            new FixedGateGateway(new ActuationGateState(true, false, null, null, null)),
            NullLogger<UiaFirstPricingJobExecutor>.Instance,
            Microsoft.Extensions.Options.Options.Create(ApprovedUiaOptions()),
            new FixedPmsIdentityProvider(),
            PricingTestAuthority.TrustedPublicKeys);

        var spec = new PricingJobSpec(
            JobId: "job-blindrun-1",
            ExcelPath: Path.Combine(_tempDir, "does-not-matter.xlsx"),
            NdcColumn: PricingJobDefaults.NdcColumn,
            SupplierColumn: PricingJobDefaults.SupplierColumn,
            CostColumn: PricingJobDefaults.CostColumn);

        var result = await executor.RunAsync(spec, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PricingJobStatus.Failed, result.Progress.Status);
        Assert.NotNull(result.Error); // operator-facing pre-flight reason, never a silent run
    }

    /// <summary>Fake Helper IPC that never connects — drives the pre-flight to fail-closed.</summary>
    private sealed class UnreachableIpcCommandClient : IIpcCommandClient
    {
        public bool IsConnected => false;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct) =>
            throw new InvalidOperationException("SendAsync must not be called when the pipe is unreachable");
    }

    [Theory]
    [InlineData(false, false, false, false, ActuationRejectionCodes.GateDisabled)]
    [InlineData(true, true, false, false, ActuationRejectionCodes.GateDryRun)]
    [InlineData(true, false, true, false, ActuationRejectionCodes.KillSwitchTripped)]
    [InlineData(true, false, false, true, ActuationRejectionCodes.CompromiseDetected)]
    public void PricingActuationPreflight_RejectsEveryClosedLiveGateAxis(
        bool enabled, bool dryRun, bool killed, bool compromised, string expected)
    {
        var state = new ActuationGateState(
            enabled,
            dryRun,
            null,
            null,
            killed ? DateTimeOffset.UtcNow : null,
            CompromiseDetected: compromised);

        Assert.Equal(expected, PricingActuationPreflight.RejectionCode(state, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void PricingActuationPreflight_RejectsActiveUserPause()
    {
        var now = DateTimeOffset.UtcNow;
        var state = new ActuationGateState(true, false, now.AddMinutes(1), "user", null);

        Assert.Equal(ActuationRejectionCodes.GatePaused, PricingActuationPreflight.RejectionCode(state, now));
    }

    private sealed class FixedGateGateway(ActuationGateState state) : IActuationGateway
    {
        public Task<ActuationGateState> GetStateAsync(CancellationToken ct) => Task.FromResult(state);
        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct) => throw new NotSupportedException();
        public Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public void PricingJobRunner_NullThrottle_FallsBackToDefault()
    {
        // The optional throttle parameter must default cleanly — back-compat for callers
        // that haven't been updated to pass a value yet (none in production, but the
        // constructor shape is public).
        var reader = new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance);
        var writer = new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance);

        var runner = new PricingJobRunner(
            reader,
            writer,
            _db,
            NullLogger<PricingJobRunner>.Instance,
            brainEvaluator: null,
            interLookupDelay: null);

        Assert.NotNull(runner);
    }

    // ── B1: hung-Helper early abort + resumability ───────────────────────────────────────────────
    // A hung/disconnected Helper returns NO response for every NDC lookup. Without the abort the loop
    // grinds the whole workbook (each row eats a reconnect + up-to-30s timeout), marks everything
    // "failed", and reports a finished job — masking "Helper IPC is down" as "nothing was priced".

    [Fact]
    public async Task RunAsync_HelperUnreachable_AbortsEarly_Halted_AndLeavesRowsUnpersisted()
    {
        var xlsx = CreateNdcWorkbook(rowCount: 10);
        var spec = NewSpec(xlsx, "job-helper-unreachable");
        var runner = NewIpcRunner();
        var deadHelper = new CountingNullIpcClient();

        var progress = await RunIpcAsync(runner, spec, deadHelper);

        Assert.Equal(PricingJobStatus.Halted, progress.Status);                       // distinct, resumable status
        Assert.Equal("helper_unreachable", progress.HaltReason);                      // stable code threaded to the cockpit badge
        Assert.Equal(10, progress.TotalItems);                                        // the workbook really had 10 rows
        Assert.Equal(PricingJobRunner.MaxConsecutiveIpcFailuresBeforeAbort,
            deadHelper.SendCount);                                                    // aborted at 3 — did NOT grind all 10
        Assert.Empty(_db.GetPricingResults(spec.JobId));                              // nothing persisted → fully resumable
    }

    [Fact]
    public async Task RunAsync_TransientIpcGaps_DoNotAbort_WhenHelperKeepsResponding()
    {
        // Only CONSECUTIVE no-responses abort. A Helper that keeps answering (even with errors)
        // between gaps is alive — the counter must reset on every response, so the job completes.
        var xlsx = CreateNdcWorkbook(rowCount: 9);
        var spec = NewSpec(xlsx, "job-intermittent-ipc");
        var runner = NewIpcRunner();
        var flakyHelper = new TwoNullThenReachableIpcClient(); // null, null, reachable, repeating

        var progress = await RunIpcAsync(runner, spec, flakyHelper);

        Assert.Equal(PricingJobStatus.Failed, progress.Status); // not halted, but row failures still forbid success
        Assert.Equal("pricing_job_failed", progress.HaltReason);
        Assert.Equal(9, flakyHelper.SendCount);                    // every row attempted exactly once
    }

    [Fact]
    public async Task UiaFirstExecutor_NoMatch_WritesReviewOutputButReturnsFailed()
    {
        var xlsx = CreateNdcWorkbook(rowCount: 1);
        var spec = NewSpec(xlsx, "job-uia-no-match");
        var ipc = new InteractiveNoMatchIpcClient();
        PricingTestAuthority.InstallApproval(_db, ApprovedUiaContract());
        var executor = new UiaFirstPricingJobExecutor(
            NewIpcRunner(),
            ipc,
            _db,
            new FixedGateGateway(new ActuationGateState(true, false, null, null, null)),
            NullLogger<UiaFirstPricingJobExecutor>.Instance,
            Microsoft.Extensions.Options.Options.Create(ApprovedUiaOptions()),
            new FixedPmsIdentityProvider(),
            PricingTestAuthority.TrustedPublicKeys);

        var result = await executor.RunAsync(spec, CancellationToken.None);

        Assert.False(result.Ok);
        Assert.Equal(PricingJobStatus.Failed, result.Progress.Status);
        Assert.Equal("pricing_job_failed", result.Progress.HaltReason);
        Assert.Equal(0, result.Progress.CompletedItems);
        Assert.Equal(1, result.Progress.FailedItems);
        Assert.Equal(1, ipc.LookupCount);
        Assert.Single(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
    }

    [Fact]
    public async Task RunAsync_ActuationGateCloses_HaltsImmediately_AndLeavesCurrentRowResumable()
    {
        var xlsx = CreateNdcWorkbook(rowCount: 10);
        var spec = NewSpec(xlsx, "job-gate-closed");
        var runner = NewIpcRunner();
        var helper = new GateClosedIpcClient();

        var progress = await RunIpcAsync(runner, spec, helper);

        Assert.Equal(PricingJobStatus.Halted, progress.Status);
        Assert.Equal("actuation_gate_closed", progress.HaltReason);
        Assert.Equal(1, helper.SendCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task RunAsync_HelperInnerResultIdentityMismatch_HaltsWithoutPersistence()
    {
        var xlsx = CreateNdcWorkbook(rowCount: 1);
        var spec = NewSpec(xlsx, "job-helper-inner-mismatch");

        var progress = await RunIpcAsync(
            NewIpcRunner(), spec, new MismatchedInnerResultIpcClient());

        Assert.Equal(PricingJobStatus.Halted, progress.Status);
        Assert.Equal("pricing_result_integrity_failed", progress.HaltReason);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
        Assert.Empty(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
    }

    private PricingJobRunner NewIpcRunner() => new(
        new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
        new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
        _db, NullLogger<PricingJobRunner>.Instance,
        brainEvaluator: null,
        interLookupDelay: TimeSpan.Zero,
        trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);

    [Fact]
    public async Task PricingJobRunner_OversizeRequiredPayloadFailsBeforeAnyHelperActuation()
    {
        var xlsx = CreateNdcWorkbook(
            PricingResultPayloadBudget.MaximumRequiredRows + 1);
        var spec = NewSpec(xlsx, "job-payload-oversize");
        var runner = NewIpcRunner();
        var helper = new CountingNullIpcClient();

        var progress = await RunIpcAsync(runner, spec, helper);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_payload_too_large", progress.HaltReason);
        Assert.Equal(0, helper.SendCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task PricingJobRunner_InvalidHeavyMetricsFailBeforeAnyHelperActuation()
    {
        var xlsx = CreateInvalidNdcWorkbook(
            PricingResultPayloadBudget.MaximumSerializedMetric + 1);
        var spec = NewSpec(xlsx, "job-invalid-heavy");
        var runner = NewIpcRunner();
        var helper = new CountingNullIpcClient();

        var progress = await RunIpcAsync(runner, spec, helper);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_result_payload_too_large", progress.HaltReason);
        Assert.Equal(PricingResultPayloadBudget.MaximumSerializedMetric + 1, progress.TotalItems);
        Assert.Equal(0, helper.SendCount);
        Assert.Empty(_db.GetPricingResults(spec.JobId));
    }

    [Fact]
    public async Task PricingJobRunner_InvalidNdc_WritesReviewOutputButReturnsFailed()
    {
        var xlsx = CreateInvalidNdcWorkbook(rowCount: 1);
        var spec = NewSpec(xlsx, "job-uia-invalid-ndc");
        var helper = new CountingNullIpcClient();

        var progress = await RunIpcAsync(NewIpcRunner(), spec, helper);

        Assert.Equal(PricingJobStatus.Failed, progress.Status);
        Assert.Equal("pricing_job_failed", progress.HaltReason);
        Assert.Equal(0, progress.CompletedItems);
        Assert.Equal(1, progress.FailedItems);
        Assert.Equal(0, helper.SendCount);
        Assert.Single(Directory.GetFiles(_tempDir, "*-priced-*.xlsx"));
    }

    [Fact]
    public async Task PricingJobRunner_RevocationAfterTempFlush_NeverPublishesSibling()
    {
        var issuedAt = DateTimeOffset.UtcNow;
        var contract = PricingTestAuthority.Contract(modality: "uia");
        var grant = PricingTestAuthority.InstallApproval(
            _db,
            contract,
            issuedAt,
            issuedAt.AddDays(7));
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
        var xlsx = CreateInvalidNdcWorkbook(rowCount: 1);
        var sourceBytes = File.ReadAllBytes(xlsx);
        var outputPath = Path.Combine(_tempDir, "uia-revoked-priced.xlsx");
        AgentStateDb.PricingApprovalLedgerResult? revocationResult = null;
        var writer = new ExcelPricingWriter(
            NullLogger<ExcelPricingWriter>.Instance,
            _ => outputPath,
            () =>
            {
                var revokedAt = DateTimeOffset.UtcNow;
                revocationResult = PricingTestAuthority.InstallRevocation(
                    _db,
                    PricingTestAuthority.Revocation(grant, revokedAt),
                    revokedAt);
            });
        var runner = new PricingJobRunner(
            new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
            writer,
            _db,
            NullLogger<PricingJobRunner>.Instance,
            brainEvaluator: null,
            interLookupDelay: TimeSpan.Zero,
            trustedApprovalKeys: PricingTestAuthority.TrustedPublicKeys);
        var spec = NewSpec(xlsx, "job-uia-revoke-before-publication");

        var progress = await runner.RunAsync(
            spec,
            new CountingNullIpcClient(),
            contract,
            authority!,
            CancellationToken.None);

        Assert.NotNull(revocationResult);
        Assert.True(revocationResult!.Succeeded, revocationResult.Code);
        Assert.Equal(PricingJobStatus.Halted, progress.Status);
        Assert.Equal("pricing_cost_basis_approval_revoked", progress.HaltReason);
        Assert.False(File.Exists(outputPath));
        Assert.Equal(sourceBytes, File.ReadAllBytes(xlsx));
        Assert.Empty(Directory.EnumerateFiles(_tempDir, ".suavo-priced-*"));
    }

    private static PricingJobSpec NewSpec(string xlsx, string jobId) => new(
        JobId: jobId, ExcelPath: xlsx,
        NdcColumn: PricingJobDefaults.NdcColumn,
        SupplierColumn: PricingJobDefaults.SupplierColumn,
        CostColumn: PricingJobDefaults.CostColumn);

    private Task<PricingJobProgress> RunIpcAsync(
        PricingJobRunner runner,
        PricingJobSpec spec,
        IIpcCommandClient client)
    {
        var contract = PricingTestAuthority.Contract(modality: "uia");
        var authority = PricingTestAuthority.InstallAuthority(_db, contract);
        return runner.RunAsync(
            spec,
            client,
            contract,
            authority,
            CancellationToken.None);
    }

    private static AgentOptions ApprovedUiaOptions()
    {
        var contract = ApprovedUiaContract();
        return new AgentOptions
        {
            PharmacyId = PricingTestAuthority.PharmacyId,
            AgentId = PricingTestAuthority.AgentId,
            MachineFingerprint = PricingTestAuthority.MachineFingerprint,
            PricingExecutor = PricingExecutorMode.UiaFirst,
            PricingCostBasisApproval = PricingTestAuthority.ApprovalOptions(contract),
        };
    }

    private static PricingObservationContract ApprovedUiaContract()
    {
        var pmsFingerprint = PricingObservationPolicy.Digest(
            "pioneerrx_live_process_identity_v1",
            TestPmsIdentity.FileVersion,
            TestPmsIdentity.ExecutableSha256,
            TestPmsIdentity.SignerCertificateSha256,
            TestPmsIdentity.ApprovalReceiptDigest,
            TestPmsIdentity.AuthorityDigest,
            TestPmsIdentity.ApprovalCounter.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
        return PricingObservationPolicy.CreateUia(
            "uia",
            pmsFingerprint,
            TestScreenSignature,
            Array.Empty<SuavoAgent.Contracts.Learning.SelectorPatch>());
    }

    private sealed class FixedPmsIdentityProvider : IPioneerRxAutonomyIdentityProvider
    {
        public PioneerRxAutonomyIdentity? Current(DateTimeOffset now) => TestPmsIdentity;
    }

    private string CreateNdcWorkbook(int rowCount)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Sheet1");
        ws.Cell(1, 1).Value = PricingJobDefaults.NdcColumn;
        for (int i = 0; i < rowCount; i++)
            ws.Cell(i + 2, 1).Value = $"{50000 + i:D5}-{1000 + i:D4}-01"; // valid 5-4-2 NDC format
        wb.SaveAs(path);
        return path;
    }

    private string CreateInvalidNdcWorkbook(int rowCount)
    {
        var path = Path.Combine(_tempDir, $"{Guid.NewGuid():N}.xlsx");
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Sheet1");
        sheet.Cell(1, 1).Value = PricingJobDefaults.NdcColumn;
        for (var index = 0; index < rowCount; index++)
            sheet.Cell(index + 2, 1).Value = $"invalid-{index:D4}";
        workbook.SaveAs(path);
        return path;
    }

    /// <summary>Helper that hangs: every lookup returns no response at all.</summary>
    private sealed class CountingNullIpcClient : IIpcCommandClient
    {
        public int SendCount;
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            SendCount++;
            return Task.FromResult<IpcResponse?>(null);
        }
    }

    /// <summary>Helper that drops 2 of every 3 lookups but keeps responding in between (max 2
    /// consecutive nulls) — proves the abort is on CONSECUTIVE failures, reset by any response.</summary>
    private sealed class TwoNullThenReachableIpcClient : IIpcCommandClient
    {
        public int SendCount;
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            SendCount++;
            if (SendCount % 3 == 0) // every 3rd call the Helper answers (an error response is still "reachable")
                return Task.FromResult<IpcResponse?>(new IpcResponse(
                    request.Id, IpcStatus.InternalError, request.Command, null,
                    new IpcError("E_BUSY", "supplier grid busy", Retryable: true, AttemptCount: 0)));
            return Task.FromResult<IpcResponse?>(null);
        }
    }

    private sealed class GateClosedIpcClient : IIpcCommandClient
    {
        public int SendCount;
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            SendCount++;
            var pricing = JsonSerializer.Deserialize<NdcPricingRequest>(request.Data!.Value)!;
            var result = new SupplierPriceResult(
                pricing.JobId, pricing.RowIndex, pricing.Ndc, false, null, null,
                PricingSafetyErrors.ActuationGateClosed(ActuationRejectionCodes.GatePaused));
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null));
        }
    }

    private sealed class MismatchedInnerResultIpcClient : IIpcCommandClient
    {
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(
            IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            var pricing = JsonSerializer.Deserialize<NdcPricingRequest>(
                request.Data!.Value)!;
            var result = new SupplierPriceResult(
                pricing.JobId,
                pricing.RowIndex,
                "00093512401",
                true,
                "McKesson",
                0.01m,
                null);
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null));
        }
    }

    private sealed class InteractiveNoMatchIpcClient : IIpcCommandClient
    {
        public int LookupCount { get; private set; }
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            if (request.Command == IpcCommands.Ping)
            {
                var data = JsonSerializer.SerializeToElement(
                    new HelperPingInfo(1234, 1, 1, true));
                return Task.FromResult<IpcResponse?>(new IpcResponse(
                    request.Id, IpcStatus.Ok, request.Command, data, null));
            }

            if (request.Command == IpcCommands.PricingObservationContext)
            {
                var data = JsonSerializer.SerializeToElement(
                    new PricingScreenObservationContext(4321, TestScreenSignature));
                return Task.FromResult<IpcResponse?>(new IpcResponse(
                    request.Id, IpcStatus.Ok, request.Command, data, null));
            }

            var pricing = JsonSerializer.Deserialize<NdcPricingRequest>(request.Data!.Value)!;
            LookupCount++;
            var result = new SupplierPriceResult(
                pricing.JobId, pricing.RowIndex, pricing.Ndc,
                false, null, null, "No supplier rows found");
            return Task.FromResult<IpcResponse?>(new IpcResponse(
                request.Id,
                IpcStatus.Ok,
                request.Command,
                JsonSerializer.SerializeToElement(result),
                null));
        }
    }
}
