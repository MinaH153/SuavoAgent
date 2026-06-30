using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.Compliance;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Connectors;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// Guards the install-posture resolution + vertical-config baking pipeline:
/// absent/blocked → fail-closed to HIPAA; verified → honor the config.
/// </summary>
public class InstallVerticalWiringTests
{
    // ── ResolveInstallPosture ────────────────────────────────────────────────

    [Fact]
    public void ResolvePosture_absent_vc_returns_hipaa_default()
    {
        var config = BaseConfig();  // no VerticalConfig fields
        var posture = InstallOrchestrator.ResolveInstallPosture(config, AnyVerifier());
        Assert.Equal("hipaa", posture.ComplianceMode);
        Assert.Equal("pioneerrx", posture.SystemConnector);
    }

    [Fact]
    public void ResolvePosture_blocked_vc_fails_closed_to_hipaa()
    {
        // Blocked = bad base64 signature → blocked → hipaa
        var dto = DefaultDto();
        var config = BaseConfig() with
        {
            VerticalConfigRaw = "{}",
            VerticalConfig = dto,
            VerticalConfigSignature = Convert.ToBase64String(new byte[64]),  // garbage sig
            VerticalConfigKeyId = TestKeyId,
        };
        var posture = InstallOrchestrator.ResolveInstallPosture(config, MakeVerifier());
        Assert.Equal("hipaa", posture.ComplianceMode);
    }

    [Fact]
    public void ResolvePosture_verified_vc_honors_config()
    {
        var dto = new VerticalConfigDto(
            Vertical: "none-vertical",
            ComplianceMode: "none",
            SystemConnector: "none",
            ConnectorLabel: "your system",
            RedactionProfileId: "none",
            Framing: new VerticalFraming("SuavoAgent", "your system", "business", "License ID"),
            Compliance: new VerticalCompliance(false, "terms-v1"));

        var canonical = VerticalConfigVerifier.Canonicalize(dto);
        var sigBytes = TestKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var sig = Convert.ToBase64String(sigBytes);

        var config = BaseConfig() with
        {
            VerticalConfigRaw = canonical,
            VerticalConfig = dto,
            VerticalConfigSignature = sig,
            VerticalConfigKeyId = TestKeyId,
        };
        var posture = InstallOrchestrator.ResolveInstallPosture(config, MakeVerifier());
        Assert.Equal("none", posture.ComplianceMode);
        Assert.Equal("none", posture.SystemConnector);
    }

    // ── Anti-downgrade (spec rule #2) ────────────────────────────────────────

    [Fact]
    public void ResolvePosture_verified_downgrade_below_lkg_is_refused()
    {
        // A verified 'none' config on a box whose last-known-good is HIPAA → refused → HIPAA.
        var config = SignedConfig(DefaultDto());  // complianceMode "none", verified
        var posture = InstallOrchestrator.ResolveInstallPosture(
            config, MakeVerifier(), lastKnownGood: ComplianceMode.Hipaa);
        Assert.Equal("hipaa", posture.ComplianceMode);
        Assert.Equal("pioneerrx", posture.SystemConnector);
    }

    [Fact]
    public void ResolvePosture_verified_none_honored_when_no_prior_good()
    {
        var config = SignedConfig(DefaultDto());
        var posture = InstallOrchestrator.ResolveInstallPosture(
            config, MakeVerifier(), lastKnownGood: ComplianceMode.None);
        Assert.Equal("none", posture.ComplianceMode);
        Assert.Equal("none", posture.SystemConnector);
    }

    [Fact]
    public void ResolvePosture_verified_upgrade_is_honored()
    {
        // Verified HIPAA on a box whose lkg is None → upgrade honored (not refused).
        var hipaaDto = DefaultDto() with
        {
            ComplianceMode = "hipaa", SystemConnector = "pioneerrx", ConnectorLabel = "PioneerRx",
            RedactionProfileId = "phi-v1",
        };
        var config = SignedConfig(hipaaDto);
        var posture = InstallOrchestrator.ResolveInstallPosture(
            config, MakeVerifier(), lastKnownGood: ComplianceMode.None);
        Assert.Equal("hipaa", posture.ComplianceMode);
        Assert.Equal("pioneerrx", posture.SystemConnector);
    }

    // ── BakeVerticalConfig ───────────────────────────────────────────────────

    [Fact]
    public void BakeVerticalConfig_writes_compliance_and_connector_keys()
    {
        var agent = new Dictionary<string, object?>();
        InstallOrchestrator.BakeVerticalConfig(agent, InstallPosture.HipaaDefault);
        Assert.Equal("hipaa", agent["ComplianceMode"]);
        Assert.Equal("pioneerrx", agent["SystemConnector"]);
    }

    // ── Connector factory (smoke) ────────────────────────────────────────────

    [Fact]
    public void NullConnector_probe_is_not_detected_and_discover_returns_null()
    {
        var c = SystemConnectorFactory.Select("none");
        var probe = c.Probe();
        Assert.False(probe.Detected);
        Assert.Null(c.Discover(probe));
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string TestKeyId = "test-v1";

    private static VerticalConfigVerifier MakeVerifier() =>
        new(new Dictionary<string, ECDsa>(StringComparer.Ordinal) { [TestKeyId] = TestKey });

    private static VerticalConfigVerifier AnyVerifier() => MakeVerifier();

    private static SetupConfig BaseConfig() => new(
        PharmacyId: "PH-test",
        ApiKey: "sk-test",
        CloudUrl: "https://suavollc.com",
        ReleaseTag: "v3.99.0",
        LearningMode: false,
        AgentId: "15c16aae-fa55-49c6-9d4c-971606243b86");

    private static VerticalConfigDto DefaultDto() => new(
        Vertical: "default",
        ComplianceMode: "none",
        SystemConnector: "none",
        ConnectorLabel: "your system",
        RedactionProfileId: "none",
        Framing: new VerticalFraming("SuavoAgent", "your system", "business", "License ID"),
        Compliance: new VerticalCompliance(false, "terms-v1"));

    /// <summary>Build a SetupConfig carrying <paramref name="dto"/> signed by TestKey (verifies under MakeVerifier()).</summary>
    private static SetupConfig SignedConfig(VerticalConfigDto dto)
    {
        var canonical = VerticalConfigVerifier.Canonicalize(dto);
        var sig = Convert.ToBase64String(TestKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        return BaseConfig() with
        {
            VerticalConfigRaw = canonical,
            VerticalConfig = dto,
            VerticalConfigSignature = sig,
            VerticalConfigKeyId = TestKeyId,
        };
    }
}
