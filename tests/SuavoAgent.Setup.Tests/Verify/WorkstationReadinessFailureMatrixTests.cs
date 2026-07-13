using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public sealed class WorkstationReadinessFailureMatrixTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-12T20:00:00Z");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MissingEvidence_RemainsInProbation(string? json)
    {
        var result = Probe(json, PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("Waiting", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"checkedAt\":{}}")]
    public void UnreadableEvidence_FailsClosedAsWarning(string json)
    {
        var result = Probe(json, PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
    }

    [Fact]
    public void MissingExpectedTarget_NeverAcceptsOtherwiseReadyEvidence()
    {
        var result = Probe(PendingJson(), expected: null);

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("Target identity", result.Detail, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("v3.80.1", AgentId)]
    [InlineData("3.80.0", "99999999-9999-4999-8999-999999999999")]
    public void WrongReleaseOrAgent_IsRejected(string version, string agentId)
    {
        var result = Probe(
            PendingJson(version: version, agentId: agentId),
            PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("target release", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void VersionNormalization_AcceptsWhitespaceAndLeadingV()
    {
        var result = Probe(
            PendingJson(version: "  V3.80.0  "),
            PendingTarget(version: "v3.80.0"));

        Assert.Equal(GateState.Ok, result.State);
    }

    [Fact]
    public void PendingInstall_RequiresExactProvisioningTransaction()
    {
        var result = Probe(
            PendingJson(provisioningId: "99999999-9999-4999-8999-999999999999"),
            PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("install transaction", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveTarget_RejectsPendingProvisioningIdentity()
    {
        var result = Probe(PendingJson(), ActiveTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("active workstation", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("not-a-time")]
    [InlineData("2026-07-12T20:01:01.0000000+00:00")]
    [InlineData("2026-07-12T19:57:59.0000000+00:00")]
    public void InvalidFutureOrStaleTimestamp_IsRejected(string checkedAt)
    {
        var result = Probe(PendingJson(checkedAt: checkedAt), PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("stale", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PendingTarget_RequiresValidDeviceProof()
    {
        var result = Probe(
            PendingJson(deviceProofOverride: "null"),
            PendingTarget());

        Assert.Equal(GateState.Warn, result.State);
        Assert.Contains("readiness", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ActiveTarget_RequiresExplicitNullDeviceProof()
    {
        var ready = Probe(ActiveJson(deviceProof: "null"), ActiveTarget());
        var pendingObject = Probe(ActiveJson(deviceProof: "{}"), ActiveTarget());
        var missing = Probe(ActiveJson(deviceProof: null), ActiveTarget());

        Assert.Equal(GateState.Ok, ready.State);
        Assert.Equal(GateState.Warn, pendingObject.State);
        Assert.Equal(GateState.Warn, missing.State);
    }

    [Theory]
    [InlineData("status", "false")]
    [InlineData("helperAttached", "\"true\"")]
    [InlineData("ipcConnected", "null")]
    [InlineData("actuationReady", "0")]
    [InlineData("sqlConnected", "{}")]
    [InlineData("schemaCanaryGreen", "[]")]
    [InlineData("pmsCode", "\"pms_db_unreachable\"")]
    public void AnyMalformedOrNegativeReadinessField_BlocksActivation(
        string field,
        string replacement)
    {
        var json = ActiveJson(deviceProof: "null")
            .Replace($"\"{field}\": true", $"\"{field}\": {replacement}", StringComparison.Ordinal)
            .Replace($"\"{field}\": \"ok\"", $"\"{field}\": {replacement}", StringComparison.Ordinal)
            .Replace(
                $"\"{field}\": \"pms_operational\"",
                $"\"{field}\": {replacement}",
                StringComparison.Ordinal);

        Assert.Equal(GateState.Warn, Probe(json, ActiveTarget()).State);
    }

    private static GateResult Probe(
        string? json,
        WorkstationActivationTarget? expected) =>
        new WorkstationReadinessProbe(
            () => json,
            () => expected,
            () => Now).Check();

    private static WorkstationActivationTarget PendingTarget(string version = "3.80.0") => new(
        version,
        AgentId,
        new DeviceProvisioningExpectation(
            DeviceCode,
            ProvisioningId,
            AgentId,
            PharmacyId,
            Fingerprint,
            KeyId,
            Challenge,
            SqlServerCertificateSha256: null));

    private static WorkstationActivationTarget ActiveTarget() =>
        new("3.80.0", AgentId, PendingProof: null);

    private static string PendingJson(
        string version = "3.80.0",
        string agentId = AgentId,
        string provisioningId = ProvisioningId,
        string? checkedAt = null,
        string? deviceProofOverride = null)
    {
        var proof = deviceProofOverride ?? $$"""
          {
            "deviceCode": "{{DeviceCode}}",
            "provisioningId": "{{ProvisioningId}}",
            "agentId": "{{AgentId}}",
            "pharmacyId": "{{PharmacyId}}",
            "fingerprint": "{{Fingerprint}}",
            "keyId": "{{KeyId}}",
            "challenge": "{{Challenge}}",
            "sqlServerCertificateSha256": null,
            "signature": "{{Signature}}",
            "canonicalDigest": "{{Digest}}"
          }
          """;
        return $$"""
        {
          "status": "ok",
          "version": "{{version}}",
          "agentId": "{{agentId}}",
          "provisioningId": "{{provisioningId}}",
          "checkedAt": "{{checkedAt ?? Now.ToString("o")}}",
          "helperAttached": true,
          "ipcConnected": true,
          "actuationReady": true,
          "sqlConnected": true,
          "schemaCanaryGreen": true,
          "pmsCode": "pms_operational",
          "deviceProof": {{proof}}
        }
        """;
    }

    private static string ActiveJson(string? deviceProof)
    {
        var proofProperty = deviceProof is null
            ? string.Empty
            : $",\n  \"deviceProof\": {deviceProof}";
        return $$"""
        {
          "status": "ok",
          "version": "3.80.0",
          "agentId": "{{AgentId}}",
          "provisioningId": null,
          "checkedAt": "{{Now:o}}",
          "helperAttached": true,
          "ipcConnected": true,
          "actuationReady": true,
          "sqlConnected": true,
          "schemaCanaryGreen": true,
          "pmsCode": "pms_operational"{{proofProperty}}
        }
        """;
    }

    private const string AgentId = "11111111-1111-4111-8111-111111111111";
    private const string PharmacyId = "33333333-3333-4333-8333-333333333333";
    private const string ProvisioningId = "22222222-2222-4222-8222-222222222222";
    private const string DeviceCode = "device-code-123";
    private const string Fingerprint = "workstation-fingerprint";
    private const string KeyId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Challenge = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly string Signature = Convert.ToBase64String(new byte[64])
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static readonly string Digest = DeviceProvisioningProofCanonical.Digest(new(
        DeviceCode,
        ProvisioningId,
        AgentId,
        PharmacyId,
        Fingerprint,
        KeyId,
        Challenge));
}
