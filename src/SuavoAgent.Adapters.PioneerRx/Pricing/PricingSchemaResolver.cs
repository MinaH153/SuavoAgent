using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Adapters.PioneerRx.Pricing;

/// <summary>
/// Pure-logic half of pricing-schema discovery. Given the <c>sys.columns</c> dump for the Inventory
/// schema, rank candidate catalog tables and emit a <see cref="DiscoveredPricingSchema"/> or explain
/// the failure.
///
/// Splitting the SQL fetch from the ranking keeps this unit testable without a live database —
/// the SQL-adjacent code lives in <c>PricingSchemaDiscovery</c>.
/// </summary>
public static class PricingSchemaResolver
{
    private static readonly string[] CostColumnPriority =
    {
        "Cost", "LastCost", "UnitCost", "CostPerUnit", "AcquisitionCost",
    };

    private static readonly string[] CostPerUnitColumnPriority =
    {
        "CostPerUnit", "UnitCost", "Cost_Unit", "PerUnitCost",
    };

    private static readonly string[] NdcColumnPriority = { "NDC", "NDCCode", "NdcCode" };

    private static readonly string[] ItemIdColumnPriority = { "ItemID", "ItemId", "DispensedItemID" };

    private static readonly string[] SupplierIdColumnPriority = { "SupplierID", "SupplierId", "VendorID", "VendorId" };

    private static readonly string[] SupplierNameOnCatalogPriority =
    {
        "SupplierName", "Supplier", "Vendor", "VendorName",
    };

    private static readonly string[] StatusColumnPriority =
    {
        "Status", "SupplierStatus", "AvailabilityStatus",
    };

    /// <summary>
    /// Canonical status values shown green/white in the live Pricing tab. Until Joshua confirms
    /// against Nadim's data, the agent stays conservative — only rows with explicit "Available"
    /// (or equivalent) status are considered for the cheapest-supplier pick.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultAvailableStatusValues = new[]
    {
        "Available", "Active",
    };

