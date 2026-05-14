using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// <summary>
/// Comp 2.1 verifier tests. Phase 2 OTA signature verification scaffolding.
/// </summary>
/// <remarks>
/// Tests use <see cref="ECDsa.Create"/>-generated test keypairs so the
/// embedded placeholder PEM doesn't block. Comp 2.2 adds tests for
/// <c>LoadEmbeddedTrustStore</c> with a real key + the RFC 8785
/// canonicaliser swap-in.
/// </remarks>
public class RulesetSignatureVerifierTests
{
    private const string TestKeyId = "test-key-2026-05-14";

    /// <summary>
    /// Build a trust store with a single fresh ECDsa P-256 keypair under
    /// <see cref="TestKeyId"/>. Returns the verifier + the matching
    /// PRIVATE key so the caller can sign test bundles end-to-end.
    /// </summary>
    private static (RulesetSignatureVerifier Verifier, ECDsa SigningKey) BuildSingleKeyVerifier()
    {
        var signing = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicForVerify = ECDsa.Create();
        publicForVerify.ImportSubjectPublicKeyInfo(signing.ExportSubjectPublicKeyInfo(), out _);

        var trustStore = new Dictionary<string, ECDsa>(StringComparer.Ordinal)
        {
            [TestKeyId] = publicForVerify,
        };
        return (new RulesetSignatureVerifier(trustStore), signing);
    }

    private static RulesetBundle BuildSignedBundle(RulesetV1 ruleset, ECDsa signingKey)
    {
        // Sign with the SAME canonicaliser the verifier uses (Comp 2.1
        // placeholder; Comp 2.2 swaps in RFC 8785).
        var canonical = RulesetSignatureVerifier.CanonicalizeForSigning(ruleset);
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var sig = signingKey.SignData(bytes, HashAlgorithmName.SHA256);
        return new RulesetBundle(ruleset, sig, ETag: "test");
    }

    private static RulesetV1 BuildTestRuleset(
        string? expiresAt = null,
        string? keyId = null,
        int versionInt = 11)
    {
        return new RulesetV1
        {
            RulesetVersion = "v1.1",
            RulesetVersionInt = versionInt,
            KeyId = keyId ?? TestKeyId,
            SignedAt = "2026-05-14T00:00:00Z",
            ExpiresAt = expiresAt,
            SignatureAlg = "ECDSA_P256_SHA256",
        };
    }

    // ── ExtractKeyId — hyphen-safe parsing ─────────────────────────────────

    [Fact]
    public void ExtractKeyId_hyphen_safe_returns_full_key_id_segment()
    {
        // Production-shaped resource name with hyphens inside the key_id.
        const string resName =
            "SuavoAgent.Diagnostics.Resources.ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem";

        var keyId = RulesetSignatureVerifier.ExtractKeyId(resName);

        Assert.Equal("ruleset-v1-key-2026-05-13", keyId);
    }

    [Fact]
    public void ExtractKeyId_missing_prefix_throws_ArgumentException()
    {
        const string resName = "SuavoAgent.Diagnostics.Resources.something-else.pub.pem";

        var ex = Assert.Throws<ArgumentException>(
            () => RulesetSignatureVerifier.ExtractKeyId(resName));

        Assert.Contains("ruleset-signing-key-", ex.Message);
    }

    [Fact]
    public void ExtractKeyId_empty_key_id_segment_throws_ArgumentException()
    {
        // Suffix immediately follows the prefix — zero-length key_id.
        const string resName =
            "SuavoAgent.Diagnostics.Resources.ruleset-signing-key-.pub.pem";

        var ex = Assert.Throws<ArgumentException>(
            () => RulesetSignatureVerifier.ExtractKeyId(resName));

        Assert.Contains("empty key_id", ex.Message);
    }

    // ── Constructor — fail-closed on zero / null trust store ──────────────

