using System.Globalization;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Pure, bounded Feature-B gross-margin-proxy judgment. It computes reimbursement minus acquisition;
/// it does not claim net profit because downstream fees, reversals, rebates, and clawbacks are not
/// inputs. It never guesses identity, availability, eligibility, amount
/// denominator, or missing money. The orchestration layer validates evidence provenance and recency;
/// this layer independently enforces canonical NDCs, duplicate refusal, affirmative candidate gates,
/// bounded decimal inputs, and checked arithmetic before selecting argmax(reimbursement - cost).
/// </summary>
public static class ProfitOptimizer
{
    public readonly record struct NdcCandidate(
        string Ndc,
        string Manufacturer,
        decimal AcquisitionCost,
        decimal Reimbursement,
        bool Available,
        bool Eligible);

    public readonly record struct PreferredNdc(
        string Ndc,
        string Manufacturer,
        decimal Profit,
        decimal AcquisitionCost,
        decimal Reimbursement,
        decimal? DeltaOverRunnerUp);

    private readonly record struct EvaluatedCandidate(NdcCandidate Candidate, decimal Profit);

    /// <summary>Invariant money parser retained for bounded source adapters. Eligibility still requires
    /// <see cref="IsValidAmount"/>, so parsing alone never makes a value recommendation-ready.</summary>
    public static bool TryParseMoney(string? text, out decimal value) =>
        decimal.TryParse(
            (text ?? string.Empty).Trim(),
            NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out value);

    public static bool IsValidAmount(decimal value)
    {
        if (value <= 0 || value > PreferredNdcEvidencePolicy.MaximumAmount)
            return false;
        var scale = (decimal.GetBits(value)[3] >> 16) & 0x7f;
        return scale <= PreferredNdcEvidencePolicy.MaximumDecimalScale;
    }

    public static PreferredNdc? SelectMostProfitable(IEnumerable<NdcCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var evaluated = new List<EvaluatedCandidate>();
        var identities = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (identities.Count >= PreferredNdcEvidencePolicy.MaximumCandidatesPerWorkbook)
                return null;
            if (!PreferredNdcEvidencePolicy.IsCanonicalNdc11(candidate.Ndc) ||
                !identities.Add(candidate.Ndc))
                return null;
            if (!candidate.Available || !candidate.Eligible)
                continue;
            if (!IsValidAmount(candidate.AcquisitionCost) ||
                !IsValidAmount(candidate.Reimbursement))
                return null;

            decimal profit;
            try
            {
                profit = checked(candidate.Reimbursement - candidate.AcquisitionCost);
            }
            catch (OverflowException)
            {
                return null;
            }
            evaluated.Add(new EvaluatedCandidate(candidate, profit));
        }

        if (evaluated.Count == 0)
            return null;

        var bestIndex = 0;
        for (var index = 1; index < evaluated.Count; index++)
        {
            if (IsBetter(evaluated[index], evaluated[bestIndex]))
                bestIndex = index;
        }
        var best = evaluated[bestIndex];

        decimal? delta = null;
        if (evaluated.Count > 1)
        {
            decimal? runnerUpProfit = null;
            for (var index = 0; index < evaluated.Count; index++)
            {
                if (index == bestIndex)
                    continue;
                if (runnerUpProfit is null || evaluated[index].Profit > runnerUpProfit.Value)
                    runnerUpProfit = evaluated[index].Profit;
            }
            try
            {
                delta = checked(best.Profit - runnerUpProfit!.Value);
            }
            catch (OverflowException)
            {
                return null;
            }
        }

        return new PreferredNdc(
            best.Candidate.Ndc,
            best.Candidate.Manufacturer,
            best.Profit,
            best.Candidate.AcquisitionCost,
            best.Candidate.Reimbursement,
            delta);
    }

    /// <summary>Projects only complete amounts. A caller that needs pair-wide completeness guarantees
    /// must validate the original collection first; <see cref="PreferredNdcReportRunner"/> does so.</summary>
    public static IReadOnlyList<NdcCandidate> ToCandidates(IEnumerable<PreferredNdcCandidate> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return read
            .Where(candidate =>
                candidate.AcquisitionCost is not null && candidate.Reimbursement is not null)
            .Select(candidate => new NdcCandidate(
                candidate.Ndc,
                candidate.Manufacturer,
                candidate.AcquisitionCost!.Value,
                candidate.Reimbursement!.Value,
                candidate.Available,
                candidate.Eligible))
            .ToArray();
    }

    private static bool IsBetter(EvaluatedCandidate candidate, EvaluatedCandidate best)
    {
        if (candidate.Profit != best.Profit)
            return candidate.Profit > best.Profit;
        if (candidate.Candidate.AcquisitionCost != best.Candidate.AcquisitionCost)
            return candidate.Candidate.AcquisitionCost < best.Candidate.AcquisitionCost;
        return string.CompareOrdinal(candidate.Candidate.Ndc, best.Candidate.Ndc) < 0;
    }
}
