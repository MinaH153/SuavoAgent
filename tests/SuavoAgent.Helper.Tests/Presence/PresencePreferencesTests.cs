using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresencePreferencesTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    [Fact]
    public void SafeDefault_IsVisibleAndSessionGated()
    {
        var p = PresencePreferences.SafeDefault();

        Assert.True(p.Enabled);
        Assert.True(p.CursorVisible);
        Assert.True(p.SuppressWhenSessionDisconnected);
        Assert.True(p.IsCursorActive);
        Assert.Equal(PresenceTones.Acting, p.Tone);
        Assert.Equal("labels", p.BubbleVerbosity);
        Assert.True(p.GlideSpeedPxPerSec > 0);
    }

    [Fact]
    public void IsCursorActive_FalseWhenDisabledOrHidden()
    {
        var p = PresencePreferences.SafeDefault();
        Assert.False((p with { Enabled = false }).IsCursorActive);
        Assert.False((p with { CursorVisible = false }).IsCursorActive);
    }

    [Fact]
    public void FromJson_NullOrEmpty_ReturnsSafeDefault()
    {
        Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson(null, Log));
        Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson("", Log));
    }

    [Fact]
    public void FromJson_BadJson_ReturnsSafeDefault()
    {
        Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson("{not json", Log));
    }

    [Fact]
    public void FromJson_OverridesAndClamps()
    {
        var p = PresencePreferences.FromJson(
            "{\"cursorVisible\":false,\"glideSpeedPxPerSec\":999999,\"cursorSizePx\":2,\"glowIntensity\":5.0}", Log);

        Assert.False(p.CursorVisible);
        Assert.Equal(8000, p.GlideSpeedPxPerSec); // clamped to max
        Assert.Equal(8, p.CursorSizePx);          // clamped to min
        Assert.Equal(1.0, p.GlowIntensity);       // clamped to [0,1]
    }
}
