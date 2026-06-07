using ClosedXML.Excel;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Config;
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
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"suavo_exec_cfg_{Guid.NewGuid():N}");
    private readonly AgentStateDb _db;

    public PricingExecutorConfigTests()
    {
        Directory.CreateDirectory(_tempDir);
        _db = new AgentStateDb(Path.Combine(_tempDir, "state.db"));
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
    public void AgentOptions_PricingExecutor_DefaultsToSqlFirst()
    {
        // Default is intentionally SqlFirst — existing pharmacies must not silently switch
        // to UIA on upgrade. Opt-in only via explicit appsettings or env override.
        var options = new AgentOptions();
        Assert.Equal(PricingExecutorMode.SqlFirst, options.PricingExecutor);
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
            NullLogger<UiaFirstPricingJobExecutor>.Instance);

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

        var progress = await runner.RunAsync(spec, deadHelper, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Halted, progress.Status);                       // distinct, resumable status
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

        var progress = await runner.RunAsync(spec, flakyHelper, CancellationToken.None);

        Assert.Equal(PricingJobStatus.Completed, progress.Status); // never 3 consecutive nulls → never aborts
        Assert.Equal(9, flakyHelper.SendCount);                    // every row attempted exactly once
    }

    private PricingJobRunner NewIpcRunner() => new(
        new ExcelPricingReader(NullLogger<ExcelPricingReader>.Instance),
        new ExcelPricingWriter(NullLogger<ExcelPricingWriter>.Instance),
        _db, NullLogger<PricingJobRunner>.Instance,
        brainEvaluator: null, interLookupDelay: TimeSpan.Zero);

    private static PricingJobSpec NewSpec(string xlsx, string jobId) => new(
        JobId: jobId, ExcelPath: xlsx,
        NdcColumn: PricingJobDefaults.NdcColumn,
        SupplierColumn: PricingJobDefaults.SupplierColumn,
        CostColumn: PricingJobDefaults.CostColumn);

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
}
