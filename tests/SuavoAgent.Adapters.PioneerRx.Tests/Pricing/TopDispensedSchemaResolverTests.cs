using System;
using System.Collections.Generic;
using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

public class TopDispensedSchemaResolverTests
{
    private static CatalogItemJoin ItemJoin() => new(
        ItemTableSchema: "Inventory", ItemTable: "Item",
        ItemIdColumnInCatalog: "ItemID", ItemIdColumnInItem: "ItemID", NdcColumnInItem: "NDC");

    private static InventoryColumnInfo Col(string col, string table = "Item", string schema = "Inventory")
        => new(schema, table, col, "nvarchar", true, MaxLength: 128, Precision: 0, Scale: 0);

    private static List<InventoryColumnInfo> FullItemColumns() => new()
    {
        Col("ItemID"), Col("NDC"), Col("Name"), Col("Strength"),
        Col("BrandGeneric"), Col("RxOtc"), Col("DeaSchedule"),
        Col("SomeOtherColumn"),
        // noise from another table must be ignored
        Col("Name", table: "Supplier"),
    };

    [Fact]
    public void Resolve_discovers_all_item_columns()
    {
        var spec = TopDispensedSchemaResolver.Resolve(ItemJoin(), FullItemColumns());

        Assert.NotNull(spec);
        Assert.Equal("Inventory", spec!.ItemTableSchema);
        Assert.Equal("Item", spec.ItemTable);
        Assert.Equal("ItemID", spec.ItemIdColumnInItem);
        Assert.Equal("NDC", spec.NdcColumnInItem);
        Assert.Equal("Name", spec.DrugNameColumn);
        Assert.Equal("Strength", spec.StrengthColumn);
        Assert.Equal("BrandGeneric", spec.BrandGenericColumn);
        Assert.Equal("Generic", spec.GenericValue);
        Assert.Equal("RxOtc", spec.RxOtcColumn);
        Assert.Equal("Rx", spec.RxValue);
        Assert.Equal("DeaSchedule", spec.ScheduleColumn);
        Assert.Equal("0", spec.NoScheduleValue);
        Assert.Equal("nvarchar", spec.BrandGenericColumnShape!.DataType);
        Assert.Equal(128, spec.RxOtcColumnShape!.MaxLength);
    }

    [Fact]
    public void Resolve_fails_closed_without_a_generics_gate_column()
    {
        var cols = new List<InventoryColumnInfo> { Col("ItemID"), Col("NDC"), Col("Name") }; // no BrandGeneric
        Assert.Null(TopDispensedSchemaResolver.Resolve(ItemJoin(), cols));
    }

    [Fact]
    public void Resolve_fails_closed_without_a_drug_name_column()
    {
        var cols = new List<InventoryColumnInfo> { Col("ItemID"), Col("NDC"), Col("BrandGeneric") }; // no Name
        Assert.Null(TopDispensedSchemaResolver.Resolve(ItemJoin(), cols));
    }

    [Fact]
    public void Resolve_leaves_optional_gates_null_when_absent()
    {
        var cols = new List<InventoryColumnInfo> { Col("ItemID"), Col("NDC"), Col("Name"), Col("BrandGeneric") };
        var spec = TopDispensedSchemaResolver.Resolve(ItemJoin(), cols);

        Assert.NotNull(spec);
        Assert.Null(spec!.StrengthColumn);
        Assert.Null(spec.RxOtcColumn);
        Assert.Null(spec.RxValue);       // no value without a column
        Assert.Null(spec.ScheduleColumn);
        Assert.Null(spec.NoScheduleValue);
    }

    [Fact]
    public void Overrides_pin_columns_and_values_for_nonstandard_installs()
    {
        // Pharmacy whose Item table names don't match any heuristic + a numeric generic flag.
        var cols = new List<InventoryColumnInfo>
        {
            Col("ItemID"), Col("NDC"), Col("ItemDesc"), Col("MultiSourceCode"), Col("LegendCode"), Col("CtrlSched"),
        };
        var overrides = new TopDispensedColumnOverrides(
            DrugNameColumn: "ItemDesc",
            BrandGenericColumn: "MultiSourceCode",
            RxOtcColumn: "LegendCode",
            ScheduleColumn: "CtrlSched",
            GenericValue: "G", RxValue: "L", NoScheduleValue: "N");

        var spec = TopDispensedSchemaResolver.Resolve(ItemJoin(), cols, overrides);

        Assert.NotNull(spec);
        Assert.Equal("ItemDesc", spec!.DrugNameColumn);
        Assert.Equal("MultiSourceCode", spec.BrandGenericColumn);
        Assert.Equal("G", spec.GenericValue);
        Assert.Equal("LegendCode", spec.RxOtcColumn);
        Assert.Equal("L", spec.RxValue);
        Assert.Equal("CtrlSched", spec.ScheduleColumn);
        Assert.Equal("N", spec.NoScheduleValue);
    }

    [Fact]
    public void Resolve_null_when_no_item_join()
        => Assert.Null(TopDispensedSchemaResolver.Resolve(null, FullItemColumns()));
}
