using SuavoAgent.Contracts.Canary;
using SuavoAgent.Core.Canary;
using Xunit;

namespace SuavoAgent.Core.Tests.Canary;

public class SchemaCanaryEscalationTests
{
    [Fact]
    public void Clean_NoHold()
    {
        var state = CanaryHoldState.Clear;
        var result = SchemaCanaryEscalation.Transition(state, CanarySeverity.None);
        Assert.False(result.IsInHold);
    }

    [Fact]
    public void Warning_DoesNotBlock()
    {
        var state = CanaryHoldState.Clear;
        var result = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.False(result.IsInHold);
        Assert.Equal(1, result.ConsecutiveWarnings);
    }

    [Fact]
    public void Warning_ThreeConsecutive_EscalatesToCritical()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        Assert.True(state.IsInHold);
        Assert.Equal(CanarySeverity.Critical, state.EffectiveSeverity);
    }

    [Fact]
    public void Warning_CleanCycle_ResetsCounter()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Warning);
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.None);
        Assert.Equal(0, state.ConsecutiveWarnings);
        Assert.False(state.IsInHold);
    }

    [Fact]
    public void Critical_EntersHold_BlockedCycleOne()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.IsInHold);
        Assert.Equal(1, state.BlockedCycles);
    }

    [Fact]
    public void Critical_ThreeCycles_DashboardEscalation()
    {
        var state = CanaryHoldState.Clear;
        for (int i = 0; i < 3; i++)
            state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.ShouldAlertDashboard);
    }

    [Fact]
    public void Critical_TwelveCycles_PhoneEscalation()
    {
        var state = CanaryHoldState.Clear;
        for (int i = 0; i < 12; i++)
            state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.ShouldAlertPhone);
    }

    [Fact]
    public void Acknowledge_ClearsHold()
    {
        var state = CanaryHoldState.Clear;
        state = SchemaCanaryEscalation.Transition(state, CanarySeverity.Critical);
        Assert.True(state.IsInHold);
        state = SchemaCanaryEscalation.Acknowledge(state, "operator-1");
        Assert.False(state.IsInHold);
    }
}
