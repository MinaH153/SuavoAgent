namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// One row of a generated "Top N most dispensed" worklist — the sheet Nadim otherwise
/// produces by hand (Rx Binoculars → Transaction Search → Top-X report → export). Column
/// shape matches his real export: Drug / Strength / NDC / Total Dispensed. This becomes the
/// INPUT to the pricing loop (ExcelPricingReader → per-NDC cheapest-supplier → writeback),
/// closing the last manual step so generate → price → write-back runs end to end.
/// </summary>
public sealed record TopDispensedRow(
    string DrugName,
    string Strength,
    string Ndc,
    decimal TotalDispensed);

/// <summary>
/// The install-resolved column set needed to generate the top-dispensed worklist from the
/// PioneerRx DB. The RxTransaction side is canary-pinned in <c>SqlTopDispensedQueryBuilder</c>;
/// the Item-side columns are pharmacy-resolved (schema discovery / config), so this record
/// carries them and the builder fails closed when the required ones are absent — never guessing
/// a schema on a PHI box.
///
/// <para>Filters mirror Nadim's spoken recipe exactly ("generics only, Rx not OTC, no schedule"):
/// <see cref="BrandGenericColumn"/> = <see cref="GenericValue"/> (required — the "generics only"
/// gate), and the optional Rx-not-OTC and no-schedule gates when those columns are resolved.</para>
/// </summary>
public sealed record TopDispensedSpec(
    string ItemTableSchema,
    string ItemTable,
    string ItemIdColumnInItem,
    string NdcColumnInItem,
    string DrugNameColumn,
    string? StrengthColumn,
    string BrandGenericColumn,
    string GenericValue,
    string? RxOtcColumn = null,
    string? RxValue = null,
    string? ScheduleColumn = null,
    string? NoScheduleValue = null);

/// <summary>
/// Operator overrides for top-dispensed schema resolution. Any non-null column name pins that
/// column (skips heuristic discovery — used when a pharmacy's Item table names are non-standard or
/// discovery is ambiguous); the *Value fields set what "generic" / "Rx" / "no schedule" equal in
/// this install's data (defaults: Generic / Rx / 0). All optional — an empty instance means
/// "discover everything, use default match values".
/// </summary>
public sealed record TopDispensedColumnOverrides(
    string? DrugNameColumn = null,
    string? StrengthColumn = null,
    string? BrandGenericColumn = null,
    string? RxOtcColumn = null,
    string? ScheduleColumn = null,
    string? GenericValue = null,
    string? RxValue = null,
    string? NoScheduleValue = null);
