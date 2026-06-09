using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// The slime-mold frontier explorer: an <see cref="IReasoner"/> decorator that, in a SANDBOX app, selects
/// the next click by Physarum dynamics — <c>score = conductance + brainEndorsement − trailPenalty + explore</c>
/// — instead of taking the inner brain's single pick. This is what makes conductance LOAD-BEARING (the moat):
/// verified tubes thicken (post-run, in <see cref="EdgeReinforcement"/>) and the explorer follows them, while
/// the brain (Qwen3) is consulted only every N steps as a semantic interpreter whose endorsement nudges the
/// score (never the driver). Constructed PER RUN, so the step counter needs no synchronization (the loop calls
/// <see cref="ReasonNextAsync"/> sequentially).
///
/// <para>Used ONLY on explore_sandbox runs (wired by <c>NavigateLoopFactory</c> when policy options are present);
/// live navigate_app / replay_template use the inner <c>TieredBrainReasoner</c> directly. The store is READ-ONLY
/// here — all reinforcement/decay happens post-run + on the evaporation tick, never mid-step (no double-write).</para>
/// </summary>
public sealed class PhysarumActionPolicy : IReasoner
{
    private readonly IReasoner _inner;
    private readonly IEdgeConductanceStore _store;
    private readonly ConductanceParams _params;
    private readonly PhysarumPolicyOptions _opts;
    private readonly ILogger<PhysarumActionPolicy> _logger;
    private readonly string _processName;
    private int _stepIndex;

    public PhysarumActionPolicy(
        IReasoner inner,
        IEdgeConductanceStore store,
        ConductanceParams conductanceParams,
        PhysarumPolicyOptions options,
        ILogger<PhysarumActionPolicy> logger,
        string processName)
    {
        _inner = inner;
        _store = store;
        _params = conductanceParams;
        _opts = options;
        _logger = logger;
        _processName = processName;
    }

    public async Task<ReasonResult> ReasonNextAsync(
        AgentObjective objective,
        WorkingMemory memory,
        IReadOnlySet<string> allowedActions,
        bool allowCloud,
        CancellationToken ct)
    {
        var step = _stepIndex++;
        var screen = memory.LatestScreen;
        var candidates = CandidateEnumerator.Enumerate(screen, allowedActions, _processName, _opts.TopK);

        // Oscillatory brain scheduling: consult Qwen3 every N steps (step 0 grounded), OR whenever we have
        // no enumerated candidate to fall back on. Off-ticks with candidates are pure conductance — zero LLM.
        var brainTick = (step % _opts.BrainTickInterval) == 0;
        string? brainSig = null;
        var usedCloud = false;
        if (brainTick || candidates.Count == 0)
        {
            var inner = await _inner.ReasonNextAsync(objective, memory, allowedActions, allowCloud, ct).ConfigureAwait(false);
            usedCloud = inner.UsedCloud;

            // Done = objective met → honor (terminates the run + triggers the verified-skill harvest).
            if (inner.Action.Kind == NextActionKind.Done)
                return inner;

            // Escalate / cloud-denied = the brain can't or won't pick (e.g. the on-device LLM is unavailable
            // or unsure). In a SANDBOX EXPLORE run there is NO human to escalate to and the explorer's whole
            // job is to keep exploring — so when we have enumerated candidates, fall back to pure Physarum
            // selection (conductance + explore jitter, no endorsement) instead of ending the run. Only defer
            // to the brain's terminal when there is nothing to rank (truly stuck). This is what lets the legs
            // explore without the brain every tick — the moat's CPU-cheap, brain-intermittent design.
            if (inner.CloudDenied || inner.Action.Kind == NextActionKind.Escalate)
            {
                if (candidates.Count == 0)
                    return inner;
                // else fall through with brainSig == null → pure conductance exploration.
            }
            else
            {
                // The brain proposed an action: endorse it as a candidate, but only if it's a click verb
                // (v1 explore is click-only; the sandbox gate denies type/press, so emitting one would just
                // GateDeny-terminate).
                if (inner.Action.Kind == NextActionKind.Act && IsClickVerb(inner.Action.Verb))
                    brainSig = inner.Action.Signature();

                if (candidates.Count == 0)
                    return inner; // nothing to rank — defer to the brain's single decision
            }
        }

        var stateHash = screen?.ContentHash ?? string.Empty;
        var best = candidates[0];
        var bestScore = double.NegativeInfinity;
        for (var i = 0; i < candidates.Count; i++)
        {
            var c = candidates[i];
            var score = Score(objective, stateHash, c, brainSig, memory.History, step);
            if (score > bestScore)
            {
                bestScore = score;
                best = c;
            }
        }

        _logger.LogDebug(
            "physarum explore step={Step} brainTick={BrainTick} candidates={Count} picked={Sig} score={Score:F3}",
            step, brainTick, candidates.Count, best.ActionSig, bestScore);

        return new ReasonResult(NextAction.Act(best.Verb, best.Parameters), usedCloud);
    }

    // Verification is NEVER decorated — the execution-grounded verifier is the trust line; pass straight through.
    public Task<VerifyResult> VerifyPostconditionAsync(
        AgentObjective objective, NextAction lastAction, PerceivedScreen before, PerceivedScreen after,
        bool allowCloud, CancellationToken ct)
        => _inner.VerifyPostconditionAsync(objective, lastAction, before, after, allowCloud, ct);

    private double Score(
        AgentObjective objective, string stateHash, CandidateAction c, string? brainSig,
        IReadOnlyList<StepRecord> history, int step)
    {
        var conductance = _store.Get(new EdgeKey(objective.PharmacyId, objective.TaskKey, stateHash, c.ActionSig), _params.Floor);
        var endorsement = brainSig is not null && c.ActionSig == brainSig ? _opts.BrainEndorsementBonus : 0.0;

        var revisits = 0;
        if (history is not null)
            foreach (var h in history)
                if (h.DecisionScreenHash == stateHash && h.ActionSignature == c.ActionSig) revisits++;
        var trail = _opts.TrailPenalty * revisits;

        return conductance + endorsement - trail + Explore(c.ActionSig, step);
    }

    /// <summary>Deterministic, bounded [0, ExploreEpsilon) jitter that varies by (candidate, step) — breaks ties
    /// and induces exploration on flat-conductance screens without RNG (so runs are reproducible/resumable).</summary>
    private double Explore(string actionSig, int step)
    {
        // FNV-1a over the signature → a stable seed (string.GetHashCode is not stable across processes).
        uint h = 2166136261u;
        foreach (var ch in actionSig) { h ^= ch; h *= 16777619u; }
        var mixed = (h * 0.6180339887498949) + (step * 0.7548776662466927);
        var frac = mixed - System.Math.Floor(mixed);
        return _opts.ExploreEpsilon * frac;
    }

    private static bool IsClickVerb(string? verb)
        => verb is "click_by_label" or "click_by_signature";
}
