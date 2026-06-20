using System;

namespace SuavoAgent.Helper.Presence;

/// <summary>Pure glide planning: duration scales with travel distance and the
/// configured glide speed, clamped so a tiny hop still reads and a long sweep
/// never drags. Spring/lerp only — no spline pathing (research-verified).</summary>
public static class PresenceMotion
{
    public const int MinGlideMs = 120;
    public const int MaxGlideMs = 900;

    public static (int durationMs, string easing) PlanGlide(
        int fromX, int fromY, int toX, int toY, PresencePreferences prefs)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);
        var speed = Math.Max(1, prefs.GlideSpeedPxPerSec);
        var ms = (int)Math.Round(distance / speed * 1000.0);
        return (Math.Clamp(ms, MinGlideMs, MaxGlideMs), prefs.Easing);
    }
}
