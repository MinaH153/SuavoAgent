using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// CloudFrameUpload: when enabled, VisionCaptureWorker POSTs the scrubbed ScreenFrame to
/// /api/agent/screen-frame for the live dashboard view. When disabled (default), nothing
/// leaves the box. (End-to-end — agent online, Vision on, dashboard render — is validated
/// on a real box; this covers the gate + wiring.)
/// </summary>
public class VisionCaptureWorkerCloudUploadTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly FakeIpcCommandClient _ipc = new();
    private const string PharmacyId = "pharm-frame";
    private const string SessionId = "learn-frame";

    public VisionCaptureWorkerCloudUploadTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_vcf_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, PharmacyId);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class FakePostSigner : IPostSigner
    {
        public int Calls;
        public string? LastPath;
        public object? LastPayload;

        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct)
        {
            Calls++;
            LastPath = path;
            LastPayload = payload;
            return Task.FromResult<JsonElement?>(null);
        }

        public Task<JsonElement?> PostSignedVerifiedAsync(string path, object payload, string publicKeyDer, CancellationToken ct)
            => Task.FromResult<JsonElement?>(null);
    }

    private VisionCaptureWorker Build(bool uploadEnabled, IPostSigner cloud) =>
        new(
            NullLogger<VisionCaptureWorker>.Instance,
            Options.Create(new AgentOptions { PharmacyId = PharmacyId }),
            StaticOptionsMonitor<VisionOptions>.Create(new VisionOptions
            {
                Enabled = true,
                PeriodicCapture = new VisionPeriodicCaptureOptions { Enabled = true },
                CloudFrameUpload = new VisionCloudFrameUploadOptions { Enabled = uploadEnabled },
            }),
            _db,
            _ipc,
            cloud: cloud);

    private void RespondWithFrame()
    {
        var frame = new ScreenFrame
        {
            Id = "f",
            CapturedAt = DateTimeOffset.UnixEpoch,
            Width = 1,
            Height = 1,
            ExtractorId = "t",
            TextRegions = new[] { new TextRegion { Text = "Pricing", Bounds = new Rect(0, 0, 1, 1), Confidence = 0.9 } },
        };
        var data = JsonSerializer.SerializeToElement(
            new { storageId = "s1", frame },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ipc.NextResponse = req => new IpcResponse(req.Id, 200, "", data, null);
    }

    [Fact]
    public async Task Enabled_PostsScrubbedFrameToScreenFrameEndpoint()
    {
        RespondWithFrame();
        var cloud = new FakePostSigner();

        await Build(uploadEnabled: true, cloud).TickAsync(CancellationToken.None);

        Assert.Equal(1, cloud.Calls);
        Assert.Equal("/api/agent/screen-frame", cloud.LastPath);
        Assert.NotNull(cloud.LastPayload);
    }

    [Fact]
    public async Task Disabled_DoesNotPost()
    {
        RespondWithFrame();
        var cloud = new FakePostSigner();

        await Build(uploadEnabled: false, cloud).TickAsync(CancellationToken.None);

        Assert.Equal(0, cloud.Calls);
    }
}
