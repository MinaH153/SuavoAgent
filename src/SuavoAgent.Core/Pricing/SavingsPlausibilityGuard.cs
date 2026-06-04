namespace SuavoAgent.Core.Pricing;

/// <summary>
/// Makes the agent intelligent about the savings numbers it is about to emit — the last line of
/// "the number must be real." Catches the failure modes a per-pharmacy data layout invites:
/// a baseline/quantity that is impossible (≤0, NaN, absurd magnitude from a wrong column or a unit
/// mismatch) is DROPPED so no garbage savings can form; a savings that is merely very large (a
/// possible-but-suspicious win) is still emitted but FLAGGED for human review.
/// </summary>
public static class SavingsPlausibilityGuard
{
    public sealed record Result(decimal? Baseline, decimal? Quantity, string? RejectReason, string? ReviewFlag);

    /// <summary>
    /// Sanitize a candidate (baseline, quantity) pair against the sourced cost. Out-of-range values
    /// are nulled (with a reason); a high savings fraction is surfaced as a review flag but kept.
    /// </summary>
    /// <param name="baseline">Pharmacy's current per-unit cost (may be null).</param>
    /// <param name="sourced">Cheapest sourced per-unit cost (for the savings-fraction sanity check).</param>
    /// <param name="quantity">Aggregate dispensed quantity (may be null).</param>
    /// <param name="maxUnitCost">Reject a baseline at or above this (data error). Drugs can be costly
    /// — keep generous; this only catches magnitude blunders.</param>
    /// <param name="maxQuantity">Reject a quantity at or above this (data error).</param>
    /// <param name="suspiciousSavingsFraction">Flag (don't reject) when (baseline−sourced)/baseline
    /// is at or above this — a real generic switch CAN save this much, so a human verifies the
    /// column/units rather than the agent silently suppressing a genuine win.</param>
    public static Result Evaluate(
        decimal? baseline,
        decimal? sourced,
        decimal? quantity,
        decimal maxUnitCost,
        decimal maxQuantity,
        decimal suspiciousSavingsFraction)
    {
        decimal? b = baseline;
        decimal? q = quantity;
        string? reject = null;

        if (b is not null && (b <= 0 || b > maxUnitCost))
        {
            reject = $"baseline {b} outside (0, {maxUnitCost}]";
            b = null;
        }
        if (q is not null && (q <= 0 || q > maxQuantity))
        {
            reject = reject is null
                ? $"quantity {q} outside (0, {maxQuantity}]"
                : reject + $"; quantity {q} outside (0, {maxQuantity}]";
            q = null;
        }

        string? review = null;
        if (b is { } bb && sourced is { } s && s > 0 && bb > s)
        {
            var fraction = (bb - s) / bb;
            if (fraction >= suspiciousSavingsFraction)
                review = $"implied savings {fraction:P0} ≥ {suspiciousSavingsFraction:P0} — verify baseline column / units";
        }

        return new Result(b, q, reject, review);
    }
}
