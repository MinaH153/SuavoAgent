using System;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

public sealed class LoopBudgetTests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TryStep_AllowsExactlyMaxSteps_ThenStops()
    {
        var budget = new LoopBudget(maxSteps: 3, maxCloud: 99, deadline: null, utcNow: () => T0);

        Assert.True(budget.TryStep());
        Assert.True(budget.TryStep());
        Assert.True(budget.TryStep());
        Assert.False(budget.TryStep());
        Assert.Equal(3, budget.StepsUsed);
    }

    [Fact]
    public void Cloud_RemainsUntilMaxConsumed()
    {
        var budget = new LoopBudget(maxSteps: 99, maxCloud: 2, deadline: null, utcNow: () => T0);

        Assert.True(budget.CloudRemaining);
        budget.ConsumeCloud();
        Assert.True(budget.CloudRemaining);
        budget.ConsumeCloud();
        Assert.False(budget.CloudRemaining);
        Assert.Equal(2, budget.CloudUsed);
    }

    [Fact]
    public void DeadlinePassed_TrueOnceElapsed()
    {
        var now = T0;
        var budget = new LoopBudget(maxSteps: 99, maxCloud: 99, deadline: TimeSpan.FromSeconds(10), utcNow: () => now);

        Assert.False(budget.DeadlinePassed());
        now = T0.AddSeconds(9);
        Assert.False(budget.DeadlinePassed());
        now = T0.AddSeconds(10);
        Assert.True(budget.DeadlinePassed());
    }

    [Fact]
    public void NoDeadline_NeverPasses()
    {
        var now = T0;
        var budget = new LoopBudget(maxSteps: 99, maxCloud: 99, deadline: null, utcNow: () => now);

        now = T0.AddHours(24);
        Assert.False(budget.DeadlinePassed());
    }
}
