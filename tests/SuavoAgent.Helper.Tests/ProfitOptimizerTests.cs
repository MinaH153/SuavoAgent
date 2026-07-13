using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;
using Candidate = SuavoAgent.Helper.Workflows.ProfitOptimizer.NdcCandidate;

namespace SuavoAgent.Helper.Tests;

public sealed class ProfitOptimizerTests
{
    private static Candidate C(
        string ndc,
        decimal cost,
        decimal reimbursement,
        bool available = true,
        bool eligible = true,
        string manufacturer = "Mfr") =>
        new(ndc, manufacturer, cost, reimbursement, available, eligible);

    [Fact]
    public void Selects_argmax_not_first_row_with_checked_profit()
    {
        var result = ProfitOptimizer.SelectMostProfitable(
        [
            C("11111111111", 4m, 10m),
            C("22222222222", 3m, 12m),
            C("33333333333", 8m, 12m),
        ]);

        Assert.Equal("22222222222", result!.Value.Ndc);
        Assert.Equal(9m, result.Value.Profit);
        Assert.Equal(3m, result.Value.DeltaOverRunnerUp);
    }

    [Fact]
    public void Exact_profit_tie_prefers_lower_cost_then_canonical_ndc()
    {
        var result = ProfitOptimizer.SelectMostProfitable(
        [
            C("33333333333", 4m, 11m),
            C("22222222222", 2m, 9m),
            C("11111111111", 2m, 9m),
        ]);

        Assert.Equal("11111111111", result!.Value.Ndc);
        Assert.Equal(0m, result.Value.DeltaOverRunnerUp);
    }

    [Fact]
    public void Requires_affirmative_availability_and_plan_eligibility()
    {
        var result = ProfitOptimizer.SelectMostProfitable(
        [
            C("11111111111", 1m, 100m, available: false),
            C("22222222222", 1m, 90m, eligible: false),
            C("33333333333", 3m, 10m),
        ]);

        Assert.Equal("33333333333", result!.Value.Ndc);
    }

    [Theory]
    [InlineData("11111-1111-11")]
    [InlineData("1111111111")]
    [InlineData("1111111111A")]
    public void Refuses_noncanonical_ndc(string ndc)
    {
        Assert.Null(ProfitOptimizer.SelectMostProfitable([C(ndc, 1m, 2m)]));
    }

    [Fact]
    public void Refuses_duplicate_ndc_instead_of_treating_duplicate_as_runner_up()
    {
        Assert.Null(ProfitOptimizer.SelectMostProfitable(
        [
            C("11111111111", 1m, 5m),
            C("11111111111", 2m, 6m),
        ]));
    }

    [Theory]
    [InlineData("0", "10")]
    [InlineData("-1", "10")]
    [InlineData("1000000.0001", "10")]
    [InlineData("1.00001", "10")]
    [InlineData("1", "1000000.0001")]
    [InlineData("1", "10.00001")]
    public void Refuses_out_of_bounds_or_over_precision_amounts(string costText, string reimbursementText)
    {
        var cost = decimal.Parse(costText, System.Globalization.CultureInfo.InvariantCulture);
        var reimbursement = decimal.Parse(
            reimbursementText,
            System.Globalization.CultureInfo.InvariantCulture);

        Assert.Null(ProfitOptimizer.SelectMostProfitable(
            [C("11111111111", cost, reimbursement)]));
    }

    [Fact]
    public void Returns_null_when_no_candidate_is_affirmatively_eligible()
    {
        Assert.Null(ProfitOptimizer.SelectMostProfitable(Array.Empty<Candidate>()));
        Assert.Null(ProfitOptimizer.SelectMostProfitable(
            [C("11111111111", 1m, 2m, available: false)]));
    }

    [Fact]
    public void Reports_least_bad_candidate_when_all_profits_are_negative()
    {
        var result = ProfitOptimizer.SelectMostProfitable(
        [
            C("11111111111", 10m, 6m),
            C("22222222222", 10m, 3m),
        ]);

        Assert.Equal("11111111111", result!.Value.Ndc);
        Assert.Equal(-4m, result.Value.Profit);
    }

    [Theory]
    [InlineData("3.16", true)]
    [InlineData("-1.25", true)]
    [InlineData("$12.99", false)]
    [InlineData("1,234.50", false)]
    [InlineData("N/A", false)]
    public void Money_parser_is_invariant_and_does_not_accept_decorated_values(string text, bool expected)
    {
        Assert.Equal(expected, ProfitOptimizer.TryParseMoney(text, out _));
    }

    [Fact]
    public void Projection_preserves_affirmative_flags_and_drops_missing_amounts()
    {
        var timestamp = new DateTimeOffset(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);
        var candidates = new[]
        {
            ContractCandidate("11111111111", 3m, 10m, timestamp),
            ContractCandidate("22222222222", null, 20m, timestamp),
        };

        var mapped = Assert.Single(ProfitOptimizer.ToCandidates(candidates));
        Assert.Equal("11111111111", mapped.Ndc);
        Assert.True(mapped.Available);
        Assert.True(mapped.Eligible);
    }

    private static PreferredNdcCandidate ContractCandidate(
        string ndc,
        decimal? cost,
        decimal? reimbursement,
        DateTimeOffset timestamp) =>
        new(
            ndc,
            "Mfr",
            cost,
            reimbursement,
            Available: true,
            Eligible: true,
            PreferredNdcAmountBasis.PerDispensedFill,
            PreferredNdcAmountBasis.PerDispensedFill,
            PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
            PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            timestamp,
            timestamp,
            HistoricalSampleCount: 0);
}
