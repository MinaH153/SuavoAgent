using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceNarrateTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private sealed class FakeBubble : IBubbleRenderer
    {
        public List<string> Shown { get; } = new();
        public List<string> Reanchors { get; } = new();
        public int Hides;
        public void Show(string text, string tone, int x, int y) => Shown.Add($"{text}@{x},{y}");
        public void Reanchor(int x, int y) => Reanchors.Add($"{x},{y}");
        public void Hide() => Hides++;
    }

    private sealed class NoopCursor : IPresenceRenderer
    {
        public void Glide(int fx, int fy, int tx, int ty, int d, string e, string tone, int dia) { }
        public void Reticle(int x, int y, int dia, string tone) { }
        public void ClickPulse(int x, int y, string tone) { }
        public void Hide() { }
        public void Show() { }
    }

    [Fact]
    public void Narrate_WhenVisible_ShowsCaption()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "7");

        Assert.Contains(b.Shown, s => s.StartsWith("Clicking 7@"));
    }

    [Fact]
    public void Narrate_PhiLabel_RendersActionOnly()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "123-45-6789"); // SSN-shaped → must be dropped

        Assert.Contains(b.Shown, s => s.StartsWith("Clicking…@"));
        Assert.DoesNotContain(b.Shown, s => s.Contains("123-45-6789"));
    }

    [Fact]
    public void Narrate_WhenBubbleHidden_NoOp()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault() with { BubbleVisible = false });
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(), store, Log, bubble: b);

        c.Narrate("Clicking", "7");

        Assert.Empty(b.Shown);
    }

    [Fact]
    public void MoveTo_AfterNarrate_ReanchorsBubble()
    {
        var b = new FakeBubble();
        var c = new PresenceController(new NoopCursor(),
            new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log, bubble: b);

        c.Narrate("Clicking", "7"); // bubble showing
        c.MoveTo(10, 10);           // first place
        c.MoveTo(200, 50);          // glide → reanchor

        Assert.Contains("200,50", b.Reanchors);
    }
}
