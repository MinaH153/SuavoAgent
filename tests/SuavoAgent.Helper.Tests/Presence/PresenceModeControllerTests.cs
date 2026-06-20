using System;
using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceModeControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private sealed class FakeGlow : IGlowRenderer
    {
        public List<string> Shown { get; } = new();
        public int Hides;
        public void Show(string tone, double intensity) => Shown.Add(tone);
        public void Hide() => Hides++;
    }

    private sealed class NoopCursor : IPresenceRenderer
    {
        public void Glide(int a, int b, int c, int d, int e, string f, string g, int h) { }
        public void Reticle(int a, int b, int c, string d) { }
        public void ClickPulse(int a, int b, string c) { }
        public void Hide() { }
        public void Show() { }
    }

    private static readonly DateTimeOffset Now = new(2026, 6, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void HumanInput_ThenEvaluate_GlowsObserving()
    {
        var glow = new FakeGlow();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log,
            glow: glow, clock: () => Now);

        c.OnHumanInput();
        var mode = c.EvaluateMode();

        Assert.Equal(PresenceMode.Observing, mode);
        Assert.Contains(PresenceTones.Observing, glow.Shown);
    }

    [Fact]
    public void AgentActivity_ThenEvaluate_GlowsDriving()
    {
        var glow = new FakeGlow();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log,
            glow: glow, clock: () => Now);

        c.MoveTo(10, 10); // agent activity stamps Now + evaluates → Driving
        var mode = c.EvaluateMode();

        Assert.Equal(PresenceMode.Driving, mode);
        Assert.Contains(PresenceTones.Acting, glow.Shown);
    }

    [Fact]
    public void GlowHidden_WhenGlowVisibleFalse()
    {
        var glow = new FakeGlow();
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault() with { GlowVisible = false });
        var c = new PresenceController(new NoopCursor(), store, Log, glow: glow, clock: () => Now);

        c.MoveTo(10, 10);
        c.EvaluateMode();

        Assert.Empty(glow.Shown);
    }
}
