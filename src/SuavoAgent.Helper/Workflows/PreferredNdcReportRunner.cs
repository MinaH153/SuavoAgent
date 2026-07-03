using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Feature B (preferred-NDC-by-insurance) — B3 the REPORT runner. For each (medication, plan) request it
/// asks the reader (<see cref="IPreferredNdcDataSource"/>, the box-mapped SQL/UIA seam) for the candidate
/// NDCs, then applies the pure judgment (<see cref="ProfitOptimizer.SelectMostProfitable"/>, argmax of
/// reimbursement − acquisition cost) and emits one <see cref="PreferredNdcReportRow"/> per pair. This is
/// the READ-ONLY deliverable Nadim asked for — "run me a report and I'll do it manually" — and the spec's
/// v1: it carries 100% of the value with ~0% of the TOS risk (no write into PioneerRx).
///
/// <para>Mirrors <c>PricingJobRunner</c>'s split: the reader gathers (IO, box), the optimizer judges
/// (pure), the runner orchestrates + shapes the report row. Fail-closed at every step — a wrong
/// preferred NDC silently steers every future fill of that drug+plan to a worse margin, so a row is only
/// "OK" when the reader found the pair AND the optimizer had a real, cost+reimbursement-backed winner.</para>
/// </summary>
public sealed class PreferredNdcReportRunner
{
    private readonly IPreferredNdcDataSource _source;

    public PreferredNdcReportRunner(IPreferredNdcDataSource source) => _source = source;

    /// <summary>Builds the report — one row per request, in request order. Never throws for a single bad
    /// pair: a reader/optimizer failure becomes that row's explicit status, and the batch continues.</summary>
    public async Task<IReadOnlyList<PreferredNdcReportRow>> RunAsync(
        IReadOnlyList<PreferredNdcRequest> requests, CancellationToken ct)
    {
        var rows = new List<PreferredNdcReportRow>(requests.Count);
        foreach (var req in requests)
        {
            ct.ThrowIfCancellationRequested();
            rows.Add(await BuildRowAsync(req, ct).ConfigureAwait(false));
        }
        return rows;
    }

    private async Task<PreferredNdcReportRow> BuildRowAsync(PreferredNdcRequest req, CancellationToken ct)
    {
        PreferredNdcReadResult read;
        try
        {
            read = await _source.ReadCandidatesAsync(req, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Row(req, PreferredNdcStatus.Error(ex.GetType().Name), ReimbursementBasis.Unspecified, 0);
        }

        if (!read.Found || read.Candidates.Count == 0)
            return Row(req, read.ErrorMessage is { Length: > 0 } e ? PreferredNdcStatus.Error(e) : PreferredNdcStatus.NoData,
                read.Basis, read.Candidates?.Count ?? 0);

        // Project the reader's candidates onto the engine's shape. Only rows with BOTH a cost and a
        // reimbursement can be judged — a missing number is a fail-closed skip (never a guessed profit),
        // exactly as the optimizer fails closed on a non-positive cost.
        var priced = read.Candidates
            .Where(c => c.AcquisitionCost is { } && c.Reimbursement is { })
            .Select(c => new ProfitOptimizer.NdcCandidate(
                c.Ndc, c.Manufacturer, c.AcquisitionCost!.Value, c.Reimbursement!.Value, c.Status));

        var best = ProfitOptimizer.SelectMostProfitable(priced);
        if (best is not { } b)
            return Row(req, PreferredNdcStatus.NoEligible, read.Basis, read.Candidates.Count);

        return new PreferredNdcReportRow(
            req.DrugGroupKey, req.PlanId, PreferredNdcStatus.Ok,
            b.Ndc, b.Manufacturer, b.AcquisitionCost, b.Reimbursement, b.Profit, b.DeltaOverRunnerUp,
            read.Basis, read.Candidates.Count);
    }

    private static PreferredNdcReportRow Row(PreferredNdcRequest req, string status, ReimbursementBasis basis, int considered) =>
        new(req.DrugGroupKey, req.PlanId, status, null, null, null, null, null, null, basis, considered);
}
