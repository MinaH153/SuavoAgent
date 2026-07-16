using System.Text.Json;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class AutonomyExecutionModeTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"autonomyExecutionMode\":null}")]
    [InlineData("{\"autonomyExecutionMode\":\"supervised\"}")]
    [InlineData("{\"autonomyExecutionMode\":\"AUTO\"}")]
    [InlineData("{\"autonomyExecutionMode\":true}")]
    public void MissingOrUnknownAuthority_FailsClosedToSupervised(string json)
    {
        using var document = JsonDocument.Parse(json);
        Assert.Equal(
            AutonomyExecutionMode.Supervised,
            HeartbeatWorker.ReadAutonomyExecutionMode(document.RootElement));
    }

    [Fact]
    public void ExactSignedAutoValue_ParsesAsAuto()
    {
        using var document = JsonDocument.Parse("{\"autonomyExecutionMode\":\"auto\"}");
        Assert.Equal(
            AutonomyExecutionMode.Auto,
            HeartbeatWorker.ReadAutonomyExecutionMode(document.RootElement));
    }

    [Fact]
    public void PricingScope_BindsVerifiedPioneerRxVersionExecutableApprovalAndUiContract()
    {
        var identity = new PioneerRxAutonomyIdentity(
            "1.2.3.4", new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), 7);
        var binding = new ActivePmsAdapterBinding(
            "pharmacy", "session", new string('e', 64), new string('f', 64),
            "operator", DateTimeOffset.UtcNow);
        var exact = HeartbeatWorker.BuildPricingAutonomyScope(
            identity, binding, PricingExecutorMode.UiaFirst);

        Assert.Equal("1.2.3.4", exact.AppVersion);
        Assert.Equal(binding.TemplateDigest, exact.TemplateDigest);
        Assert.Equal(binding.ModelDigest, exact.ModelDigest);
        Assert.NotEqual(exact.ScopeDigest, HeartbeatWorker.BuildPricingAutonomyScope(
            identity with { FileVersion = "1.2.3.5" },
            binding,
            PricingExecutorMode.UiaFirst).ScopeDigest);
        Assert.NotEqual(exact.ScopeDigest, HeartbeatWorker.BuildPricingAutonomyScope(
            identity with { ExecutableSha256 = new string('9', 64) },
            binding,
            PricingExecutorMode.UiaFirst).ScopeDigest);
        Assert.NotEqual(exact.ScopeDigest, HeartbeatWorker.BuildPricingAutonomyScope(
            identity,
            binding with { TemplateDigest = new string('8', 64) },
            PricingExecutorMode.UiaFirst).ScopeDigest);
    }

    [Fact]
    public void TerminalIdentityLossOrChange_KeepsAdmittedScopeAndFailsStability()
    {
        var identity = new PioneerRxAutonomyIdentity(
            "1.2.3.4", new string('a', 64), new string('b', 64),
            new string('c', 64), new string('d', 64), 7);
        var admittedScope = HeartbeatWorker.BuildPricingAutonomyScope(
            identity, null, PricingExecutorMode.UiaFirst);
        var admission = new HeartbeatWorker.PricingAutonomyAdmission(
            true, admittedScope, true);
        var changedScope = HeartbeatWorker.BuildPricingAutonomyScope(
            identity with { ExecutableSha256 = new string('9', 64) },
            null,
            PricingExecutorMode.UiaFirst);

        var lost = HeartbeatWorker.EvaluatePricingTerminalIdentity(admission, null);
        var changed = HeartbeatWorker.EvaluatePricingTerminalIdentity(
            admission, changedScope);

        Assert.False(lost.Stable);
        Assert.False(changed.Stable);
        Assert.Equal(admittedScope.ScopeDigest, lost.EvidenceScope.ScopeDigest);
        Assert.Equal(admittedScope.ScopeDigest, changed.EvidenceScope.ScopeDigest);
        Assert.NotEqual(changedScope.ScopeDigest, changed.EvidenceScope.ScopeDigest);
    }

    [Fact]
    public void UnprovenAdmission_IsRejectedWhenDurableLatchCannotCommit()
    {
        var digest = new string('a', 64);
        var scope = TaskAutonomyScope.Create(
            "pricing", "pricing", "pioneerrx", "unverified",
            digest, digest, digest, PricingExecutorMode.UiaFirst);

        Assert.False(HeartbeatWorker.EnforcePricingAdmissionIdentity(
            new(true, scope, TrustedIdentity: false),
            _ => false));
    }
}
