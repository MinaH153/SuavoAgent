using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// EmitBudget tests. Spec §4 emit-rate contract: 10 events/sec per
/// component. Excess events still local-journal but the Sentry POST
/// returns false (caller drops the POST), and the counter increments.
public class EmitBudgetTests
{
    [Fact]
    public void First_10_events_at_default_budget_consume_tokens()
    {
        var budget = new EmitBudget(eventsPerSecond: 10);
        for (int i = 0; i < 10; i++)
        {
            Assert.True(budget.TryConsume(WireComponent.Core),
                $"Event {i + 1} should consume a token");
        }
        Assert.Equal(0, budget.RateLimitedTotal);
    }

    [Fact]
    public void Burst_beyond_capacity_drops_with_rate_limited_counter()
    {
        var budget = new EmitBudget(eventsPerSecond: 10);
        for (int i = 0; i < 10; i++) budget.TryConsume(WireComponent.Core);
        // 11th event in the same second is dropped
        Assert.False(budget.TryConsume(WireComponent.Core));
        Assert.Equal(1, budget.RateLimitedTotal);
    }

    [Fact]
    public void Different_components_have_independent_buckets()
    {
        var budget = new EmitBudget(eventsPerSecond: 2);
        Assert.True(budget.TryConsume(WireComponent.Core));
        Assert.True(budget.TryConsume(WireComponent.Core));
        Assert.False(budget.TryConsume(WireComponent.Core)); // Core exhausted
        Assert.True(budget.TryConsume(WireComponent.Helper)); // Helper independent
    }

    [Fact]
    public void Refill_restores_capacity_after_interval()
    {
        // Use 100ms refill window for a fast test.
        var budget = new EmitBudget(eventsPerSecond: 2, refillInterval: TimeSpan.FromMilliseconds(100));
        Assert.True(budget.TryConsume(WireComponent.Core));
        Assert.True(budget.TryConsume(WireComponent.Core));
        Assert.False(budget.TryConsume(WireComponent.Core));

        Thread.Sleep(150);

        Assert.True(budget.TryConsume(WireComponent.Core),
            "Bucket should refill after interval elapses");
    }

    [Fact]
    public void Sustained_overrun_increments_counter_each_drop()
    {
        var budget = new EmitBudget(eventsPerSecond: 3);
        // Burn the capacity
        for (int i = 0; i < 3; i++) budget.TryConsume(WireComponent.Core);
        // 7 sustained drops
        for (int i = 0; i < 7; i++) budget.TryConsume(WireComponent.Core);
        Assert.Equal(7, budget.RateLimitedTotal);
    }
}
