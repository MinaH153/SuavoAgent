using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

/// <summary>
/// Feature B B3 report runner: reader (gather) → ProfitOptimizer (judge) → one report row per pair.
/// Uses an in-memory fake data source so the whole report path is testable off the box (the real SQL/UIA
/// reader is the box-mapped B2). Proves: it picks the most-profitable NDC (not row 1), and fails closed
/// with an explicit status — never a guessed recommendation — on missing data.
/// </summary>
public class PreferredNdcReportRunnerTests
{
    // Fake IPreferredNdcDataSource: returns pre-seeded candidates per (drugGroup, plan) key.
    private sealed class FakeSource : IPreferredNdcDataSource
    {
        private readonly Dictionary<string, PreferredNdcReadResult> _byKey;
        public FakeSource(Dictionary<string, PreferredNdcReadResult> byKey) => _byKey = byKey;
        public Task<PreferredNdcReadResult> ReadCandidatesAsync(PreferredNdcRequest r, CancellationToken ct)
        {
            var key = r.DrugGroupKey + "|" + r.PlanId;
            if (_byKey.TryGetValue(key, out var res))
                return Task.FromResult(res with { JobId = r.JobId, RowIndex = r.RowIndex });
            return Task.FromResult(new PreferredNdcReadResult(r.JobId, r.RowIndex, r.DrugGroupKey, r.PlanId,
                false, new List<PreferredNdcCandidate>(), ReimbursementBasis.Unspecified, "plan_not_found"));
        }
    }

    private static PreferredNdcCandidate Cand(string ndc, string mfr, decimal? cost, decimal? reimb, string status = "Active") =>
        new(ndc, mfr, cost, reimb, status);

    private static PreferredNdcReadResult Read(string drug, string plan, ReimbursementBasis basis, params PreferredNdcCandidate[] cands) =>
        new("job", 0, drug, plan, true, cands.ToList(), basis, null);

    [Fact]
    public async Task Picks_the_most_profitable_ndc_not_row_1()
    {
        // Row 1 has the highest reimbursement but also a high cost; the LAST row wins on profit.
        var read = Read("omeprazole-40", "PLAN-A", ReimbursementBasis.ContractOrMac,
            Cand("00093-1000-01", "Row1 Labs", cost: 8.00m, reimb: 12.00m),   // profit 4.00
            Cand("00093-2000-01", "Mid Labs",  cost: 5.00m, reimb: 10.00m),   // profit 5.00
            Cand("00093-3000-01", "Best Labs", cost: 3.00m, reimb: 11.00m));  // profit 8.00  <- winner
        var runner = new PreferredNdcReportRunner(new FakeSource(new() { ["omeprazole-40|PLAN-A"] = read }));

        var rows = await runner.RunAsync(new[] { new PreferredNdcRequest("job", 0, "omeprazole-40", "PLAN-A") }, CancellationToken.None);

        var row = Assert.Single(rows);
        Assert.Equal(PreferredNdcStatus.Ok, row.Status);
        Assert.Equal("00093-3000-01", row.PreferredNdc);
        Assert.Equal("Best Labs", row.Manufacturer);
        Assert.Equal(8.00m, row.Profit);
        Assert.Equal(3.00m, row.DeltaOverRunnerUp);        // 8.00 winner − 5.00 runner-up
        Assert.Equal(ReimbursementBasis.ContractOrMac, row.Basis);
        Assert.Equal(3, row.CandidatesConsidered);
    }

    [Fact]
    public async Task Fails_closed_when_the_reader_finds_no_pair()
    {
        var runner = new PreferredNdcReportRunner(new FakeSource(new()));  // nothing seeded
        var rows = await runner.RunAsync(new[] { new PreferredNdcRequest("job", 0, "unknown", "PLAN-X") }, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.StartsWith("ERROR:", row.Status);            // "plan_not_found" surfaced, no recommendation
        Assert.Null(row.PreferredNdc);
    }

    [Fact]
    public async Task Fails_closed_when_no_candidate_has_both_cost_and_reimbursement()
    {
        // Candidates exist but every one is missing a number → NO_ELIGIBLE, never a guessed profit.
        var read = Read("drug", "PLAN-A", ReimbursementBasis.ContractOrMac,
            Cand("11111-1111-11", "A", cost: 4.00m, reimb: null),
            Cand("22222-2222-22", "B", cost: null, reimb: 10.00m));
        var runner = new PreferredNdcReportRunner(new FakeSource(new() { ["drug|PLAN-A"] = read }));

        var rows = await runner.RunAsync(new[] { new PreferredNdcRequest("job", 0, "drug", "PLAN-A") }, CancellationToken.None);
        var row = Assert.Single(rows);
        Assert.Equal(PreferredNdcStatus.NoEligible, row.Status);
        Assert.Null(row.Profit);
        Assert.Equal(2, row.CandidatesConsidered);
    }

    [Fact]
    public async Task One_row_per_request_in_order()
    {
        var src = new FakeSource(new()
        {
            ["a|P"] = Read("a", "P", ReimbursementBasis.ContractOrMac, Cand("1-1-1", "M", 2m, 5m)),
            ["b|P"] = Read("b", "P", ReimbursementBasis.AdjudicatedHistory, Cand("2-2-2", "N", 1m, 3m)),
        });
        var runner = new PreferredNdcReportRunner(src);
        var rows = await runner.RunAsync(new[]
        {
            new PreferredNdcRequest("job", 0, "a", "P"),
            new PreferredNdcRequest("job", 1, "b", "P"),
        }, CancellationToken.None);

        Assert.Equal(2, rows.Count);
        Assert.Equal("a", rows[0].DrugGroupKey);
        Assert.Equal("b", rows[1].DrugGroupKey);
        Assert.Equal(ReimbursementBasis.AdjudicatedHistory, rows[1].Basis);
    }
}
