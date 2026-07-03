using System.Globalization;
using SuavoAgent.Contracts.Pricing;

namespace SuavoAgent.Helper.Workflows;

/// <summary>
/// Feature B (preferred-NDC-by-insurance) — B1 the pure profit engine. The <c>argmax(profit)</c> MIRROR
/// of <see cref="PricingGridReader.SelectCheapest"/>'s <c>argmin(cost)</c>: given the candidate NDCs for
/// one (medication, insurance) pair, each with an acquisition cost and an expected reimbursement under
/// that plan, pick the NDC that MAXIMIZES profit (<c>reimbursement − cost</c>) and report the delta over
/// the runner-up.
///
/// <para>UI-free + IO-free by construction — pure logic over numbers, unit-testable without a box. The
/// data (cost + reimbursement per NDC) is the reader's job (B2 / the SQL recon, B0); this is only the
/// judgment. Spec: <c>docs/recon/feature-b-preferred-ndc-by-insurance-spec.md</c> §3 + §8.</para>
///
/// <para><b>Mirrors Feature A's selection discipline exactly</b> (so the moat's correctness invariants
/// carry across both surfaces): compute the extremum over ALL candidates (never trust input order — a
/// PioneerRx report's row order is not authoritative); skip ineligible candidates (blank NDC, unusable
/// status, missing/invalid numbers); and <b>fail closed</b> — return null rather than a guessed pick when
/// nothing qualifies. The number that reaches the pharmacy must be REAL: a wrong preferred-NDC silently
/// steers every future fill of that drug+plan to a worse margin, the exact trust-killer Feature A guards
/// against on the cost side.</para>
/// </summary>
public static class ProfitOptimizer
{
    /// <summary>One candidate NDC for a (medication, plan): its acquisition cost and the expected
    /// reimbursement under that plan. <paramref name="Status"/> mirrors the Supplier-grid status
    /// discipline (Available / Discontinued / …); empty = usable (don't over-filter when absent).</summary>
    public readonly record struct NdcCandidate(
        string Ndc,
        string Manufacturer,
        decimal AcquisitionCost,
        decimal Reimbursement,
        string Status = "")
    {
        /// <summary>Profit under the plan = reimbursement − acquisition cost. May be ≤ 0 (a loss).</summary>
        public decimal Profit => Reimbursement - AcquisitionCost;
    }

    /// <summary>
    /// The most-profitable NDC + its profit, plus the delta over the next-best candidate (the spec's
    /// "delta vs. runner-up" — the pharmacist's confidence signal: a tiny delta means the choice barely
    /// matters / verify; a large delta means a clear winner). <paramref name="DeltaOverRunnerUp"/> is
    /// null when there is exactly one eligible candidate (no runner-up to compare).
    /// </summary>
    public readonly record struct PreferredNdc(
        string Ndc,
        string Manufacturer,
        decimal Profit,
        decimal AcquisitionCost,
        decimal Reimbursement,
        decimal? DeltaOverRunnerUp);

    /// <summary>Parse a money/number cell from a report/SQL string — InvariantCulture, lenient styles
    /// (matches <see cref="PricingGridReader.TryParseCost"/> so cost parsing is identical across
    /// features). A leading <c>$</c> or thousands separators are tolerated.</summary>
    public static bool TryParseMoney(string? text, out decimal value) =>
        decimal.TryParse(
            (text ?? string.Empty).Trim().TrimStart('$'),
            NumberStyles.Any,
            CultureInfo.InvariantCulture,
            out value);

