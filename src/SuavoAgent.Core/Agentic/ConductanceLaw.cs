using System;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Tunable parameters for the Physarum/ACO edge-reinforcement law. Defaults are starting guesses to
/// calibrate on real telemetry (TSP-benchmark values do NOT transfer); Floor/Ceiling mirror the
/// existing <c>FeedbackEvent.ConfidenceFloor</c>/<c>ConfidenceCeiling</c> for consistency.
/// </summary>
public readonly record struct ConductanceParams(
    double Alpha = 0.15,    // reinforcement gain on verified flux
    double Gamma = 0.05,    // semantic-score weight (the LLM as a SCORER, not the controller)
    double Beta = 0.03,     // evaporation rate (always-on decay-of-the-unused)
    double Mu = 1.3,        // flux exponent (>1 ⇒ superlinear, sharpens the winning edge)
    double Floor = 0.1,     // revival floor (a faded edge stays revivable)
    double Ceiling = 0.95)  // monopoly ceiling (no edge dominates a decision point absolutely)
{
    // NOTE: must construct explicitly — `new ConductanceParams()` invokes the implicit zero-init
    // struct ctor (NOT the primary ctor's defaults), which would zero Ceiling and clamp every edge to 0.
    public static readonly ConductanceParams Default = new(
        Alpha: 0.15, Gamma: 0.05, Beta: 0.03, Mu: 1.3, Floor: 0.1, Ceiling: 0.95);
}

/// <summary>
/// The Physarum / ant-colony tube-reinforcement law for SuavoAgent's (state→action) edge conductance —
/// the slime-mold dynamics that strengthen VERIFIED paths and let unused ones evaporate. Pure +
/// side-effect-free (mirrors <see cref="Adapters.PostconditionEvaluator"/> / <c>NavigateSafety</c> so
/// it is trivially unit-testable).
///
/// <para>
/// Discretized Tero–Nakagaki / ACO: <c>D_next = D·(1−β) + α·φ(Q) + γ·S</c>, clamped to [Floor, Ceiling].
/// <list type="bullet">
/// <item><c>Q</c> = VERIFIED success flux this round (0 for an unverified or failed step). Phase-1's
/// <see cref="Adapters.PostconditionEvaluator"/> produces the verdict; the law NEVER reinforces
/// unverified work — "the number shown to the client must be REAL."</item>
/// <item><c>φ(Q) = Q^μ</c> with μ&gt;1 ⇒ superlinear reinforcement; the winning edge sharpens instead
/// of many mediocre edges lingering.</item>
/// <item><c>S</c> = the small on-device model's semantic score for the edge (LLM = scorer, not driver).</item>
/// <item><c>β</c> = evaporation: every edge decays toward Floor each round, so a stale/failing path
/// fades and its replacement overtakes it. This is the always-on decay-of-the-unused, and it
/// auto-handles PioneerRx UI drift. Floor&gt;0 keeps a faded edge revivable.</item>
/// </list>
/// </para>
///
/// <para>
/// This governs ONLY frontier EXPLORATION edge weights. Production execution of a KNOWN verified
/// template is DETERMINISTIC replay (no conductance, no stochasticity) — the cache, not the legs.
/// </para>
/// </summary>
public static class ConductanceLaw
{
    /// <summary>
    /// One discrete update of an edge's conductance. <paramref name="verifiedFlux"/> MUST be the
    /// magnitude of EXECUTION-VERIFIED successes this round — pass 0 for an unverified or failed step
    /// (the law never rewards unverified work). An unused edge calls this with flux=0, score=0 ⇒ pure
    /// evaporation. Result is always clamped to [Floor, Ceiling].
    /// </summary>
    public static double Step(double conductance, double verifiedFlux, double semanticScore, ConductanceParams p)
    {
        var flux = verifiedFlux <= 0.0 ? 0.0 : Math.Pow(verifiedFlux, p.Mu);
        var next = conductance * (1.0 - p.Beta) + p.Alpha * flux + p.Gamma * Math.Max(0.0, semanticScore);
        return Math.Clamp(next, p.Floor, p.Ceiling);
    }

    /// <summary>Pure evaporation for an edge not used this round (flux=0, score=0).</summary>
    public static double Decay(double conductance, ConductanceParams p) => Step(conductance, 0.0, 0.0, p);
}
