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
    IReadOnlyList<SelectorPatch>? Patches = null,
    string? PmsFingerprint = null,
    string? ScreenSignatureV1 = null,
    string CostBasis = PricingApprovalContract.CostPerUnitBasis);

/// <summary>
/// PHI-free live structural identity captured by Helper immediately before a
/// UIA/vision pricing run. Core binds this exact screen to the persisted input
/// identity and every selector request in that run.
/// </summary>
public sealed record PricingScreenObservationContext(
    int ProcessId,
    string ScreenSignatureV1);

/// <summary>
/// Returned by Helper to Core after reading the Pricing tab for one NDC.
/// <c>Observations</c> carries the GREEN-tier selector-resolution telemetry for the run's
/// steps (M2a capture); null/empty for the SQL path, which drives no selectors.
///
/// <para>M1 savings: <c>CostPerUnit</c> is the SOURCED (cheapest-supplier) cost. To make the
/// cloud's savings ledger non-zero, the run must also capture — by SQL or Vision —
/// <c>BaselineCostPerUnit</c> (what the pharmacy pays TODAY, per NDC) and <c>Quantity</c>
/// (aggregate dispensed units over the window). The cloud computes
/// <c>savings_total = (BaselineCostPerUnit − CostPerUnit) × Quantity</c>; all three must be
/// non-null and Found for a dollar figure to appear. Both are nullable so today's
/// cheapest-cost-only runs still upload cleanly (savings stays NULL — never a wrong number).</para>
/// </summary>
public record SupplierPriceResult(
    string JobId,
    int RowIndex,
    string Ndc,
    bool Found,
    string? SupplierName,
    decimal? CostPerUnit,
    string? ErrorMessage,
    IReadOnlyList<SelectorObservation>? Observations = null,
    decimal? BaselineCostPerUnit = null,
    decimal? Quantity = null,
    int OmittedSelectorObservations = 0,
    decimal? PackageCost = null,
    string CostBasis = PricingApprovalContract.CostPerUnitBasis);

/// <summary>Stable, PHI-free safety failures shared by Helper and Core.</summary>
public static class PricingSafetyErrors
{
    public const string ActuationGateClosedPrefix = "actuation_gate_closed:";

    public static string ActuationGateClosed(string? rejectionCode)
        => ActuationGateClosedPrefix + (string.IsNullOrWhiteSpace(rejectionCode)
            ? "unknown"
            : rejectionCode.Trim().ToLowerInvariant());

    public static bool IsActuationGateClosed(string? error)
        => error?.StartsWith(ActuationGateClosedPrefix, StringComparison.Ordinal) == true;
}

/// <summary>
/// Persisted in AgentStateDb; describes a full pricing job run.
/// </summary>
public record PricingJobSpec(
    string JobId,
    string ExcelPath,
    string NdcColumn,
    string SupplierColumn,
    string CostColumn,
    string? ApprovalId = null,
    string? GrantDigest = null,
    string CostBasis = PricingApprovalContract.CostPerUnitBasis);

public static class PricingJobDefaults
{
    public const string NdcColumn = "NDC";
    public const string SupplierColumn = "Best Supplier";
    public const string CostColumn = "Best Cost Per Unit";
    public const string PackageSupplierColumn = "Cheapest Supplier";
    public const string PackageCostColumn = "Cost";
    public const string AmbiguousLegacyCostColumn = "Best Cost";
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
    string Status,
    // Stable machine reason-code when Status is "halted" (e.g. "helper_unreachable") so the cloud
    // cockpit shows an exact badge instead of inferring from free-text. Null for non-halted runs.
    string? HaltReason = null);

/// <summary>
/// PHI-free local phase signal. It intentionally cannot carry an NDC, path,
/// supplier, drug name, or free-text detail.
/// </summary>
public sealed record PricingJobLocalProgress(
    PricingJobLocalPhase Phase,
    int ProcessedItems,
    int TotalItems,
    int NeedsReviewItems);

public enum PricingJobLocalPhase
{
    PricingItems,
    CreatingSpreadsheet,
    VerifyingResults,
}

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
