using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceMotionTests
{
    [Fact]
    public void PlanGlide_ZeroDistance_ClampsToMin()
    {
        var (dur, _) = PresenceMotion.PlanGlide(100, 100, 100, 100, PresencePreferences.SafeDefault());
        Assert.Equal(PresenceMotion.MinGlideMs, dur);
    }

    [Fact]
    public void PlanGlide_HugeDistance_ClampsToMax()
    {
        var (dur, _) = PresenceMotion.PlanGlide(0, 0, 100000, 0, PresencePreferences.SafeDefault());
        Assert.Equal(PresenceMotion.MaxGlideMs, dur);
    }

    [Fact]
    public void PlanGlide_ScalesWithDistanceAndSpeed()
    {
        var prefs = PresencePreferences.SafeDefault(); // 1600 px/s
        var (dur, easing) = PresenceMotion.PlanGlide(0, 0, 800, 0, prefs); // 800px/1600 = 500ms
        Assert.Equal(500, dur);
        Assert.Equal(prefs.Easing, easing);
    }
}
