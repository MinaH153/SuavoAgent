using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Read-only Feature-B report orchestration. A row becomes <c>OK</c> only when the response identity is
/// exact, every candidate identity is canonical and unique, every otherwise-eligible candidate has
/// complete bounded amounts, availability/plan eligibility are affirmative, amount denominators match,
/// and named evidence is recent enough. Any pair-local exception becomes an explicit row status while
/// cancellation still stops the batch.
/// </summary>
public sealed class PreferredNdcReportRunner
{
    private readonly IPreferredNdcDataSource _source;
    private readonly TimeProvider _timeProvider;

    public PreferredNdcReportRunner(
        IPreferredNdcDataSource source,
        TimeProvider? timeProvider = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<PreferredNdcReportRow>> RunAsync(
        IReadOnlyList<PreferredNdcRequest> requests,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var rows = new List<PreferredNdcReportRow>(requests.Count);
        foreach (var request in requests)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await BuildContainedRowAsync(request, ct).ConfigureAwait(false));
        }
        return rows.ToArray();
    }

    private async Task<PreferredNdcReportRow> BuildContainedRowAsync(
        PreferredNdcRequest request,
        CancellationToken ct)
    {
        try
        {
            return await BuildRowAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.Error(ex.GetType().Name),
                ReimbursementBasis.Unspecified,
                considered: 0);
        }
    }

    private async Task<PreferredNdcReportRow> BuildRowAsync(
        PreferredNdcRequest request,
        CancellationToken ct)
    {
        var read = await _source.ReadCandidatesAsync(request, ct).ConfigureAwait(false);
        if (!HasExactResponseIdentity(request, read))
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.Error("response_identity_mismatch"),
                ReimbursementBasis.Unspecified,
                considered: 0);
        }

        var candidates = read.Candidates ?? Array.Empty<PreferredNdcCandidate>();
        if (!read.Found || candidates.Count == 0)
        {
            var status = read.ErrorMessage is { Length: > 0 } error
                ? PreferredNdcStatus.Error(SafeReaderCode(error))
                : PreferredNdcStatus.NoData;
            return EmptyRow(request, status, read.Basis, candidates.Count);
        }
        if (candidates.Count > PreferredNdcEvidencePolicy.MaximumCandidatesPerWorkbook)
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.ManualReviewEvidenceInvalid,
                read.Basis,
                candidates.Count);
        }

        var ndcs = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (!PreferredNdcEvidencePolicy.IsCanonicalNdc11(candidate.Ndc))
            {
                return EmptyRow(
                    request,
                    PreferredNdcStatus.ManualReviewCandidateIdentityInvalid,
                    read.Basis,
                    candidates.Count);
            }
            if (!ndcs.Add(candidate.Ndc))
            {
                return EmptyRow(
                    request,
                    PreferredNdcStatus.ManualReviewDuplicateNdc,
                    read.Basis,
                    candidates.Count);
            }
        }

        var affirmative = candidates
            .Where(candidate => candidate.Available && candidate.Eligible)
            .ToArray();
        if (affirmative.Length == 0)
            return EmptyRow(request, PreferredNdcStatus.NoEligible, read.Basis, candidates.Count);

        if (affirmative.Any(candidate =>
                candidate.AcquisitionCost is null || candidate.Reimbursement is null))
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.ManualReviewIncompleteCandidateData,
                read.Basis,
                candidates.Count);
        }

        var now = _timeProvider.GetUtcNow();
        if (read.Basis == ReimbursementBasis.Unspecified ||
            affirmative.Any(candidate =>
                candidate.AcquisitionAmountBasis != affirmative[0].AcquisitionAmountBasis) ||
            affirmative.Any(candidate => !HasRecommendationGradeEvidence(candidate, read.Basis, now)))
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.ManualReviewEvidenceInvalid,
                read.Basis,
                candidates.Count);
        }

        var priced = affirmative.Select(candidate => new ProfitOptimizer.NdcCandidate(
            candidate.Ndc,
            candidate.Manufacturer,
            candidate.AcquisitionCost!.Value,
            candidate.Reimbursement!.Value,
            candidate.Available,
            candidate.Eligible));
        var best = ProfitOptimizer.SelectMostProfitable(priced);
        if (best is not { } winner)
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.ManualReviewEvidenceInvalid,
                read.Basis,
                candidates.Count);
        }
        if (winner.Profit <= 0)
        {
            return EmptyRow(
                request,
                PreferredNdcStatus.ManualReviewNoProfitableCandidate,
                read.Basis,
                candidates.Count);
        }

        var winnerEvidence = affirmative.Single(candidate =>
            string.Equals(candidate.Ndc, winner.Ndc, StringComparison.Ordinal));
        return new PreferredNdcReportRow(
            request.DrugGroupKey,
            request.PlanId,
            PreferredNdcStatus.Ok,
            winner.Ndc,
            winner.Manufacturer,
            winner.AcquisitionCost,
            winner.Reimbursement,
            winner.Profit,
            winner.DeltaOverRunnerUp,
            read.Basis,
            winnerEvidence.AcquisitionAmountBasis,
            winnerEvidence.AcquisitionEvidenceProvenance,
            winnerEvidence.ReimbursementEvidenceProvenance,
            winnerEvidence.AcquisitionEvidenceAsOfUtc,
            winnerEvidence.ReimbursementEvidenceAsOfUtc,
            winnerEvidence.HistoricalSampleCount,
            candidates.Count);
    }

    private static bool HasExactResponseIdentity(
        PreferredNdcRequest request,
        PreferredNdcReadResult read) =>
        string.Equals(request.JobId, read.JobId, StringComparison.Ordinal) &&
        request.RowIndex == read.RowIndex &&
        string.Equals(request.DrugGroupKey, read.DrugGroupKey, StringComparison.Ordinal) &&
        string.Equals(request.PlanId, read.PlanId, StringComparison.Ordinal);

    private static bool HasRecommendationGradeEvidence(
        PreferredNdcCandidate candidate,
        ReimbursementBasis reimbursementBasis,
        DateTimeOffset now)
    {
        if (candidate.AcquisitionAmountBasis == PreferredNdcAmountBasis.Unspecified ||
            candidate.ReimbursementAmountBasis != candidate.AcquisitionAmountBasis ||
            candidate.AcquisitionEvidenceProvenance !=
                PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport ||
            candidate.ReimbursementEvidenceProvenance ==
                PreferredNdcEvidenceProvenance.Unspecified ||
            !IsRecentUtc(candidate.AcquisitionEvidenceAsOfUtc, now) ||
            !IsRecentUtc(candidate.ReimbursementEvidenceAsOfUtc, now) ||
            candidate.HistoricalSampleCount is not { } sampleCount || sampleCount < 0 ||
            !ProfitOptimizer.IsValidAmount(candidate.AcquisitionCost!.Value) ||
            !ProfitOptimizer.IsValidAmount(candidate.Reimbursement!.Value))
            return false;

        return reimbursementBasis switch
        {
            ReimbursementBasis.ContractOrMac =>
                candidate.ReimbursementEvidenceProvenance ==
                PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            ReimbursementBasis.AdjudicatedHistory =>
                candidate.ReimbursementEvidenceProvenance ==
                PreferredNdcEvidenceProvenance.PioneerRxAdjudicatedClaimsExport &&
                sampleCount >= PreferredNdcEvidencePolicy.MinimumHistoricalSampleCount,
            _ => false,
        };
    }

    private static bool IsRecentUtc(DateTimeOffset? value, DateTimeOffset now) =>
        value is { } timestamp &&
        timestamp.Offset == TimeSpan.Zero &&
        timestamp <= now + PreferredNdcEvidencePolicy.MaximumFutureClockSkew &&
        now - timestamp <= PreferredNdcEvidencePolicy.MaximumEvidenceAge;

    private static string SafeReaderCode(string value)
    {
        var safe = value.Length is > 0 and <= 64 &&
            value.All(character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
        return safe ? value : "reader_failed";
    }

    private static PreferredNdcReportRow EmptyRow(
        PreferredNdcRequest request,
        string status,
        ReimbursementBasis basis,
        int considered) =>
        new(
            request.DrugGroupKey,
            request.PlanId,
            status,
            null,
            null,
            null,
            null,
            null,
            null,
            basis,
            PreferredNdcAmountBasis.Unspecified,
            PreferredNdcEvidenceProvenance.Unspecified,
            PreferredNdcEvidenceProvenance.Unspecified,
            null,
            null,
            null,
            considered);
}
