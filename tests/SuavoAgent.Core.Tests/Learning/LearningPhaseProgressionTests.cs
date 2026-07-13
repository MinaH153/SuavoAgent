using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public sealed class LearningPhaseProgressionTests
{
    [Fact]
    public void Discovery_AdvancesOnlyAfterSevenDayDuration()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.Null(LearningPhaseProgression.Evaluate(
            "discovery", started, started + TimeSpan.FromDays(7) - TimeSpan.FromTicks(1), false));

        var decision = LearningPhaseProgression.Evaluate(
            "discovery", started, started + TimeSpan.FromDays(7), false);

        Assert.NotNull(decision);
        Assert.Equal("pattern", decision.NextPhase);
        Assert.Equal("discovery_duration", decision.Reason);
    }

    [Fact]
    public void Pattern_AdvancesOnPhaseGateBeforeNormalDuration()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        var decision = LearningPhaseProgression.Evaluate(
            "pattern", started, started + TimeSpan.FromHours(72), patternPhaseGateReady: true);

        Assert.NotNull(decision);
        Assert.Equal("model", decision.NextPhase);
        Assert.Equal("pattern_phase_gate", decision.Reason);
    }

    [Fact]
    public void Pattern_FallsBackToFourteenDayDurationWithoutSeedGate()
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.Null(LearningPhaseProgression.Evaluate(
            "pattern", started, started + TimeSpan.FromDays(13), false));

        var decision = LearningPhaseProgression.Evaluate(
            "pattern", started, started + TimeSpan.FromDays(14), false);
        Assert.NotNull(decision);
        Assert.Equal("model", decision.NextPhase);
        Assert.Equal("pattern_duration", decision.Reason);
    }

    [Theory]
    [InlineData("model")]
    [InlineData("approved")]
    [InlineData("active")]
    public void HumanGatedPhases_NeverAutomaticallyAdvance(string phase)
    {
        var started = DateTimeOffset.Parse("2026-01-01T00:00:00Z");

        Assert.Null(LearningPhaseProgression.Evaluate(
            phase, started, started + TimeSpan.FromDays(365), patternPhaseGateReady: true));
    }

    [Fact]
    public void LegacyNextPhaseApi_AlsoCannotAutoApproveModel()
    {
        Assert.Null(LearningSession.GetNextPhase(
            "model", DateTimeOffset.UtcNow - TimeSpan.FromDays(365)));
    }

    [Fact]
    public void PersistedPhaseTimestamp_SurvivesRestartAndDrivesDecision()
    {
        var path = Path.Combine(Path.GetTempPath(), $"suavo_phase_restart_{Guid.NewGuid():N}.db");
        DateTimeOffset persisted;
        try
        {
            using (var first = new AgentStateDb(path))
            {
                first.CreateLearningSession("session-restart", "pharmacy-1");
                first.UpdateLearningPhase("session-restart", "pattern");
                first.InsertAppliedSeed(
                    "seed-digest-1", "pattern", "2026-01-01T00:00:00Z", 3, 0,
                    sessionId: "session-restart");
                persisted = first.GetPhaseChangedAt("session-restart");
            }

            using var restarted = new AgentStateDb(path);
            var restored = restarted.GetPhaseChangedAt("session-restart");
            Assert.Equal(persisted, restored);
            Assert.Equal(
                "seed-digest-1",
                restarted.GetLatestAppliedSeed("session-restart", "pattern")?.SeedDigest);

            var decision = LearningPhaseProgression.Evaluate(
                restarted.GetLearningSession("session-restart")!.Value.Phase,
                restored,
                restored + TimeSpan.FromDays(14),
                patternPhaseGateReady: false);
            Assert.Equal("model", decision?.NextPhase);
        }
        finally
        {
            TryDelete(path);
            TryDelete(path + "-wal");
            TryDelete(path + "-shm");
        }
    }

    [Fact]
    public void DiscoveryCapture_AlwaysSuppressesRuleEmission()
    {
        var options = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = "generate",
            RuleGeneration = true,
            AutoApproveOnFingerprintMatch = true,
        };

        Assert.False(LearningWorker.ShouldEmitTemplateRules(options, captureOnly: true));
        Assert.True(LearningWorker.ShouldEmitTemplateRules(options, captureOnly: false));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
