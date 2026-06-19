using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class BubbleTextTests
{
    [Fact] public void For_WithLabel_JoinsKindAndLabel()
        => Assert.Equal("Clicking 7", BubbleText.For("Clicking", "7"));

    [Fact] public void For_NoLabel_EllipsizesKind()
        => Assert.Equal("Typing…", BubbleText.For("Typing", null));

    [Fact] public void For_EmptyKind_FallsBackToWorking()
        => Assert.Equal("Working…", BubbleText.For("  ", null));

    [Fact] public void For_LongLabel_Truncates()
    {
        var text = BubbleText.For("Clicking", new string('x', 80));
        Assert.True(text.Length <= "Clicking ".Length + 49);
        Assert.EndsWith("…", text);
    }
}
