using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class WaveGateFailedPayloadTests
{
    [Fact]
    public void Construct_AssignsAllFields()
    {
        var committedAt = DateTimeOffset.UtcNow;

        var payload = new WaveGateFailedPayload(
            WaveId: "W3",
            AttemptNumber: 2,
            FailureSummary: "Helper crashed at day 5 of 7-day soak",
            RootCauseClass: "pilot-crash-midsoak",
            RemediationPlanCommittedAt: committedAt,
            NextAttemptEstimated: "after-fix");

        Assert.Equal("W3", payload.WaveId);
        Assert.Equal(2, payload.AttemptNumber);
        Assert.Equal("Helper crashed at day 5 of 7-day soak", payload.FailureSummary);
        Assert.Equal("pilot-crash-midsoak", payload.RootCauseClass);
        Assert.Equal(committedAt, payload.RemediationPlanCommittedAt);
        Assert.Equal("after-fix", payload.NextAttemptEstimated);
    }

    [Theory]
    [InlineData("code-bug")]
    [InlineData("scope-error")]
    [InlineData("blocker-external")]
    [InlineData("architectural-error")]
    [InlineData("pilot-crash-midsoak")]
    public void RootCauseClass_AcceptsAllCanonicalValues(string rootCause)
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "test",
            RootCauseClass: rootCause,
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: "unknown");

        Assert.Equal(rootCause, payload.RootCauseClass);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("when-blocker-clears")]
    [InlineData("after-fix")]
    public void NextAttemptEstimated_AcceptsAllCanonicalValues(string nextAttempt)
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "test",
            RootCauseClass: "code-bug",
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: nextAttempt);

        Assert.Equal(nextAttempt, payload.NextAttemptEstimated);
    }

    [Fact]
    public void RemediationPlanCommittedAt_NullableForUnplannedFailures()
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "blocked on Yubikey delivery",
            RootCauseClass: "blocker-external",
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: "when-blocker-clears");

        Assert.Null(payload.RemediationPlanCommittedAt);
    }
}
