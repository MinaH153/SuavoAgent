using System;
using System.Linq;
using SuavoAgent.Contracts.Worklists;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>Pure pharmacist-logic for the inventory/dispensing worklist features — invoice recon,
/// short-dated stock, will-call return-to-stock. Money/inventory correctness, box-free.</summary>
public class WorklistPlannersTests
{
    // ---- Invoice reconciliation ----
    [Fact]
    public void Invoice_flags_short_ship_overcharge_and_not_received_only()
    {
        var invoice = new[]
        {
            new InvoiceLine("A", "Drug A", QtyInvoiced: 100, UnitCostInvoiced: 2.00m),  // received 100 @ 2.00 -> clean
            new InvoiceLine("B", "Drug B", QtyInvoiced: 100, UnitCostInvoiced: 2.00m),  // received 90 -> SHORT 10 * 2.00 = 20
            new InvoiceLine("C", "Drug C", QtyInvoiced: 50,  UnitCostInvoiced: 3.00m),  // expected 2.50 -> OVER 0.50 * 50 = 25
            new InvoiceLine("D", "Drug D", QtyInvoiced: 40,  UnitCostInvoiced: 1.00m),  // never received -> 40 * 1.00 = 40
        };
        var received = new[]
        {
            new ReceivedLine("A", QtyReceived: 100, UnitCostExpected: 2.00m),
            new ReceivedLine("B", QtyReceived: 90,  UnitCostExpected: 2.00m),
            new ReceivedLine("C", QtyReceived: 50,  UnitCostExpected: 2.50m),
        };

        var lines = InvoiceReconciler.Reconcile(invoice, received);

        Assert.Equal(3, lines.Count);                                  // A (clean) excluded
        Assert.DoesNotContain(lines, l => l.Ndc == "A");
        // sorted by recoverable dollars desc: D(40), C(25), B(20)
        Assert.Equal(new[] { "D", "C", "B" }, lines.Select(l => l.Ndc).ToArray());
        Assert.Equal(ReconFlags.NotReceived, lines[0].Flag);
        Assert.Equal(40m, lines[0].RecoverableDollars);
        Assert.Equal(ReconFlags.Overcharge, lines[1].Flag);
        Assert.Equal(25m, lines[1].RecoverableDollars);
        Assert.Equal(ReconFlags.ShortShip, lines[2].Flag);
        Assert.Equal(20m, lines[2].RecoverableDollars);
    }

    [Fact]
    public void Invoice_flags_a_small_per_unit_overcharge_that_is_real_dollars_in_aggregate()
    {
        // 0.004/unit over on 1000 units = $4.00 recoverable — must flag despite the tiny per-unit delta.
        var lines = InvoiceReconciler.Reconcile(
            new[] { new InvoiceLine("Z", "Cheap generic", QtyInvoiced: 1000, UnitCostInvoiced: 0.032m) },
            new[] { new ReceivedLine("Z", QtyReceived: 1000, UnitCostExpected: 0.028m) });
        var line = Assert.Single(lines);
        Assert.Equal(ReconFlags.Overcharge, line.Flag);
        Assert.Equal(4.00m, line.RecoverableDollars);
    }

    [Fact]
    public void Invoice_recovers_both_short_and_overcharge_on_the_same_line()
    {
        // Received 80 of 100 (short 20 @ $12 = $240) AND overcharged $3/unit on the 80 that arrived ($240).
        // True recoverable is $480 — not just the short.
        var lines = InvoiceReconciler.Reconcile(
            new[] { new InvoiceLine("Q", "Both", QtyInvoiced: 100, UnitCostInvoiced: 12.00m) },
            new[] { new ReceivedLine("Q", QtyReceived: 80, UnitCostExpected: 9.00m) });
        var line = Assert.Single(lines);
        Assert.Equal(480m, line.RecoverableDollars);
        Assert.Contains(ReconFlags.ShortShip, line.Flag);
        Assert.Contains(ReconFlags.Overcharge, line.Flag);
    }

