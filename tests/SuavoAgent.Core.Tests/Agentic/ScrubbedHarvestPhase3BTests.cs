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
        // The per-param trusted-catalog guard fires first (catalog PHI in a value) — pinpoints the key.
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
    public void NameFragmentedAcrossClick_Refused_TrajectoryGoalEcho()
    {
        // The two-char fragments "Jo"/"hn" each clear every per-step + per-run veto (too short for the
        // 3-letter goal-echo token, no name shape). The click between them resets the contiguous RUN —
        // but the WHOLE-trajectory keyboard stream "John" is substring-matched against the goal token
        // "john", so the cross-click assembly is still caught.
        var skill = Harvest(new AgentObjective("find john profile", "task.flow", "ph"), out var reason,
            Step("s0", "type_into_field", ("text", "Jo")),
            Click("s1", "Search"),
            Step("s2", "type_into_field", ("text", "hn")));

        Assert.Null(skill);
        Assert.Contains("goal_echo", reason); // via the full CertifyFreeText stack on the concatenation
        Assert.DoesNotContain("Jo", reason);
    }

    [Fact]
    public void DigitsFragmentedAcrossClick_Refused_TrajectoryDigits()
    {
        // "123" and "456" each clear the per-step identifier-digit veto; the whole-trajectory digit count
        // (6) refuses the identifier assembled across the click.
        var skill = Harvest(Obj("enter the reference"), out var reason,
            Step("s0", "type_into_field", ("text", "123")),
            Click("s1", "Next"),
            Step("s2", "type_into_field", ("text", "456")));

        Assert.Null(skill);
        Assert.Contains("identifier_digits", reason);
    }

    [Fact]
    public void EmailFragmentedAcrossClick_Refused()
    {
        // F1 (Fable): "jane.rivera" + "@example.com" each clear EmailShape alone (no full a@b.c); the
        // catalog has no email rule. The whole-trajectory pass must run the FULL CertifyFreeText stack
        // (incl. EmailShape) on the concatenation so the assembled email is caught across the click.
        var skill = Harvest(Obj("update the contact info now"), out var reason,
            Step("s0", "type_into_field", ("text", "jane.rivera")),
            Click("s1", "Field2"),
            Step("s2", "type_into_field", ("text", "@example.com")));

        Assert.Null(skill);
        Assert.Contains("free_text_email", reason);
        Assert.DoesNotContain("jane", reason);
    }

    [Fact]
    public void SingleRunValueSharingShortGoalSubstring_Banks()
    {
        // F3 (Fable): no click fragments the stream (one keyboard run), so the broad substring goal-echo
        // must NOT fire — "tablets" contains the goal token "tab", but per-run token-equality (tablets ≠
        // tab) correctly allows it. The cross-click substring pass is gated on keyboardRuns >= 2.
        var skill = Harvest(Obj("open the orders tab"), out var reason,
            Step("s0", "type_into_field", ("text", "tablets")));

        Assert.NotNull(skill);
        Assert.Null(reason);
    }

    [Fact]
    public void CleanCrossClickTrajectory_Banks()
    {
        // Non-PHI typed values that don't echo the goal must NOT be tripped by the cross-click stream:
        // "hi" + "30" assembles to "hi30" — no goal-token substring, under the digit threshold.
        var skill = Harvest(Obj("open notepad and type hi"), out var reason,
            Step("s0", "type_into_field", ("text", "hi")),
            Click("s1", "Tab2"),
            Step("s2", "type_into_field", ("text", "30")));

        Assert.NotNull(skill);
        Assert.Null(reason);
    }

    [Fact]
    public void CanonicalPricingFlow_StillBanks_AfterCrossClickDefense()
    {
        // Regression guard: the pricing workflow types a number and CLICKS the "Price" label. The typed
        // "12.99" shares no token substring with the goal, and the click label is not in the keyboard
        // stream — so the whole-trajectory defense must not break the canonical flow.
        var skill = Harvest(Obj("update the price to 12.99"), out var reason,
            Click("s0", "Price"),
            Step("s1", "type_into_field", ("text", "12.99")));

        Assert.NotNull(skill);
        Assert.Null(reason);
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
    public void VerbSignatureMismatch_SigOnly_Refused_DefenseInDepth()
    {
        // A forged sig-only step whose structured verb disagrees with the signature's verb prefix could
        // smuggle a type action under the click standard — refused, never guessed (no params → the
        // verb-prefix cross-check is the available guard).
        var forged = new StepRecord("s0", "type_into_field(text=whatever)", ActStatus.Success,
            PostconditionVerdict.Met, "click_by_label", null);
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

    // ───────── (a2) Codex HIPAA round-2 holes: each PHI/forged input must be REFUSED ─────────

    [Fact]
    public void SplitTypedName_AcrossSteps_Refused_KeyboardStream() // Codex #1 CRITICAL
    {
        // "Jo" + "hn" → "John". Each chunk passes per-step (goal-echo needs 3+ alpha; name-shape needs
        // two words). The cross-step keyboard-stream pass concatenates them → "John" echoes the goal.
        var skill = Harvest(new AgentObjective("open john record", "task.flow", "ph"), out var reason,
            Step("s0", "type_into_field", ("text", "Jo")),
            Step("s1", "type_into_field", ("text", "hn")));

        Assert.Null(skill);
        Assert.NotNull(reason);
        Assert.Contains("keyboard_stream", reason);
        Assert.DoesNotContain("john", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SplitTypedName_NoGoalEcho_StillRefused_NameShapeInStream()
    {
        // Even with no goal provenance, "John" + " Doe" assembles to "John Doe" → name-shape in stream.
        var skill = Harvest(Obj("navigate to billing"), out var reason,
            Step("s0", "type_into_field", ("text", "John")),
            Step("s1", "type_into_field", ("text", " Doe")));

        Assert.Null(skill);
        Assert.Contains("keyboard_stream", reason);
        Assert.Contains("free_text_name_shape", reason);
    }

    [Theory]
    [InlineData("123 45 6789")]   // SSN with spaces (Codex #2)
    [InlineData("01.02.1990")]    // DOB with dots
    [InlineData("123 456")]       // separated MRN
    public void SeparatedNumericIdentifier_Refused_TotalDigitCount(string typed)
    {
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "type_into_field", ("text", typed)));

        Assert.Null(skill);
        Assert.Contains("free_text_identifier_digits", reason);
        Assert.DoesNotContain("123", reason);
    }

    [Fact]
    public void UnicodeDiacriticName_GoalEcho_Refused() // Codex #3 HIGH
    {
        // Goal "find patient García"; typed "garcia". The ASCII tokenizer would miss "García"→"Garc";
        // diacritic+case fold on BOTH sides makes "garcia" == "garcia".
        var skill = Harvest(new AgentObjective("find patient García profile", "task.flow", "ph"),
            out var reason,
            Step("s0", "type_into_field", ("text", "garcia")));

        Assert.Null(skill);
        Assert.Contains("free_text_goal_echo:text", reason);
        Assert.DoesNotContain("garcia", reason);
    }

    [Fact]
    public void TypedEmail_Refused_EmailRule() // Codex #6 (certifier rule)
    {
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "type_into_field", ("text", "jane.rivera@example.com")));

        Assert.Null(skill);
        Assert.Contains("free_text_email:text", reason);
        Assert.DoesNotContain("jane", reason);
    }

    [Fact]
    public void HelperRejectedStep_Dropped_NeverBanks() // Codex #6 (Outcome gate)
    {
        // The Helper REJECTED a typed-email step (PhiPatternGuard), but its Verdict reads Met. Without
        // the Outcome==Success gate it would still bank. With the gate it's dropped — and since it's the
        // only candidate step, nothing banks.
        var rejected = new StepRecord("s0",
            NextAction.Act("type_into_field", new Dictionary<string, object?> { ["text"] = "x@y.com" }).Signature(),
            ActStatus.Rejected, PostconditionVerdict.Met, "type_into_field",
            new Dictionary<string, string> { ["text"] = "x@y.com" });
        var skill = Harvest(Obj(), out var reason, rejected);

        Assert.Null(skill);
        Assert.Null(reason); // dropped pre-certification → silent (nothing to bank), not a refusal code
    }

    [Fact]
    public void RejectedDirtyStep_DoesNotContaminate_CleanRunStillBanks()
    {
        // A rejected PHI step interleaved with clean executed clicks: the rejected step is DROPPED, the
        // clean steps still bank (the reject doesn't poison the trajectory).
        var rejected = new StepRecord("sX",
            NextAction.Act("type_into_field", new Dictionary<string, object?> { ["text"] = "a@b.com" }).Signature(),
            ActStatus.Rejected, PostconditionVerdict.Met, "type_into_field",
            new Dictionary<string, string> { ["text"] = "a@b.com" });
        var skill = Harvest(Obj(), out var reason,
            Click("s0", "Seven"), rejected, Click("s1", "Eight"));

        Assert.Null(reason);
        Assert.NotNull(skill);
        Assert.Equal(2, skill!.Steps.Count); // only the two clean clicks
    }

    [Fact]
    public void StructuralKeySmuggling_OnFreeTextVerb_Refused() // Codex #4 HIGH
    {
        // type_into_field(label="Jane Doe"): `label` is structural for CLICKS but NOT for type_into_field
        // (verb-scoped). It is held to the free-text standard → name-shape refuses it. (The signature is
        // built from the same params so reconstruction-equality holds; the value veto is what fires.)
        var sig = NextAction.Act("type_into_field",
            new Dictionary<string, object?> { ["text"] = "12.99", ["label"] = "Jane Doe" }).Signature();
        var step = new StepRecord("s0", sig, ActStatus.Success, PostconditionVerdict.Met, "type_into_field",
            new Dictionary<string, string> { ["text"] = "12.99", ["label"] = "Jane Doe" });
        var skill = Harvest(Obj(), out var reason, step);

        Assert.Null(skill);
        Assert.Contains("free_text_name_shape:label", reason);
        Assert.DoesNotContain("Jane", reason);
    }

    [Fact]
    public void ParamsSignatureMismatch_FullEquality_Refused() // Codex #5 HIGH
    {
        // ActionSignature claims a clean type, but ActionParams carry a dirty `label`. The prefix-only
        // check would pass (both "type_into_field"); full reconstruction equality catches that the
        // rebuilt signature (which INCLUDES label="Jane Doe") differs from the banked one → refuse, so
        // the dirty ParamsJson never persists.
        var cleanSig = NextAction.Act("type_into_field",
            new Dictionary<string, object?> { ["text"] = "12.99" }).Signature();
        var step = new StepRecord("s0", cleanSig, ActStatus.Success, PostconditionVerdict.Met, "type_into_field",
            new Dictionary<string, string> { ["text"] = "12.99", ["label"] = "Jane Doe" });
        var skill = Harvest(Obj(), out var reason, step);

        Assert.Null(skill);
        Assert.Contains("signature_reconstruction_mismatch", reason);
        Assert.DoesNotContain("Jane", reason);
    }

    [Fact]
    public void ClickLabel_MayEchoGoalVocabulary_StillBanks()
    {
        // DESIGN PIN (not a hole): click labels legitimately share vocabulary with the goal — "click
        // Price" to "update the price". The goal-echo veto is for TYPED text (run-specific values), NOT
        // labels (UI control names), so a label echoing the goal must STILL bank. The bare-name-label
        // residual is deferred wholly to Phase-3C label grounding (see design brief). This guards
        // against a regression that re-introduces a label goal-echo veto and breaks the pricing flow.
        var skill = Harvest(new AgentObjective("update the price to 12.99", "task.flow", "ph"),
            out var reason, Click("s0", "Price"), Click("s1", "Update"));

        Assert.Null(reason);
        Assert.NotNull(skill);
        Assert.Equal(2, skill!.Steps.Count);
    }

    [Fact]
    public void MultiStepChordSpelling_Refused_StreamLetterCap() // Codex #7 HIGH
    {
        // "J o" + "h n" → spelled "John" across two press_keys steps. Per-step each is only 2 letters
        // (≤ cap); the trajectory stream sums to 4 single-letter mains → keyboard_spelling_veto.
        var skill = Harvest(Obj(), out var reason,
            Step("s0", "press_keys", ("chords", "J o")),
            Step("s1", "press_keys", ("chords", "h n")));

        Assert.Null(skill);
        Assert.Contains("keyboard_spelling_veto", reason);
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
