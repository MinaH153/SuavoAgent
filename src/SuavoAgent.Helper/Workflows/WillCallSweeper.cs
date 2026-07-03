using SuavoAgent.Contracts.Worklists;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Will-Call Return-to-Stock Sweep — the pure sweeper. From the will-call/pickup queue, flag filled Rxs
/// sitting UNCLAIMED longer than the board's limit and emit a return-to-stock pick list (oldest first).
/// UI/IO-free → unit-testable off the box. LOCAL-ONLY output (the pick list never leaves the box), minimum
/// identifiers only (Rx number + drug), never patient PHI.
///
/// <para>Discipline: a boundary is a boundary — an Rx sitting EXACTLY the limit is still within it (only
/// strictly-past goes to RTS), so a compliant Rx is never pulled early. Skip malformed rows; order oldest
/// first so the most-overdue returns are worked first.</para>
/// </summary>
public static class WillCallSweeper
{
    /// <param name="asOf">"today" for the run (passed in for deterministic tests).</param>
    /// <param name="limitDays">The board's / pharmacy's unclaimed limit (e.g. 14). Rxs unclaimed MORE than
    /// this many days are returned to stock.</param>
    public static IReadOnlyList<ReturnToStockLine> Sweep(
        IEnumerable<WillCallEntry> queue, DateOnly asOf, int limitDays)
    {
        var lines = new List<ReturnToStockLine>();
        foreach (var e in queue)
        {
            // Malformed → SKIP, never pull. A blank/unparseable fill date must NOT fail open: a default
            // date (0001-01-01) would compute as ~739,000 days overdue and yank a possibly-just-filled Rx
            // off the shelf. Skip it (and any future-dated fill) rather than return a live Rx to stock.
            if (string.IsNullOrWhiteSpace(e.RxNumber) || e.FilledDate == default) continue;
            var days = asOf.DayNumber - e.FilledDate.DayNumber;
            if (days <= limitDays) continue;                             // still within the limit — leave it
            lines.Add(new ReturnToStockLine(e.RxNumber, e.Drug, e.Ndc, e.FilledDate, days, e.Qty));
        }

        return lines
            .OrderByDescending(l => l.DaysUnclaimed)                     // most overdue first
            .ThenBy(l => l.RxNumber, StringComparer.Ordinal)
            .ToList();
    }
}
