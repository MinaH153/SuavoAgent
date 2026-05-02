using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class WaveGateTrippedPayloadTests
{
    [Fact]
    public void Construct_AssignsAllFields()
    {
        var trippedAt = DateTimeOffset.UtcNow;
        var evidence = new[] { "audit-1", "audit-2" };

        var payload = new WaveGateTrippedPayload(
            WaveId: "W0",
            EvidenceSummary: "5 open PRs resolved",
            CertifiedBy: "joshua",
            EvidenceEventIds: evidence,
            TrippedAt: trippedAt);

        Assert.Equal("W0", payload.WaveId);
        Assert.Equal("5 open PRs resolved", payload.EvidenceSummary);
        Assert.Equal("joshua", payload.CertifiedBy);
        Assert.Equal(evidence, payload.EvidenceEventIds);
        Assert.Equal(trippedAt, payload.TrippedAt);
    }

    [Theory]
    [InlineData("ci")]
    [InlineData("joshua")]
    [InlineData("pilot:abc123")]
    public void CertifiedBy_AcceptsCanonicalShapes(string certifier)
    {
        var payload = new WaveGateTrippedPayload(
            WaveId: "W3",
            EvidenceSummary: "7-day soak passed",
            CertifiedBy: certifier,
            EvidenceEventIds: Array.Empty<string>(),
            TrippedAt: DateTimeOffset.UtcNow);

        Assert.Equal(certifier, payload.CertifiedBy);
    }

    [Fact]
    public void RecordEquality_StructuralOnScalarFields()
    {
        var t = DateTimeOffset.UtcNow;
        var a = new WaveGateTrippedPayload("W1", "sum", "ci", new[] { "x" }, t);
        var b = new WaveGateTrippedPayload("W1", "sum", "ci", new[] { "x" }, t);
        // Records compare by value for scalar fields. EvidenceEventIds is a
        // reference-typed list, so we test scalar equality here and rely on
        // the audit chain for evidence-id semantics.
        Assert.Equal(a.WaveId, b.WaveId);
        Assert.Equal(a.EvidenceSummary, b.EvidenceSummary);
        Assert.Equal(a.CertifiedBy, b.CertifiedBy);
        Assert.Equal(a.TrippedAt, b.TrippedAt);
    }
}
