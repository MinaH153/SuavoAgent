using System;
using System.Collections.Generic;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Routes a navigate run's VERIFIED verdict trail into edge conductance — this is "feed verified
/// verdicts into learning" made concrete, and the substrate <c>PhysarumActionPolicy</c> will select
/// over. Pure logic over an <see cref="IEdgeConductanceStore"/> (testable with the in-memory store).
///
/// <para>Verified-only: a step reinforces its edge ONLY when its postcondition was
/// <see cref="PostconditionVerdict.Met"/> (the verdict produced by Phase-1's
/// <see cref="Adapters.PostconditionEvaluator"/>). A visited-but-not-verified step decays its edge.
/// This is why the verifier had to be real first — the slime mold can't be rewarded for work that
/// didn't actually happen.</para>
/// </summary>
public static class EdgeReinforcement
{
    /// <summary>
    /// Apply one run's step history to the store: Met ⇒ reinforce (verifiedFlux=1); NotMet/Ambiguous/
    /// unknown ⇒ decay (visited but unverified). Untouched edges are handled by the periodic
    /// <see cref="Evaporate"/> sweep (mirrors FeedbackProcessor.ProcessDecay on a tick), not here.
    /// Steps with no state/action key (terminal Done/Escalate, or a strike) are skipped.
    /// </summary>
    public static void ApplyRun(
        IEdgeConductanceStore store,
        string pharmacyId,
        string taskKey,
        IReadOnlyList<StepRecord> history,
        ConductanceParams p,
        Func<EdgeKey, double>? semanticScore = null)
    {
        if (store is null || history is null) return;
        foreach (var s in history)
        {
            if (string.IsNullOrEmpty(s.DecisionScreenHash) || string.IsNullOrEmpty(s.ActionSignature))
                continue; // terminal / no-action step — nothing to reinforce

            var edge = new EdgeKey(pharmacyId, taskKey, s.DecisionScreenHash, s.ActionSignature);
            var current = store.Get(edge, p.Floor);
            var next = s.Verdict == PostconditionVerdict.Met
                ? ConductanceLaw.Step(current, verifiedFlux: 1, semanticScore: semanticScore?.Invoke(edge) ?? 0.0, p)
                : ConductanceLaw.Decay(current, p); // visited but unverified ⇒ evaporate, never reward
            store.Set(edge, next);
        }
    }

    /// <summary>
    /// Periodic evaporation: decay EVERY known edge for a (pharmacy, task) toward the Floor — the
    /// always-on decay-of-the-unused that prunes stale paths and auto-handles PMS UI drift. Call on a
    /// tick (like FeedbackProcessor.ProcessDecay), independent of any run.
    /// </summary>
    public static void Evaporate(IEdgeConductanceStore store, string pharmacyId, string taskKey, ConductanceParams p)
    {
        if (store is null) return;
        foreach (var edge in store.Edges(pharmacyId, taskKey))
            store.Set(edge, ConductanceLaw.Decay(store.Get(edge, p.Floor), p));
    }
}
