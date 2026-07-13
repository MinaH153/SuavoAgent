using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Resolves the Item-table columns the top-dispensed generator needs (drug name, strength, and the
/// Generic / Rx-not-OTC / No-Schedule filter columns) from an observed Inventory column snapshot —
/// the same <see cref="InventoryColumnInfo"/> input <see cref="PricingSchemaResolver"/> uses. Reuses
/// the pricing discovery's <see cref="CatalogItemJoin"/> for the item table + id + NDC columns, so
/// this only has to find the extra descriptive/classification columns.
///
/// <para>The resolver records missing Rx/OTC or schedule columns as null so discovery diagnostics stay
/// explicit. <see cref="SqlTopDispensedQueryBuilder"/> then refuses execution with fixed reason codes;
/// it never silently broadens Nadim's requested Generic + Rx + No-Schedule population.</para>
///
/// <para>Operator overrides win over heuristics for non-standard installs; the *Value fields say what
/// "generic"/"Rx"/"no schedule" equal in this pharmacy's data (defaults Generic / Rx / 0). Final
/// column + value ground-truthing happens on the live PMS, exactly like pricing schema discovery.</para>
/// </summary>
public static class TopDispensedSchemaResolver
{
    private static readonly string[] DrugNamePriority =
        { "Name", "DrugName", "ItemName", "ItemDescription", "Description", "GenericName" };
    private static readonly string[] StrengthPriority =
        { "Strength", "DoseStrength", "Str", "StrengthDescription" };
    private static readonly string[] BrandGenericPriority =
        { "BrandGeneric", "BrandGenericType", "BrandGenericIndicator", "BrandOrGeneric", "GenericIndicator", "MultiSource" };
    private static readonly string[] RxOtcPriority =
        { "RxOtc", "RxOtcType", "RxOtcIndicator", "RxOrOtc", "LegendStatus", "LegendType" };
    private static readonly string[] SchedulePriority =
        { "DeaSchedule", "DeaClass", "DeaScheduleType", "Schedule", "ControlledSubstanceCode", "CSchedule" };

    private const string DefaultGenericValue = "Generic";
    private const string DefaultRxValue = "Rx";
    private const string DefaultNoScheduleValue = "0";

    public static TopDispensedSpec? Resolve(
        CatalogItemJoin? itemJoin,
        IReadOnlyList<InventoryColumnInfo> inventoryColumns,
        TopDispensedColumnOverrides? overrides = null)
    {
        if (itemJoin is null || inventoryColumns is null) return null;

        // Only consider columns that live on the resolved Item table.
        var itemCols = inventoryColumns
            .Where(c => string.Equals(c.SchemaName, itemJoin.ItemTableSchema, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(c.TableName, itemJoin.ItemTable, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (itemCols.Count == 0) return null;

        var drugName = Blank(overrides?.DrugNameColumn) ?? Pick(itemCols, DrugNamePriority);
        var brandGeneric = Blank(overrides?.BrandGenericColumn) ?? Pick(itemCols, BrandGenericPriority);
        // Mandatory: without a name to write and a generics gate to filter, refuse (fail-closed).
        if (drugName is null || brandGeneric is null) return null;

        var strength = Blank(overrides?.StrengthColumn) ?? Pick(itemCols, StrengthPriority);
        var rxOtc = Blank(overrides?.RxOtcColumn) ?? Pick(itemCols, RxOtcPriority);
        var schedule = Blank(overrides?.ScheduleColumn) ?? Pick(itemCols, SchedulePriority);

        return new TopDispensedSpec(
            ItemTableSchema: itemJoin.ItemTableSchema,
            ItemTable: itemJoin.ItemTable,
            ItemIdColumnInItem: itemJoin.ItemIdColumnInItem,
            NdcColumnInItem: itemJoin.NdcColumnInItem,
            DrugNameColumn: drugName,
            StrengthColumn: strength,
            BrandGenericColumn: brandGeneric,
            GenericValue: Blank(overrides?.GenericValue) ?? DefaultGenericValue,
            RxOtcColumn: rxOtc,
            RxValue: rxOtc is null ? null : (Blank(overrides?.RxValue) ?? DefaultRxValue),
            ScheduleColumn: schedule,
            NoScheduleValue: schedule is null ? null : (Blank(overrides?.NoScheduleValue) ?? DefaultNoScheduleValue),
            BrandGenericColumnShape: ShapeFor(itemCols, brandGeneric),
            RxOtcColumnShape: ShapeFor(itemCols, rxOtc),
            ScheduleColumnShape: ShapeFor(itemCols, schedule));
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string? Pick(IEnumerable<InventoryColumnInfo> cols, IReadOnlyList<string> priority)
    {
        var names = cols.Select(c => c.ColumnName).ToList();
        foreach (var p in priority)
        {
            var match = names.FirstOrDefault(n => string.Equals(n, p, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return null;
    }

    private static PricingSqlColumnShape? ShapeFor(
        IEnumerable<InventoryColumnInfo> columns,
        string? name)
    {
        if (name is null) return null;
        var column = columns.FirstOrDefault(candidate =>
            candidate.ColumnName.Equals(name, StringComparison.OrdinalIgnoreCase));
        return column is null
            ? null
            : new PricingSqlColumnShape(
                column.DataType,
                column.MaxLength,
                column.Precision,
                column.Scale,
                column.IsNullable);
    }
}
