using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Pins skill decay/retirement (slime-mold cache hygiene): a replay SUCCESS re-thickens + resets the
/// failure streak; consecutive replay FAILURES retire the skill so it's excluded from selection; a
/// re-harvest revives a retired skill. Environmental outcomes never reach here (the worker filters them).
/// </summary>
public class VerifiedSkillDecayTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public VerifiedSkillDecayTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_skilldecay_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    private VerifiedSkill Bank(string label = "Seven")
    {
        var skill = VerifiedSkill.Create("ph", "task.calc", "calc.exe",
            new List<VerifiedStep> { new("s0", $"click_by_label(label={label},process_name=calc.exe)") });
        _db.UpsertVerifiedSkill(skill.SkillId, skill.PharmacyId, skill.TaskKey, skill.App, skill.SerializeSteps(), skill.StepsHash);
        return skill;
    }

    [Fact]
    public void Success_Thickens_AndResetsFailureStreak()
    {
        var s = Bank();
        _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        var (count, streak, retired) = _db.RecordSkillReplayOutcome(s.SkillId, success: true);
        Assert.False(retired);
        Assert.Equal(0, streak);          // success reset the streak
        Assert.Equal(2, count);           // banked (1) + replay success (1)
    }

    [Fact]
    public void ConsecutiveFailures_RetireTheSkill()
    {
        var s = Bank();
        _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        var (_, streak, retired) = _db.RecordSkillReplayOutcome(s.SkillId, success: false); // 3rd → retire
        Assert.Equal(3, streak);
        Assert.True(retired);
        // Retired ⇒ excluded from selection + explicit fetch.
        Assert.Null(_db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe"));
        Assert.Null(_db.GetVerifiedSkill(s.SkillId));
    }

    [Fact]
    public void InterruptedFailureStreak_DoesNotRetire()
    {
        var s = Bank();
        _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        _db.RecordSkillReplayOutcome(s.SkillId, success: true);   // resets streak
        var (_, _, retired) = _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        Assert.False(retired);
        Assert.NotNull(_db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe"));
    }

    [Fact]
    public void ReHarvest_RevivesRetiredSkill()
    {
        var s = Bank();
        for (var i = 0; i < 3; i++) _db.RecordSkillReplayOutcome(s.SkillId, success: false);
        Assert.Null(_db.GetVerifiedSkill(s.SkillId)); // retired

        Bank(); // exploration re-verifies + re-banks the SAME path → revive
        Assert.NotNull(_db.GetVerifiedSkill(s.SkillId));
        Assert.NotNull(_db.GetBestVerifiedSkillForTask("ph", "task.calc", "calc.exe"));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
