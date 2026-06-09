using Microsoft.Extensions.Options;
using Serilog;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

/// <summary>
/// Pins the platform-independent contract of the window-scoped sandbox capturer: a zero HWND is never
/// available, and an unavailable capturer fails closed (CapturePrimaryAsync returns null, never throws).
/// Actual PrintWindow pixel behaviour is Windows-only and validated on-box (PARTIAL-until-on-box).
/// </summary>
public class WindowScopedScreenCaptureTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private static IOptions<AgentOptions> Opts(bool enabled = true) =>
        Options.Create(new AgentOptions { Vision = new VisionOptions { Enabled = enabled, MinIntervalMs = 0 } });

    [Fact]
    public void IsAvailable_ZeroHwnd_IsFalse()
    {
        var cap = new WindowScopedScreenCapture(Opts(enabled: true), Log, IntPtr.Zero);
        Assert.False(cap.IsAvailable); // no resolved window ⇒ never available, regardless of platform
    }

    [Fact]
    public void IsAvailable_NonZeroHwnd_TracksPlatformAndEnabled()
    {
        var cap = new WindowScopedScreenCapture(Opts(enabled: true), Log, new IntPtr(0x1234));
        // Available only on Windows (the capture is Win32 PrintWindow); fail-closed elsewhere.
        Assert.Equal(OperatingSystem.IsWindows(), cap.IsAvailable);

        var disabled = new WindowScopedScreenCapture(Opts(enabled: false), Log, new IntPtr(0x1234));
        Assert.False(disabled.IsAvailable);
    }

    [Fact]
    public async Task CapturePrimaryAsync_NotAvailable_ReturnsNull()
    {
        var cap = new WindowScopedScreenCapture(Opts(enabled: true), Log, IntPtr.Zero);
        var result = await cap.CapturePrimaryAsync(CancellationToken.None);
        Assert.Null(result); // fail-closed when not available — never throws
    }
}
