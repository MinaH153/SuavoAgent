using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the amortize step (lever 3): a SUCCESSFUL, execution-verified run is banked as the clean verified
/// (state→action) path; an unsuccessful run banks nothing; unverified detours are dropped; the SkillId is
/// deterministic (re-verifying the same path upserts the same skill); and the PHI guard REFUSES any
/// trajectory whose verified path used a type/press step (could embed patient data).
/// </summary>
public class VerifiedTrajectoryHarvesterTests
{
    private const string App = "calc.exe";
    private static readonly AgentObjective Obj = new("compute 7x8", "task.calc", "ph");

    // realistic structural click signature (what CandidateEnumerator / the policy actually emit)
    private static string Click(string label) => $"click_by_label(label={label},process_name={App})";

    private static StepRecord Step(string state, string action, PostconditionVerdict? verdict) =>
        new(state, action, ActStatus.Success, verdict);

    private static AgenticLoopResult Result(TerminationReason termination, params StepRecord[] history) =>
        new(termination, history.Length, 0, false, null,
            new WorkingMemory(Obj, null, history.ToList(), 0, 0));

    [Fact]
    public void DoneRun_AllMet_BanksOrderedVerifiedPath()
    {
        var result = Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met),
            Step("s1", Click("x"), PostconditionVerdict.Met),
            Step("s2", Click("8"), PostconditionVerdict.Met));

        var skill = VerifiedTrajectoryHarvester.Harvest(Obj, App, result);

        Assert.NotNull(skill);
        Assert.Equal(new[] { Click("7"), Click("x"), Click("8") }, skill!.Steps.Select(s => s.ActionSignature));
        Assert.Equal("ph", skill.PharmacyId);
        Assert.Equal("task.calc", skill.TaskKey);
        Assert.Equal(App, skill.App);
    }

    [Fact]
    public void NonDoneTermination_BanksNothing()
    {
        foreach (var term in new[] { TerminationReason.NoProgress, TerminationReason.BudgetExhausted, TerminationReason.Escalated, TerminationReason.GateDenied, TerminationReason.Cancelled })
        {
            var result = Result(term, Step("s0", Click("7"), PostconditionVerdict.Met));
            Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, App, result));
        }
    }

    [Fact]
    public void UnverifiedDetours_AreDropped_OnlyMetStepsBanked()
    {
        var result = Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met),
            Step("s1", Click("dead"), PostconditionVerdict.NotMet),     // dead click — no screen change
            Step("s1", Click("amb"), PostconditionVerdict.Ambiguous),   // also unverified
            Step("s1", Click("x"), PostconditionVerdict.Met));          // recovered, advanced

        var skill = VerifiedTrajectoryHarvester.Harvest(Obj, App, result);

        Assert.NotNull(skill);
        Assert.Equal(new[] { Click("7"), Click("x") }, skill!.Steps.Select(s => s.ActionSignature));
    }

    [Fact]
    public void TypeStepInVerifiedPath_IsRefused_PhiGuard()
    {
        // Phase-3B: a SIG-ONLY type_into_field (no structured params) cannot be certified value-by-value
        // — the whole trajectory is refused (bank nothing) rather than persisting a possibly
        // patient-data-bearing signature. Structured + certified-clean type steps DO bank now
        // (see ScrubbedHarvestPhase3BTests).
        var result = Result(TerminationReason.Done,
            Step("s0", Click("Search"), PostconditionVerdict.Met),
            Step("s1", "type_into_field(text=John Patient,process_name=pms.exe)", PostconditionVerdict.Met));
        Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, App, result));
    }

    [Fact]
    public void PressKeysInVerifiedPath_IsRefused_PhiGuard()
    {
        // Phase-3B: sig-only press_keys (no structured chords param) refuses fail-closed, same as above.
        var result = Result(TerminationReason.Done,
            Step("s0", Click("Open"), PostconditionVerdict.Met),
            Step("s1", "press_keys(chords=Enter)", PostconditionVerdict.Met));
        Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, App, result));
    }

    [Fact]
    public void DoneRun_WithNoMetActuatingSteps_BanksNothing()
    {
        var result = Result(TerminationReason.Done,
            Step("s0", Click("dead"), PostconditionVerdict.NotMet),
            Step("", "", PostconditionVerdict.Met)); // terminal/no-action — no key
        Assert.Null(VerifiedTrajectoryHarvester.Harvest(Obj, App, result));
    }

    [Fact]
    public void SkillId_IsDeterministic_AcrossIdenticalTrajectories()
    {
        var a = VerifiedTrajectoryHarvester.Harvest(Obj, App, Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met), Step("s1", Click("8"), PostconditionVerdict.Met)));
        var b = VerifiedTrajectoryHarvester.Harvest(Obj, App, Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met), Step("s1", Click("8"), PostconditionVerdict.Met)));

        Assert.Equal(a!.SkillId, b!.SkillId);
        Assert.Equal(a.StepsHash, b.StepsHash);
    }

    [Fact]
    public void DifferentPath_YieldsDifferentSkillId()
    {
        var a = VerifiedTrajectoryHarvester.Harvest(Obj, App, Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met)));
        var b = VerifiedTrajectoryHarvester.Harvest(Obj, App, Result(TerminationReason.Done,
            Step("s0", Click("9"), PostconditionVerdict.Met)));
        Assert.NotEqual(a!.SkillId, b!.SkillId);
    }

    [Fact]
    public void SerializeRoundTrips()
    {
        var skill = VerifiedTrajectoryHarvester.Harvest(Obj, App, Result(TerminationReason.Done,
            Step("s0", Click("7"), PostconditionVerdict.Met), Step("s1", Click("8"), PostconditionVerdict.Met)))!;
        var round = VerifiedSkill.DeserializeSteps(skill.SerializeSteps());
        Assert.Equal(skill.Steps, round);
    }
}
