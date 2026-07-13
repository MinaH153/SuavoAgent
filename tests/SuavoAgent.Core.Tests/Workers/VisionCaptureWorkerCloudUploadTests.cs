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
/// CloudFrameUpload: when enabled, VisionCaptureWorker POSTs metadata-only geometry and
/// allow-listed roles to /api/agent/screen-frame. Label-bearing ScreenFrame fields must
/// never enter the cloud serializer. When disabled (default), nothing leaves the box.
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

    private void RespondWithFrame(ScreenFrame? frame = null)
    {
        frame ??= new ScreenFrame
        {
            Id = "Jane Doe frame RX-839201",
            CapturedAt = DateTimeOffset.UnixEpoch,
            Width = 100,
            Height = 100,
            ExtractorId = "Oxycodone at 123 Main Street",
            TextRegions = new[]
            {
                new TextRegion
                {
                    Text = "Jane Doe takes Oxycodone RX-839201 at 123 Main Street",
                    Bounds = new Rect(0, 0, 10, 10),
                    Confidence = 0.9,
                },
            },
            Elements = new[]
            {
                new VisualElement
                {
                    Role = "Button",
                    Name = "Open Jane Doe",
                    AutomationId = "patient-rx-839201",
                    Bounds = new Rect(10, 10, 20, 20),
                    Confidence = 0.8,
                },
                new VisualElement
                {
                    Role = "Jane Doe",
                    Name = "123 Main Street",
                    AutomationId = "Oxycodone",
                    Bounds = new Rect(20, 20, 20, 20),
                    Confidence = 0.7,
                },
            },
        };
        var data = JsonSerializer.SerializeToElement(
            new { storageId = "s1", frame },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        _ipc.NextResponse = req => new IpcResponse(req.Id, 200, "", data, null);
    }

    [Fact]
    public async Task Enabled_PostsOnlyGeometryStatusAndAllowListedRoles()
    {
        RespondWithFrame();
        var cloud = new FakePostSigner();

        await Build(uploadEnabled: true, cloud).TickAsync(CancellationToken.None);

        Assert.Equal(1, cloud.Calls);
        Assert.Equal("/api/agent/screen-frame", cloud.LastPath);
        Assert.NotNull(cloud.LastPayload);

        var json = JsonSerializer.Serialize(cloud.LastPayload);
        foreach (var forbidden in new[]
                 {
                     "Jane Doe", "Oxycodone", "123 Main Street", "RX-839201",
                     "patient-rx-839201", "textRegions", "TextRegions", "name", "Name",
                     "automationId", "AutomationId", "extractorId", "ExtractorId",
                     "confidence", "Confidence", "rawObservation", "windowTitle",
                 })
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(json);
        var frame = document.RootElement.GetProperty("frame");
        Assert.Equal(
            new[] { "capturedAt", "elements", "height", "id", "regions", "status", "width" },
            frame.EnumerateObject().Select(property => property.Name).Order().ToArray());
        Assert.Equal("captured", frame.GetProperty("status").GetString());

        var region = frame.GetProperty("regions")[0];
        Assert.Equal(new[] { "bounds" }, region.EnumerateObject().Select(property => property.Name).ToArray());

        var elements = frame.GetProperty("elements");
        Assert.Equal("button", elements[0].GetProperty("role").GetString());
        Assert.Equal("element", elements[1].GetProperty("role").GetString());
        Assert.All(
            elements.EnumerateArray(),
            element => Assert.Equal(
                new[] { "bounds", "role" },
                element.EnumerateObject().Select(property => property.Name).Order().ToArray()));
    }

    [Fact]
    public async Task Disabled_DoesNotPost()
    {
        RespondWithFrame();
        var cloud = new FakePostSigner();

        await Build(uploadEnabled: false, cloud).TickAsync(CancellationToken.None);

        Assert.Equal(0, cloud.Calls);
    }

    [Fact]
    public async Task InvalidFrameDimensions_FailClosedWithoutCloudPost()
    {
        RespondWithFrame(new ScreenFrame
        {
            Id = "Jane Doe",
            CapturedAt = DateTimeOffset.UnixEpoch,
            Width = 0,
            Height = 100,
            ExtractorId = "Oxycodone",
        });
        var cloud = new FakePostSigner();

        await Build(uploadEnabled: true, cloud).TickAsync(CancellationToken.None);

        Assert.Equal(0, cloud.Calls);
    }
}
