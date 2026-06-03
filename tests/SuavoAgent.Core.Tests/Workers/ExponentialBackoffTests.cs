using System;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

/// <summary>
/// Stateful exponential backoff for reconnect loops — replaces the fixed 60s SQL-retry delay so a
/// down dependency isn't hammered every minute. Grows from initial to a cap, resets on success.
/// </summary>
public class ExponentialBackoffTests
{
    [Fact]
    public void NextDelay_GrowsExponentially_FromInitial()
    {
        var b = new ExponentialBackoff(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600), factor: 2.0);
        Assert.Equal(TimeSpan.FromSeconds(60), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(120), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(240), b.NextDelay());
        Assert.Equal(TimeSpan.FromSeconds(480), b.NextDelay());
    }

    [Fact]
    public void NextDelay_CapsAtMax()
    {
        var b = new ExponentialBackoff(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600), factor: 2.0);
        for (var i = 0; i < 12; i++)
        {
            var d = b.NextDelay();
            Assert.True(d <= TimeSpan.FromSeconds(600), $"delay {d} exceeded cap");
        }
    }

    [Fact]
    public void Reset_ReturnsToInitial()
    {
        var b = new ExponentialBackoff(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(600));
        b.NextDelay();
        b.NextDelay();
        b.Reset();
        Assert.Equal(TimeSpan.FromSeconds(60), b.NextDelay());
    }
}
