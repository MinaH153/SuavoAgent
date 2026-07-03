using SuavoAgent.Contracts.Worklists;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Wholesaler Invoice-vs-Received Reconciliation — the pure reconciler. Joins invoice lines against what
/// PioneerRx received and emits ONLY the exceptions worth recovering money on: a line never received, a
/// short shipment, or an over-charged unit cost. UI-free + IO-free (the reads are the reader's job), so
/// the money logic is unit-testable off the box.
///
/// <para>Money discipline (like the rest of the moat): a penny of recoverable credit is real dollars to
/// the pharmacy, so compute it exactly and NEVER invent a discrepancy — a clean line, or one whose
/// numbers agree within a cent, is not on the worklist. Sorted by recoverable dollars so staff work the
/// biggest credits first.</para>
/// </summary>
public static class InvoiceReconciler
{
    /// <summary>Tiny epsilon to ignore float noise on an otherwise-matching quantity/price.</summary>
    private const decimal Epsilon = 0.0001m;

    /// <summary>Flag an exception only when it's worth at least a cent of recoverable money — small
    /// per-unit deltas can still be real dollars in aggregate (a $0.004/unit overcharge on 1000 units is
    /// $4), so the threshold is on RECOVERABLE DOLLARS, not per-unit.</summary>
    private const decimal MinRecoverable = 0.01m;

    /// <summary>The reconciliation exceptions, largest recoverable credit first.</summary>
    public static IReadOnlyList<ReconLine> Reconcile(
        IEnumerable<InvoiceLine> invoice, IEnumerable<ReceivedLine> received)
    {
        // Index received by NDC (last one wins if duplicated — a merged receiving record).
        var recvByNdc = new Dictionary<string, ReceivedLine>(StringComparer.Ordinal);
        foreach (var r in received)
            if (!string.IsNullOrWhiteSpace(r.Ndc)) recvByNdc[r.Ndc] = r;

        var lines = new List<ReconLine>();
        foreach (var inv in invoice)
        {
            if (string.IsNullOrWhiteSpace(inv.Ndc)) continue;   // malformed invoice row — skip
            if (inv.QtyInvoiced <= 0) continue;                  // nothing billed — nothing to recover

            if (!recvByNdc.TryGetValue(inv.Ndc, out var recv))
            {
                // Invoiced but PioneerRx has no receiving record → the whole invoiced amount is at issue.
                lines.Add(new ReconLine(inv.Ndc, inv.Description, ReconFlags.NotReceived,
                    inv.QtyInvoiced, 0m, inv.UnitCostInvoiced, 0m,
                    Round(inv.QtyInvoiced * inv.UnitCostInvoiced)));
                continue;
            }

            // A line can be BOTH short-shipped AND overcharged — recover both or the pharmacy leaves money
            // on the table (and the row mis-ranks). Short: recover the shorted qty at the invoiced price.
            var shortQty = inv.QtyInvoiced - recv.QtyReceived;
            var shortDollars = shortQty > Epsilon ? Round(shortQty * inv.UnitCostInvoiced) : 0m;

            // Overcharge: invoiced unit cost above the expected/contract cost, on the qty that arrived.
            // Flag on the aggregate recoverable, not the per-unit size — small per-unit × large qty is real.
            var overPerUnit = inv.UnitCostInvoiced - recv.UnitCostExpected;
            var billedQty = Math.Min(inv.QtyInvoiced, recv.QtyReceived);
            var overDollars = recv.UnitCostExpected > 0m && overPerUnit > Epsilon
                ? Round(overPerUnit * billedQty) : 0m;

            var recoverable = shortDollars + overDollars;
            if (recoverable >= MinRecoverable)
            {
                var flag = shortDollars > 0m && overDollars > 0m ? ReconFlags.ShortShip + "+" + ReconFlags.Overcharge
                    : shortDollars > 0m ? ReconFlags.ShortShip
                    : ReconFlags.Overcharge;
                lines.Add(new ReconLine(inv.Ndc, inv.Description, flag,
                    inv.QtyInvoiced, recv.QtyReceived, inv.UnitCostInvoiced, recv.UnitCostExpected, recoverable));
            }
            // else: quantities + price agree → clean, not on the worklist.
        }

        return lines
            .OrderByDescending(l => l.RecoverableDollars)
            .ThenBy(l => l.Ndc, StringComparer.Ordinal)
            .ToList();
    }

    private static decimal Round(decimal d) => Math.Round(d, 2, MidpointRounding.AwayFromZero);
}
