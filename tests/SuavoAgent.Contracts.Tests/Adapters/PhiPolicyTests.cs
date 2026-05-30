using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Adapters;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Adapters;

/// <summary>
/// PhiPolicy is the per-adapter PHI column guard (HIPAA invariant #1: every
/// adapter MUST carry a non-empty policy). These pin the generic behavior; the
/// bit-for-bit port of PioneerRxConstants.IsPhiColumn is pinned separately in
/// the PioneerRx adapter test project.
/// </summary>
public class PhiPolicyTests
{
    private static IReadOnlySet<string> Blocklist(params string[] cols) =>
        new HashSet<string>(cols, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Create_WithEmptyBlocklistAndEmptyPatterns_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            PhiPolicy.Create(Blocklist(), Array.Empty<string>()));
    }

    [Fact]
    public void Create_WithBlocklistOnly_Succeeds()
    {
        var policy = PhiPolicy.Create(Blocklist("PatientName"), Array.Empty<string>());
        Assert.Contains("PatientName", policy.ColumnBlocklist);
    }

    [Fact]
    public void Create_WithPatternsOnly_Succeeds()
    {
        var policy = PhiPolicy.Create(Blocklist(), new[] { "ssn" });
        Assert.Contains("ssn", policy.ColumnPatterns);
    }

    [Fact]
    public void IsPhiColumn_ExactBlocklistMatch_True()
    {
        var policy = PhiPolicy.Create(Blocklist("PatientName"), Array.Empty<string>());
        Assert.True(policy.IsPhiColumn("PatientName"));
    }

    [Fact]
    public void IsPhiColumn_BlocklistMatchIsCaseInsensitive_True()
    {
        var policy = PhiPolicy.Create(Blocklist("PatientName"), Array.Empty<string>());
        Assert.True(policy.IsPhiColumn("patientname"));
    }

    [Fact]
    public void IsPhiColumn_SubstringPatternMatch_True()
    {
        var policy = PhiPolicy.Create(Blocklist(), new[] { "ssn" });
        Assert.True(policy.IsPhiColumn("PatientSSN"));
    }

    [Fact]
    public void IsPhiColumn_NoMatch_False()
    {
        var policy = PhiPolicy.Create(Blocklist("PatientName"), new[] { "ssn" });
        Assert.False(policy.IsPhiColumn("DrugName"));
    }
}
