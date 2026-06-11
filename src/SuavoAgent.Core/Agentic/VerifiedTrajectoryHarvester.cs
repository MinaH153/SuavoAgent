using System.Collections.Generic;
using System.Text.Json;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// The amortize step: turns a SUCCESSFUL, execution-verified run into a bankable <see cref="VerifiedSkill"/>.
/// Pure logic over the loop's result (testable; no DB/IO) — the persistence + thickening happens in the
/// caller via the SQLite store, off the hot path. This is the "derive once → bank" half of lever 3.
///
/// <para>VERIFIED-ONLY by construction, two gates: (1) the run must have reached its objective
/// (<see cref="TerminationReason.Done"/> — the reasoner returned Done); (2) every banked step is a
/// Phase-1 <see cref="PostconditionVerdict.Met"/> step. Unverified detours (NotMet/Ambiguous dead clicks
/// that didn't change the screen) are DROPPED, never banked — the banked chain is the clean verified path
/// that actually advanced the screen toward success. Nothing is banked from a run that didn't succeed.</para>
///
/// <para><b>PHI-certified by construction (Phase-3B).</b> Banked signatures + params are persisted
/// VERBATIM, so every step must be certified provably PHI-free by <see cref="HarvestPhiCertifier"/>
/// before banking: verb allowlisted, signature + every param key/value scrub-certified (certify-or-refuse
/// — never bank a transformed value), free-text (typed) values held to the strictest standard
/// (shadow denylist + identifier-digit / name-shape / goal-echo vetoes), and the final serialized
/// steps_json re-certified end-of-pipe. ANY uncertifiable step refuses the WHOLE trajectory (a partial
/// chain is unreplayable anyway) — bank nothing rather than possible PHI. This lifts the old
/// click-only hard-stop on the navigate path: type_into_field / press_keys steps now bank when, and
/// only when, their exact values are certified clean.</para>
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

        // Gate 2: bank ONLY the verified (Met) actuating steps, in order. NotMet/Ambiguous/unknown
        // actuating steps are exploration detours that produced no observable effect — dropped, never
        // banked (verified-only). Terminal / no-action steps carry no (state, action) key — skipped.
        var steps = new List<VerifiedStep>();
        for (var i = 0; i < history.Count; i++)
        {
            var s = history[i];
            if (string.IsNullOrEmpty(s.DecisionScreenHash) || string.IsNullOrEmpty(s.ActionSignature))
                continue;
            if (s.Verdict != PostconditionVerdict.Met)
                continue;

            // Gate 3 (Phase-3B): the step's action must be CERTIFIED PHI-free to persist verbatim.
            // One uncertifiable step refuses the whole trajectory — bank nothing, never a partial or
            // possibly-PHI-bearing skill.
            var cert = HarvestPhiCertifier.CertifyStep(s, objective.Goal);
            if (!cert.Certified)
            {
                refusalReason = $"step{i}:{cert.RefusalReason}";
                return null;
            }

            var paramsJson = s.ActionParams is { Count: > 0 } ? JsonSerializer.Serialize(s.ActionParams) : null;
            steps.Add(new VerifiedStep(s.DecisionScreenHash, s.ActionSignature, s.ActionVerb, paramsJson));
        }

        if (steps.Count == 0) return null;
        var skill = VerifiedSkill.Create(objective.PharmacyId, objective.TaskKey, app ?? string.Empty, steps);

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
