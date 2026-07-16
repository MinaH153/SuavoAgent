using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Verify;
using Xunit;

namespace SuavoAgent.Setup.Tests.Verify;

public sealed class WorkstationReadinessProbeTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.Parse("2026-07-10T20:00:00Z");

    [Fact]
    public void ExactFreshTargetWithFullPmsPathIsReady()
    {
        var result = Run(Json(
            version: "3.80.0",
            agentId: AgentId,
            status: "ok",
            checkedAt: Now,
            helper: true,
            ipc: true,
            actuation: true,
            sql: true,
            schema: true,
            pmsCode: "pms_operational"));

        Assert.Equal(GateState.Ok, result.State);
    }

    [Theory]
    [InlineData(false, true, true, true, true, "pms_operational")]
    [InlineData(true, false, true, true, true, "pms_operational")]
    [InlineData(true, true, false, true, true, "pms_operational")]
    [InlineData(true, true, true, false, true, "pms_operational")]
    [InlineData(true, true, true, true, false, "pms_operational")]
    [InlineData(true, true, true, true, true, "pms_db_unreachable")]
    public void AnyMissingInteractivePmsProofStaysInProbation(
        bool helper,
        bool ipc,
        bool actuation,
        bool sql,
        bool schema,
        string pmsCode)
    {
        var result = Run(Json(
            "3.80.0", AgentId, "not_ready", Now,
            helper, ipc, actuation, sql, schema, pmsCode));

        Assert.Equal(GateState.Warn, result.State);
    }

    [Fact]
    public void StaleOrWrongTargetEvidenceNeverMeansReady()
    {
        Assert.Equal(
            GateState.Warn,
            Run(Json("3.79.0", AgentId, "ok", Now, true, true, true, true, true, "pms_operational")).State);
        Assert.Equal(
            GateState.Warn,
            Run(Json("3.80.0", AgentId, "ok", Now.AddMinutes(-3), true, true, true, true, true, "pms_operational")).State);
    }

    private static GateResult Run(string? json) =>
        new WorkstationReadinessProbe(
            () => json,
            () => new WorkstationActivationTarget(
                "3.80.0",
                AgentId,
                new DeviceProvisioningExpectation(
                    DeviceCode,
                    ProvisioningId,
                    AgentId,
                    PharmacyId,
                    Fingerprint,
                    KeyId,
                    Challenge,
                    SqlServerCertificateSha256: null)),
            () => Now).Check();

    private static string Json(
        string version,
        string agentId,
        string status,
        DateTimeOffset checkedAt,
        bool helper,
        bool ipc,
        bool actuation,
        bool sql,
        bool schema,
        string pmsCode) => $$"""
        {
          "status": "{{status}}",
          "version": "{{version}}",
          "agentId": "{{agentId}}",
          "provisioningId": "{{ProvisioningId}}",
          "checkedAt": "{{checkedAt:o}}",
          "helperAttached": {{helper.ToString().ToLowerInvariant()}},
          "ipcConnected": {{ipc.ToString().ToLowerInvariant()}},
          "actuationReady": {{actuation.ToString().ToLowerInvariant()}},
          "sqlConnected": {{sql.ToString().ToLowerInvariant()}},
          "schemaCanaryGreen": {{schema.ToString().ToLowerInvariant()}},
          "pmsCode": "{{pmsCode}}",
          "deviceProof": {
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
        }
        """;

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
