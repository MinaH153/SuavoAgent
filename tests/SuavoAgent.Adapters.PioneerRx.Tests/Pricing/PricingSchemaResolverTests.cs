using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

public class PricingSchemaResolverTests
{
    // Denormalized catalog — NDC + Supplier name live on the pricing table.
    [Fact]
    public void Resolve_Denormalized_Catalog_WithNdcAndSupplierName_Succeeds()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "ItemPricing", "ItemPricingID", "uniqueidentifier"),
            col("Inventory", "ItemPricing", "NDC", "varchar"),
            col("Inventory", "ItemPricing", "SupplierName", "varchar"),
            col("Inventory", "ItemPricing", "Cost", "money"),
            col("Inventory", "ItemPricing", "CostPerUnit", "decimal"),
            col("Inventory", "ItemPricing", "Status", "varchar"),
            col("Inventory", "Item", "ItemID", "int"),
            col("Inventory", "Item", "NDC", "varchar"),
            col("Inventory", "Item", "ItemName", "varchar"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);

        Assert.True(outcome.Ok, outcome.Reason);
        Assert.NotNull(outcome.Schema);
        var schema = outcome.Schema!;
        Assert.Equal("Inventory", schema.CatalogSchema);
        Assert.Equal("ItemPricing", schema.CatalogTable);
        Assert.Equal("Cost", schema.CostColumn);
        Assert.Equal("CostPerUnit", schema.CostPerUnitColumn);
        Assert.Equal("NDC", schema.NdcColumn);
        Assert.Null(schema.ItemJoin);
        Assert.Equal(SupplierResolution.Denormalized, schema.SupplierSource.Resolution);
        Assert.Equal("SupplierName", schema.SupplierSource.NameColumnInCatalog);
        Assert.True(schema.ConfidenceScore >= 0.7, $"Confidence={schema.ConfidenceScore}");
    }

    // Normalized catalog — supplier lives in a separate Inventory.Supplier table, joined via SupplierID.
    [Fact]
    public void Resolve_Normalized_Catalog_WithSupplierJoin_Succeeds()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "ItemPricing", "ItemPricingID", "uniqueidentifier"),
            col("Inventory", "ItemPricing", "NDC", "varchar"),
            col("Inventory", "ItemPricing", "SupplierID", "uniqueidentifier"),
            col("Inventory", "ItemPricing", "Cost", "money"),
            col("Inventory", "ItemPricing", "Status", "varchar"),
            col("Inventory", "Supplier", "SupplierID", "uniqueidentifier"),
            col("Inventory", "Supplier", "SupplierName", "varchar"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);

        Assert.True(outcome.Ok, outcome.Reason);
        var schema = outcome.Schema!;
        Assert.Equal(SupplierResolution.JoinedSupplierTable, schema.SupplierSource.Resolution);
        Assert.Equal("Supplier", schema.SupplierSource.SupplierTable);
        Assert.Equal("SupplierID", schema.SupplierSource.SupplierIdColumnInCatalog);
        Assert.Equal("SupplierID", schema.SupplierSource.SupplierIdColumnInSupplier);
        Assert.Equal("SupplierName", schema.SupplierSource.SupplierNameColumnInSupplier);
    }

    // Catalog carries ItemID only → must join to Inventory.Item for NDC.
    [Fact]
    public void Resolve_CatalogWithoutNdc_ResolvesItemJoin()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "ItemSupplier", "ItemID", "int"),
            col("Inventory", "ItemSupplier", "SupplierName", "varchar"),
            col("Inventory", "ItemSupplier", "Cost", "money"),
            col("Inventory", "Item", "ItemID", "int"),
            col("Inventory", "Item", "NDC", "varchar"),
            col("Inventory", "Item", "ItemName", "varchar"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);

        Assert.True(outcome.Ok, outcome.Reason);
        var schema = outcome.Schema!;
        Assert.Null(schema.NdcColumn);
        Assert.NotNull(schema.ItemJoin);
        Assert.Equal("Item", schema.ItemJoin!.ItemTable);
        Assert.Equal("ItemID", schema.ItemJoin.ItemIdColumnInCatalog);
        Assert.Equal("NDC", schema.ItemJoin.NdcColumnInItem);
    }

    [Fact]
    public void Resolve_NoCostColumn_Fails()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "Item", "ItemID", "int"),
            col("Inventory", "Item", "NDC", "varchar"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);
        Assert.False(outcome.Ok);
        Assert.Contains("Cost", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_SupplierIdPresentButNoSupplierTable_Fails()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "ItemPricing", "NDC", "varchar"),
            col("Inventory", "ItemPricing", "SupplierID", "int"),
            col("Inventory", "ItemPricing", "Cost", "money"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);
        Assert.False(outcome.Ok);
        Assert.Contains("Supplier", outcome.Reason ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Resolve_NoSupplierNameOrId_Fails()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("Inventory", "ItemPricing", "NDC", "varchar"),
            col("Inventory", "ItemPricing", "Cost", "money"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void Resolve_EmptyColumns_Fails()
    {
        var outcome = PricingSchemaResolver.Resolve(Array.Empty<InventoryColumnInfo>());
        Assert.False(outcome.Ok);
    }

    [Fact]
    public void Resolve_CaseInsensitiveMatching()
    {
        var cols = new List<InventoryColumnInfo>
        {
            col("inventory", "itempricing", "ndc", "varchar"),
            col("inventory", "itempricing", "suppliername", "varchar"),
            col("inventory", "itempricing", "cost", "money"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);
        Assert.True(outcome.Ok, outcome.Reason);
    }

    [Fact]
    public void Resolve_PrefersHigherScoreCatalog()
    {
        var cols = new List<InventoryColumnInfo>
        {
            // Weak candidate: has Cost + Supplier but no NDC / ItemID — should not win.
            col("Inventory", "WeakTable", "SupplierName", "varchar"),
            col("Inventory", "WeakTable", "Cost", "money"),
            // Strong candidate: Cost + NDC + Supplier + CostPerUnit + Status.
            col("Inventory", "StrongTable", "NDC", "varchar"),
            col("Inventory", "StrongTable", "SupplierName", "varchar"),
            col("Inventory", "StrongTable", "Cost", "money"),
            col("Inventory", "StrongTable", "CostPerUnit", "decimal"),
            col("Inventory", "StrongTable", "Status", "varchar"),
        };

        var outcome = PricingSchemaResolver.Resolve(cols);
        Assert.True(outcome.Ok, outcome.Reason);
        Assert.Equal("StrongTable", outcome.Schema!.CatalogTable);
    }

    private static InventoryColumnInfo col(string schema, string table, string column, string type) =>
        new(schema, table, column, type, true);
}
