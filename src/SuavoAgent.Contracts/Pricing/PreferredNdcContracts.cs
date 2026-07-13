using SuavoAgent.Contracts.Learning;

namespace SuavoAgent.Contracts.Pricing;

// ===================================================================================================
// Feature B (preferred-NDC-by-insurance) — B2 the READER CONTRACT (the seam between B1 and the box).
//
// Feature A's modality-agnostic seam is ISupplierPriceLookup: "given one NDC → cheapest supplier+cost",
// implemented by SQL (primary), UIA (via Helper IPC), or fake (tests). These contracts are the EXACT
// analog for Feature B: "given one (medication, plan) → the candidate NDCs each with acquisition cost +
// expected reimbursement", which ProfitOptimizer (B1, already built) then ranks by the explicit
// reimbursement-minus-acquisition gross-margin proxy. That value is not net profit.
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
/// Sent by Core to the reader: find the highest gross-margin-proxy NDC for one
/// (medication, insurance plan) pair. Clinical interchangeability is an upstream pharmacist gate.
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
/// One candidate NDC for the (medication, plan), as the reader surfaces it. The two amounts are nullable
/// so incomplete source data is represented honestly, but an otherwise-eligible candidate with either
/// amount missing blocks the entire pair from becoming <c>OK</c>. Availability and plan eligibility are
/// affirmative booleans: unknown is false, never implicitly usable. Amount basis, evidence provenance,
/// evidence age, and historical sample count travel with the numbers so a caller cannot subtract unlike
/// units or recommend from anonymous/stale evidence.
/// </summary>
public record PreferredNdcCandidate(
    string Ndc,
    string Manufacturer,
    decimal? AcquisitionCost,
    decimal? Reimbursement,
    bool Available,
    bool Eligible,
    PreferredNdcAmountBasis AcquisitionAmountBasis,
    PreferredNdcAmountBasis ReimbursementAmountBasis,
    PreferredNdcEvidenceProvenance AcquisitionEvidenceProvenance,
    PreferredNdcEvidenceProvenance ReimbursementEvidenceProvenance,
    DateTimeOffset? AcquisitionEvidenceAsOfUtc,
    DateTimeOffset? ReimbursementEvidenceAsOfUtc,
    int? HistoricalSampleCount);

/// <summary>The denominator shared by acquisition and reimbursement. Profit is valid only when both
/// amounts use the same explicit, non-unspecified basis.</summary>
public enum PreferredNdcAmountBasis
{
    Unspecified = 0,
    PerDispensedFill = 1,
    PerPackage = 2,
    PerUnit = 3,
}

/// <summary>The concrete source cohort that produced the evidence. This is separate from
/// <see cref="ReimbursementBasis"/>: basis says how reimbursement was calculated; provenance says where
/// the evidence came from. Unspecified provenance can never produce a recommendation.</summary>
public enum PreferredNdcEvidenceProvenance
{
    Unspecified = 0,
    PioneerRxAcquisitionCostExport = 1,
    PioneerRxContractOrMacExport = 2,
    PioneerRxAdjudicatedClaimsExport = 3,
}

/// <summary>Fixed local safety bounds for the offline report. These are admission/recommendation gates,
/// not claims about PioneerRx data quality. Any value outside the envelope requires manual review.</summary>
public static class PreferredNdcEvidencePolicy
{
    public const decimal MaximumAmount = 1_000_000m;
    public const int MaximumDecimalScale = 4;
    public const int MaximumCandidatesPerWorkbook = 10_000;
    public const int MinimumHistoricalSampleCount = 10;
    public const int MaximumHistoricalSampleCount = 1_000_000;
    public static readonly TimeSpan MaximumEvidenceAge = TimeSpan.FromDays(30);
    public static readonly TimeSpan MaximumFutureClockSkew = TimeSpan.FromMinutes(5);

    public static bool IsCanonicalNdc11(string? value) =>
        value is { Length: 11 } && value.All(character => character is >= '0' and <= '9');
}

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

/// <summary>
/// B3 — one line of the READ-ONLY report Nadim asked for ("run me a report and I'll do it manually"):
/// for a (medication, plan), the highest reimbursement-minus-acquisition candidate and the numbers
/// behind it, so the pharmacist can review it before setting the PioneerRx preferred item by hand.
/// This is a gross-margin proxy, not net profit. <c>Status</c> is explicit — "OK",
/// "NO_DATA" (reader found nothing), "NO_ELIGIBLE" (candidates present but none had a usable cost +
/// reimbursement → fail-closed, no guess), "MANUAL_REVIEW:NO_PROFITABLE_CANDIDATE" (all eligible
/// choices have a non-positive proxy), or "ERROR:{why}". The dollar fields are null unless Status=OK.
/// <c>Basis</c> carries the honesty framing (a live contract/MAC rate vs. an estimate from paid claims).
/// </summary>
public record PreferredNdcReportRow(
    string DrugGroupKey,
    string PlanId,
    string Status,
    string? PreferredNdc,
    string? Manufacturer,
    decimal? AcquisitionCost,
    decimal? Reimbursement,
    decimal? Profit,
    decimal? DeltaOverRunnerUp,
    ReimbursementBasis Basis,
    PreferredNdcAmountBasis AmountBasis,
    PreferredNdcEvidenceProvenance AcquisitionEvidenceProvenance,
    PreferredNdcEvidenceProvenance ReimbursementEvidenceProvenance,
    DateTimeOffset? AcquisitionEvidenceAsOfUtc,
    DateTimeOffset? ReimbursementEvidenceAsOfUtc,
    int? HistoricalSampleCount,
    int CandidatesConsidered);

/// <summary>Explicit report statuses — a wrong preferred-NDC steers every future fill of that drug+plan
/// to a worse margin, so a row is only "OK" when a real winner was found; every other outcome is a named
/// non-price marker, never a guessed number.</summary>
public static class PreferredNdcStatus
{
    public const string Ok = "OK";
    public const string NoData = "NO_DATA";
    public const string NoEligible = "NO_ELIGIBLE";
    public const string ManualReviewIncompleteCandidateData = "MANUAL_REVIEW:INCOMPLETE_CANDIDATE_DATA";
    public const string ManualReviewCandidateIdentityInvalid = "MANUAL_REVIEW:CANDIDATE_IDENTITY_INVALID";
    public const string ManualReviewDuplicateNdc = "MANUAL_REVIEW:DUPLICATE_NDC";
    public const string ManualReviewEvidenceInvalid = "MANUAL_REVIEW:EVIDENCE_INVALID";
    public const string ManualReviewNoProfitableCandidate = "MANUAL_REVIEW:NO_PROFITABLE_CANDIDATE";
    public static string Error(string why) => $"ERROR:{why}";
}