    /// <summary>
    /// Returns the best-guess pricing schema from an Inventory-schema column snapshot.
    /// Confidence is 0.0–1.0 — &gt;=0.7 is "ship it"; &lt;0.5 falls back to UIA-only until Joshua verifies.
    /// </summary>
    public static PricingDiscoveryOutcome Resolve(IReadOnlyList<InventoryColumnInfo> inventoryColumns)
    {
        if (inventoryColumns.Count == 0)
            return PricingDiscoveryOutcome.Fail("No columns observed in Inventory schema");

        var notes = new List<string>();

        // Bucket columns by table for candidate ranking.
        var comparer = new SchemaTableComparer();
        var byTable = new Dictionary<(string Schema, string Table), List<InventoryColumnInfo>>(comparer);
        foreach (var c in inventoryColumns)
        {
            var key = (c.SchemaName, c.TableName);
            if (!byTable.TryGetValue(key, out var list))
            {
                list = new List<InventoryColumnInfo>();
                byTable[key] = list;
            }
            list.Add(c);
        }

        // Score each table on "looks like a pricing catalog": must have Cost, should have NDC or ItemID FK.
        var ranked = new List<(
            (string Schema, string Table) Key,
            double Score,
            string CostColumn,
            string? CostPerUnitColumn,
            string? NdcColumn,
            string? ItemIdColumn,
            string? SupplierNameColumn,
            string? SupplierIdColumn,
            string? StatusColumn,
            List<string> Notes)>();

        foreach (var kv in byTable)
        {
            var cols = kv.Value;
            var tableSchema = kv.Key.Schema;
            var tableName = kv.Key.Table;

            var costInfo = PickInfo(cols, CostColumnPriority, IsExactNumeric);
            if (costInfo == null) continue;
            var costCol = costInfo.ColumnName;

            var costPerUnit = PickInfo(cols, CostPerUnitColumnPriority, IsExactNumeric)?.ColumnName;
            var ndc = PickInfo(cols, NdcColumnPriority, IsBoundedText)?.ColumnName;
            var itemId = Pick(cols, ItemIdColumnPriority);
            var supplierName = PickInfo(cols, SupplierNameOnCatalogPriority, IsBoundedText)?.ColumnName;
            var supplierId = Pick(cols, SupplierIdColumnPriority);
            var status = PickInfo(cols, StatusColumnPriority, IsBoundedText)?.ColumnName;

            double score = 0.25;                                       // has Cost
            if (ndc != null) score += 0.30;
            else if (itemId != null) score += 0.20;
            if (supplierName != null) score += 0.15;
            if (supplierId != null) score += 0.15;
            if (costPerUnit != null) score += 0.10;
            if (status != null) score += 0.05;

            var tableNotes = new List<string>
            {
                $"Candidate {tableSchema}.{tableName}: Cost={costCol}, CostPerUnit={costPerUnit ?? "-"}, " +
                $"NDC={ndc ?? "-"}, ItemID={itemId ?? "-"}, SupplierName={supplierName ?? "-"}, " +
                $"SupplierID={supplierId ?? "-"}, Status={status ?? "-"} (score={score:F2})",
            };

            ranked.Add(((tableSchema, tableName), score, costCol, costPerUnit, ndc, itemId,
                       supplierName, supplierId, status, tableNotes));
        }

        if (ranked.Count == 0)
            return PricingDiscoveryOutcome.Fail(
                "No Inventory table had a recognizable Cost column", notes);

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        foreach (var c in ranked) notes.AddRange(c.Notes);

        var best = ranked[0];
        if (best.Score < 0.55)
            return PricingDiscoveryOutcome.Fail(
                $"Best candidate {best.Key.Schema}.{best.Key.Table} scored {best.Score:F2} — below 0.55 threshold",
                notes);

        // Resolve Supplier join if the catalog row carries SupplierID but no name.
        CatalogSupplierSource supplierSource;
        if (best.SupplierNameColumn != null)
        {
            supplierSource = new CatalogSupplierSource(
                SupplierResolution.Denormalized,
                NameColumnInCatalog: best.SupplierNameColumn,
                AccountColumnInCatalog: null,
                SupplierTableSchema: null,
                SupplierTable: null,
                SupplierIdColumnInCatalog: null,
                SupplierIdColumnInSupplier: null,
                SupplierNameColumnInSupplier: null);
        }
        else if (best.SupplierIdColumn != null)
        {
            var supplierJoin = ResolveSupplierJoin(byTable, best.SupplierIdColumn);
            if (supplierJoin == null)
                return PricingDiscoveryOutcome.Fail(
                    $"Catalog {best.Key.Schema}.{best.Key.Table} has {best.SupplierIdColumn} " +
                    "but no matching Supplier/Vendor table with a name column was found",
                    notes);

            supplierSource = supplierJoin with { SupplierIdColumnInCatalog = best.SupplierIdColumn };
        }
        else
        {
            return PricingDiscoveryOutcome.Fail(
                $"Catalog {best.Key.Schema}.{best.Key.Table} has no SupplierName or SupplierID column",
                notes);
        }

        // Resolve Item join if the catalog doesn't carry NDC directly.
        CatalogItemJoin? itemJoin = null;
        if (best.NdcColumn == null)
        {
            if (best.ItemIdColumn == null)
                return PricingDiscoveryOutcome.Fail(
                    $"Catalog {best.Key.Schema}.{best.Key.Table} has neither NDC nor ItemID — cannot map an NDC",
                    notes);

            itemJoin = ResolveItemJoin(byTable, best.ItemIdColumn);
            if (itemJoin == null)
                return PricingDiscoveryOutcome.Fail(
                    $"Catalog requires Item join on {best.ItemIdColumn} but Inventory.Item.NDC was not observed",
                    notes);
        }

        var schema = new DiscoveredPricingSchema(
            CatalogSchema: best.Key.Schema,
            CatalogTable: best.Key.Table,
            CostColumn: best.CostColumn,
            CostPerUnitColumn: best.CostPerUnitColumn,
            NdcColumn: best.NdcColumn,
            ItemJoin: itemJoin,
            SupplierSource: supplierSource,
            StatusColumn: best.StatusColumn,
            AvailableStatusValues: DefaultAvailableStatusValues,
            ConfidenceScore: best.Score,
            DiagnosticNotes: notes,
            CostColumnShape: ShapeFor(byTable[best.Key], best.CostColumn),
            CostPerUnitColumnShape: ShapeFor(byTable[best.Key], best.CostPerUnitColumn),
            NdcColumnShape: ShapeFor(byTable[best.Key], best.NdcColumn),
            StatusColumnShape: ShapeFor(byTable[best.Key], best.StatusColumn));

        return new PricingDiscoveryOutcome(true, schema, null, notes);
    }

