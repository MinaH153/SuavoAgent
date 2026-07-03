using System.Linq;
using SuavoAgent.Contracts.Worklists;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// Below-Reorder Purchase Worklist — the planner's buying rules: flag only items at/under reorder point
/// with nothing on order, size the buy to reach the point (respecting the min reorder qty), and never
/// emit a bad line. Pure logic, box-free.
/// </summary>
public class ReorderPlannerTests
{
    private static ReorderGridRow Row(string ndc, decimal boh, decimal rop, decimal roq, decimal onOrder, string item = "Drug") =>
        new(item, ndc, boh, rop, roq, onOrder);

    [Fact]
    public void Flags_only_items_at_or_below_reorder_point_with_nothing_on_order()
    {
        var rows = new[]
        {
            Row("00001-0001-01", boh: 5,  rop: 20, roq: 30, onOrder: 0),   // below, none on order -> FLAG
            Row("00002-0002-01", boh: 20, rop: 20, roq: 30, onOrder: 0),   // AT reorder point -> FLAG
            Row("00003-0003-01", boh: 50, rop: 20, roq: 30, onOrder: 0),   // above -> skip
            Row("00004-0004-01", boh: 2,  rop: 20, roq: 30, onOrder: 40),  // below but already inbound -> skip
            Row("",              boh: 0,  rop: 20, roq: 30, onOrder: 0),    // malformed -> skip
        };

        var lines = ReorderPlanner.PlanReorders(rows);

        Assert.Equal(2, lines.Count);
        Assert.Contains(lines, l => l.Ndc == "00001-0001-01");
        Assert.Contains(lines, l => l.Ndc == "00002-0002-01");
        Assert.DoesNotContain(lines, l => l.Ndc == "00003-0003-01");   // above ROP
        Assert.DoesNotContain(lines, l => l.Ndc == "00004-0004-01");   // on order
    }

    [Fact]
    public void Suggested_qty_reaches_the_reorder_point_but_at_least_the_reorder_qty()
    {
        // Shortfall 15 (ROP 20 - BOH 5) but the item's min reorder qty is 30 -> buy 30 (honor pack size).
        var big = ReorderPlanner.PlanReorders(new[] { Row("00001-0001-01", boh: 5, rop: 20, roq: 30, onOrder: 0) });
        Assert.Equal(30m, big.Single().SuggestedQty);

        // Shortfall 40 (ROP 50 - BOH 10) exceeds the min reorder qty 12 -> buy the shortfall, 40.
        var deep = ReorderPlanner.PlanReorders(new[] { Row("00002-0002-01", boh: 10, rop: 50, roq: 12, onOrder: 0) });
        Assert.Equal(40m, deep.Single().SuggestedQty);
    }

    [Fact]
    public void Orders_deepest_shortfall_first()
    {
        var lines = ReorderPlanner.PlanReorders(new[]
        {
            Row("00001-0001-01", boh: 18, rop: 20, roq: 10, onOrder: 0),   // shortfall 2
            Row("00002-0002-01", boh: 1,  rop: 40, roq: 10, onOrder: 0),   // shortfall 39 -> first
            Row("00003-0003-01", boh: 15, rop: 25, roq: 10, onOrder: 0),   // shortfall 10
        });

        Assert.Equal(new[] { "00002-0002-01", "00003-0003-01", "00001-0001-01" }, lines.Select(l => l.Ndc).ToArray());
    }
}
