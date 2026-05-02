using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Tests for the periodic vision capture worker. The worker is the missing
/// Core-side caller that turns the Helper's already-wired capture_screen
/// IPC handler from a dormant endpoint into a real capture pipeline.
/// </summary>
public class VisionCaptureWorkerTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly FakeIpcCommandClient _ipc = new();
    private const string PharmacyId = "pharm-vc-test";
    private const string SessionId = "learn-agent-vc-test";

    public VisionCaptureWorkerTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_vc_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, PharmacyId);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private VisionCaptureWorker BuildWorker(
        bool visionEnabled = true,
        bool periodicEnabled = true,
        bool requireForeground = true,
        int intervalSec = 30,
        VisionCaptureTelemetry? telemetry = null) =>
        new(
            NullLogger<VisionCaptureWorker>.Instance,
            Options.Create(new AgentOptions { PharmacyId = PharmacyId }),
            StaticOptionsMonitor<VisionOptions>.Create(new VisionOptions
            {
                Enabled = visionEnabled,
                PeriodicCapture = new VisionPeriodicCaptureOptions
                {
                    Enabled = periodicEnabled,
                    IntervalSeconds = intervalSec,
                    RequireForegroundMatch = requireForeground,
                },
            }),
            _db,
            _ipc,
            telemetry: telemetry);

    [Fact]
    public async Task Tick_NoActiveSession_Skips()
    {
        var worker = BuildWorker();
        var orphanDb = new AgentStateDb(Path.Combine(Path.GetTempPath(),
            $"suavo_vc_orphan_{Guid.NewGuid():N}.db"));
        // No CreateLearningSession on the orphan DB — GetActiveSessionId returns null.
        try
        {
            var orphanWorker = new VisionCaptureWorker(
                NullLogger<VisionCaptureWorker>.Instance,
                Options.Create(new AgentOptions { PharmacyId = "different-pharmacy" }),
                StaticOptionsMonitor<VisionOptions>.Create(new VisionOptions { Enabled = true,
                    PeriodicCapture = new VisionPeriodicCaptureOptions { Enabled = true } }),
                orphanDb,
                _ipc);
            var result = await orphanWorker.TickAsync(CancellationToken.None);
            Assert.False(result.Success);
            Assert.Equal("no_active_session", result.Reason);
            Assert.Equal(0, _ipc.SendCount);
        }
        finally { orphanDb.Dispose(); }
    }

    [Fact]
    public async Task Tick_PharmacyIdEmpty_Skips()
    {
        var worker = new VisionCaptureWorker(
            NullLogger<VisionCaptureWorker>.Instance,
            Options.Create(new AgentOptions { PharmacyId = null }),
            StaticOptionsMonitor<VisionOptions>.Create(new VisionOptions { Enabled = true,
                PeriodicCapture = new VisionPeriodicCaptureOptions { Enabled = true } }),
            _db,
            _ipc);

        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("no_pharmacy_id", result.Reason);
        Assert.Equal(0, _ipc.SendCount);
    }

    [Fact]
    public async Task Tick_VisionDisabled_SkipsWithoutAuditOrIpc()
    {
        var auditCountBefore = _db.GetAuditEntryCount();
        var telemetry = new VisionCaptureTelemetry();
        var worker = BuildWorker(visionEnabled: false, telemetry: telemetry);

        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("vision_disabled", result.Reason);
        Assert.Equal(auditCountBefore, _db.GetAuditEntryCount());
        Assert.Equal(0, _ipc.SendCount);
        Assert.Equal("skipped", telemetry.Snapshot().LastOutcome);
        Assert.Equal("vision_disabled", telemetry.Snapshot().LastReason);
    }

    [Fact]
    public async Task Tick_PeriodicCaptureDisabled_SkipsWithoutAuditOrIpc()
    {
        var auditCountBefore = _db.GetAuditEntryCount();
        var worker = BuildWorker(periodicEnabled: false);

        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("periodic_capture_disabled", result.Reason);
        Assert.Equal(auditCountBefore, _db.GetAuditEntryCount());
        Assert.Equal(0, _ipc.SendCount);
    }

    [Fact]
    public async Task Tick_IpcDisconnected_Skips_WithTerminalAuditEntry()
    {
        // Every attempted capture should have a terminal audit outcome so
        // forensic review never sees a dangling request row.
        _ipc.ConnectShouldSucceed = false;
        _ipc.IsConnectedValue = false;
        var auditCountBefore = _db.GetAuditEntryCount();

        var telemetry = new VisionCaptureTelemetry();
        var worker = BuildWorker(telemetry: telemetry);
        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("ipc_disconnected", result.Reason);
        Assert.Equal(auditCountBefore + 2, _db.GetAuditEntryCount());
        Assert.Equal(0, _ipc.SendCount);
        Assert.Equal("failed", telemetry.Snapshot().LastOutcome);
        Assert.Equal("ipc_disconnected", telemetry.Snapshot().LastReason);
    }

    [Fact]
    public async Task Tick_HelperReturnsSuccess_WritesPreAndPostAuditWithStorageId()
    {
        // Happy path: Helper returns 200 with storageId in payload.
        // Audit chain MUST contain both the pre-send entry (status=request)
        // and the followup entry (status=complete) with StorageId set so a
        // forensic auditor can pair the row to the encrypted screen file.
        _ipc.IsConnectedValue = true;
        _ipc.NextResponse = (req) => new IpcResponse(
            req.Id, 200, req.Command,
            JsonSerializer.SerializeToElement(new { storageId = "store-abc-123", frame = (object?)null }),
            null);

        var auditCountBefore = _db.GetAuditEntryCount();
        var telemetry = new VisionCaptureTelemetry();
        var worker = BuildWorker(telemetry: telemetry);
        var result = await worker.TickAsync(CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("store-abc-123", result.StorageId);
        Assert.Equal(auditCountBefore + 2, _db.GetAuditEntryCount());
        Assert.True(_db.VerifyAuditChain());
        Assert.Equal(1, telemetry.Snapshot().CapturedCount);
        Assert.Equal("captured", telemetry.Snapshot().LastOutcome);
        Assert.Equal("store-abc-123", telemetry.Snapshot().LastStorageId);
    }

    [Fact]
    public async Task Tick_HelperReturnsNotForeground_ReportsFailure()
    {
        // not_foreground is the alt-tab gate firing — expected, not an error.
        _ipc.IsConnectedValue = true;
        _ipc.NextResponse = (req) => new IpcResponse(
            req.Id, 403, req.Command, null,
            new IpcError("not_foreground", "Capture refused — PMS not foreground", false, 0));

        var worker = BuildWorker();
        var auditCountBefore = _db.GetAuditEntryCount();
        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("not_foreground", result.Reason);
        Assert.Equal(auditCountBefore + 2, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task Tick_HelperReturnsCaptureFailed_ReportsFailure_WithTerminalAuditEntry()
    {
        _ipc.IsConnectedValue = true;
        _ipc.NextResponse = (req) => new IpcResponse(
            req.Id, 500, req.Command, null,
            new IpcError("capture_failed", "Vision rate-limited", false, 0));

        var worker = BuildWorker();
        var auditCountBefore = _db.GetAuditEntryCount();
        var result = await worker.TickAsync(CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal("capture_failed", result.Reason);
        Assert.Equal(auditCountBefore + 2, _db.GetAuditEntryCount());
    }

    [Fact]
    public async Task Tick_HelperReturnsSuccess_EachCaptureGetsUniqueCommandId()
    {
        // CommandId is the audit-chain pivot key — paired pre/post entries
        // must share the same id, but different captures must NOT collide.
        _ipc.IsConnectedValue = true;
        _ipc.NextResponse = (req) => new IpcResponse(
            req.Id, 200, req.Command,
            JsonSerializer.SerializeToElement(new { storageId = (string?)null, frame = (object?)null }),
            null);

        var worker = BuildWorker();
        var r1 = await worker.TickAsync(CancellationToken.None);
        var r2 = await worker.TickAsync(CancellationToken.None);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotEqual(r1.CommandId, r2.CommandId);
    }
}

internal sealed class FakeIpcCommandClient : IIpcCommandClient
{
    public bool IsConnectedValue { get; set; } = true;
    public bool ConnectShouldSucceed { get; set; } = true;
    public Func<IpcRequest, IpcResponse?> NextResponse { get; set; } =
        _ => new IpcResponse("", 200, "", null, null);
    public int SendCount { get; private set; }

    public bool IsConnected => IsConnectedValue;

    public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) =>
        Task.FromResult(ConnectShouldSucceed);

    public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
    {
        SendCount++;
        return Task.FromResult(NextResponse(request));
    }
}

internal sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
{
    private readonly T _value;

    private StaticOptionsMonitor(T value)
    {
        _value = value;
    }

    public T CurrentValue => _value;

    public T Get(string? name) => _value;

    public IDisposable? OnChange(Action<T, string?> listener) => null;

    public static StaticOptionsMonitor<T> Create(T value) => new(value);
}
