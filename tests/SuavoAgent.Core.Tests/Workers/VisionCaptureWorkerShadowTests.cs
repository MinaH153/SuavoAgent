using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// W4b activation: when ShadowReasoning is enabled, VisionCaptureWorker deserializes the
/// ScreenFrame the Helper already returns and hands it to the reasoner (observe-only). When
/// disabled (default), the frame is ignored exactly as before.
/// </summary>
public class VisionCaptureWorkerShadowTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;
    private readonly FakeIpcCommandClient _ipc = new();
    private const string PharmacyId = "pharm-shadow";
    private const string SessionId = "learn-shadow";

    public VisionCaptureWorkerShadowTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_vcs_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession(SessionId, PharmacyId);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }

    private sealed class FakeReasoner : IVisionShadowReasoner
    {
        public int Calls;
        public ScreenFrame? LastFrame;
        public Task<BrainDecision> ObserveAsync(ScreenFrame frame, string skillId, CancellationToken ct = default)
        {
            Calls++;
            LastFrame = frame;
            return Task.FromResult(new BrainDecision
            {
                Outcome = MatchOutcome.NoMatch,
                Tier = DecisionTier.OperatorRequired,
                Reason = "test",
            });
        }
    }

    private VisionCaptureWorker Build(bool shadowEnabled, IVisionShadowReasoner reasoner) =>
        new(
            NullLogger<VisionCaptureWorker>.Instance,
            Options.Create(new AgentOptions { PharmacyId = PharmacyId }),
            StaticOptionsMonitor<VisionOptions>.Create(new VisionOptions
            {
                Enabled = true,
                PeriodicCapture = new VisionPeriodicCaptureOptions { Enabled = true },
                ShadowReasoning = new VisionShadowReasoningOptions { Enabled = shadowEnabled },
            }),
            _db,
            _ipc,
            reasoner: reasoner);

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
    public async Task ShadowReasoningEnabled_ObservesCapturedFrame()
    {
        RespondWithFrame();
        var reasoner = new FakeReasoner();

        await Build(shadowEnabled: true, reasoner).TickAsync(CancellationToken.None);

        Assert.Equal(1, reasoner.Calls);
        Assert.Equal("Pricing", reasoner.LastFrame!.TextRegions[0].Text);
    }

    [Fact]
    public async Task ShadowReasoningDisabled_DoesNotObserve()
    {
        RespondWithFrame();
        var reasoner = new FakeReasoner();

        await Build(shadowEnabled: false, reasoner).TickAsync(CancellationToken.None);

        Assert.Equal(0, reasoner.Calls);
    }
}
