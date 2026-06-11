using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Phase-3B (scrubbing-aware harvest) — the gate that lifts the navigate-path click-only hard-stop.
/// Two properties are pinned:
///
/// (a) PHI NEVER reaches verified_skills: a trajectory whose typed values / labels / chords carry
///     anything the certification stack flags (trusted catalog, shadow denylist, identifier digits,
///     bare-name shape, goal echo, chord spelling) is REFUSED WHOLE — and the refusal reason never
///     contains the offending value.
/// (b) A clean trajectory with type/press steps BANKS with structured params and round-trips through
///     <see cref="VerifiedSkillReplayer"/> deterministically to Completed — the ratchet cashes in.
/// </summary>
public class ScrubbedHarvestPhase3BTests
{
    private const string Proc = "pms.exe";

    private static AgentObjective Obj(string goal = "complete the checkout flow") => new(goal, "task.flow", "ph");

    private static NextAction Action(string verb, params (string Key, string Value)[] ps) =>
        NextAction.Act(verb, ps.ToDictionary(p => p.Key, p => (object?)p.Value));

    /// <summary>A Met step with structured verb + params (what ContextAccumulator captures).</summary>
    private static StepRecord Step(string state, string verb, params (string Key, string Value)[] ps) =>
        new(state, Action(verb, ps).Signature(), ActStatus.Success, PostconditionVerdict.Met,
            verb, ps.ToDictionary(p => p.Key, p => p.Value));

    private static StepRecord Click(string state, string label) =>
        Step(state, "click_by_label", ("label", label), ("process_name", Proc));

    private static AgenticLoopResult Result(params StepRecord[] history) =>
        new(TerminationReason.Done, history.Length, 0, false, null,
            new WorkingMemory(Obj(), null, history.ToList(), 0, 0));

    private static VerifiedSkill? Harvest(AgentObjective obj, out string? reason, params StepRecord[] history)
        => VerifiedTrajectoryHarvester.Harvest(obj, Proc, Result(history), out reason);

    // ───────────────────────── (a) PHI never reaches verified_skills ─────────────────────────

    [Theory]
    [InlineData("123-45-6789")]   // SSN (trusted catalog)
    [InlineData("01/02/1990")]    // DOB / date shape (trusted catalog)
    [InlineData("858-555-1234")]  // phone (trusted catalog)
    public void TypedCatalogPhi_RefusesWholeTrajectory(string phi)
    {
        var skill = Harvest(Obj("navigate to billing"), out var reason,
            Click("s0", "Search"),
            Step("s1", "type_into_field", ("text", phi)));

        Assert.Null(skill);
        Assert.Contains("param_trusted_phi:text", reason);
        Assert.DoesNotContain(phi, reason); // the refusal reason itself must never leak the value
    }

    [Fact]
    public void TypedBareName_Refused_NameShapeVeto()
    {
        // "John Doe" carries no catalog context keyword (no "Patient:" prefix) — the regex catalog
        // misses it. The name-shape veto is the layer that stops it from being banked.
        var skill = Harvest(Obj("navigate to billing"), out var reason,
            Step("s0", "type_into_field", ("text", "John Doe")));

        Assert.Null(skill);
        Assert.Contains("free_text_name_shape:text", reason);
        Assert.DoesNotContain("John", reason);
    }

    [Fact]
    public void TypedGoalEchoedToken_Refused_GoalEchoVeto()
    {
        // The free-form Goal is the only channel real patient data can enter action params through
        // (the perceived screen is scrubbed). A single lowercase surname echoed from the goal has no
        // name SHAPE — the goal-echo veto is what catches it.
        var skill = Harvest(new AgentObjective("find smith profile and review it", "task.flow", "ph"),
            out var reason,
            Step("s0", "type_into_field", ("text", "smith")));

        Assert.Null(skill);
        Assert.Contains("free_text_goal_echo:text", reason);
        Assert.DoesNotContain("smith", reason);
    }

