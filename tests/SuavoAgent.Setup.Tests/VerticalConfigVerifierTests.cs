using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Setup;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public class VerticalConfigVerifierTests
{
    // ── shared ephemeral key ─────────────────────────────────────────────────
    private static readonly ECDsa TestKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private const string TestKeyId = "test-v1";

    private static VerticalConfigVerifier MakeVerifier() =>
        new(new Dictionary<string, ECDsa>(StringComparer.Ordinal) { [TestKeyId] = TestKey });

    // ── RFC 8785 golden vectors ──────────────────────────────────────────────

    [Fact]
    public void CanonicalizeRaw_golden_escaping()
    {
        // From cross-repo contract: sorted + no escape of &<>+ or café
        var input = """{"z":"a&b<c>d+e","a":"café","m":"plain"}""";
        var expected = """{"a":"café","m":"plain","z":"a&b<c>d+e"}""";
        Assert.Equal(expected, VerticalConfigVerifier.CanonicalizeRaw(input));
    }

    [Fact]
    public void CanonicalizeRaw_cross_system_number_vector_matches_exact_bytes()
    {
        const string input =
            """
            {
              "numbers": [333333333.33333329, 1E30, 4.50, 2e-3, 0.000000000000000000000000001],
              "string": "\u20ac$\u000F\u000aA'\u0042\u0022\u005c\\\"\/",
              "literals": [null, true, false]
            }
            """;
        const string expected =
            """{"literals":[null,true,false],"numbers":[333333333.3333333,1e+30,4.5,0.002,1e-27],"string":"€$\u000f\nA'B\"\\\\\"/"}""";

        Assert.Equal(expected, VerticalConfigVerifier.CanonicalizeRaw(input));
        Assert.Equal(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(VerticalConfigVerifier.CanonicalizeRaw(input)));
    }

    [Theory]
    [InlineData("{\"patient-name-sentinel\":1,\"patient-name-sentinel\":2}")]
    [InlineData("{\"x\":01}")]
    [InlineData("[1e400]")]
    [InlineData("{\"x\":1,}")]
    public void CanonicalizeRaw_rejects_non_signable_json_without_echoing_input(string input)
    {
        var error = Assert.Throws<JsonException>(() =>
            VerticalConfigVerifier.CanonicalizeRaw(input));

        Assert.Equal("rfc8785_input_invalid", error.Message);
        Assert.DoesNotContain("patient-name-sentinel", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Canonicalize_default_dto_golden()
    {
        var dto = new VerticalConfigDto(
            Vertical: "default",
            ComplianceMode: "none",
            SystemConnector: "none",
            ConnectorLabel: "your system",
            RedactionProfileId: "none",
            Framing: new VerticalFraming("SuavoAgent", "your system", "business", "License ID"),
            Compliance: new VerticalCompliance(false, "terms-v1"));

        const string expected =
            """{"compliance":{"baaRequired":false,"consentCopyId":"terms-v1"},"complianceMode":"none","connectorLabel":"your system","framing":{"businessNoun":"business","idLabel":"License ID","productNoun":"SuavoAgent","systemNoun":"your system"},"redactionProfileId":"none","systemConnector":"none","vertical":"default"}""";
        Assert.Equal(expected, VerticalConfigVerifier.Canonicalize(dto));
    }

    // ── ExtractKeyId ─────────────────────────────────────────────────────────

    [Fact]
    public void ExtractKeyId_parses_resource_name()
    {
        var resName = "SuavoSetup.Resources.vertical-config-signing-key-vertical-v1.pub.pem";
        Assert.Equal("vertical-v1", VerticalConfigVerifier.ExtractKeyId(resName));
    }

    // ── Five-state matrix ────────────────────────────────────────────────────

    [Fact]
    public void Absent_vertical_config_is_blocked()
    {
        var v = MakeVerifier();
        var r = v.Verify(new ParsedVerticalConfig(null, null, null, null));
        Assert.Equal(VerticalVerificationOutcome.Blocked, r.Outcome);
        Assert.Equal("vertical_config_missing", r.FailureReason);
    }

    [Fact]
    public void Present_unsigned_vertical_config_is_blocked()
    {
        var v = MakeVerifier();
        var r = v.Verify(new ParsedVerticalConfig("{}", null, null, null));
        Assert.Equal(VerticalVerificationOutcome.Blocked, r.Outcome);
        Assert.Equal("vertical_config_signature_missing", r.FailureReason);
    }

    [Fact]
    public void State3_signed_malformed_dto_yields_Blocked()
    {
        var v = MakeVerifier();
        // Raw present + signature present + Dto == null (malformed JSON was parsed as null)
        var r = v.Verify(new ParsedVerticalConfig("{not valid}", null, "sig", TestKeyId));
        Assert.Equal(VerticalVerificationOutcome.Blocked, r.Outcome);
        Assert.Equal("malformed_dto", r.FailureReason);
    }

    [Fact]
    public void State4_bad_signature_yields_Blocked()
    {
        var dto = DefaultDto();
        var canonical = VerticalConfigVerifier.Canonicalize(dto);
        var badSig = Convert.ToBase64String(new byte[64]); // garbage sig
        var vc = new ParsedVerticalConfig(canonical, dto, badSig, TestKeyId);
        var r = MakeVerifier().VerifyWithKey(vc, TestKey);
        Assert.Equal(VerticalVerificationOutcome.Blocked, r.Outcome);
    }

    [Fact]
    public void State5_valid_signature_yields_Verified()
    {
        var dto = DefaultDto();
        var canonical = VerticalConfigVerifier.Canonicalize(dto);
        var sigBytes = TestKey.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.Rfc3279DerSequence);
        var sig = Convert.ToBase64String(sigBytes);
        var vc = new ParsedVerticalConfig(canonical, dto, sig, TestKeyId);

        var r = MakeVerifier().VerifyWithKey(vc, TestKey);
        Assert.Equal(VerticalVerificationOutcome.Verified, r.Outcome);
        Assert.Equal("none", r.Config!.ComplianceMode);
    }

    [Fact]
    public void Unknown_key_id_yields_Blocked()
    {
        var dto = DefaultDto();
        var vc = new ParsedVerticalConfig("{}", dto, "sig", "unknown-key");
        var r = MakeVerifier().Verify(vc);
        Assert.True(r.IsBlocked);
    }

    [Fact]
    public void LoadEmbeddedTrustStore_loads_placeholder_key()
    {
        // Verifies the embedded PEM resource compiles in and can be loaded.
        var verifier = VerticalConfigVerifier.LoadEmbeddedTrustStore();
        Assert.NotNull(verifier);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static VerticalConfigDto DefaultDto() => new(
        Vertical: "default",
        ComplianceMode: "none",
        SystemConnector: "none",
        ConnectorLabel: "your system",
        RedactionProfileId: "none",
        Framing: new VerticalFraming("SuavoAgent", "your system", "business", "License ID"),
        Compliance: new VerticalCompliance(false, "terms-v1"));
}
