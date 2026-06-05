using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// Deploy-critical: pins every row of the OTA crash-loop guard decision table. A wrong
/// decision here either bricks the fleet (failing to downgrade a bad OTA) or thrashes a
/// healthy update (downgrading a good one), so each branch + boundary is asserted explicitly.
/// </summary>
public sealed class OtaProbationEvaluatorTests
{
    private const int Max = OtaProbationEvaluator.DefaultMaxProbationBoots; // 3

    [Fact]
    public void NoPendingProbation_ReturnsNoProbation_EvenWhenOtherFieldsLookLikeDowngrade()
    {
        // Pending=false must short-circuit: a stale boot count + missing old set must NOT downgrade.
        var state = new OtaProbationState(
            HasPendingProbation: false, HealthConfirmed: false,
            BootsSinceSwap: 99, MaxProbationBoots: Max, OldSetAvailable: false);

        Assert.Equal(OtaProbationDecision.NoProbation, OtaProbationEvaluator.Evaluate(state));
    }

    [Fact]
    public void HealthConfirmed_Commits_EvenAtOrPastCrashLoopThreshold()
    {
        // Healthy ALWAYS wins over crash-count: a set that finally proves healthy on its last
        // allowed boot still commits rather than getting downgraded.
        var state = new OtaProbationState(
            HasPendingProbation: true, HealthConfirmed: true,
            BootsSinceSwap: Max + 5, MaxProbationBoots: Max, OldSetAvailable: true);

        Assert.Equal(OtaProbationDecision.Commit, OtaProbationEvaluator.Evaluate(state));
    }

    [Fact]
    public void CrashLoop_WithOldSet_Downgrades()
    {
        var state = new OtaProbationState(
            HasPendingProbation: true, HealthConfirmed: false,
            BootsSinceSwap: Max + 1, MaxProbationBoots: Max, OldSetAvailable: true);

        Assert.Equal(OtaProbationDecision.Downgrade, OtaProbationEvaluator.Evaluate(state));
    }

    [Fact]
    public void CrashLoop_NoOldSet_EscalatesNoRollback_NeverThrashes()
    {
        var state = new OtaProbationState(
            HasPendingProbation: true, HealthConfirmed: false,
            BootsSinceSwap: Max + 1, MaxProbationBoots: Max, OldSetAvailable: false);

        Assert.Equal(OtaProbationDecision.EscalateNoRollback, OtaProbationEvaluator.Evaluate(state));
    }

    [Fact]
    public void AtThreshold_NotYetHealthy_StillContinues_GivesTheBootAFairAttempt()
    {
        // Codex Q4: the Max-th boot must get a real attempt, not be pre-empted by a >= boundary.
        var state = new OtaProbationState(
            HasPendingProbation: true, HealthConfirmed: false,
            BootsSinceSwap: Max, MaxProbationBoots: Max, OldSetAvailable: true);

        Assert.Equal(OtaProbationDecision.Continue, OtaProbationEvaluator.Evaluate(state));
    }

    [Theory]
    [InlineData(0, OtaProbationDecision.Continue)]
    [InlineData(1, OtaProbationDecision.Continue)]
    [InlineData(2, OtaProbationDecision.Continue)]
    [InlineData(3, OtaProbationDecision.Continue)]  // == Max → still a full attempt
    [InlineData(4, OtaProbationDecision.Downgrade)] // > Max → trips
    [InlineData(5, OtaProbationDecision.Downgrade)]
    public void BootBoundary_TripsStrictlyPastMax(int boots, OtaProbationDecision expected)
    {
        var state = new OtaProbationState(
            HasPendingProbation: true, HealthConfirmed: false,
            BootsSinceSwap: boots, MaxProbationBoots: Max, OldSetAvailable: true);

        Assert.Equal(expected, OtaProbationEvaluator.Evaluate(state));
    }
}
