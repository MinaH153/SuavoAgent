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
            DiagnosticNotes: Array.Empty<string>(),
            CostColumnShape: MoneyShape(),
            CostPerUnitColumnShape: MoneyShape(),
            NdcColumnShape: TextShape("char", 11),
            StatusColumnShape: TextShape("varchar", 16));

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("SELECT TOP 1", sql);
        Assert.Contains("p.[SupplierName] AS SupplierName", sql);
        Assert.Contains("p.[Cost] AS Cost", sql);
        Assert.Contains("p.[CostPerUnit] AS CostPerUnit", sql);
        Assert.Contains("p.[Status] AS CatalogStatus", sql);
        Assert.Contains("FROM [Inventory].[ItemPricing] AS p", sql);
        Assert.DoesNotContain("JOIN", sql); // no item join, no supplier join
        Assert.Contains("WHERE p.[NDC] = @ndc", sql);
        Assert.Contains("p.[Status] IN (@status0, @status1)", sql);
        Assert.DoesNotContain("'Available'", sql);
        Assert.DoesNotContain("'Active'", sql);
        // CostPerUnitColumn is declared → rank by per-unit (the savings-ledger quantity), not pack cost.
        Assert.Contains("p.[CostPerUnit] > 0", sql);
        Assert.Contains("ORDER BY p.[CostPerUnit] ASC", sql);
        Assert.DoesNotContain("ORDER BY p.[Cost] ASC", sql);
    }

    [Fact]
    public void Build_RejectsJoinedSupplierSchemaWithoutDedicatedPerUnitCost()
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

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal("pricing_cost_basis_unresolved", error.Message);
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
    public void Build_RejectsSchemaWhenNoPerUnitColumn()
    {
        var schema = MakeSimpleSchema() with { CostPerUnitColumn = null };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal("pricing_cost_basis_unresolved", error.Message);
    }

    [Fact]
    public void Build_EscapesBracketInIdentifiers()
    {
        var schema = MakeSimpleSchema() with { CatalogTable = "Item]Pricing" };

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);
        Assert.Contains("[Item]]Pricing]", sql);
    }

    [Fact]
    public void Build_RejectsMissingStatusColumn()
    {
        var schema = MakeSimpleSchema() with { StatusColumn = null };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.StatusEligibilityUnresolvedCode, error.Message);
    }

    [Fact]
    public void Build_RejectsEmptyStatusAllowlist()
    {
        var schema = MakeSimpleSchema() with { AvailableStatusValues = Array.Empty<string>() };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.StatusEligibilityUnresolvedCode, error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("AVAIL")]
    [InlineData("A")]
    [InlineData("Backordered")]
    [InlineData("Available'); DROP TABLE Inventory.ItemPricing;--")]
    public void Build_RejectsUnknownOrMalformedEligibleStatus(string value)
    {
        var schema = MakeSimpleSchema() with { AvailableStatusValues = new[] { value } };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.StatusEligibilityUnresolvedCode, error.Message);
    }

    [Fact]
    public void Build_RejectsDuplicateStatusAllowlist()
    {
        var schema = MakeSimpleSchema() with
        {
            AvailableStatusValues = new[] { "Available", "available" },
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.StatusEligibilityUnresolvedCode, error.Message);
    }

    [Fact]
    public void Build_AcceptsKnownStatusesCaseInsensitively_WithoutEmbeddingValues()
    {
        var schema = MakeSimpleSchema() with
        {
            AvailableStatusValues = new[] { "available", "ACTIVE" },
        };

        var sql = SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema);

        Assert.Contains("IN (@status0, @status1)", sql);
        Assert.DoesNotContain("available", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ACTIVE", sql);
    }

    [Fact]
    public void Build_NdcParameterName_IsStable()
    {
        Assert.Equal("@ndc", SqlPricingQueryBuilder.NdcParameter);
    }

    [Fact]
    public void Build_RejectsBitStatusColumn_InsteadOfBindingTextLabels()
    {
        var schema = MakeSimpleSchema() with
        {
            StatusColumnShape = new PricingSqlColumnShape("bit", 1, 1, 0, false),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.ColumnTypeUnresolvedCode, error.Message);
    }

    [Fact]
    public void Build_RejectsNdcTypeMismatch()
    {
        var schema = MakeSimpleSchema() with
        {
            NdcColumnShape = new PricingSqlColumnShape("int", 4, 10, 0, false),
        };

        var error = Assert.Throws<InvalidOperationException>(() =>
            SqlPricingQueryBuilder.BuildCheapestSupplierQuery(schema));

        Assert.Equal(SqlPricingQueryBuilder.ColumnTypeUnresolvedCode, error.Message);
    }

    private static DiscoveredPricingSchema MakeSimpleSchema() => new(
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
        ConfidenceScore: 0.8,
        DiagnosticNotes: Array.Empty<string>(),
        CostColumnShape: MoneyShape(),
        CostPerUnitColumnShape: MoneyShape(),
        NdcColumnShape: TextShape("varchar", 11),
        StatusColumnShape: TextShape("varchar", 16));

    private static PricingSqlColumnShape MoneyShape() =>
        new("decimal", 9, 18, 4, false);

    private static PricingSqlColumnShape TextShape(string type, int characters) =>
        new(type, type.StartsWith('n') ? characters * 2 : characters, null, null, false);
}
