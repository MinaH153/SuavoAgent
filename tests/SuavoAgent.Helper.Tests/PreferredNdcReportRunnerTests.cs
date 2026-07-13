using SuavoAgent.Contracts.Pricing;
using SuavoAgent.Helper.Workflows;
using Xunit;

namespace SuavoAgent.Helper.Tests;

public sealed class PreferredNdcReportRunnerTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

    private sealed class DelegateSource(
        Func<PreferredNdcRequest, PreferredNdcReadResult> read) : IPreferredNdcDataSource
    {
        public Task<PreferredNdcReadResult> ReadCandidatesAsync(
            PreferredNdcRequest request,
            CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(read(request));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task Emits_ok_only_for_complete_recent_named_evidence_and_picks_argmax()
    {
        var candidates = new[]
        {
            ContractCandidate("11111111111", 8m, 12m),
            ContractCandidate("22222222222", 3m, 11m, manufacturer: "Best Labs"),
            ContractCandidate("33333333333", 5m, 10m),
        };
        var request = Request();
        var runner = Runner(request, Read(request, candidates));

        var row = Assert.Single(await runner.RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.Ok, row.Status);
        Assert.Equal("22222222222", row.PreferredNdc);
        Assert.Equal("Best Labs", row.Manufacturer);
        Assert.Equal(8m, row.Profit);
        Assert.Equal(3m, row.DeltaOverRunnerUp);
        Assert.Equal(PreferredNdcAmountBasis.PerDispensedFill, row.AmountBasis);
        Assert.Equal(
            PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
            row.AcquisitionEvidenceProvenance);
        Assert.Equal(
            PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            row.ReimbursementEvidenceProvenance);
        Assert.Equal(Now, row.AcquisitionEvidenceAsOfUtc);
        Assert.Equal(Now, row.ReimbursementEvidenceAsOfUtc);
        Assert.Equal(0, row.HistoricalSampleCount);
    }

    public static IEnumerable<object[]> IdentityMismatches()
    {
        var request = Request();
        var good = Read(request, [ContractCandidate("11111111111", 1m, 2m)]);
        yield return [good with { JobId = "other" }];
        yield return [good with { RowIndex = 2 }];
        yield return [good with { DrugGroupKey = "other-drug" }];
        yield return [good with { PlanId = "other-plan" }];
    }

    [Theory]
    [MemberData(nameof(IdentityMismatches))]
    public async Task Refuses_any_response_identity_mismatch(PreferredNdcReadResult mismatched)
    {
        var request = Request();
        var runner = Runner(request, mismatched);

        var row = Assert.Single(await runner.RunAsync([request], default));

        Assert.Equal("ERROR:response_identity_mismatch", row.Status);
        Assert.Null(row.PreferredNdc);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Any_otherwise_eligible_candidate_missing_either_amount_blocks_ok(
        bool missingCost,
        bool missingReimbursement)
    {
        var request = Request();
        var incomplete = ContractCandidate(
            "22222222222",
            missingCost ? null : 2m,
            missingReimbursement ? null : 10m);
        var read = Read(request,
        [
            ContractCandidate("11111111111", 3m, 9m),
            incomplete,
        ]);

        var row = Assert.Single(await Runner(request, read).RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewIncompleteCandidateData, row.Status);
        Assert.Null(row.PreferredNdc);
    }

    [Fact]
    public async Task Missing_amount_on_explicitly_ineligible_candidate_does_not_poison_pair()
    {
        var request = Request();
        var read = Read(request,
        [
            ContractCandidate("11111111111", 3m, 9m),
            ContractCandidate("22222222222", null, null) with { Eligible = false },
        ]);

        var row = Assert.Single(await Runner(request, read).RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.Ok, row.Status);
        Assert.Equal("11111111111", row.PreferredNdc);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task Requires_affirmative_availability_and_eligibility(bool available, bool eligible)
    {
        var request = Request();
        var read = Read(request,
            [ContractCandidate("11111111111", 1m, 10m) with
            {
                Available = available,
                Eligible = eligible,
            }]);

        var row = Assert.Single(await Runner(request, read).RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.NoEligible, row.Status);
    }

    [Fact]
    public async Task Refuses_noncanonical_or_duplicate_candidate_identity()
    {
        var request = Request();
        var noncanonical = Read(request, [ContractCandidate("11111-1111-11", 1m, 10m)]);
        var duplicate = Read(request,
        [
            ContractCandidate("11111111111", 1m, 10m),
            ContractCandidate("11111111111", 2m, 11m),
        ]);

        var badIdentity = Assert.Single(await Runner(request, noncanonical).RunAsync([request], default));
        var collision = Assert.Single(await Runner(request, duplicate).RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewCandidateIdentityInvalid, badIdentity.Status);
        Assert.Equal(PreferredNdcStatus.ManualReviewDuplicateNdc, collision.Status);
    }

    [Fact]
    public async Task Refuses_unspecified_or_incompatible_amount_basis()
    {
        var request = Request();
        var unspecified = ContractCandidate("11111111111", 1m, 10m) with
        {
            AcquisitionAmountBasis = PreferredNdcAmountBasis.Unspecified,
        };
        var incompatible = ContractCandidate("11111111111", 1m, 10m) with
        {
            ReimbursementAmountBasis = PreferredNdcAmountBasis.PerUnit,
        };

        var first = Assert.Single(await Runner(request, Read(request, [unspecified]))
            .RunAsync([request], default));
        var second = Assert.Single(await Runner(request, Read(request, [incompatible]))
            .RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, first.Status);
        Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, second.Status);
    }

    [Fact]
    public async Task Refuses_mixed_amount_basis_across_the_compared_candidate_cohort()
    {
        var request = Request();
        var perFill = ContractCandidate("11111111111", 3m, 10m);
        var perUnit = ContractCandidate("22222222222", 1m, 9m) with
        {
            AcquisitionAmountBasis = PreferredNdcAmountBasis.PerUnit,
            ReimbursementAmountBasis = PreferredNdcAmountBasis.PerUnit,
        };

        var row = Assert.Single(await Runner(request, Read(request, [perFill, perUnit]))
            .RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, row.Status);
        Assert.Null(row.PreferredNdc);
    }

    [Fact]
    public async Task Refuses_unspecified_mismatched_stale_or_future_evidence()
    {
        var request = Request();
        var cases = new[]
        {
            ContractCandidate("11111111111", 1m, 10m) with
            {
                AcquisitionEvidenceProvenance = PreferredNdcEvidenceProvenance.Unspecified,
            },
            ContractCandidate("11111111111", 1m, 10m) with
            {
                ReimbursementEvidenceProvenance = PreferredNdcEvidenceProvenance.PioneerRxAdjudicatedClaimsExport,
            },
            ContractCandidate("11111111111", 1m, 10m) with
            {
                AcquisitionEvidenceAsOfUtc = Now - PreferredNdcEvidencePolicy.MaximumEvidenceAge - TimeSpan.FromSeconds(1),
            },
            ContractCandidate("11111111111", 1m, 10m) with
            {
                ReimbursementEvidenceAsOfUtc = Now + PreferredNdcEvidencePolicy.MaximumFutureClockSkew + TimeSpan.FromSeconds(1),
            },
        };

        foreach (var candidate in cases)
        {
            var row = Assert.Single(await Runner(request, Read(request, [candidate]))
                .RunAsync([request], default));
            Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, row.Status);
        }
    }

    [Fact]
    public async Task Historical_basis_requires_named_provenance_and_minimum_sample_count()
    {
        var request = Request();
        var tooFew = HistoryCandidate("11111111111", 1m, 10m, 9);
        var enough = HistoryCandidate("11111111111", 1m, 10m, 10);

        var rejected = Assert.Single(await Runner(
            request,
            Read(request, [tooFew], ReimbursementBasis.AdjudicatedHistory))
            .RunAsync([request], default));
        var accepted = Assert.Single(await Runner(
            request,
            Read(request, [enough], ReimbursementBasis.AdjudicatedHistory))
            .RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, rejected.Status);
        Assert.Equal(PreferredNdcStatus.Ok, accepted.Status);
        Assert.Equal(10, accepted.HistoricalSampleCount);
    }

    [Theory]
    [InlineData("1000000.0001", "10")]
    [InlineData("1.00001", "10")]
    [InlineData("1", "1000000.0001")]
    public async Task Refuses_unbounded_or_over_precision_arithmetic_inputs(
        string costText,
        string reimbursementText)
    {
        var request = Request();
        var candidate = ContractCandidate(
            "11111111111",
            decimal.Parse(costText, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(reimbursementText, System.Globalization.CultureInfo.InvariantCulture));

        var row = Assert.Single(await Runner(request, Read(request, [candidate]))
            .RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewEvidenceInvalid, row.Status);
    }

    [Fact]
    public async Task Contains_pair_exception_and_continues_next_request()
    {
        var first = Request("job", 0, "first", "PLAN-A");
        var second = Request("job", 1, "second", "PLAN-A");
        var source = new DelegateSource(request =>
        {
            if (request.RowIndex == 0)
                throw new OverflowException("pair-local failure");
            return Read(request, [ContractCandidate("11111111111", 1m, 10m)]);
        });
        var runner = new PreferredNdcReportRunner(source, new FixedTimeProvider(Now));

        var rows = await runner.RunAsync([first, second], default);

        Assert.Equal("ERROR:OverflowException", rows[0].Status);
        Assert.Equal(PreferredNdcStatus.Ok, rows[1].Status);
    }

    [Fact]
    public async Task All_loss_pair_is_manual_review_with_no_recommendation_fields()
    {
        var request = Request();
        var read = Read(request,
        [
            ContractCandidate("11111111111", 10m, 6m),
            ContractCandidate("22222222222", 12m, 5m),
        ]);

        var row = Assert.Single(await Runner(request, read).RunAsync([request], default));

        Assert.Equal(PreferredNdcStatus.ManualReviewNoProfitableCandidate, row.Status);
        Assert.Null(row.PreferredNdc);
        Assert.Null(row.AcquisitionCost);
        Assert.Null(row.Reimbursement);
        Assert.Null(row.Profit);
        Assert.Null(row.AcquisitionEvidenceAsOfUtc);
        Assert.Null(row.ReimbursementEvidenceAsOfUtc);
    }

    private static PreferredNdcReportRunner Runner(
        PreferredNdcRequest request,
        PreferredNdcReadResult result) =>
        new(new DelegateSource(_ => result), new FixedTimeProvider(Now));

    private static PreferredNdcRequest Request(
        string jobId = "job",
        int rowIndex = 0,
        string drug = "omeprazole-40",
        string plan = "PLAN-A") =>
        new(jobId, rowIndex, drug, plan);

    private static PreferredNdcReadResult Read(
        PreferredNdcRequest request,
        IReadOnlyList<PreferredNdcCandidate> candidates,
        ReimbursementBasis basis = ReimbursementBasis.ContractOrMac) =>
        new(
            request.JobId,
            request.RowIndex,
            request.DrugGroupKey,
            request.PlanId,
            Found: true,
            candidates,
            basis,
            ErrorMessage: null);

    private static PreferredNdcCandidate ContractCandidate(
        string ndc,
        decimal? cost,
        decimal? reimbursement,
        string manufacturer = "Mfr") =>
        new(
            ndc,
            manufacturer,
            cost,
            reimbursement,
            Available: true,
            Eligible: true,
            PreferredNdcAmountBasis.PerDispensedFill,
            PreferredNdcAmountBasis.PerDispensedFill,
            PreferredNdcEvidenceProvenance.PioneerRxAcquisitionCostExport,
            PreferredNdcEvidenceProvenance.PioneerRxContractOrMacExport,
            Now,
            Now,
            HistoricalSampleCount: 0);

    private static PreferredNdcCandidate HistoryCandidate(
        string ndc,
        decimal cost,
        decimal reimbursement,
        int samples) =>
        ContractCandidate(ndc, cost, reimbursement) with
        {
            ReimbursementEvidenceProvenance = PreferredNdcEvidenceProvenance.PioneerRxAdjudicatedClaimsExport,
            HistoricalSampleCount = samples,
        };
}
