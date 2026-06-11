using System.Collections.Generic;
using System.Text.Json;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// The amortize step: turns a SUCCESSFUL, execution-verified run into a bankable <see cref="VerifiedSkill"/>.
/// Pure logic over the loop's result (testable; no DB/IO) — the persistence + thickening happens in the
/// caller via the SQLite store, off the hot path. This is the "derive once → bank" half of lever 3.
///
/// <para>VERIFIED-ONLY by construction: (1) the run must have reached its objective
/// (<see cref="TerminationReason.Done"/> — the reasoner returned Done); (2) every banked step was
/// actually EXECUTED for real (<see cref="ActStatus.Success"/> — a Helper-REJECTED step, e.g. a
/// PHI-pattern reject on typed text, never banks even if its Verdict reads Met) AND verified its
/// postcondition (Phase-1 <see cref="PostconditionVerdict.Met"/>). Unverified detours and
/// rejected/dry-run steps are DROPPED — the banked chain is the clean executed-and-verified path.
/// Nothing is banked from a run that didn't succeed.</para>
///
/// <para><b>PHI-certified by construction (Phase-3B, two-pass).</b> Banked signatures + params are
/// persisted VERBATIM, so the WHOLE trajectory is certified provably PHI-free by
/// <see cref="HarvestPhiCertifier.CertifyTrajectory"/>: per-step structure (verb allowlist;
/// reconstruction-equality of the banked params↔signature; verb-scoped structural keys; trusted-catalog
/// scrub-certification of signature + every key/value — certify-or-refuse, never a transformed value;
/// chord grammar) PLUS a cross-step keyboard-stream pass (concatenated typed text + chord letters run
/// through the full free-text vetoes — catalog, shadow denylist, email, total-digit, name-shape,
/// goal-echo — so a name/identifier assembled key-by-key across steps is caught). The final serialized
/// steps_json is re-certified end-of-pipe. ANY uncertifiable step refuses the WHOLE trajectory (a
/// partial chain is unreplayable anyway) — bank nothing rather than possible PHI. This lifts the old
/// click-only hard-stop: type_into_field / press_keys steps bank when, and only when, their exact
/// values are certified clean.</para>
/// </summary>
public static class VerifiedTrajectoryHarvester
{
    /// <summary>
    /// Extract the verified skill from a finished run, or null when nothing should be banked.
    /// <paramref name="app"/> is the allowlisted sandbox process for explore runs (part of the skill key);
    /// pass empty for non-sandbox navigate runs.
    /// </summary>
    public static VerifiedSkill? Harvest(AgentObjective objective, string app, AgenticLoopResult result)
        => Harvest(objective, app, result, out _);

    /// <summary>
    /// As <see cref="Harvest(AgentObjective, string, AgenticLoopResult)"/>, additionally surfacing WHY a
    /// trajectory was refused. <paramref name="refusalReason"/> is a PHI-free operational code (e.g.
    /// <c>step2:free_text_goal_echo:text</c>) — safe to log; null when banked or when there was simply
    /// nothing to bank (non-Done run / no verified steps).
    /// </summary>
    public static VerifiedSkill? Harvest(
        AgentObjective objective, string app, AgenticLoopResult result, out string? refusalReason)
    {
        refusalReason = null;
        if (objective is null || result is null) return null;

        // Gate 1: only a run that reached its objective is worth banking as a replayable skill.
        if (result.Termination != TerminationReason.Done) return null;

        var history = result.FinalMemory?.History;
        if (history is null || history.Count == 0) return null;

        // Gate 2: bank ONLY the EXECUTED-and-verified actuating steps, in order. A step is bankable
        // only when it (a) carries a (state, action) key, (b) was actually EXECUTED for real
        // (Outcome == Success — a Helper-REJECTED step, e.g. a PHI-pattern reject on typed text, must
        // NEVER bank even if its Verdict reads Met; Codex #6), and (c) verified its postcondition
        // (Verdict == Met). NotMet/Ambiguous detours and rejected/dry-run steps are dropped — the
        // banked chain is the clean executed-and-verified path. Terminal / no-action steps carry no key.
        var certifiable = new List<StepRecord>();
        var banked = new List<VerifiedStep>();
        foreach (var s in history)
        {
            if (string.IsNullOrEmpty(s.DecisionScreenHash) || string.IsNullOrEmpty(s.ActionSignature))
                continue;
            if (s.Outcome != ActStatus.Success)
                continue;
            if (s.Verdict != PostconditionVerdict.Met)
                continue;
            certifiable.Add(s);
        }

        if (certifiable.Count == 0) return null;

        // Gate 3 (Phase-3B): the WHOLE trajectory must be CERTIFIED PHI-free to persist verbatim — both
        // per-step structure AND the cross-step keyboard stream (a name/identifier assembled across
        // steps). One uncertifiable step refuses the whole trajectory — bank nothing, never a partial
        // or possibly-PHI-bearing skill. The refusal code is PHI-free and indexes the FILTERED list.
        var cert = HarvestPhiCertifier.CertifyTrajectory(certifiable, objective.Goal);
        if (!cert.Certified)
        {
            refusalReason = cert.RefusalReason;
            return null;
        }

        foreach (var s in certifiable)
        {
            var paramsJson = s.ActionParams is { Count: > 0 } ? JsonSerializer.Serialize(s.ActionParams) : null;
            banked.Add(new VerifiedStep(s.DecisionScreenHash, s.ActionSignature, s.ActionVerb, paramsJson));
        }

        var skill = VerifiedSkill.Create(objective.PharmacyId, objective.TaskKey, app ?? string.Empty, banked);

        // Gate 4 (Phase-3B, end-of-pipe): re-certify the EXACT serialized string that will land in
        // verified_skills.steps_json — belt-and-suspenders against PHI shapes assembled across value
        // boundaries by serialization.
        var pipe = HarvestPhiCertifier.CertifySerializedSteps(skill.SerializeSteps());
        if (!pipe.Certified)
        {
            refusalReason = pipe.RefusalReason;
            return null;
        }

        return skill;
    }
}
