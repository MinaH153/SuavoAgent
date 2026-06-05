using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

public sealed class ContextAccumulatorTests
{
    private static readonly AgentObjective Obj = AgenticTestKit.Objective;

    [Fact]
    public void RecordStep_ReturnsNewInstance_PriorUnchanged()
    {
        var m0 = ContextAccumulator.Start(Obj);
        var m1 = ContextAccumulator.RecordStep(m0, "h1", NextAction.Act("click"), ActStatus.Success, PostconditionVerdict.Met, memoryWindow: 8);

        Assert.NotSame(m0, m1);
        Assert.Empty(m0.History);
        Assert.Single(m1.History);
    }

    [Fact]
    public void History_BoundedToMemoryWindow()
    {
        var m = ContextAccumulator.Start(Obj);
        for (var i = 0; i < 10; i++)
            m = ContextAccumulator.RecordStep(m, $"h{i}", NextAction.Act($"verb{i}"), ActStatus.Success, PostconditionVerdict.NotMet, memoryWindow: 3);

        Assert.Equal(3, m.History.Count);
        Assert.Equal("verb9()", m.History[^1].ActionSignature);
        Assert.Equal("verb7()", m.History[0].ActionSignature);
    }

    [Fact]
    public void RecordObservation_SetsLatest_AndResetsNullStrikes()
    {
        var m = ContextAccumulator.Start(Obj);
        m = ContextAccumulator.RecordPerceiveNull(m);
        Assert.Equal(1, m.PerceiveNullStrikes);

        var screen = AgenticTestKit.Screen("hX");
        m = ContextAccumulator.RecordObservation(m, screen);

        Assert.Same(screen, m.LatestScreen);
        Assert.Equal(0, m.PerceiveNullStrikes);
    }

    [Fact]
    public void RecordPerceiveNull_Increments()
    {
        var m = ContextAccumulator.Start(Obj);
        m = ContextAccumulator.RecordPerceiveNull(m);
        m = ContextAccumulator.RecordPerceiveNull(m);
        Assert.Equal(2, m.PerceiveNullStrikes);
    }

    [Fact]
    public void ConsecutiveNoProgress_CountsIdenticalMoves_ResetsOnChange()
    {
        var action = NextAction.Act("click", null);
        var m = ContextAccumulator.Start(Obj);

        m = ContextAccumulator.RecordStep(m, "same", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        Assert.Equal(1, m.ConsecutiveNoProgress);

        m = ContextAccumulator.RecordStep(m, "same", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        Assert.Equal(2, m.ConsecutiveNoProgress);

        // different screen ⇒ run resets to 1
        m = ContextAccumulator.RecordStep(m, "different", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        Assert.Equal(1, m.ConsecutiveNoProgress);
    }

    [Fact]
    public void IsNoProgress_TrueAtWindow()
    {
        var action = NextAction.Act("click");
        var m = ContextAccumulator.Start(Obj);
        m = ContextAccumulator.RecordStep(m, "s", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        m = ContextAccumulator.RecordStep(m, "s", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        Assert.False(ContextAccumulator.IsNoProgress(m, window: 3));

        m = ContextAccumulator.RecordStep(m, "s", action, ActStatus.Rejected, PostconditionVerdict.NotMet, 8);
        Assert.True(ContextAccumulator.IsNoProgress(m, window: 3));
    }
}
