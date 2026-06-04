using SuavoAgent.Core.Autonomy;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public class TaskAutonomyEvaluatorTests
{
    [Theory]
    [InlineData(0, true, 1)]
    [InlineData(5, true, 6)]
    [InlineData(11, true, 12)]
    [InlineData(5, false, 0)]   // any non-clean run resets the streak
    [InlineData(0, false, 0)]
    [InlineData(-3, true, 1)]   // defensive: negative current never produces a negative
    public void NextStreak(int current, bool clean, int expected)
        => Assert.Equal(expected, TaskAutonomyEvaluator.NextStreak(current, clean));

    [Theory]
    [InlineData(0, 12, AutonomyLevel.Supervised)]
    [InlineData(11, 12, AutonomyLevel.Supervised)]
    [InlineData(12, 12, AutonomyLevel.Eligible)]
    [InlineData(50, 12, AutonomyLevel.Eligible)]
    public void LevelFor(int streak, int threshold, AutonomyLevel expected)
        => Assert.Equal(expected, TaskAutonomyEvaluator.LevelFor(streak, threshold));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void LevelFor_NonPositiveThreshold_NeverAutoGrantsAtZeroStreak(int badThreshold)
    {
        // A misconfigured threshold must not let a never-run task be Eligible.
        Assert.Equal(AutonomyLevel.Supervised, TaskAutonomyEvaluator.LevelFor(0, badThreshold));
    }

    [Theory]
    [InlineData(AutonomyLevel.Eligible, true, true)]    // the ONLY true case
    [InlineData(AutonomyLevel.Eligible, false, false)]  // earned but not enabled → supervised
    [InlineData(AutonomyLevel.Supervised, true, false)] // enabled but not earned → supervised
    [InlineData(AutonomyLevel.Supervised, false, false)]
    public void MayRunUnsupervised_IsFailClosed(AutonomyLevel level, bool enabled, bool expected)
        => Assert.Equal(expected, TaskAutonomyEvaluator.MayRunUnsupervised(level, enabled));
}
