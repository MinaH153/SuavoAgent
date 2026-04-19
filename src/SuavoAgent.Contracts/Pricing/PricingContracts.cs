namespace SuavoAgent.Contracts.Pricing;

/// <summary>
/// Sent by Core to Helper: look up pricing for one NDC in PioneerRx.
/// </summary>
public record NdcPricingRequest(
    string JobId,
    int RowIndex,
    string Ndc);

/// <summary>
/// Returned by Helper to Core after reading the Pricing tab for one NDC.
/// </summary>
public record SupplierPriceResult(
    string JobId,
    int RowIndex,
    string Ndc,
    bool Found,
    string? SupplierName,
    decimal? CostPerUnit,
    string? ErrorMessage);

/// <summary>
/// Persisted in AgentStateDb; describes a full pricing job run.
/// </summary>
public record PricingJobSpec(
    string JobId,
    string ExcelPath,
    string NdcColumn,
    string SupplierColumn,
    string CostColumn);

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
