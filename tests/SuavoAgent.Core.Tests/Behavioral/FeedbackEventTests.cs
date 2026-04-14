using SuavoAgent.Core.Behavioral;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class FeedbackEventTests
{
    [Fact]
    public void Record_Roundtrip_PreservesAllFields()
    {
        var evt = new FeedbackEvent(
            SessionId: "sess-1",
            EventType: "writeback_outcome",
            Source: "WritbackEngine",
            SourceId: "wb-42",
            TargetType: "routine",
            TargetId: "rt-7",
            PayloadJson: "{\"rx\":123}",
            DirectiveType: DirectiveType.ConfidenceAdjust,
            DirectiveJson: "{\"delta\":0.05}",
            CausalChainJson: "[\"a\",\"b\"]")
        {
            Id = 99,
            AppliedAt = "2026-04-13T00:00:00Z",
            AppliedBy = "FeedbackLoop"
        };

        Assert.Equal("sess-1", evt.SessionId);
        Assert.Equal("writeback_outcome", evt.EventType);
        Assert.Equal("WritbackEngine", evt.Source);
        Assert.Equal("wb-42", evt.SourceId);
        Assert.Equal("routine", evt.TargetType);
        Assert.Equal("rt-7", evt.TargetId);
        Assert.Equal("{\"rx\":123}", evt.PayloadJson);
        Assert.Equal(DirectiveType.ConfidenceAdjust, evt.DirectiveType);
        Assert.Equal("{\"delta\":0.05}", evt.DirectiveJson);
        Assert.Equal("[\"a\",\"b\"]", evt.CausalChainJson);
        Assert.Equal(99, evt.Id);
        Assert.Equal("2026-04-13T00:00:00Z", evt.AppliedAt);
        Assert.Equal("FeedbackLoop", evt.AppliedBy);
        Assert.NotNull(evt.CreatedAt);
    }

    [Theory]
    [InlineData("success", 0.05)]
    [InlineData("already_at_target", 0.02)]
    [InlineData("verified_with_drift", 0.03)]
    [InlineData("post_verify_mismatch", -0.10)]
    [InlineData("status_conflict", -0.15)]
    [InlineData("sql_error", -0.05)]
    [InlineData("connection_reset", 0.0)]
    [InlineData("trigger_blocked", -0.08)]
    public void OutcomeToDelta_KnownOutcomes_ReturnsCorrectDelta(string outcome, double expected)
    {
        Assert.Equal(expected, FeedbackEvent.OutcomeToDelta(outcome));
    }

    [Fact]
    public void OutcomeToDelta_UnknownOutcome_ReturnsZero()
    {
        Assert.Equal(0.0, FeedbackEvent.OutcomeToDelta("never_heard_of_this"));
    }

    [Fact]
    public void ApplyConfidenceDelta_CapsAtCeiling()
    {
        // 0.93 + 0.05 = 0.98 → clamped to 0.95
        Assert.Equal(0.95, FeedbackEvent.ApplyConfidenceDelta(0.93, 0.05));
    }

    [Fact]
    public void ApplyConfidenceDelta_FloorsAtMinimum()
    {
        // 0.12 - 0.15 = -0.03 → clamped to 0.1
        Assert.Equal(0.1, FeedbackEvent.ApplyConfidenceDelta(0.12, -0.15));
    }

    [Fact]
    public void ApplyConfidenceDelta_ExactCeiling()
    {
        // 0.90 + 0.05 = 0.95 — exactly at ceiling, no clamp needed
        Assert.Equal(0.95, FeedbackEvent.ApplyConfidenceDelta(0.90, 0.05));
    }

    [Fact]
    public void ApplyDecay_SubtractsOneHundredth()
    {
        Assert.Equal(0.71, FeedbackEvent.ApplyDecay(0.72));
    }

    [Fact]
    public void ApplyDecay_StopsAtFloor_WhenAtFloor()
    {
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.50));
    }

    [Fact]
    public void ApplyDecay_StopsAtFloor_WhenJustAbove()
    {
        // 0.505 - 0.01 = 0.495 → clamped to 0.50
        Assert.Equal(0.50, FeedbackEvent.ApplyDecay(0.505));
    }

    [Fact]
    public void ApplyDecay_At051_DecaysToFloor()
    {
        // 0.51 - 0.01 = 0.50 — exactly at DecayFloor
        var result = FeedbackEvent.ApplyDecay(0.51);
        Assert.Equal(0.50, result, precision: 2);
    }

    [Fact]
    public void ApplyDecay_AtFloor_StaysAtFloor()
    {
        // Already at 0.50 — should stay at 0.50 (early return branch)
        var result = FeedbackEvent.ApplyDecay(0.50);
        Assert.Equal(0.50, result, precision: 2);
    }

    [Fact]
    public void ApplyDecay_BelowFloor_ClampsToFloor()
    {
        // 0.49 is below DecayFloor of 0.50 — should clamp up to 0.50
        var result = FeedbackEvent.ApplyDecay(0.49);
        Assert.Equal(0.50, result, precision: 2);
    }
}
