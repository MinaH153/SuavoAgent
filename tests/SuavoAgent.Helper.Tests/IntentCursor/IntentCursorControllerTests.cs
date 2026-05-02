using Serilog;
using System.Text.RegularExpressions;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Helper.IntentCursor;
using Xunit;

namespace SuavoAgent.Helper.Tests.IntentCursor;

public class IntentCursorControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public async Task ShowAsync_ValidRequest_DispatchesRendererOnce()
    {
        var renderer = new FakeRenderer();
        var controller = new IntentCursorController(renderer, Log);

        var result = await controller.ShowAsync(
            new IntentCursorRequest(100, 200, DurationMs: 850, DiameterPx: 40, Opacity: 0.65),
            CancellationToken.None);

        Assert.True(result.Accepted);
        Assert.Null(result.ErrorCode);
        var rendered = Assert.Single(renderer.Requests);
        Assert.Equal(100, rendered.X);
        Assert.Equal(200, rendered.Y);
        Assert.Equal(850, rendered.DurationMs);
        Assert.Equal(40, rendered.DiameterPx);
        Assert.Equal(0.65, rendered.Opacity, precision: 2);
    }

    [Fact]
    public async Task ShowAsync_InvalidCoordinates_RejectsBeforeRenderer()
    {
        var renderer = new FakeRenderer();
        var controller = new IntentCursorController(renderer, Log);

        var result = await controller.ShowAsync(
            new IntentCursorRequest(double.NaN, 200),
            CancellationToken.None);

        Assert.False(result.Accepted);
        Assert.Equal("invalid_coordinates", result.ErrorCode);
        Assert.Empty(renderer.Requests);
    }

    [Fact]
    public void Policy_ClampsVisualFieldsAndKeepsNoTextPayload()
    {
        var accepted = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(
                X: -120.9,
                Y: 333.6,
                DurationMs: 60_000,
                DiameterPx: 512,
                Opacity: 9,
                Tone: "not-a-tone"),
            out var render,
            out var errorCode);

        Assert.True(accepted);
        Assert.Null(errorCode);
        Assert.NotNull(render);
        Assert.Equal(-121, render.X);
        Assert.Equal(334, render.Y);
        Assert.Equal(IntentCursorPolicy.MaxDurationMs, render.DurationMs);
        Assert.Equal(IntentCursorPolicy.MaxDiameterPx, render.DiameterPx);
        Assert.Equal(IntentCursorPolicy.MaxOpacity, render.Opacity, precision: 2);
        Assert.Equal(IntentCursorTones.Agent, render.Tone);
    }

    [Fact]
    public void Policy_RejectsNonScreenCoordinateSpace()
    {
        var accepted = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(100, 200, CoordinateSpace: "window"),
            out var render,
            out var errorCode);

        Assert.False(accepted);
        Assert.Null(render);
        Assert.Equal("unsupported_coordinate_space", errorCode);
    }

    [Fact]
    public void Policy_ResolvesPrimaryCenterAnchorWithoutCloudCoordinates()
    {
        var accepted = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(
                Anchor: IntentCursorAnchors.PrimaryCenter,
                DurationMs: 900,
                Tone: IntentCursorTones.Attention),
            out var render,
            out var errorCode,
            (string anchor, out double x, out double y) =>
            {
                Assert.Equal(IntentCursorAnchors.PrimaryCenter, anchor);
                x = 960;
                y = 540;
                return true;
            });

        Assert.True(accepted);
        Assert.Null(errorCode);
        Assert.NotNull(render);
        Assert.Equal(960, render.X);
        Assert.Equal(540, render.Y);
        Assert.Equal(IntentCursorTones.Attention, render.Tone);
    }

    [Fact]
    public void Policy_RejectsAnchorMixedWithRawCoordinates()
    {
        var accepted = IntentCursorPolicy.TryNormalize(
            new IntentCursorRequest(
                X: 100,
                Y: 200,
                Anchor: IntentCursorAnchors.PrimaryCenter),
            out var render,
            out var errorCode,
            (string _, out double x, out double y) =>
            {
                x = 0;
                y = 0;
                return true;
            });

        Assert.False(accepted);
        Assert.Null(render);
        Assert.Equal("ambiguous_coordinates", errorCode);
    }

    [Fact]
    public void Policy_FadesCursorWithoutExceedingRequestedOpacity()
    {
        const double opacity = 0.72;
        const int duration = 1200;

        Assert.Equal(0, IntentCursorPolicy.OpacityAt(opacity, 0, duration), precision: 2);
        Assert.InRange(IntentCursorPolicy.OpacityAt(opacity, 60, duration), 0.30, 0.40);
        Assert.Equal(opacity, IntentCursorPolicy.OpacityAt(opacity, 250, duration), precision: 2);
        Assert.InRange(IntentCursorPolicy.OpacityAt(opacity, 1120, duration), 0.20, 0.35);
        Assert.Equal(0, IntentCursorPolicy.OpacityAt(opacity, duration, duration), precision: 2);
    }

    [Fact]
    public void WindowsRenderer_DoesNotDeclareInputInjectionApis()
    {
        var source = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                "src", "SuavoAgent.Helper", "IntentCursor", "WindowsIntentCursorRenderer.cs"));

        Assert.DoesNotMatch(new Regex(@"extern\s+.*\bSendInput\b"), source);
        Assert.DoesNotMatch(new Regex(@"extern\s+.*\bSetCursorPos\b"), source);
        Assert.DoesNotMatch(new Regex(@"extern\s+.*\bmouse_event\b"), source);
        Assert.DoesNotContain("SetWindowsHookEx", source);
        Assert.DoesNotContain("InvokePattern", source);
        Assert.Contains("WS_EX_TRANSPARENT", source);
        Assert.Contains("WS_EX_NOACTIVATE", source);
        Assert.Contains("SW_SHOWNOACTIVATE", source);
        Assert.Contains("OpacityAt", source);
    }

    private sealed class FakeRenderer : IIntentCursorRenderer
    {
        public List<IntentCursorRenderRequest> Requests { get; } = new();

        public Task ShowAsync(IntentCursorRenderRequest request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.CompletedTask;
        }
    }
}
