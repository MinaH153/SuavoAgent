using System.Collections.Generic;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

/// <summary>
/// Pins the durable verified-skill store: banking a skill records success_count=1; re-banking the SAME
/// verified path increments it (the tube thickening), not duplicates; it survives restart; and distinct
/// paths are distinct rows. This is the persistent half of the amortize ratchet.
/// </summary>
public class VerifiedSkillStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public VerifiedSkillStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_skill_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    private static VerifiedSkill Skill(string app, params string[] actions)
    {
        var steps = new List<VerifiedStep>();
        for (var i = 0; i < actions.Length; i++) steps.Add(new VerifiedStep($"s{i}", actions[i]));
        return VerifiedSkill.Create("ph", "task.calc", app, steps);
    }

    private int Upsert(VerifiedSkill s) =>
        _db.UpsertVerifiedSkill(s.SkillId, s.PharmacyId, s.TaskKey, s.App, s.SerializeSteps(), s.StepsHash);

    [Fact]
    public void FirstBank_SuccessCountIsOne()
    {
        var s = Skill("calc.exe", "click(7)", "click(8)");
        Assert.Equal(1, Upsert(s));
        var row = _db.GetVerifiedSkillRaw(s.SkillId);
        Assert.NotNull(row);
        Assert.Equal(1, row!.Value.SuccessCount);
    }

    [Fact]
    public void ReBankingSamePath_ThickensSuccessCount_NoDuplicate()
    {
        var s = Skill("calc.exe", "click(7)", "click(8)");
        Assert.Equal(1, Upsert(s));
        Assert.Equal(2, Upsert(s));
        Assert.Equal(3, Upsert(s));
        Assert.Equal(1, _db.GetVerifiedSkillCountForTask("ph", "task.calc")); // one row, thickened
        Assert.Equal(3, _db.GetVerifiedSkillRaw(s.SkillId)!.Value.SuccessCount);
    }

    [Fact]
    public void DistinctPaths_AreSeparateRows()
    {
        Upsert(Skill("calc.exe", "click(7)", "click(8)"));
        Upsert(Skill("calc.exe", "click(9)", "click(0)"));
        Assert.Equal(2, _db.GetVerifiedSkillCountForTask("ph", "task.calc"));
    }

    [Fact]
    public void SurvivesRestart()
    {
        var s = Skill("calc.exe", "click(7)", "click(8)");
        Upsert(s);
        Upsert(s);
        _db.Dispose();

        using var db2 = new AgentStateDb(_dbPath);
        var row = db2.GetVerifiedSkillRaw(s.SkillId);
        Assert.NotNull(row);
        Assert.Equal(2, row!.Value.SuccessCount);
        Assert.Equal(s.SerializeSteps(), row.Value.StepsJson);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
