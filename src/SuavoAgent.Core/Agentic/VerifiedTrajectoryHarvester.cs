using System.Collections.Generic;

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
/// </summary>
public static class VerifiedTrajectoryHarvester
{
    /// <summary>
    /// Extract the verified skill from a finished run, or null when nothing should be banked.
    /// <paramref name="app"/> is the allowlisted sandbox process for explore runs (part of the skill key);
    /// pass empty for non-sandbox navigate runs.
    /// </summary>
    public static VerifiedSkill? Harvest(AgentObjective objective, string app, AgenticLoopResult result)
    {
        if (objective is null || result is null) return null;

        // Gate 1: only a run that reached its objective is worth banking as a replayable skill.
        if (result.Termination != TerminationReason.Done) return null;

        var history = result.FinalMemory?.History;
        if (history is null || history.Count == 0) return null;

        // Gate 2: bank ONLY the verified (Met) actuating steps, in order. NotMet/Ambiguous/unknown
        // actuating steps are exploration detours that produced no observable effect — dropped, never
        // banked (verified-only). Terminal / no-action steps carry no (state, action) key — skipped.
        var steps = new List<VerifiedStep>();
        foreach (var s in history)
        {
            if (string.IsNullOrEmpty(s.DecisionScreenHash) || string.IsNullOrEmpty(s.ActionSignature))
                continue;
            if (s.Verdict != PostconditionVerdict.Met)
                continue;
            // PHI guard (Codex 2026-06-08): a banked signature is persisted verbatim. Only structural CLICK
            // signatures are PHI-safe (label = a UI control name, automationId = structural — green-tier). A
            // type_into_field / press_keys signature embeds the typed VALUE, which on the navigate path could
            // be patient data — so a verified trajectory that used one is REFUSED entirely (bank nothing)
            // rather than persisting a partial or PHI-bearing skill. explore_sandbox is already click-only;
            // this hard-stops the navigate path too. Lifting this needs a scrubbing-aware harvest (Phase-3B).
            if (!IsBankableActionSig(s.ActionSignature))
                return null;
            steps.Add(new VerifiedStep(s.DecisionScreenHash, s.ActionSignature));
        }

        if (steps.Count == 0) return null;
        return VerifiedSkill.Create(objective.PharmacyId, objective.TaskKey, app ?? string.Empty, steps);
    }

    /// <summary>Only structural click signatures are PHI-safe to persist verbatim (see PHI guard above).</summary>
    private static bool IsBankableActionSig(string actionSig)
        => actionSig.StartsWith("click_by_label(", System.StringComparison.Ordinal)
           || actionSig.StartsWith("click_by_signature(", System.StringComparison.Ordinal);
}
