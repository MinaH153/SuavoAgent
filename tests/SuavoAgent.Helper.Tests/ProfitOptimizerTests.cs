using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;
using Cand = SuavoAgent.Helper.Workflows.ProfitOptimizer.NdcCandidate;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// Feature B / B1 — the profit engine. Pure-logic coverage of the argmax(profit) judgment that decides
/// the preferred NDC. Mirrors PricingGridReaderTests (the argmin(cost) spine): the cheapest test proves
/// "don't trust position"; here the most-profitable test does the same, plus the delta-vs-runner-up and
/// the fail-closed-on-missing-data discipline the spec requires (§8 acceptance).
/// </summary>
public sealed class ProfitOptimizerTests
{
    private static Cand C(string ndc, decimal cost, decimal reimb, string status = "Available", string mfr = "Mfr") =>
        new(ndc, mfr, cost, reimb, status);

    // ── argmax: most profit, not input order ──

    [Fact]
    public void SelectMostProfitable_picks_max_profit_not_first_row()
    {
        // Report row order is not authoritative — compute the max over ALL candidates.
        var rows = new[]
        {
            C("11111111111", cost: 4.00m, reimb: 10.00m),  // profit 6.00 (first, but not best)
            C("22222222222", cost: 3.00m, reimb: 12.00m),  // profit 9.00 (best)
            C("33333333333", cost: 8.00m, reimb: 12.00m),  // profit 4.00
        };

        var r = ProfitOptimizer.SelectMostProfitable(rows);

        Assert.NotNull(r);
        Assert.Equal("22222222222", r!.Value.Ndc);
        Assert.Equal(9.00m, r.Value.Profit);
    }

    [Fact]
    public void Profit_is_reimbursement_minus_cost()
    {
        var r = ProfitOptimizer.SelectMostProfitable(new[] { C("n", cost: 3.25m, reimb: 10.00m) });
        Assert.Equal(6.75m, r!.Value.Profit);
        Assert.Equal(3.25m, r.Value.AcquisitionCost);
        Assert.Equal(10.00m, r.Value.Reimbursement);
    }

    // ── delta vs runner-up (the pharmacist's confidence signal) ──

    [Fact]
    public void Delta_is_gap_over_second_best()
    {
        var rows = new[]
        {
            C("a", cost: 2m, reimb: 12m),   // profit 10
            C("b", cost: 3m, reimb: 10m),   // profit 7  (runner-up)
            C("c", cost: 5m, reimb: 9m),    // profit 4
        };

        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("a", r!.Value.Ndc);
        Assert.Equal(3m, r.Value.DeltaOverRunnerUp); // 10 − 7
    }

    [Fact]
    public void Delta_is_null_for_single_candidate()
    {
        var r = ProfitOptimizer.SelectMostProfitable(new[] { C("only", cost: 2m, reimb: 9m) });
        Assert.NotNull(r);
        Assert.Null(r!.Value.DeltaOverRunnerUp);
    }

    [Fact]
    public void Delta_is_zero_on_exact_profit_tie()
    {
        // Two NDCs, same profit — an honest "equivalent, verify" 0 delta, not a missing/fake gap.
        var rows = new[]
        {
            C("aaa", cost: 2m, reimb: 9m),   // profit 7
            C("bbb", cost: 4m, reimb: 11m),  // profit 7
        };
        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal(0m, r!.Value.DeltaOverRunnerUp);
        Assert.Equal(7m, r.Value.Profit);
    }

    // ── tie-breaks (deterministic) ──

    [Fact]
    public void Tie_on_profit_prefers_lower_acquisition_cost()
    {
        var rows = new[]
        {
            C("highcost", cost: 8m, reimb: 15m),  // profit 7, cost 8
            C("lowcost",  cost: 2m, reimb: 9m),   // profit 7, cost 2 → wins (less cash tied up)
        };
        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("lowcost", r!.Value.Ndc);
    }

    [Fact]
    public void Fully_tied_prefers_ordinal_lower_ndc_and_is_deterministic()
    {
        var rows = new[]
        {
            C("99999999999", cost: 3m, reimb: 10m),
            C("11111111111", cost: 3m, reimb: 10m), // same profit + same cost → ordinal-lower NDC wins
        };
        var a = ProfitOptimizer.SelectMostProfitable(rows);
        var b = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("11111111111", a!.Value.Ndc);
        Assert.Equal(a.Value.Ndc, b!.Value.Ndc); // stable across calls
    }

    // ── eligibility / fail-closed (mirrors SelectCheapest's skips) ──

    [Fact]
    public void Skips_discontinued_even_if_more_profitable()
    {
        var rows = new[]
        {
            C("ghost", cost: 1m, reimb: 20m, status: "Discontinued"), // profit 19 but unusable
            C("real",  cost: 3m, reimb: 10m, status: "Available"),    // profit 7  → chosen
        };
        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("real", r!.Value.Ndc);
        Assert.Equal(7m, r.Value.Profit);
    }

    [Theory]
    [InlineData("Discontinued")]
    [InlineData("Unavailable")]
    [InlineData("Inactive")]
    [InlineData("Do Not Use")]
    public void Unusable_status_values_are_excluded(string status)
    {
        var rows = new[] { C("x", cost: 1m, reimb: 50m, status: status) };
        Assert.Null(ProfitOptimizer.SelectMostProfitable(rows)); // the only candidate is unusable → no pick
    }

    [Fact]
    public void Empty_status_is_usable()
    {
        var r = ProfitOptimizer.SelectMostProfitable(new[] { C("x", cost: 2m, reimb: 9m, status: "") });
        Assert.NotNull(r); // don't over-filter when status column is absent
    }

