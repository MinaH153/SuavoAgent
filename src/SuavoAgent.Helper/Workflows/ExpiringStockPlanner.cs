using SuavoAgent.Contracts.Worklists;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Short-Dated / Expiring Stock Worklist — the pure planner. From the on-hand lots, surface what is
/// already EXPIRED (pull it) and what is SHORT_DATED (within the alert window — return for credit while
/// still eligible), ranked by dollars at risk so staff act on the biggest exposure first. UI/IO-free →
/// unit-testable off the box.
///
/// <para>Discipline: skip malformed / zero-qty lots (nothing at risk), compute dollars exactly, and mark
/// return-eligibility by the wholesaler's minimum-remaining-dating rule (a lot too close to expiry is a
/// write-off, not a return). Deterministic ordering (dollars desc, then soonest expiry) so the sheet is
/// stable.</para>
/// </summary>
public static class ExpiringStockPlanner
{
    /// <param name="asOf">"today" for the run (passed in so the planner is deterministic + testable).</param>
    /// <param name="alertWindowDays">Flag lots expiring within this many days (e.g. 180).</param>
    /// <param name="minReturnDatingDays">Wholesaler minimum remaining shelf life for a credit-return
    /// (e.g. 90–180). A short-dated lot with fewer days left than this is flagged but NOT return-eligible.</param>
    public static IReadOnlyList<ExpiringLine> Plan(
        IEnumerable<InventoryLot> lots, DateOnly asOf, int alertWindowDays, int minReturnDatingDays)
    {
        var lines = new List<ExpiringLine>();
        foreach (var lot in lots)
        {
            if (string.IsNullOrWhiteSpace(lot.Ndc)) continue;    // malformed — skip
            if (lot.QtyOnHand <= 0) continue;                     // nothing on hand — nothing at risk

            var days = lot.Expiration.DayNumber - asOf.DayNumber; // negative = already expired
            var dollars = Round(lot.QtyOnHand * lot.UnitCost);

            if (days < 0)
            {
                // Already expired — pull it. Never "return-eligible".
                lines.Add(new ExpiringLine(lot.Ndc, lot.Description, lot.Lot, lot.Expiration, days,
                    lot.QtyOnHand, dollars, ExpiringFlags.Expired, ReturnEligible: false));
            }
            else if (days <= alertWindowDays)
            {
                // Within the alert window — return for credit only if enough dating remains for the WS rule.
                lines.Add(new ExpiringLine(lot.Ndc, lot.Description, lot.Lot, lot.Expiration, days,
                    lot.QtyOnHand, dollars, ExpiringFlags.ShortDated, ReturnEligible: days >= minReturnDatingDays));
            }
            // else: plenty of shelf life — not on the worklist.
        }

        return lines
            .OrderByDescending(l => l.DollarsAtRisk)
            .ThenBy(l => l.DaysToExpiry)
            .ThenBy(l => l.Ndc, StringComparer.Ordinal)
            .ToList();
    }

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
}
