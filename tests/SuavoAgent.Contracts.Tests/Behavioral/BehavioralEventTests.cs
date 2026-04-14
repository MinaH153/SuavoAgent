using System.Reflection;
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class BehavioralEventTests
{
    [Fact]
    public void TreeSnapshotEvent_RoundTripsViaJson()
    {
        var ev = BehavioralEvent.TreeSnapshot("abc123");
        var json = JsonSerializer.Serialize(ev);
        var restored = JsonSerializer.Deserialize<BehavioralEvent>(json);

        Assert.NotNull(restored);
        Assert.Equal(BehavioralEventType.TreeSnapshot, restored!.Type);
        Assert.Equal("abc123", restored.TreeHash);
    }

    [Fact]
    public void InteractionEvent_FactoryProducesCorrectFields()
    {
        var ev = BehavioralEvent.Interaction(
            subtype: "click",
            treeHash: "tree1",
            elementId: "btn-ok",
            controlType: "Button",
            className: "WinForm",
            nameHash: "namehash1");

        Assert.Equal(BehavioralEventType.Interaction, ev.Type);
        Assert.Equal("click", ev.Subtype);
        Assert.Equal("tree1", ev.TreeHash);
        Assert.Equal("btn-ok", ev.ElementId);
        Assert.Equal("Button", ev.ControlType);
        Assert.Equal("WinForm", ev.ClassName);
        Assert.Equal("namehash1", ev.NameHash);
    }

    [Fact]
    public void KeystrokeFactory_CapsDigitCountAt3()
    {
        var ev = BehavioralEvent.Keystroke(
            category: KeystrokeCategory.Digit,
            timing: TimingBucket.Normal,
            sequenceCount: 9999);

        Assert.Equal(BehavioralEventType.KeystrokeCategory, ev.Type);
        Assert.Equal(3, ev.KeystrokeCount);
        Assert.Equal(KeystrokeCategory.Digit, ev.KeystrokeCat);
    }

    [Fact]
    public void KeystrokeFactory_NonDigitPreservesCount()
    {
        var ev = BehavioralEvent.Keystroke(
            category: KeystrokeCategory.Alpha,
            timing: TimingBucket.Rapid,
            sequenceCount: 7);

        Assert.Equal(7, ev.KeystrokeCount);
    }

    [Fact]
    public void WithSeq_AssignsSequenceNumber()
    {
        var ev = BehavioralEvent.TreeSnapshot("hash1");
        var sequenced = ev.WithSeq(42);

        Assert.Equal(42L, sequenced.Seq);
        Assert.Equal(0L, ev.Seq); // original unchanged
    }

    [Fact]
    public void ScrubbedElement_DoesNotHaveForbiddenProperties()
    {
        var type = typeof(ScrubbedElement);
        var forbidden = new[] { "Value", "Text", "Selection", "HelpText", "ItemStatus" };

        foreach (var name in forbidden)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.Null(prop);
        }
    }

    [Fact]
    public void ScrubbedElement_HasExpectedProperties()
    {
        var type = typeof(ScrubbedElement);
        var expected = new[] { "ControlType", "AutomationId", "ClassName", "NameHash", "BoundingRect", "Depth", "ChildIndex" };

        foreach (var name in expected)
        {
            var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            Assert.NotNull(prop);
        }
    }
}
