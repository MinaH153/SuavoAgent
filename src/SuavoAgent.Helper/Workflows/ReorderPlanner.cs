using SuavoAgent.Contracts.Worklists;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Below-Reorder Purchase Worklist — the pure planner. Given the inventory grid rows, decide which items
/// need reordering and how much to buy. UI-free + IO-free (like ProfitOptimizer / SelectCheapest) so the
/// pharmacist logic is unit-testable off the box; the live grid read is the reader's job.
///
/// <para>The rule (a buyer's daily judgment): flag an item when its balance-on-hand has fallen to or
/// below its reorder point AND there is nothing already on order (don't double-order what's inbound).
/// Suggested quantity brings stock back to at least the reorder point, but never below the item's own
/// reorder quantity (the pack/case multiple the buyer set) — so we honor min-order sizing.</para>
///
/// <para>Same discipline as the rest of the moat: skip malformed rows (blank NDC), never emit a negative
/// or zero buy, and produce nothing rather than a wrong line — a bad reorder ties up cash or stocks out.</para>
/// </summary>
public static class ReorderPlanner
{
    /// <summary>The items that need reordering, most-below-reorder-point first (the buyer works the
    /// deepest shortfalls first). Rows already on order or above their reorder point are excluded.</summary>
    public static IReadOnlyList<ReorderLine> PlanReorders(IEnumerable<ReorderGridRow> rows)
    {
        var lines = new List<(ReorderLine line, decimal shortfall)>();
        foreach (var r in rows)
        {
            if (string.IsNullOrWhiteSpace(r.Ndc)) continue;      // malformed row — skip
            if (r.OnOrder > 0) continue;                          // already inbound — don't double-order
            if (r.Boh > r.ReorderPoint) continue;                // above reorder point — fine

            var suggested = SuggestedQty(r);
            if (suggested <= 0) continue;                         // nothing sensible to buy — fail closed

            lines.Add((
                new ReorderLine(r.Item, r.Ndc, r.Boh, r.ReorderPoint, suggested,
                    BestSupplier: null, BestCostPerUnit: r.CostPerUnit),
                shortfall: r.ReorderPoint - r.Boh));
        }

        // Deepest shortfall first; stable tie-break on NDC so the worklist is deterministic.
        return lines
            .OrderByDescending(x => x.shortfall)
            .ThenBy(x => x.line.Ndc, StringComparer.Ordinal)
            .Select(x => x.line)
            .ToList();
    }

    /// <summary>Buy enough to reach the reorder point, but at least the item's reorder quantity (its
    /// min/pack multiple). A reorder point with no explicit reorder qty falls back to the shortfall.</summary>
    private static decimal SuggestedQty(ReorderGridRow r)
    {
        var shortfall = r.ReorderPoint - r.Boh;                  // > 0 here (Boh <= ReorderPoint)
        var toPoint = shortfall > 0 ? shortfall : 0m;
        return Math.Max(r.ReorderQty, toPoint);
    }
}