    [Fact]
    public void TypedIdentifierShapedNumber_Refused()
    {
        // A bare 6+ digit run (Rx number / MRN / member id typed into a search box) has no catalog
        // context keyword — refused by shape.
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "type_into_field", ("text", "1234567")));

        Assert.Null(skill);
        Assert.Contains("free_text_identifier_digits:text", reason);
    }

    [Fact]
    public void TypedNdc_Refused_ShadowDenylistEnforced()
    {
        // NDC shapes are shadow-mode (measure-only) on other surfaces; on the brand-new banked-typed-text
        // surface they are ENFORCED.
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "type_into_field", ("text", "00071-0155-23")));

        Assert.Null(skill);
        Assert.Contains("free_text_shadow_denylist:text", reason);
        Assert.DoesNotContain("00071", reason);
    }

    [Fact]
    public void SigOnlyTypeStep_Refused_StructuredParamsRequired()
    {
        // A type step without structured params cannot be certified value-by-value (the unescaped
        // signature can't be split back into exact values) — refused, fail-closed.
        var sigOnly = new StepRecord("s0", "type_into_field(text=anything)", ActStatus.Success, PostconditionVerdict.Met);
        var skill = Harvest(Obj(), out var reason, sigOnly);

        Assert.Null(skill);
        Assert.Contains("structured_params_required:type_into_field", reason);
    }

    [Fact]
    public void VerbSignatureMismatch_Refused_DefenseInDepth()
    {
        // A forged/corrupt step whose structured verb disagrees with the signature's verb prefix could
        // smuggle a type action under the click standard — refused, never guessed.
        var forged = new StepRecord("s0", "type_into_field(text=whatever)", ActStatus.Success,
            PostconditionVerdict.Met, "click_by_label",
            new Dictionary<string, string> { ["label"] = "Save" });
        var skill = Harvest(Obj(), out var reason, forged);

        Assert.Null(skill);
        Assert.Contains("verb_signature_mismatch", reason);
    }

    [Fact]
    public void UnknownVerb_RefusesTrajectory()
    {
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "run_workflow", ("workflow_id", "wf1")));

        Assert.Null(skill);
        Assert.Contains("verb_not_bankable:run_workflow", reason);
    }

    [Fact]
    public void ChordSpelling_Refused_SpellingVeto()
    {
        // PHI could be spelled key-by-key; >2 single-LETTER chord mains is refused.
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "press_keys", ("chords", "J o h n")));

        Assert.Null(skill);
        Assert.Contains("chords_spelling_veto", reason);
    }

    [Fact]
    public void ClickLabelWithLastFirstName_Refused()
    {
        // A patient-row label ("Doe, John") echoed into a click — the trusted LastFirst rule catches it
        // (the shipped click standard, now pinned).
        var skill = Harvest(Obj(), out var reason, Click("s0", "Doe, John"));

        Assert.Null(skill);
        Assert.NotNull(reason);
        Assert.DoesNotContain("Doe", reason);
    }

    [Fact]
    public void OneDirtyStep_RefusesWholeTrajectory_NeverPartial()
    {
        var skill = Harvest(Obj("navigate to billing"), out _,
            Click("s0", "Search"),                                  // clean
            Step("s1", "type_into_field", ("text", "John Doe")),    // dirty
            Click("s2", "Submit"));                                 // clean

        Assert.Null(skill); // nothing banked — not a partial skill of the clean steps
    }

    [Fact]
    public void CertifySerializedSteps_RefusesPhiBearingJson()
    {
        // End-of-pipe unit: the exact steps_json string is re-certified before persist.
        var dirty = JsonSerializer.Serialize(new[] { new VerifiedStep("s0", "click_by_label(label=123-45-6789)") });
        var cert = HarvestPhiCertifier.CertifySerializedSteps(dirty);

        Assert.False(cert.Certified);
        Assert.Equal("serialized_steps_trusted_phi", cert.RefusalReason);
    }

    // ───────────────────── (b) clean trajectories bank + replay round-trip ─────────────────────

    [Fact]
    public void CleanTypedNumeric_Banks_WithStructuredParams()
    {
        // The canonical pricing shape: numeric values (prices/quantities) — even when the goal contains
        // the same number, numerics are exempt from the goal-echo veto.
        var skill = Harvest(new AgentObjective("update the price to 12.99", "task.flow", "ph"), out var reason,
            Click("s0", "Price"),
            Step("s1", "type_into_field", ("text", "12.99"), ("clear_first", "True")));

        Assert.Null(reason);
        Assert.NotNull(skill);
        var typed = skill!.Steps[1];
        Assert.Equal("type_into_field", typed.Verb);
        Assert.Equal("12.99", JsonSerializer.Deserialize<Dictionary<string, string>>(typed.ParamsJson!)!["text"]);
    }

    [Theory]
    [InlineData("Enter")]
    [InlineData("Ctrl+S")]
    [InlineData("Ctrl+Shift+Esc")]
    [InlineData("Tab Tab Enter")]
    [InlineData("7 5")] // single-DIGIT mains are not name-spelling — allowed
    public void CleanPressKeys_Banks(string chords)
    {
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "press_keys", ("chords", chords)));

        Assert.Null(reason);
        Assert.NotNull(skill);
        Assert.Equal("press_keys", skill!.Steps[0].Verb);
    }

    [Fact]
    public void CleanTypedTrajectory_IsDeterministic()
    {
        VerifiedSkill? Go() => Harvest(Obj(), out _,
            Step("s0", "type_into_field", ("text", "12.99")),
            Step("s1", "press_keys", ("chords", "Enter")));

        var a = Go();
        var b = Go();
        Assert.Equal(a!.SkillId, b!.SkillId);
        Assert.Equal(a.StepsHash, b.StepsHash);
    }

    // ── the ratchet oracle: harvested mixed skill replays deterministically to Completed ──

    private sealed class ScriptedApp(VerifiedSkill skill)
    {
        private readonly string[] _states = skill.Steps.Select(s => s.StateHash).Append("done").ToArray();
        private readonly string[] _expected = skill.Steps.Select(s => s.ActionSignature).ToArray();
        public int Index;
        public PerceivedScreen Screen() => new(_states[Index], true, new[] { "field:Price" }, "PMS");
        public void Act(string sig) { if (Index < _expected.Length && sig == _expected[Index]) Index++; }
    }

    private sealed class SimPerceiver(ScriptedApp app) : IPerceiver
    {
        public Task<PerceivedScreen?> PerceiveAsync(CancellationToken ct) => Task.FromResult<PerceivedScreen?>(app.Screen());
    }

    private sealed class SimActuator(ScriptedApp app) : IActuator
    {
        public Task<ActOutcome> ActAsync(NextAction action, ActuationContext ctx, CancellationToken ct)
        { if (!ctx.DryRun) app.Act(action.Signature()); return Task.FromResult(new ActOutcome(ActStatus.Success)); }
    }

    [Fact]
    public async Task HarvestedMixedSkill_ReplaysToCompleted()
    {
        // 1. Phase-3B harvest of a clean click + type + press trajectory.
        var skill = Harvest(Obj(), out var reason,
            Click("s0", "Price"),
            Step("s1", "type_into_field", ("text", "12.99"), ("clear_first", "True")),
            Step("s2", "press_keys", ("chords", "Enter")));
        Assert.Null(reason);
        Assert.NotNull(skill);

        // 2. Deterministic replay over a sim that starts at the verified entry state. The sandbox gate
        //    is click-only by design (typing is denied in explore), so replaying a type-bearing skill
        //    uses a permissive fake gate here — the LIVE wiring of type-capable replay goes through the
        //    composite navigate gate in the replay-first increment.
        var app = new ScriptedApp(skill!);
        var replayer = new VerifiedSkillReplayer(
            new SimPerceiver(app), new SimActuator(app), new FakeSafetyGate(), delay: (_, _) => Task.CompletedTask);
        var opts = new AgenticLoopOptions { SettleMaxPolls = 2, SettleStableReads = 1, SettlePollInterval = TimeSpan.Zero };

        var result = await replayer.ReplayAsync(skill!, Obj(), opts, default);

        Assert.Equal(SkillReplayOutcome.Completed, result.Outcome);
        Assert.Equal(skill!.Steps.Count, result.StepsCompleted); // every banked step executed + verified
    }

    [Fact]
    public void CleanClickOnlyTrajectory_StillBanks_ShippedBehaviorUnchanged()
    {
        var skill = Harvest(Obj(), out var reason,
            Click("s0", "Seven"), Click("s1", "Eight"));

        Assert.Null(reason);
        Assert.NotNull(skill);
        Assert.Equal(2, skill!.Steps.Count);
    }
}
