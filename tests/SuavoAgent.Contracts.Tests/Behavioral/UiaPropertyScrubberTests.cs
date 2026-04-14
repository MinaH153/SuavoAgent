using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class UiaPropertyScrubberTests
{
    private static RawElementProperties DefaultRaw(
        string controlType = "Button",
        string automationId = "btn-ok",
        string className = "WinForms.Button",
        string? name = "OK",
        string? boundingRect = "0,0,100,30",
        int depth = 2,
        int childIndex = 1) =>
        new(controlType, automationId, className, name, boundingRect, depth, childIndex);

    [Fact]
    public void Scrub_GreenPropertiesPreservedPlain()
    {
        var raw = DefaultRaw();
        var scrubbed = UiaPropertyScrubber.Scrub(raw, "test-salt");

        Assert.Equal("Button", scrubbed.ControlType);
        Assert.Equal("btn-ok", scrubbed.AutomationId);
        Assert.Equal("WinForms.Button", scrubbed.ClassName);
        Assert.Equal("0,0,100,30", scrubbed.BoundingRect);
        Assert.Equal(2, scrubbed.Depth);
        Assert.Equal(1, scrubbed.ChildIndex);
    }

    [Fact]
    public void Scrub_NameIsHmacHashed_NeverRaw()
    {
        var raw = DefaultRaw(name: "OK");
        var scrubbed = UiaPropertyScrubber.Scrub(raw, "test-salt");

        Assert.NotNull(scrubbed.NameHash);
        Assert.NotEqual("OK", scrubbed.NameHash);
        // Should look like a hex string
        Assert.Matches("^[0-9a-f]{64}$", scrubbed.NameHash!);
    }

    [Fact]
    public void Scrub_NameHashIsDeterministic()
    {
        var raw = DefaultRaw(name: "Submit");
        var h1 = UiaPropertyScrubber.Scrub(raw, "salt1").NameHash;
        var h2 = UiaPropertyScrubber.Scrub(raw, "salt1").NameHash;

        Assert.Equal(h1, h2);
    }

    [Fact]
    public void Scrub_DifferentSaltProducesDifferentHash()
    {
        var raw = DefaultRaw(name: "Submit");
        var h1 = UiaPropertyScrubber.Scrub(raw, "salt1").NameHash;
        var h2 = UiaPropertyScrubber.Scrub(raw, "salt2").NameHash;

        Assert.NotEqual(h1, h2);
    }

    [Fact]
    public void Scrub_NullName_ProducesNullHash()
    {
        var raw = DefaultRaw(name: null);
        var scrubbed = UiaPropertyScrubber.Scrub(raw, "salt");

        Assert.Null(scrubbed.NameHash);
    }

    [Fact]
    public void TryScrub_EmptyAutomationIdAndEmptyClassName_ReturnsNull()
    {
        var raw = new RawElementProperties("Button", "", "", "Label", null, 0, 0);
        var result = UiaPropertyScrubber.TryScrub(raw, "salt");

        Assert.Null(result);
    }

    [Fact]
    public void TryScrub_EmptyAutomationIdButNonEmptyClassName_IsAccepted()
    {
        var raw = new RawElementProperties("Button", "", "WinForms.Button", "Label", null, 1, 0);
        var result = UiaPropertyScrubber.TryScrub(raw, "salt");

        Assert.NotNull(result);
        Assert.Equal("WinForms.Button", result!.ClassName);
    }

    [Fact]
    public void BuildElementId_PrefersAutomationId()
    {
        var raw = DefaultRaw(automationId: "my-id", className: "Cls");
        var id = UiaPropertyScrubber.BuildElementId(raw);

        Assert.Equal("my-id", id);
    }

    [Fact]
    public void BuildElementId_FallsBackToClassNameDepthChildIndex()
    {
        var raw = new RawElementProperties("Button", "", "WinForms.Button", null, null, 3, 2);
        var id = UiaPropertyScrubber.BuildElementId(raw);

        Assert.Equal("WinForms.Button:3:2", id);
    }

    [Fact]
    public void BuildElementId_ReturnsNullWhenBothEmpty()
    {
        var raw = new RawElementProperties("Button", "", "", null, null, 0, 0);
        var id = UiaPropertyScrubber.BuildElementId(raw);

        Assert.Null(id);
    }

    [Fact]
    public void ScrubColumnHeader_AlwaysHmacHashed()
    {
        var hash = UiaPropertyScrubber.ScrubColumnHeader("Rx Number", "pharmacy-salt");

        Assert.NotNull(hash);
        Assert.NotEqual("Rx Number", hash);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ScrubColumnHeader_IsDeterministic()
    {
        var h1 = UiaPropertyScrubber.ScrubColumnHeader("Drug Name", "s1");
        var h2 = UiaPropertyScrubber.ScrubColumnHeader("Drug Name", "s1");

        Assert.Equal(h1, h2);
    }
}