    private static CatalogSupplierSource? ResolveSupplierJoin(
        Dictionary<(string Schema, string Table), List<InventoryColumnInfo>> byTable,
        string catalogSupplierIdColumn)
    {
        foreach (var kv in byTable)
        {
            var table = kv.Key.Table;
            var looksLikeSupplier = table.Contains("Supplier", StringComparison.OrdinalIgnoreCase) ||
                                    table.Contains("Vendor", StringComparison.OrdinalIgnoreCase);
            if (!looksLikeSupplier) continue;

            var id = Pick(kv.Value, SupplierIdColumnPriority);
            var name = PickInfo(kv.Value, SupplierNameOnCatalogPriority, IsBoundedText)?.ColumnName;
            if (id == null || name == null) continue;

            return new CatalogSupplierSource(
                Resolution: SupplierResolution.JoinedSupplierTable,
                NameColumnInCatalog: null,
                AccountColumnInCatalog: null,
                SupplierTableSchema: kv.Key.Schema,
                SupplierTable: table,
                SupplierIdColumnInCatalog: catalogSupplierIdColumn,
                SupplierIdColumnInSupplier: id,
                SupplierNameColumnInSupplier: name);
        }
        return null;
    }

    private static CatalogItemJoin? ResolveItemJoin(
        Dictionary<(string Schema, string Table), List<InventoryColumnInfo>> byTable,
        string catalogItemIdColumn)
    {
        foreach (var kv in byTable)
        {
            if (!kv.Key.Table.Equals("Item", StringComparison.OrdinalIgnoreCase)) continue;
            var itemId = Pick(kv.Value, ItemIdColumnPriority);
            var ndcInfo = PickInfo(kv.Value, NdcColumnPriority, IsBoundedText);
            var ndc = ndcInfo?.ColumnName;
            if (itemId == null || ndc == null) continue;

            return new CatalogItemJoin(
                ItemTableSchema: kv.Key.Schema,
                ItemTable: kv.Key.Table,
                ItemIdColumnInCatalog: catalogItemIdColumn,
                ItemIdColumnInItem: itemId,
                NdcColumnInItem: ndc,
                NdcColumnShape: ToShape(ndcInfo!));
        }
        return null;
    }

    private static string? Pick(IEnumerable<InventoryColumnInfo> cols, IReadOnlyList<string> priority)
        => PickInfo(cols, priority)?.ColumnName;

    private static InventoryColumnInfo? PickInfo(
        IEnumerable<InventoryColumnInfo> cols,
        IReadOnlyList<string> priority,
        Func<InventoryColumnInfo, bool>? policy = null)
    {
        var candidates = cols.ToList();
        foreach (var p in priority)
        {
            var match = candidates.FirstOrDefault(column =>
                string.Equals(column.ColumnName, p, StringComparison.OrdinalIgnoreCase) &&
                (policy is null || policy(column)));
            if (match != null) return match;
        }
        return null;
    }

    private static PricingSqlColumnShape? ShapeFor(
        IEnumerable<InventoryColumnInfo> columns,
        string? columnName)
    {
        if (string.IsNullOrWhiteSpace(columnName)) return null;
        var column = columns.FirstOrDefault(candidate =>
            string.Equals(candidate.ColumnName, columnName, StringComparison.OrdinalIgnoreCase));
        return column is null ? null : ToShape(column);
    }

    private static PricingSqlColumnShape ToShape(InventoryColumnInfo column) =>
        new(column.DataType, column.MaxLength, column.Precision, column.Scale, column.IsNullable);

    private static bool IsExactNumeric(InventoryColumnInfo column) =>
        column.DataType.Equals("decimal", StringComparison.OrdinalIgnoreCase) ||
        column.DataType.Equals("numeric", StringComparison.OrdinalIgnoreCase) ||
        column.DataType.Equals("money", StringComparison.OrdinalIgnoreCase) ||
        column.DataType.Equals("smallmoney", StringComparison.OrdinalIgnoreCase);

    private static bool IsBoundedText(InventoryColumnInfo column)
    {
        if (!(column.DataType.Equals("varchar", StringComparison.OrdinalIgnoreCase) ||
              column.DataType.Equals("nvarchar", StringComparison.OrdinalIgnoreCase) ||
              column.DataType.Equals("char", StringComparison.OrdinalIgnoreCase) ||
              column.DataType.Equals("nchar", StringComparison.OrdinalIgnoreCase)))
            return false;
        return column.MaxLength is null or > 0;
    }

    private sealed class SchemaTableComparer : IEqualityComparer<(string Schema, string Table)>
    {
        public bool Equals((string Schema, string Table) x, (string Schema, string Table) y)
            => string.Equals(x.Schema, y.Schema, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Table, y.Table, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Schema, string Table) obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Schema),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Table));
    }
}
