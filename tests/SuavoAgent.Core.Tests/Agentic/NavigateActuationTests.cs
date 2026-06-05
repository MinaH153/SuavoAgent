using System.Collections.Generic;
using SuavoAgent.Core.ActionGrammarV1;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Adapters;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>Pure translation of a verb-dispatch result into the loop's ActOutcome.</summary>
public sealed class NavigateActuationTests
{
    private static VerbDispatchResult Result(
        VerbDispatchOutcome outcome, string? failureReason = null, IReadOnlyDictionary<string, object?>? output = null)
        => new("inv-1", "click_by_label", "1.0", outcome, null,
            VerbRollbackEnvelope.None("inv-1"),
            output ?? new Dictionary<string, object?>(), failureReason);

    [Fact]
    public void MapResult_Success_MapsToSuccess_NoRejectionCode()
    {
        var outcome = NavigateActuation.MapResult(Result(VerbDispatchOutcome.Success, failureReason: "ignored"));

        Assert.Equal(ActStatus.Success, outcome.Status);
        Assert.Null(outcome.RejectionCode);
    }

    [Fact]
    public void MapResult_Rejected_CarriesFailureReasonAsRejectionCode()
    {
        var outcome = NavigateActuation.MapResult(Result(VerbDispatchOutcome.Rejected, failureReason: "label_not_found"));

        Assert.Equal(ActStatus.Rejected, outcome.Status);
        Assert.Equal("label_not_found", outcome.RejectionCode);
    }

    [Fact]
    public void MapResult_Failed_MapsToFailed()
    {
        var outcome = NavigateActuation.MapResult(Result(VerbDispatchOutcome.Failed, failureReason: "execution_timeout"));

        Assert.Equal(ActStatus.Failed, outcome.Status);
        Assert.Equal("execution_timeout", outcome.RejectionCode);
    }

    [Fact]
    public void MapResult_ExtractsEvidenceHashAndDryRunFromOutput()
    {
        var output = new Dictionary<string, object?> { ["evidence_hash"] = "abc123", ["dry_run"] = true };
        var outcome = NavigateActuation.MapResult(Result(VerbDispatchOutcome.Success, output: output));

        Assert.Equal("abc123", outcome.EvidenceHash);
        Assert.True(outcome.DryRun);
    }

    [Fact]
    public void MapResult_MissingOutputKeys_DefaultsSafely()
    {
        var outcome = NavigateActuation.MapResult(Result(VerbDispatchOutcome.Success));

        Assert.Null(outcome.EvidenceHash);
        Assert.False(outcome.DryRun);
    }
}
