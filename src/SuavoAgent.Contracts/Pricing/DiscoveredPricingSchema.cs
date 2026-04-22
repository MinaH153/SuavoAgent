namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Resolved PioneerRx pricing schema, derived at install time from a <c>sys.tables</c>/<c>sys.columns</c>
/// probe. Canonical output of <c>PricingSchemaDiscovery</c>; consumed by <c>SqlPricingQuery</c> to build
/// parameterized supplier-catalog reads without hard-coding table names that vary by install.
///
/// Every field is verified present in the live database before the record is handed to callers —
/// if discovery cannot resolve <see cref="CatalogTable"/>, <see cref="CostColumn"/>, and at least
/// one of <see cref="NdcColumn"/> or an <see cref="ItemJoin"/>, discovery fails closed.
/// </summary>
public sealed record DiscoveredPricingSchema(
    string CatalogSchema,
    string CatalogTable,
    string CostColumn,
    string? CostPerUnitColumn,
    string? NdcColumn,
    CatalogItemJoin? ItemJoin,
    CatalogSupplierSource SupplierSource,
    string? StatusColumn,
    IReadOnlyList<string> AvailableStatusValues,
    double ConfidenceScore,
    IReadOnlyList<string> DiagnosticNotes);

/// <summary>
/// Describes how to get from the catalog row to an NDC when the catalog table doesn't carry NDC
/// directly. Canonical case: <c>Inventory.ItemPricing.ItemID → Inventory.Item.NDC</c>.
/// </summary>
public sealed record CatalogItemJoin(
    string ItemTableSchema,
    string ItemTable,
    string ItemIdColumnInCatalog,
    string ItemIdColumnInItem,
    string NdcColumnInItem);

/// <summary>
/// Describes how to resolve the supplier display name. Two shapes observed in the field:
/// <list type="bullet">
/// <item>Denormalized — supplier name string (and optional account #) lives on the catalog row.</item>
/// <item>Normalized — catalog row has a <c>SupplierID</c> that joins to <c>Inventory.Supplier</c>.</item>
/// </list>
/// </summary>
public sealed record CatalogSupplierSource(
    SupplierResolution Resolution,
    string? NameColumnInCatalog,
    string? AccountColumnInCatalog,
    string? SupplierTableSchema,
    string? SupplierTable,
    string? SupplierIdColumnInCatalog,
    string? SupplierIdColumnInSupplier,
    string? SupplierNameColumnInSupplier);

public enum SupplierResolution
{
    /// <summary>Supplier name lives on the catalog row directly.</summary>
    Denormalized,
    /// <summary>Catalog row joins to a separate supplier table.</summary>
    JoinedSupplierTable,
}

/// <summary>Diagnostic shape used during discovery; written to DiscoveredPricingSchema.DiagnosticNotes.</summary>
public sealed record InventoryColumnInfo(
    string SchemaName,
    string TableName,
    string ColumnName,
    string DataType,
    bool IsNullable);

/// <summary>Entry-point result shape for PricingSchemaDiscovery.</summary>
public sealed record PricingDiscoveryOutcome(
    bool Ok,
    DiscoveredPricingSchema? Schema,
    string? Reason,
    IReadOnlyList<string> Notes)
{
    public static PricingDiscoveryOutcome Fail(string reason, IReadOnlyList<string>? notes = null)
        => new(false, null, reason, notes ?? Array.Empty<string>());
}
