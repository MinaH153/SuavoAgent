using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

public class SqlPricingQueryBuilderTests
{
    [Fact]
    public void Build_DenormalizedSupplier_WithNdcDirect()
    {
        var schema = new DiscoveredPricingSchema(
            CatalogSchema: "Inventory",
            CatalogTable: "ItemPricing",
            CostColumn: "Cost",
            CostPerUnitColumn: "CostPerUnit",
            NdcColumn: "NDC",
            ItemJoin: null,
            SupplierSource: new CatalogSupplierSource(
                SupplierResolution.Denormalized,
                NameColumnInCatalog: "SupplierName",
                AccountColumnInCatalog: null,
                SupplierTableSchema: null,
                SupplierTable: null,
                SupplierIdColumnInCatalog: null,
                SupplierIdColumnInSupplier: null,
                SupplierNameColumnInSupplier: null),
            StatusColumn: "Status",
            AvailableStatusValues: new[] { "Available", "Active" },
            ConfidenceScore: 0.9,
            DiagnosticNotes: Array.Empty<string>());

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("SELECT TOP 1", sql);
        Assert.Contains("p.[SupplierName] AS SupplierName", sql);
        Assert.Contains("p.[Cost] AS Cost", sql);
        Assert.Contains("p.[CostPerUnit] AS CostPerUnit", sql);
        Assert.Contains("p.[Status] AS CatalogStatus", sql);
        Assert.Contains("FROM [Inventory].[ItemPricing] AS p", sql);
        Assert.DoesNotContain("JOIN", sql); // no item join, no supplier join
        Assert.Contains("WHERE p.[NDC] = @ndc", sql);
        Assert.Contains("p.[Status] IN ('Available', 'Active')", sql);
        // CostPerUnitColumn is declared → rank by per-unit (the savings-ledger quantity), not pack cost.
        Assert.Contains("p.[CostPerUnit] > 0", sql);
        Assert.Contains("ORDER BY p.[CostPerUnit] ASC", sql);
        Assert.DoesNotContain("ORDER BY p.[Cost] ASC", sql);
    }

    [Fact]
    public void Build_JoinedSupplier_WithItemJoinForNdc()
    {
        var schema = new DiscoveredPricingSchema(
            CatalogSchema: "Inventory",
            CatalogTable: "ItemSupplier",
            CostColumn: "Cost",
            CostPerUnitColumn: null,
            NdcColumn: null,
            ItemJoin: new CatalogItemJoin(
                ItemTableSchema: "Inventory",
                ItemTable: "Item",
                ItemIdColumnInCatalog: "ItemID",
                ItemIdColumnInItem: "ItemID",
                NdcColumnInItem: "NDC"),
            SupplierSource: new CatalogSupplierSource(
                SupplierResolution.JoinedSupplierTable,
                NameColumnInCatalog: null,
                AccountColumnInCatalog: null,
                SupplierTableSchema: "Inventory",
                SupplierTable: "Supplier",
                SupplierIdColumnInCatalog: "SupplierID",
                SupplierIdColumnInSupplier: "SupplierID",
                SupplierNameColumnInSupplier: "SupplierName"),
            StatusColumn: null,
            AvailableStatusValues: Array.Empty<string>(),
            ConfidenceScore: 0.8,
            DiagnosticNotes: Array.Empty<string>());

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("sup.[SupplierName] AS SupplierName", sql);
        Assert.Contains("INNER JOIN [Inventory].[Item] AS i ON i.[ItemID] = p.[ItemID]", sql);
        Assert.Contains("INNER JOIN [Inventory].[Supplier] AS sup ON sup.[SupplierID] = p.[SupplierID]", sql);
        Assert.Contains("WHERE i.[NDC] = @ndc", sql);
        Assert.DoesNotContain("IN (", sql); // no status filter when column absent
        Assert.Contains("p.[Cost] > 0", sql);
        Assert.Contains("ORDER BY p.[Cost] ASC", sql);
        // CostPerUnit falls back to Cost when not declared.
        Assert.Contains("p.[Cost] AS CostPerUnit", sql);
    }

    [Fact]
    public void Build_RanksByPerUnitCost_WhenPerUnitColumnExists_NotPackCost()
    {
        // Regression: the savings ledger consumes CostPerUnit, so the cheapest supplier MUST be chosen
        // by per-unit cost when a per-unit column exists. Ranking by pack Cost picks the wrong supplier
        // (and reports a wrong savings figure) whenever pack sizes differ across suppliers.
        var schema = MakeSimpleSchema() with { CostPerUnitColumn = "UnitCost" };

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("ORDER BY p.[UnitCost] ASC", sql);
        Assert.DoesNotContain("ORDER BY p.[Cost] ASC", sql);
        Assert.Contains("p.[Cost] > 0", sql); // pack-cost sanity guard retained
        Assert.Contains("p.[UnitCost] > 0", sql); // per-unit guard added
    }

    [Fact]
    public void Build_RanksByPackCost_WhenNoPerUnitColumn()
    {
        // No per-unit column → savings enrichment is suppressed upstream, so the cheapest-pack row is
        // the correct cheapest-available pick and per-unit falls back to pack cost.
        var schema = MakeSimpleSchema(); // CostPerUnitColumn: null

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("ORDER BY p.[Cost] ASC", sql);
        Assert.Contains("p.[Cost] AS CostPerUnit", sql);
    }

    [Fact]
    public void Build_EscapesBracketInIdentifiers()
    {
        var schema = MakeSimpleSchema() with { CatalogTable = "Item]Pricing" };

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);
        Assert.Contains("[Item]]Pricing]", sql);
    }

    [Fact]
    public void Build_NdcParameterName_IsStable()
    {
        Assert.Equal("@ndc", SqlPricingQueryBuilder.NdcParameter);
    }

    private static DiscoveredPricingSchema MakeSimpleSchema() => new(
        CatalogSchema: "Inventory",
        CatalogTable: "ItemPricing",
        CostColumn: "Cost",
        CostPerUnitColumn: null,
        NdcColumn: "NDC",
        ItemJoin: null,
        SupplierSource: new CatalogSupplierSource(
            SupplierResolution.Denormalized,
            NameColumnInCatalog: "SupplierName",
            AccountColumnInCatalog: null,
            SupplierTableSchema: null,
            SupplierTable: null,
            SupplierIdColumnInCatalog: null,
            SupplierIdColumnInSupplier: null,
            SupplierNameColumnInSupplier: null),
        StatusColumn: null,
        AvailableStatusValues: Array.Empty<string>(),
        ConfidenceScore: 0.8,
        DiagnosticNotes: Array.Empty<string>());
}