    [Fact]
    public void Skips_blank_ndc()
    {
        var rows = new[]
        {
            C("   ", cost: 1m, reimb: 20m),  // profit 19 but no NDC → skip
            C("real", cost: 3m, reimb: 10m),
        };
        Assert.Equal("real", ProfitOptimizer.SelectMostProfitable(rows)!.Value.Ndc);
    }

    [Fact]
    public void Skips_nonpositive_acquisition_cost_as_missing_data_sentinel()
    {
        // A zero/negative cost is a missing-data sentinel, NOT a free drug — it must never inflate profit.
        var rows = new[]
        {
            C("zerocost", cost: 0m, reimb: 99m),   // would be profit 99 if trusted → must be skipped
            C("negcost",  cost: -5m, reimb: 99m),  // likewise
            C("real",     cost: 4m, reimb: 10m),   // profit 6 → the only valid pick
        };
        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("real", r!.Value.Ndc);
        Assert.Equal(6m, r.Value.Profit);
    }

    [Fact]
    public void No_eligible_candidates_returns_null()
    {
        Assert.Null(ProfitOptimizer.SelectMostProfitable(System.Array.Empty<Cand>()));
        Assert.Null(ProfitOptimizer.SelectMostProfitable(new[] { C("x", cost: 0m, reimb: 9m) }));
    }

    // ── loss-making winner is still reported (caller decides) ──

    [Fact]
    public void Reports_best_even_when_all_profits_are_negative()
    {
        // Every NDC loses money under this plan; the engine still reports the LEAST-bad (max profit),
        // truthfully negative — the report/caller flags "all unprofitable", it isn't silently dropped.
        var rows = new[]
        {
            C("a", cost: 10m, reimb: 6m),  // profit -4  → least bad
            C("b", cost: 10m, reimb: 3m),  // profit -7
        };
        var r = ProfitOptimizer.SelectMostProfitable(rows);
        Assert.Equal("a", r!.Value.Ndc);
        Assert.Equal(-4m, r.Value.Profit);
        Assert.Equal(3m, r.Value.DeltaOverRunnerUp); // -4 − (-7)
    }

    // ── money parsing (identical posture to PricingGridReader.TryParseCost) ──

    [Theory]
    [InlineData("3.16", true, 3.16)]
    [InlineData("$12.99", true, 12.99)]
    [InlineData(" 1,234.50 ", true, 1234.50)]
    [InlineData("", false, 0)]
    [InlineData("N/A", false, 0)]
    public void TryParseMoney_matches_invariant_lenient_posture(string text, bool ok, double expected)
    {
        var parsed = ProfitOptimizer.TryParseMoney(text, out var v);
        Assert.Equal(ok, parsed);
        if (ok) Assert.Equal((decimal)expected, v);
    }

    // ── B2→B1 seam: ToCandidates projects the reader contract, fail-closed on missing numbers ──

    [Fact]
    public void ToCandidates_drops_candidates_missing_cost_or_reimbursement()
    {
        var read = new[]
        {
            new PreferredNdcCandidate("good",     "M", AcquisitionCost: 3m,   Reimbursement: 10m,   Status: "Available"),
            new PreferredNdcCandidate("nocost",   "M", AcquisitionCost: null, Reimbursement: 10m,   Status: "Available"),
            new PreferredNdcCandidate("noreimb",  "M", AcquisitionCost: 3m,   Reimbursement: null,  Status: "Available"),
            new PreferredNdcCandidate("neither",  "M", AcquisitionCost: null, Reimbursement: null,  Status: "Available"),
        };

        var mapped = ProfitOptimizer.ToCandidates(read);

        Assert.Single(mapped);
        Assert.Equal("good", mapped[0].Ndc); // only the fully-populated candidate survives the boundary
    }

    [Fact]
    public void ToCandidates_carries_status_through_for_the_engine_filter()
    {
        var read = new[]
        {
            new PreferredNdcCandidate("x", "M", 3m, 10m, Status: "Discontinued"),
        };
        var mapped = ProfitOptimizer.ToCandidates(read);
        Assert.Equal("Discontinued", mapped[0].Status); // status preserved so SelectMostProfitable can filter it
    }

    [Fact]
    public void Full_seam_read_then_project_then_select_endToEnd()
    {
        // The realistic chain a B3 runner executes: reader result → ToCandidates → SelectMostProfitable.
        var readResult = new PreferredNdcReadResult(
            JobId: "j", RowIndex: 0, DrugGroupKey: "omeprazole-20", PlanId: "PLAN1",
            Found: true,
            Candidates: new[]
            {
                new PreferredNdcCandidate("11111111111", "Mfr A", 4m, 10m, "Available"),   // profit 6
                new PreferredNdcCandidate("22222222222", "Mfr B", 3m, 12m, "Available"),   // profit 9 → best
                new PreferredNdcCandidate("33333333333", "Mfr C", 1m, 30m, "Discontinued"),// profit 29 but unusable
                new PreferredNdcCandidate("44444444444", "Mfr D", null, 50m, "Available"), // missing cost → dropped
            },
            Basis: ReimbursementBasis.ContractOrMac,
            ErrorMessage: null);

        var picked = ProfitOptimizer.SelectMostProfitable(ProfitOptimizer.ToCandidates(readResult.Candidates));

        Assert.NotNull(picked);
        Assert.Equal("22222222222", picked!.Value.Ndc); // discontinued + missing-cost excluded; argmax of the rest
        Assert.Equal(9m, picked.Value.Profit);
        Assert.Equal(3m, picked.Value.DeltaOverRunnerUp); // 9 − 6
    }
}
