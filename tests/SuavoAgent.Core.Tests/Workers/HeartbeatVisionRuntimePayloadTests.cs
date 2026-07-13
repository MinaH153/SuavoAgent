using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatVisionRuntimePayloadTests
{
    private static readonly DateTimeOffset CheckedAt = new(
        2026, 7, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void DegradedOcr_EmitsFixedPhiFreeCockpitReason()
    {
        var status = new VisionRuntimeReadiness(
            VisionRuntimeReadiness.CurrentContractVersion,
            VisionEnabled: true,
            OcrConfigured: true,
            Ready: false,
            OcrReady: false,
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            ConfigurationGeneration: 11,
            CheckedAt);

        var payload = HeartbeatWorker.BuildVisionRuntimePayload(status);
        var json = JsonSerializer.SerializeToElement(payload);
        var raw = json.GetRawText();

        Assert.False(json.GetProperty("ready").GetBoolean());
        Assert.False(json.GetProperty("ocrReady").GetBoolean());
        Assert.True(json.GetProperty("requiresAttention").GetBoolean());
        Assert.Equal(
            VisionRuntimeCodes.OcrCohortVerificationFailed,
            json.GetProperty("code").GetString());
        Assert.Equal(
            VisionRuntimeCodes.OperatorMessage(
                VisionRuntimeCodes.OcrCohortVerificationFailed),
            json.GetProperty("reason").GetString());
        Assert.DoesNotContain("tessdata", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("nativeLibrary", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("screenshot", raw, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("textRegions", raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidOrAbsentStatus_NeverProducesAReadyClaim()
    {
        Assert.Null(HeartbeatWorker.BuildVisionRuntimePayload(null));
        var invalid = new VisionRuntimeReadiness(
            99,
            true,
            true,
            true,
            true,
            VisionRuntimeCodes.VisionReadyOcr,
            1,
            CheckedAt);
        Assert.Null(HeartbeatWorker.BuildVisionRuntimePayload(invalid));
    }
}
