using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.Services;
using Xunit;

namespace SuavoAgent.Setup.Tests;

/// <summary>
/// The serializer remains safe when exercised without SQL credentials, while
/// the production install coordinator independently requires verified
/// PioneerRx discovery and SQL access before activation. SQL keys are emitted
/// only when credentials were actually captured.
/// </summary>
public sealed class InstallOrchestratorTests
{
    private static readonly ECDsa VerticalKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string VerticalKeyId = "test-v1";

    [Fact]
    public void AppSettingsSerializerOmitsSqlWhenCredentialsAreAbsent()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        // Serializer behavior is tested independently. Production RunAsync
        // rejects this state before staging or authority promotion.
        ctx.SqlCredentials = null;

        var json = new InstallOrchestrator(ctx).BuildAppSettings(
            verticalConfigVerifier: Verifier());

        Assert.DoesNotContain("SqlServer", json);
        Assert.DoesNotContain("SqlDatabase", json);
        Assert.DoesNotContain("SqlUser", json);
        Assert.DoesNotContain("SqlPassword", json);
        // Cloud identity remains, but the mutable auth secret lives only in credentials.dat.
        Assert.Contains("\"PharmacyId\"", json);
        Assert.DoesNotContain("\"ApiKey\"", json);
        Assert.DoesNotContain("test-key", json);
        Assert.Contains("\"AgentId\"", json);
        Assert.Contains("\"CloudUrl\"", json);
    }

    [Fact]
    public void AppSettings_includes_sql_when_captured()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: "localhost,49202",
            Database: "PioneerPharmacySystem",
            User: "suavo_read",
            Password: "s3cret");

        var json = new InstallOrchestrator(ctx).BuildAppSettings(
            password => "DPAPI:" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(password)),
            Verifier());

        Assert.Contains("\"SqlServer\"", json);
        Assert.Contains("localhost,49202", json);
        Assert.Contains("\"SqlUser\"", json);
        Assert.Contains("\"SqlPassword\": \"DPAPI:", json);
        Assert.DoesNotContain("s3cret", json);
    }

    [Fact]
    public void AppSettings_windows_auth_omits_user_and_password()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            Server: "localhost",
            Database: "PioneerPharmacySystem",
            User: null,    // Windows / integrated auth
            Password: null);

        var json = new InstallOrchestrator(ctx).BuildAppSettings(
            verticalConfigVerifier: Verifier());

        Assert.Contains("\"SqlServer\"", json);
        Assert.DoesNotContain("\"SqlUser\"", json);
        Assert.DoesNotContain("\"SqlPassword\"", json);
    }

    [Fact]
    public void AppSettings_binds_enrolled_sql_certificate_digest()
    {
        var ctx = NewContext();
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";
        ctx.SqlCredentials = new SqlCredentialDiscovery.SqlCredentials(
            "localhost,49202", "PioneerPharmacySystem", null, null);
        var digest = new string('a', 64);

        var json = new InstallOrchestrator(ctx).BuildAppSettings(
            verticalConfigVerifier: Verifier(),
            sqlServerCertificateDigest: digest);

        Assert.Contains("\"SqlServerCertificateSha256\"", json);
        Assert.Contains(digest, json);
        Assert.DoesNotContain("SqlTrustServerCertificate", json);
    }

    [Fact]
    public void AppSettings_consented_learning_is_capture_only()
    {
        var ctx = NewContext(learningMode: true);
        ctx.AgentId = "11111111-1111-1111-1111-111111111111";

        var json = new InstallOrchestrator(ctx).BuildAppSettings(
            verticalConfigVerifier: Verifier());

        Assert.Contains("\"LearningMode\": true", json);
        Assert.Contains("\"TemplateLearning\"", json);
        Assert.Contains("\"Mode\": \"capture\"", json);
        Assert.Contains("\"RuleGeneration\": false", json);
        Assert.Contains("\"AutoApproveOnFingerprintMatch\": false", json);
    }

    private static InstallContext NewContext(bool learningMode = false)
    {
        var dto = new VerticalConfigDto(
            "pharmacy",
            "hipaa",
            "pioneerrx",
            "PioneerRx",
            "phi-v1",
            new VerticalFraming("SuavoAgent", "PioneerRx", "pharmacy", "NPI"),
            new VerticalCompliance(true, "hipaa-ba-v1"));
        var canonical = VerticalConfigVerifier.Canonicalize(dto);
        var signature = Convert.ToBase64String(VerticalKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence));
        return new(new SetupConfig(
            PharmacyId: "PH-test",
            ApiKey: "test-key",
            CloudUrl: "https://suavollc.com",
            ReleaseTag: "v3.15.0",
            LearningMode: learningMode,
            VerticalConfigRaw: canonical,
            VerticalConfig: dto,
            VerticalConfigSignature: signature,
            VerticalConfigKeyId: VerticalKeyId));
    }

    private static VerticalConfigVerifier Verifier() => new(
        new Dictionary<string, ECDsa>(StringComparer.Ordinal)
        {
            [VerticalKeyId] = VerticalKey,
        });
}
