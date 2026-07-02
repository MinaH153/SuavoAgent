using SuavoAgent.Adapters.PioneerRx.Pricing;
using SuavoAgent.Contracts.Pricing;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Pricing;

/// <summary>
/// Grounds the top-500 generator against Nadim's spoken recipe (video IMG_5917): Rx Binoculars →
/// Transaction Search, Jan 1→today, Generic / Rx-not-OTC / No Schedule → Top-X report, ranked by
/// most dispensed. Asserts the emitted SQL encodes each of those filters + the Drug/Strength/NDC/
/// Total-Dispensed shape of his real export, and fails closed when it can't build a safe query.
/// </summary>
public class SqlTopDispensedQueryBuilderTests
{
    private static readonly string[] Dispensed = { "Sold", "Completed" };

    private static TopDispensedSpec FullSpec() => new(
        ItemTableSchema: "Inventory",
        ItemTable: "Item",
        ItemIdColumnInItem: "ItemID",
        NdcColumnInItem: "NDC",
        DrugNameColumn: "Name",
        StrengthColumn: "Strength",
        BrandGenericColumn: "BrandGeneric",
        GenericValue: "Generic",
        RxOtcColumn: "RxOtcType",
        RxValue: "Rx",
        ScheduleColumn: "DeaSchedule",
        NoScheduleValue: "0");

    [Fact]
    public void Build_encodes_Nadims_full_recipe()
    {
        var sql = SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(FullSpec(), Dispensed);

        Assert.NotNull(sql);
        // Top-N ranked by most dispensed
        Assert.Contains("SELECT TOP (@topN)", sql);
        Assert.Contains("ORDER BY SUM(rt.[DispensedQuantity]) DESC", sql);
        // Output shape = his sheet
        Assert.Contains("it.[Name] AS [Drug]", sql!);
        Assert.Contains("it.[Strength] AS [Strength]", sql);
        Assert.Contains("it.[NDC] AS [NDC]", sql);
        Assert.Contains("SUM(rt.[DispensedQuantity]) AS [Total Dispensed]", sql);
        // Canary-pinned RxTransaction join
        Assert.Contains("INNER JOIN [Inventory].[Item] AS it ON rt.[DispensedItemID] = it.[ItemID]", sql);
        // Window (Jan 1) + final-fill status filter (no voids)
        Assert.Contains("rt.[DateFilled] >= @windowStart", sql);
        Assert.Contains("st.[Description] IN (@tdstatus0, @tdstatus1)", sql);
        // Generic / Rx-not-OTC / No-Schedule gates
        Assert.Contains("it.[BrandGeneric] = @generic", sql);
        Assert.Contains("it.[RxOtcType] = @rxOtc", sql);
        Assert.Contains("it.[DeaSchedule] = @noSchedule", sql);
        // Aggregated per item
        Assert.Contains("GROUP BY it.[Name], it.[Strength], it.[NDC]", sql);
    }

    [Fact]
    public void Build_omits_optional_gates_when_columns_unresolved()
    {
        var spec = FullSpec() with { RxOtcColumn = null, ScheduleColumn = null };
        var sql = SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, Dispensed);

        Assert.NotNull(sql);
        Assert.Contains("it.[BrandGeneric] = @generic", sql!); // generics gate always present
        Assert.DoesNotContain("@rxOtc", sql);
        Assert.DoesNotContain("@noSchedule", sql);
    }

    [Fact]
    public void Build_emits_empty_strength_literal_when_column_absent()
    {
        var spec = FullSpec() with { StrengthColumn = null };
        var sql = SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, Dispensed);

        Assert.NotNull(sql);
        Assert.Contains("'' AS [Strength]", sql!);
        Assert.Contains("GROUP BY it.[Name], it.[NDC]", sql); // strength not grouped when absent
    }

    [Fact]
    public void Build_fails_closed_without_dispensed_status_names()
        => Assert.Null(SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(FullSpec(), System.Array.Empty<string>()));

    [Fact]
    public void Build_fails_closed_without_the_generics_gate()
    {
        var spec = FullSpec() with { GenericValue = "" };
        Assert.Null(SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, Dispensed));
    }

    [Fact]
    public void Build_fails_closed_without_required_item_identifiers()
    {
        var spec = FullSpec() with { NdcColumnInItem = "" };
        Assert.Null(SqlTopDispensedQueryBuilder.BuildTopDispensedQuery(spec, Dispensed));
    }
}