    [Fact]
    public void Constructor_with_null_trust_store_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => new RulesetSignatureVerifier(trustStore: null!));
    }

    [Fact]
    public void Constructor_with_empty_trust_store_throws_ArgumentException()
    {
        var empty = new Dictionary<string, ECDsa>(StringComparer.Ordinal);

        var ex = Assert.Throws<ArgumentException>(
            () => new RulesetSignatureVerifier(empty));

        Assert.Contains("at least one key", ex.Message);
    }

    // ── LoadEmbeddedTrustStore — Comp 2.2 ships a real P-256 pubkey ──────

    [Fact]
    public void LoadEmbeddedTrustStore_loads_real_embedded_pubkey()
    {
        // Comp 2.2 key ceremony shipped `ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem`
        // matching the embedded ruleset's KeyId. The matching PRIVATE key
        // lives in Vault (prod) or the ops keystore (dev); this test does
        // NOT verify a signature, only that the public key parses and is
        // registered under the expected key_id.
        var verifier = RulesetSignatureVerifier.LoadEmbeddedTrustStore();
        Assert.NotNull(verifier);

        // Sanity: a Verify call with a known-bad signature still produces
        // a clean failure path (proving the verifier is functional, not
        // throwing on a parse-time hazard).
        var unknownKeyRuleset = new RulesetV1
        {
            RulesetVersion = "v1.0",
            RulesetVersionInt = 10,
            KeyId = "ruleset-v1-key-2026-05-13",
            SignedAt = "2026-05-13T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };
        var bundle = new RulesetBundle(unknownKeyRuleset, new byte[64], ETag: "");
        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.Equal("Signature mismatch", result.FailureReason);
    }

    // ── Verify — happy path + each rejection reason ───────────────────────

    [Fact]
    public void Verify_with_valid_signature_returns_Ok()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        var bundle = BuildSignedBundle(BuildTestRuleset(), signing);

        var result = verifier.Verify(bundle);

        Assert.True(result.IsValid);
        Assert.Null(result.FailureReason);
    }

    [Fact]
    public void Verify_with_unknown_key_id_returns_failure()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        // Sign with the right key but advertise a different key_id in the
        // ruleset payload — verifier trust store doesn't know that key_id.
        var ruleset = BuildTestRuleset(keyId: "unknown-rotated-out-key");
        var bundle = BuildSignedBundle(ruleset, signing);

        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.NotNull(result.FailureReason);
        Assert.Contains("Unknown key_id", result.FailureReason);
        Assert.Contains("unknown-rotated-out-key", result.FailureReason);
    }

    [Fact]
    public void Verify_with_expired_ruleset_returns_failure()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        var pastExpiry = DateTimeOffset.UtcNow.AddDays(-1).ToString("O");
        var bundle = BuildSignedBundle(BuildTestRuleset(expiresAt: pastExpiry), signing);

        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.Equal("Ruleset expired", result.FailureReason);
    }

    [Fact]
    public void Verify_with_signature_tampered_returns_mismatch()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        var bundle = BuildSignedBundle(BuildTestRuleset(), signing);

        // Flip a single byte of the signature.
        bundle.SignatureBytes[0] ^= 0xFF;

        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.Equal("Signature mismatch", result.FailureReason);
    }

    [Fact]
    public void Verify_with_payload_tampered_after_signing_returns_mismatch()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        var ruleset = BuildTestRuleset();
        var bundle = BuildSignedBundle(ruleset, signing);

        // Mutate the ruleset AFTER signing — canonical bytes change, so
        // signature no longer matches.
        bundle.Ruleset.SignedAt = "2099-01-01T00:00:00Z";

        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.Equal("Signature mismatch", result.FailureReason);
    }

    [Fact]
    public void Verify_with_missing_signature_bytes_returns_failure()
    {
        var (verifier, _) = BuildSingleKeyVerifier();
        var ruleset = BuildTestRuleset();
        var bundle = new RulesetBundle(ruleset, Array.Empty<byte>(), ETag: "");

        var result = verifier.Verify(bundle);

        Assert.False(result.IsValid);
        Assert.Equal("Missing signature", result.FailureReason);
    }

    [Fact]
    public void Verify_with_null_bundle_throws()
    {
        var (verifier, _) = BuildSingleKeyVerifier();

        Assert.Throws<ArgumentNullException>(() => verifier.Verify(bundle: null!));
    }

    [Fact]
    public void Verify_with_far_future_expiry_passes_expiry_check()
    {
        var (verifier, signing) = BuildSingleKeyVerifier();
        var farFuture = DateTimeOffset.UtcNow.AddYears(10).ToString("O");
        var bundle = BuildSignedBundle(BuildTestRuleset(expiresAt: farFuture), signing);

        var result = verifier.Verify(bundle);

        Assert.True(result.IsValid);
    }

    // ── Multi-key trust store — rotation in/out ───────────────────────────

    [Fact]
    public void Verify_with_multi_key_trust_store_selects_by_key_id()
    {
        // Two valid keys in the trust store; the bundle's key_id selects
        // which one verifies. The OTHER key is never tried.
        var keyA = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var keyB = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var publicA = ECDsa.Create();
        publicA.ImportSubjectPublicKeyInfo(keyA.ExportSubjectPublicKeyInfo(), out _);
        var publicB = ECDsa.Create();
        publicB.ImportSubjectPublicKeyInfo(keyB.ExportSubjectPublicKeyInfo(), out _);

        var trustStore = new Dictionary<string, ECDsa>(StringComparer.Ordinal)
        {
            ["key-A-2026"] = publicA,
            ["key-B-2027"] = publicB,
        };
        var verifier = new RulesetSignatureVerifier(trustStore);

        // Sign with B's PRIVATE key, advertise key_id=key-B-2027.
        var rulesetB = BuildTestRuleset(keyId: "key-B-2027");
        var canonical = RulesetSignatureVerifier.CanonicalizeForSigning(rulesetB);
        var sig = keyB.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256);
        var bundle = new RulesetBundle(rulesetB, sig, ETag: "");

        var result = verifier.Verify(bundle);
        Assert.True(result.IsValid);

        // Now sign with A's PRIVATE key but advertise key_id=key-B-2027.
        // Should fail with Signature mismatch (the verifier picks B's
        // public key and finds A's signature doesn't match).
        var sigWrong = keyA.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256);
        var bundleWrong = new RulesetBundle(rulesetB, sigWrong, ETag: "");

        var result2 = verifier.Verify(bundleWrong);
        Assert.False(result2.IsValid);
        Assert.Equal("Signature mismatch", result2.FailureReason);
    }
}