    /// <summary>
    /// Pick the NDC with the highest profit among ELIGIBLE candidates. Eligibility mirrors
    /// SelectCheapest: non-blank NDC, usable status (<see cref="PricingGridReader.IsUsableStatus"/>),
    /// and a positive acquisition cost (a zero/negative cost is a missing-data sentinel, not a free
    /// drug — fail closed on it, never let it inflate "profit"). Returns null when no candidate
    /// qualifies (fail-closed: produce no recommendation rather than a wrong one).
    ///
    /// <para>Tie-break (deterministic, like the rest of the moat): equal profit → lower acquisition cost
    /// wins (less cash tied up for the same margin); still tied → ordinal-lower NDC (stable). The delta
    /// is computed against the best DISTINCT-profit runner-up, so an exact tie reports a 0 delta — a
    /// truthful "these are equivalent, verify" signal, not a fake gap.</para>
    /// </summary>
    public static PreferredNdc? SelectMostProfitable(IEnumerable<NdcCandidate> candidates)
    {
        var eligible = new List<NdcCandidate>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Ndc)) continue;
            if (c.AcquisitionCost <= 0) continue;              // missing-cost sentinel → fail closed
            if (!PricingGridReader.IsUsableStatus(c.Status)) continue;
            eligible.Add(c);
        }

        if (eligible.Count == 0) return null;

        // Winner by INDEX (not by value) so the runner-up scan excludes exactly the winning position —
        // a value-identical duplicate row is then correctly still eligible as the runner-up.
        var bestIdx = 0;
        for (var i = 1; i < eligible.Count; i++)
        {
            if (IsBetter(eligible[i], eligible[bestIdx])) bestIdx = i;
        }
        var best = eligible[bestIdx];

        // Delta = best.Profit − (highest profit among every OTHER eligible candidate). Null when best is
        // the only candidate. An exact-tie runner-up yields 0 (honest "equivalent — verify"), not a
        // missing delta. Loss-making winners are still reported (profit can be ≤ 0); the caller decides
        // whether a negative best profit warrants flagging.
        decimal? delta = null;
        if (eligible.Count > 1)
        {
            decimal runnerUpProfit = decimal.MinValue;
            for (var i = 0; i < eligible.Count; i++)
            {
                if (i == bestIdx) continue;
                if (eligible[i].Profit > runnerUpProfit) runnerUpProfit = eligible[i].Profit;
            }
            delta = best.Profit - runnerUpProfit;
        }

        return new PreferredNdc(best.Ndc, best.Manufacturer, best.Profit,
            best.AcquisitionCost, best.Reimbursement, delta);
    }

    /// <summary>Is <paramref name="a"/> a strictly better pick than the current best? Higher profit
    /// wins; equal profit → lower acquisition cost (less cash tied up for the same margin); still equal →
    /// ordinal-lower NDC (stable determinism, so the same input always yields the same preferred NDC).</summary>
    private static bool IsBetter(NdcCandidate a, NdcCandidate best)
    {
        if (a.Profit != best.Profit) return a.Profit > best.Profit;
        if (a.AcquisitionCost != best.AcquisitionCost) return a.AcquisitionCost < best.AcquisitionCost;
        return string.CompareOrdinal(a.Ndc, best.Ndc) < 0;
    }

    /// <summary>
    /// The B2→B1 seam: project the reader's contract candidates (<see cref="PreferredNdcCandidate"/>,
    /// with NULLABLE cost/reimbursement) into the engine's <see cref="NdcCandidate"/> inputs. A candidate
    /// missing EITHER number is DROPPED here (fail-closed at the boundary) — there is no real profit to
    /// compute from a missing cost or a missing reimbursement, and a dropped candidate is invisible to
    /// argmax rather than defaulting to a misleading zero. Status carries through unchanged so the
    /// engine's usable-status filter still applies. Pure; <see cref="SelectMostProfitable"/> consumes the
    /// result. (Lives here, not in Contracts, because Contracts must not depend on Helper.)
    /// </summary>
    public static IReadOnlyList<NdcCandidate> ToCandidates(IEnumerable<PreferredNdcCandidate> read)
    {
        var list = new List<NdcCandidate>();
        foreach (var c in read)
        {
            if (c.AcquisitionCost is not { } cost) continue;       // missing cost → drop, fail closed
            if (c.Reimbursement is not { } reimb) continue;        // missing reimbursement → drop
            list.Add(new NdcCandidate(c.Ndc, c.Manufacturer, cost, reimb, c.Status));
        }
        return list;
    }
}
