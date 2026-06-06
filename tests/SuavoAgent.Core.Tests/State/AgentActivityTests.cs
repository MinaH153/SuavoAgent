using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AgentActivityTests
{
    [Fact]
    public void StartsIdle()
    {
        Assert.Null(new AgentActivity().Current);
    }

    [Fact]
    public void SetThenSnapshot_ReflectsLatest()
    {
        var a = new AgentActivity();
        a.Set("Sourcing costs", 142, 300);
        var s = a.Current;
        Assert.NotNull(s);
        Assert.Equal("Sourcing costs", s!.Label);
        Assert.Equal(142, s.Done);
        Assert.Equal(300, s.Total);
    }

    [Fact]
    public void Set_ClampsNegativeCountsToZero()
    {
        var a = new AgentActivity();
        a.Set("Reading", -5, -1);
        Assert.Equal(0, a.Current!.Done);
        Assert.Equal(0, a.Current!.Total);
    }

    [Fact]
    public void Clear_ReturnsToIdle()
    {
        var a = new AgentActivity();
        a.Set("Saving results", 3, 3);
        a.Clear();
        Assert.Null(a.Current);
    }
}
