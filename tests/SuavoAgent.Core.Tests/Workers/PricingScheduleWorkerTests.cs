using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Gate matrix for the autonomous pricing scheduler. RunOnceAsync must NEVER call the executor unless
/// every gate passes, and the Precedence-1 invariant is explicit: when the operator has chosen UiaFirst,
/// the timer refuses to run (it must never autonomously drive the live PMS UI). When all gates pass it
/// runs exactly one SqlFirst job and threads the configured workbook path + column headers into the spec.
/// </summary>
public sealed class PricingScheduleWorkerTests : IDisposable
{
    private readonly List<string> _tempFiles = new();
    private readonly List<IDisposable> _disposables = new();

    public void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); } catch { /* best effort */ }
        }
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { /* best effort */ }
        }
    }

    private string ExistingWorkbook()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suavo_sched_{Guid.NewGuid():N}.xlsx");
        File.WriteAllText(path, "x"); // the fake executor never reads it; only File.Exists matters
        _tempFiles.Add(path);
        return path;
    }

    private sealed class FakeExecutor : IPricingJobExecutor
    {
        public int Calls;
        public PricingJobSpec? LastSpec;
        public bool Ok = true;
        public string? Error;

        public Task<PricingJobExecutionResult> RunAsync(PricingJobSpec spec, CancellationToken ct)
        {
            Calls++;
            LastSpec = spec;
            var progress = new PricingJobProgress(
                spec.JobId, TotalItems: 3, CompletedItems: Ok ? 3 : 1, FailedItems: Ok ? 0 : 2,
                Status: Ok ? PricingJobStatus.Completed : PricingJobStatus.Failed);
            return Task.FromResult(new PricingJobExecutionResult(progress, "sql", Ok, Error));
        }
    }

    private sealed class RecordingPostSigner : IPostSigner
    {
        public int Calls;
        public string? LastPath;
        public JsonElement LastPayload;

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            Calls++;
            LastPath = path;
            LastPayload = JsonSerializer.SerializeToElement(payload);
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path, object payload, string publicKeyDer, CancellationToken ct) =>
            PostSignedAsync(path, payload, ct);
    }

    private AgentStateDb NewDb()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suavo_sched_db_{Guid.NewGuid():N}.db");
        var db = new AgentStateDb(path);
        _disposables.Add(db);
        _tempFiles.Add(path);
        return db;
    }

    private static PricingScheduleWorker Build(
        AgentOptions opts, IPricingJobExecutor? executor, PricingJobCloudUploader? uploader = null) =>
        new(
            NullLogger<PricingScheduleWorker>.Instance,
            StaticOptionsMonitor<AgentOptions>.Create(opts),
            executor,
            uploader);

    private static AgentOptions Enabled(string? workbookPath) => new()
    {
        PricingExecutor = PricingExecutorMode.SqlFirst,
        PricingSchedule = new PricingScheduleOptions
        {
            Enabled = true,
            WorkbookPath = workbookPath,
        },
    };

    [Fact]
    public async Task RunOnce_WhenScheduleDisabled_Skips_AndNeverCallsExecutor()
    {
        var exec = new FakeExecutor();
        var opts = Enabled(ExistingWorkbook());
        opts.PricingSchedule.Enabled = false;

        var outcome = await Build(opts, exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Skipped, outcome.Status);
        Assert.Equal("disabled", outcome.Reason);
        Assert.Equal(0, exec.Calls);
    }

    [Fact]
    public async Task RunOnce_WhenExecutorModeIsUiaFirst_Skips_NeverDrivesLivePms()
    {
        // PRECEDENCE-1: the autonomous timer must never drive the live PMS UI. Even with everything
        // else green, UiaFirst means "don't run on a schedule".
        var exec = new FakeExecutor();
        var opts = Enabled(ExistingWorkbook());
        opts.PricingExecutor = PricingExecutorMode.UiaFirst;

        var outcome = await Build(opts, exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Skipped, outcome.Status);
        Assert.Equal("not_sqlfirst", outcome.Reason);
        Assert.Equal(0, exec.Calls);
    }

    [Fact]
    public async Task RunOnce_WhenNoExecutorWired_Skips()
    {
        var outcome = await Build(Enabled(ExistingWorkbook()), executor: null)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Skipped, outcome.Status);
        Assert.Equal("no_executor", outcome.Reason);
    }

    [Fact]
    public async Task RunOnce_WhenNoWorkbookConfigured_Skips()
    {
        var exec = new FakeExecutor();

        var outcome = await Build(Enabled(workbookPath: null), exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Skipped, outcome.Status);
        Assert.Equal("no_workbook", outcome.Reason);
        Assert.Equal(0, exec.Calls);
    }

    [Fact]
    public async Task RunOnce_WhenWorkbookFileMissing_Skips()
    {
        var exec = new FakeExecutor();
        var missing = Path.Combine(Path.GetTempPath(), $"suavo_sched_missing_{Guid.NewGuid():N}.xlsx");

        var outcome = await Build(Enabled(missing), exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Skipped, outcome.Status);
        Assert.Equal("workbook_missing", outcome.Reason);
        Assert.Equal(0, exec.Calls);
    }

    [Fact]
    public async Task RunOnce_WhenAllGatesPass_RunsExactlyOneJob_WithConfiguredSpec()
    {
        var exec = new FakeExecutor();
        var workbook = ExistingWorkbook();
        var opts = Enabled(workbook);
        opts.PricingSchedule.NdcColumn = "My NDC";
        opts.PricingSchedule.SupplierColumn = "Cheapest";
        opts.PricingSchedule.CostColumn = "Cheapest Cost";

        var outcome = await Build(opts, exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Completed, outcome.Status);
        Assert.Equal("ok", outcome.Reason);
        Assert.Equal(1, exec.Calls);

        Assert.NotNull(exec.LastSpec);
        Assert.Equal(Path.GetFullPath(workbook), exec.LastSpec!.ExcelPath);
        Assert.Equal("My NDC", exec.LastSpec.NdcColumn);
        Assert.Equal("Cheapest", exec.LastSpec.SupplierColumn);
        Assert.Equal("Cheapest Cost", exec.LastSpec.CostColumn);
        Assert.False(string.IsNullOrWhiteSpace(exec.LastSpec.JobId));

        Assert.NotNull(outcome.Progress);
        Assert.Equal(3, outcome.Progress!.CompletedItems);
    }

    [Fact]
    public async Task RunOnce_WhenExecutorReportsFailure_ReturnsFailedWithReason()
    {
        var exec = new FakeExecutor { Ok = false, Error = "sql pricing lookup unavailable" };

        var outcome = await Build(Enabled(ExistingWorkbook()), exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Failed, outcome.Status);
        Assert.Equal("sql pricing lookup unavailable", outcome.Reason);
        Assert.Equal(1, exec.Calls);
        Assert.Equal(PricingJobStatus.Failed, outcome.Progress!.Status);
    }

    [Fact]
    public async Task RunOnce_WhenRunSucceeds_SurfacesInCockpit_WithNullCommandId()
    {
        // "Surface every operational entry": an autonomous run must reach the cloud (savings ledger +
        // downloadable price list) exactly like a cockpit-triggered run — keyed by the job id, with a
        // null commandId because no operator command exists.
        var exec = new FakeExecutor();
        var signer = new RecordingPostSigner();
        var uploader = new PricingJobCloudUploader(signer, NewDb(), NullLogger<PricingJobCloudUploader>.Instance);

        var outcome = await Build(Enabled(ExistingWorkbook()), exec, uploader).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Completed, outcome.Status);
        Assert.Equal(1, signer.Calls);
        Assert.Contains($"/api/agent/pricing-jobs/{exec.LastSpec!.JobId}/results", signer.LastPath);
        Assert.Equal(JsonValueKind.Null, signer.LastPayload.GetProperty("commandId").ValueKind);
    }

    [Fact]
    public async Task RunOnce_WithNoUploaderWired_StillRuns_NoCrash()
    {
        // The priced workbook is the deliverable; the cloud upload is best-effort. With no API key the
        // uploader is absent and the run must still complete cleanly.
        var exec = new FakeExecutor();

        var outcome = await Build(Enabled(ExistingWorkbook()), exec, uploader: null)
            .RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingScheduleRunStatus.Completed, outcome.Status);
        Assert.Equal(1, exec.Calls);
    }

    [Fact]
    public async Task RunOnce_DefaultColumns_MatchCockpitPricingDefaults()
    {
        var exec = new FakeExecutor();

        await Build(Enabled(ExistingWorkbook()), exec).RunOnceAsync(CancellationToken.None);

        Assert.Equal(PricingJobDefaults.NdcColumn, exec.LastSpec!.NdcColumn);
        Assert.Equal(PricingJobDefaults.SupplierColumn, exec.LastSpec.SupplierColumn);
        Assert.Equal(PricingJobDefaults.CostColumn, exec.LastSpec.CostColumn);
    }
}
