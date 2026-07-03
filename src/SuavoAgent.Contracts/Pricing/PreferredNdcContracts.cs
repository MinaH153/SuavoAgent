using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Contracts.Pricing;

// ===================================================================================================
// Feature B (preferred-NDC-by-insurance) — B2 the READER CONTRACT (the seam between B1 and the box).
//
// Feature A's modality-agnostic seam is ISupplierPriceLookup: "given one NDC → cheapest supplier+cost",
// implemented by SQL (primary), UIA (via Helper IPC), or fake (tests). These contracts are the EXACT
// analog for Feature B: "given one (medication, plan) → the candidate NDCs each with acquisition cost +
// expected reimbursement", which ProfitOptimizer (B1, already built) then judges via argmax(profit).
//
// Pure contracts (interfaces + records, no implementation). B2 = the SQL/UIA reader that satisfies
// IPreferredNdcDataSource; B0's recon tells us which tables back it. The shape is fixed HERE so the box
// session knows exactly what SQL must return, and ProfitOptimizer.NdcCandidate maps 1:1 from
// PreferredNdcCandidate (B3 does that trivial projection — kept out of Contracts to avoid a Helper dep).
//
// Spec: docs/recon/feature-b-preferred-ndc-by-insurance-spec.md (§3 decomposition, §5 the open data
// path the recon answers, §8 acceptance). Lineage: field-truth #233, spec+B0 #234, B1 engine #235.
// ===================================================================================================

/// <summary>
/// Sent by Core to the reader: find the most-profitable NDC for one (medication, insurance plan) pair.
/// The medication is identified by a drug-group key (the PioneerRx equivalents/GPI grouping the recon
/// maps — B0 §B-Q4); the plan by its PioneerRx plan/payer id. <c>Patches</c> mirrors Feature A: the job's
/// active learned selector corrections for the UIA modality (null/empty = builtin-only).
/// </summary>
public record PreferredNdcRequest(
    string JobId,
    int RowIndex,
    string DrugGroupKey,
    string PlanId,
    IReadOnlyList<SelectorPatch>? Patches = null);

/// <summary>
/// One candidate NDC for the (medication, plan), as the reader surfaces it. Mirrors the per-NDC inputs
/// ProfitOptimizer.NdcCandidate consumes. <c>AcquisitionCost</c> is the SOURCED cost (reuse Feature A's
/// cheapest-available-supplier <c>CostPerUnit</c>, or the item's cost-used-for-profit — B0 §A-Q4 / §B-Q
/// decides which Nadim reasons with). <c>Reimbursement</c> is the plan-specific expected pay (the new
/// data path B0 maps). Both nullable + <c>Status</c> carried so the engine can fail closed on a missing
/// number exactly as it fails closed on a non-positive cost — never a guessed profit.
/// </summary>
public record PreferredNdcCandidate(
    string Ndc,
    string Manufacturer,
    decimal? AcquisitionCost,
    decimal? Reimbursement,
    string Status);

/// <summary>How a reimbursement figure was obtained — sets the report's honesty framing (B0 §5.2: is
/// reimbursement deterministic pre-claim, or only known after adjudication?). The pharmacist must know
/// whether a number is a live contract/MAC rate or an estimate from recent paid claims.</summary>
public enum ReimbursementBasis
{
    /// <summary>Unknown / not yet determined (default before the reader is implemented).</summary>
    Unspecified = 0,
    /// <summary>A stored per-(NDC, plan) contract / MAC / fee-schedule rate — deterministic pre-claim.</summary>
    ContractOrMac = 1,
    /// <summary>Estimated from recently ADJUDICATED claims for this NDC under this plan — historical.</summary>
    AdjudicatedHistory = 2,
}

/// <summary>
/// Returned by the reader to Core: the candidate set + provenance, BEFORE B1 judges it. Keeping the
/// reader (gather) separate from ProfitOptimizer (judge) mirrors Feature A's split (PricingGridReader
/// reads; SelectCheapest decides) — the judgment stays pure + testable, the IO stays in the reader.
/// <c>Found</c> = the reader located the (medication, plan) and at least one candidate; on false,
/// <c>ErrorMessage</c> says why (e.g. "plan_not_found", "no_reimbursement_data"). <c>Observations</c> =
/// GREEN-tier selector telemetry for the UIA modality (null for SQL).
/// </summary>
public record PreferredNdcReadResult(
    string JobId,
    int RowIndex,
    string DrugGroupKey,
    string PlanId,
    bool Found,
    IReadOnlyList<PreferredNdcCandidate> Candidates,
    ReimbursementBasis Basis,
    string? ErrorMessage,
    IReadOnlyList<SelectorObservation>? Observations = null);

/// <summary>
/// Pluggable preferred-NDC data source — the Feature B analog of <see cref="ISupplierPriceLookup"/>.
/// "Given one (medication, plan), return the candidate NDCs with cost + reimbursement." Implementations:
/// SQL (primary, if B0 proves reimbursement is queryable), UIA (via Helper IPC, slower), fake (in-memory,
/// for tests). Lets the Feature B runner stay ignorant of data source — pick the modality that sees truth
/// (the modality-agnostic thesis), never gate on one.
///
/// <para><b>READ-ONLY by contract.</b> This interface only READS (gather → judge → report). Setting the
/// PioneerRx preferred item is the SEPARATE, default-OFF B4 writeback (gated on TOS/BAA + the full safety
/// gate) — deliberately NOT part of this reader, so the report path can never accidentally "put".</para>
/// </summary>
public interface IPreferredNdcDataSource
{
    Task<PreferredNdcReadResult> ReadCandidatesAsync(PreferredNdcRequest request, CancellationToken ct);
}
