using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private sealed class FakeRenderer : IPresenceRenderer
    {
        public List<string> Calls { get; } = new();
        public void Glide(int fx, int fy, int tx, int ty, int dur, string e, string tone, int dia)
            => Calls.Add($"glide:{fx},{fy}->{tx},{ty}");
        public void Reticle(int x, int y, int dia, string tone) => Calls.Add($"reticle:{x},{y}");
        public void ClickPulse(int x, int y, string tone) => Calls.Add($"click:{x},{y}");
        public void Hide() => Calls.Add("hide");
        public void Show() => Calls.Add("show");
    }

    [Fact]
    public void MoveTo_WhenActive_GlidesFromLastRestPoint()
    {
        var r = new FakeRenderer();
        var c = new PresenceController(r, new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log);

        c.MoveTo(100, 100); // first move: place, no glide
        c.MoveTo(300, 100); // glide 100,100 -> 300,100

        Assert.Contains("glide:100,100->300,100", r.Calls);
    }

    [Fact]
    public void Reticle_And_Click_NoOp_WhenCursorHidden_ButDoNotThrow()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var r = new FakeRenderer();
        var c = new PresenceController(r, store, Log);
        store.SetVisible(false); // hide

        c.MoveTo(10, 10);
        c.Reticle(10, 10);
        c.Click(10, 10);

        Assert.DoesNotContain(r.Calls, s => s.StartsWith("reticle"));
        Assert.DoesNotContain(r.Calls, s => s.StartsWith("click"));
        Assert.DoesNotContain(r.Calls, s => s.StartsWith("glide"));
    }

    [Fact]
    public void StoreHide_TriggersRendererHide()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var r = new FakeRenderer();
        _ = new PresenceController(r, store, Log);

        store.SetVisible(false);

        Assert.Contains("hide", r.Calls);
    }

    [Fact]
    public void Reticle_NoOp_WhenSessionNotInteractive()
    {
        var r = new FakeRenderer();
        var c = new PresenceController(r, new PresencePreferenceStore(PresencePreferences.SafeDefault()),
            Log, isSessionInteractive: () => false);

        c.Reticle(5, 5);

        Assert.Empty(r.Calls);
    }
}
