using System;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

public class SqlVolumeQueryBuilderTests
{
    private static DiscoveredPricingSchema SchemaWithItemJoin() => new(
        CatalogSchema: "Inventory",
        CatalogTable: "ItemPricing",
        CostColumn: "Cost",
        CostPerUnitColumn: "CostPerUnit",
        NdcColumn: null,
        ItemJoin: new CatalogItemJoin(
            ItemTableSchema: "Inventory",
            ItemTable: "Item",
            ItemIdColumnInCatalog: "ItemID",
            ItemIdColumnInItem: "ItemID",
            NdcColumnInItem: "NDC"),
        SupplierSource: new CatalogSupplierSource(
            SupplierResolution.Denormalized, "SupplierName", null, null, null, null, null, null),
        StatusColumn: null,
        AvailableStatusValues: Array.Empty<string>(),
        ConfidenceScore: 1.0,
        DiagnosticNotes: Array.Empty<string>());

    private static readonly string[] Dispensed = { "Sold", "Completed" };

    [Fact]
    public void BuildDispensedQuantityQuery_AggregatesViaRxTransactionItemJoinOnNdcWindowAndStatus()
    {
        var sql = SqlVolumeQueryBuilder.BuildDispensedQuantityQuery(SchemaWithItemJoin(), Dispensed);

        Assert.NotNull(sql);
        Assert.Contains("SUM(rt.[DispensedQuantity])", sql);
        Assert.Contains("[Prescription].[RxTransaction] AS rt", sql);
        Assert.Contains("INNER JOIN [Inventory].[Item] AS it ON rt.[DispensedItemID] = it.[ItemID]", sql);
        Assert.Contains("it.[NDC] = @ndc", sql);
        Assert.Contains("rt.[DateFilled] >= @windowStart", sql);
        // Void filter (Codex blocker): only final dispensed statuses are summed.
        Assert.Contains("INNER JOIN [Prescription].[RxTransactionStatusType] AS st", sql);
        Assert.Contains("st.[Description] IN (@vstatus0, @vstatus1)", sql);
        // PHI-negative: aggregate only — no per-Rx / per-patient row selected.
        Assert.DoesNotContain("Patient", sql, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BuildDispensedQuantityQuery_NoItemJoin_ReturnsNull()
    {
        var schema = SchemaWithItemJoin() with { ItemJoin = null, NdcColumn = "NDC" };
        Assert.Null(SqlVolumeQueryBuilder.BuildDispensedQuantityQuery(schema, Dispensed));
    }

    [Fact]
    public void BuildDispensedQuantityQuery_NoDispensedStatuses_ReturnsNull()
    {
        // Fail-safe: without a verified dispensed-status filter we must NOT sum (would count voids).
        Assert.Null(SqlVolumeQueryBuilder.BuildDispensedQuantityQuery(SchemaWithItemJoin(), Array.Empty<string>()));
    }
}
