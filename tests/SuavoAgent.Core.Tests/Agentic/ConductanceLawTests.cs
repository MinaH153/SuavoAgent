using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the Physarum reinforcement law: verified work strengthens an edge (superlinearly), unused/
/// unverified edges evaporate, and the result stays within [Floor, Ceiling]. The verified-only rule
/// ("never reward unverified work") is enforced here so the slime-mold dynamics can't be gamed.
/// </summary>
public class ConductanceLawTests
{
    private static readonly ConductanceParams P = ConductanceParams.Default;

    [Fact]
    public void VerifiedFlux_StrengthensAbovePureDecay()
    {
        const double d = 0.5;
        Assert.True(ConductanceLaw.Step(d, verifiedFlux: 1, semanticScore: 0, P) > ConductanceLaw.Decay(d, P));
    }

    [Fact]
    public void Reinforcement_IsSuperlinearInFlux()
    {
        const double d = 0.5;
        var baseline = ConductanceLaw.Decay(d, P);
        var addFrom1 = ConductanceLaw.Step(d, 1, 0, P) - baseline;
        var addFrom2 = ConductanceLaw.Step(d, 2, 0, P) - baseline;
        // φ(Q)=Q^μ, μ>1 ⇒ doubling the flux MORE than doubles the reinforcement.
        Assert.True(addFrom2 > 2 * addFrom1);
    }

    [Fact]
    public void UnverifiedOrUnused_OnlyDecays_NeverRewards()
    {
        const double d = 0.6;
        // flux=0 (unverified/failed) must not increase the edge — it can only decay.
        Assert.Equal(ConductanceLaw.Decay(d, P), ConductanceLaw.Step(d, 0, 0, P));
        Assert.True(ConductanceLaw.Step(d, 0, 0, P) < d);
    }

    [Fact]
    public void RepeatedDecay_ApproachesFloor_NeverBelow()
    {
        var d = 0.9;
        for (var i = 0; i < 500; i++) d = ConductanceLaw.Decay(d, P);
        Assert.Equal(P.Floor, d, precision: 6);     // settles AT the floor
        Assert.True(d >= P.Floor);                   // never below
    }

    [Fact]
    public void HugeFlux_ClampsAtCeiling()
    {
        Assert.Equal(P.Ceiling, ConductanceLaw.Step(P.Ceiling, verifiedFlux: 1000, semanticScore: 1, P), precision: 6);
    }
}
