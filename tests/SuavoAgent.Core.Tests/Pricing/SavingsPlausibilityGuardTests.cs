using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

public class SavingsPlausibilityGuardTests
{
    private const decimal MaxCost = 1_000_000m;
    private const decimal MaxQty = 100_000_000m;
    private const decimal Suspicious = 0.9m;

    private static SavingsPlausibilityGuard.Result Eval(decimal? b, decimal? s, decimal? q) =>
        SavingsPlausibilityGuard.Evaluate(b, s, q, MaxCost, MaxQty, Suspicious);

    [Fact]
    public void Plausible_values_pass_through_unflagged()
    {
        var r = Eval(0.05m, 0.0316m, 1200m);
        Assert.Equal(0.05m, r.Baseline);
        Assert.Equal(1200m, r.Quantity);
        Assert.Null(r.RejectReason);
        Assert.Null(r.ReviewFlag);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(2_000_000)] // > maxUnitCost — magnitude blunder (wrong column / unit)
    public void Impossible_baseline_is_dropped(decimal baseline)
    {
        var r = Eval(baseline, 0.03m, 1000m);
        Assert.Null(r.Baseline);
        Assert.NotNull(r.RejectReason);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(200_000_000)] // > maxQuantity
    public void Impossible_quantity_is_dropped(decimal qty)
    {
        var r = Eval(0.05m, 0.03m, qty);
        Assert.Null(r.Quantity);
        Assert.NotNull(r.RejectReason);
    }

    [Fact]
    public void Very_large_savings_is_kept_but_flagged_for_review()
    {
        // baseline 1.00, sourced 0.02 -> ~98% savings: a real generic switch CAN save this much,
        // so don't suppress it — surface it for a human to verify the column/units.
        var r = Eval(1.00m, 0.02m, 500m);
        Assert.Equal(1.00m, r.Baseline);
        Assert.Equal(500m, r.Quantity);
        Assert.Null(r.RejectReason);
        Assert.NotNull(r.ReviewFlag);
    }

    [Fact]
    public void Nulls_pass_through_untouched()
    {
        var r = Eval(null, 0.03m, null);
        Assert.Null(r.Baseline);
        Assert.Null(r.Quantity);
        Assert.Null(r.RejectReason);
        Assert.Null(r.ReviewFlag);
    }
}
