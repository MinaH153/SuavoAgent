using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

public sealed class VisionRuntimeReadinessTests
{
    private static readonly DateTimeOffset CheckedAt = new(
        2026, 7, 12, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ValidClosedStatus_RoundTrips()
    {
        var status = Ready();
        var json = JsonSerializer.SerializeToElement(status);

        var parsed = VisionRuntimeReadiness.TryParse(json);

        Assert.Equal(status, parsed);
        Assert.True(parsed!.IsValid());
    }

    [Theory]
    [InlineData("code", "Code")]
    [InlineData("\"checkedAtUtc\":", "\"path\":\"C:/screen.png\",\"checkedAtUtc\":")]
    public void UnknownOrWrongCaseFields_AreRejected(string original, string replacement)
    {
        var json = JsonSerializer.Serialize(Ready())
            .Replace(original, replacement, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);

        Assert.Null(VisionRuntimeReadiness.TryParse(document.RootElement));
    }

    [Fact]
    public void InconsistentReadyClaim_IsRejected()
    {
        var inconsistent = Ready() with { OcrReady = false };

        Assert.False(inconsistent.IsValid());
        Assert.Null(VisionRuntimeReadiness.TryParse(
            JsonSerializer.SerializeToElement(inconsistent)));
    }

    [Fact]
    public void OcrFailureWithoutConfiguredOcr_IsRejected()
    {
        var impossible = Ready() with
        {
            OcrConfigured = false,
            Ready = false,
            OcrReady = false,
            Code = VisionRuntimeCodes.OcrRuntimeInitializationFailed,
        };

        Assert.False(impossible.IsValid());
        Assert.Null(VisionRuntimeReadiness.TryParse(
            JsonSerializer.SerializeToElement(impossible)));
    }

    [Fact]
    public void HelperPing_ParsesValidRuntime_AndRejectsInjectedFields()
    {
        var ping = new HelperPingInfo(1234, 2, 2, true, Ready());
        var valid = JsonSerializer.SerializeToElement(ping);

        Assert.Equal(Ready(), HelperPingInfo.TryParse(valid)!.VisionRuntime);

        var injected = valid.GetRawText().Replace(
            "\"code\":",
            "\"windowTitle\":\"dynamic-window-value\",\"code\":",
            StringComparison.Ordinal);
        using var document = JsonDocument.Parse(injected);
        Assert.Null(HelperPingInfo.TryParse(document.RootElement));
    }

    private static VisionRuntimeReadiness Ready() => new(
        VisionRuntimeReadiness.CurrentContractVersion,
        VisionEnabled: true,
        OcrConfigured: true,
        Ready: true,
        OcrReady: true,
        VisionRuntimeCodes.VisionReadyOcr,
        ConfigurationGeneration: 7,
        CheckedAt);
}
