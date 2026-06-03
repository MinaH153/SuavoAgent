using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Sent by Core to Helper: look up pricing for one NDC in PioneerRx.
/// <c>Patches</c> carries the job's active learned selector corrections (M2b); the Helper's
/// resolver tries them before the hardcoded builtin. Null/empty = builtin-only (today's behavior).
/// </summary>
public record NdcPricingRequest(
    string JobId,
    int RowIndex,
    string Ndc,
    IReadOnlyList<SelectorPatch>? Patches = null);

/// <summary>
/// Returned by Helper to Core after reading the Pricing tab for one NDC.
/// <c>Observations</c> carries the GREEN-tier selector-resolution telemetry for the run's
/// steps (M2a capture); null/empty for the SQL path, which drives no selectors.
/// </summary>
public record SupplierPriceResult(
    string JobId,
    int RowIndex,
    string Ndc,
    bool Found,
    string? SupplierName,
    decimal? CostPerUnit,
    string? ErrorMessage,
    IReadOnlyList<SelectorObservation>? Observations = null);

/// <summary>
/// Persisted in AgentStateDb; describes a full pricing job run.
/// </summary>
public record PricingJobSpec(
    string JobId,
    string ExcelPath,
    string NdcColumn,
    string SupplierColumn,
    string CostColumn);

public static class PricingJobDefaults
{
    public const string NdcColumn = "NDC";
    public const string SupplierColumn = "Best Supplier";
    public const string CostColumn = "Best Cost";
    public const string LegacySupplierColumn = "Supplier";
    public const string LegacyCostColumn = "Cost (per unit)";
}

/// <summary>
/// Progress snapshot reported from PricingJobRunner → cloud heartbeat.
/// </summary>
public record PricingJobProgress(
    string JobId,
    int TotalItems,
    int CompletedItems,
    int FailedItems,
    string Status);

public static class PricingJobStatus
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    /// <summary>Stopped mid-run by the TieredBrain (e.g. consecutive-failure
    /// rule fired). Partial results are in SQLite; operator may resume.</summary>
    public const string Halted = "halted";
}

/// <summary>
/// Pluggable supplier-price lookup — abstracts "given one NDC, return the cheapest available
/// supplier + cost". Implementations: SQL (primary, fast), UIA (via IPC to Helper, slower),
/// fake (in-memory, for tests). Lets <c>SqlPricingJobRunner</c> stay ignorant of data source.
/// </summary>
public interface ISupplierPriceLookup
{
    Task<SupplierPriceResult> FindCheapestSupplierAsync(
        string jobId, int rowIndex, string ndc11, CancellationToken ct);
}