/// <summary>
/// Embedded-ruleset bump tests for Comp 2.1. The model and JSON file MUST
/// both carry <c>ruleset_version_int</c> to satisfy the schema's required
/// list. Future cloud bundles allocate strictly greater values; the
/// embedded value is the floor on first boot.
/// </summary>
public class RulesetVersionIntTests
{
    [Fact]
    public void Embedded_ruleset_carries_ruleset_version_int_10()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        Assert.Equal(10, ruleset.RulesetVersionInt);
    }

    [Fact]
    public void RulesetV1_round_trips_ruleset_version_int_through_json()
    {
        var original = new RulesetV1
        {
            RulesetVersion = "v1.2",
            RulesetVersionInt = 12,
            KeyId = "k",
            SignedAt = "2026-05-14T00:00:00Z",
            SignatureAlg = "ECDSA_P256_SHA256",
        };

        var json = JsonSerializer.Serialize(original);
        var roundTripped = JsonSerializer.Deserialize<RulesetV1>(json)!;

        Assert.Equal(12, roundTripped.RulesetVersionInt);
    }

    [Fact]
    public void RulesetV1_default_ruleset_version_int_is_10()
    {
        // Comp 2.1 embedded value is 10. Cloud bundles always allocate >=11.
        Assert.Equal(10, new RulesetV1().RulesetVersionInt);
    }
}