    // ---- Short-dated / expiring ----
    [Fact]
    public void Expiring_flags_expired_and_shortdated_ranked_by_dollars_and_marks_return_eligibility()
    {
        var asOf = new DateOnly(2026, 7, 3);
        var lots = new[]
        {
            new InventoryLot("A", "Drug A", "L1", asOf.AddDays(-5),  QtyOnHand: 10, UnitCost: 4.00m),  // EXPIRED, $40
            new InventoryLot("B", "Drug B", "L2", asOf.AddDays(30),  QtyOnHand: 100, UnitCost: 1.00m), // short, $100, days 30 < 90 -> not returnable
            new InventoryLot("C", "Drug C", "L3", asOf.AddDays(150), QtyOnHand: 20, UnitCost: 2.00m),  // short, $40, days 150 >= 90 -> returnable
            new InventoryLot("D", "Drug D", "L4", asOf.AddDays(400), QtyOnHand: 5,  UnitCost: 9.00m),  // far out -> not on list
        };

        var lines = ExpiringStockPlanner.Plan(lots, asOf, alertWindowDays: 180, minReturnDatingDays: 90);

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, l => l.Ndc == "D");
        // dollars desc: B($100), A($40 expired), C($40) — A before C on tie by soonest expiry (A negative days)
        Assert.Equal("B", lines[0].Ndc);
        Assert.Equal(100m, lines[0].DollarsAtRisk);
        Assert.False(lines[0].ReturnEligible);                        // only 30 days left
        var c = lines.Single(l => l.Ndc == "C");
        Assert.Equal(ExpiringFlags.ShortDated, c.Flag);
        Assert.True(c.ReturnEligible);                                // 150 days >= 90
        var a = lines.Single(l => l.Ndc == "A");
        Assert.Equal(ExpiringFlags.Expired, a.Flag);
        Assert.False(a.ReturnEligible);
    }

    // ---- Will-call return-to-stock ----
    [Fact]
    public void WillCall_returns_only_rxs_strictly_past_the_limit_oldest_first()
    {
        var asOf = new DateOnly(2026, 7, 3);
        var queue = new[]
        {
            new WillCallEntry("RX100", "Drug A", "A", asOf.AddDays(-14), Qty: 30), // exactly 14 -> within limit, keep
            new WillCallEntry("RX200", "Drug B", "B", asOf.AddDays(-15), Qty: 60), // 15 -> RTS
            new WillCallEntry("RX300", "Drug C", "C", asOf.AddDays(-40), Qty: 90), // 40 -> RTS (oldest)
            new WillCallEntry("RX400", "Drug D", "D", asOf.AddDays(-3),  Qty: 15), // recent -> keep
        };

        var rts = WillCallSweeper.Sweep(queue, asOf, limitDays: 14);

        Assert.Equal(new[] { "RX300", "RX200" }, rts.Select(l => l.RxNumber).ToArray()); // oldest first
        Assert.Equal(40, rts[0].DaysUnclaimed);
        Assert.DoesNotContain(rts, l => l.RxNumber == "RX100");        // exactly the limit — not pulled early
    }

    [Fact]
    public void WillCall_skips_a_bad_fill_date_instead_of_pulling_a_live_rx()
    {
        var asOf = new DateOnly(2026, 7, 3);
        var rts = WillCallSweeper.Sweep(new[]
        {
            new WillCallEntry("RX500", "Drug", "N", default, Qty: 30),          // blank/default date -> SKIP (not maximally overdue)
            new WillCallEntry("RX600", "Drug", "N", asOf.AddDays(-20), Qty: 30), // real overdue -> RTS
        }, asOf, limitDays: 14);

        var line = Assert.Single(rts);
        Assert.Equal("RX600", line.RxNumber);                          // the default-date row is not pulled
    }
}
