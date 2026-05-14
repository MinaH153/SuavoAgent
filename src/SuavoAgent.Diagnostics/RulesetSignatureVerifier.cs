using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Diagnostics;

/// <summary>
/// ECDsa P-256 signature verifier for Phase 2 OTA-distributed rulesets.
/// </summary>
/// <remarks>
/// <para>
/// Multi-key trust store from day one (Codex Comp 2 chunk C HIGH RESOLVED).
/// A single embedded pubkey makes rotation impossible — compromise rotation
/// cannot safely rely on an OTA signed by the compromised old key, and
/// normal rotation requires an agent rebuild. The trust store is loaded
/// eagerly at startup from N embedded PEM resources named
/// <c>ruleset-signing-key-&lt;key_id&gt;.pub.pem</c>; the bundle's
/// <c>key_id</c> selects which key verifies its signature.
/// </para>
/// <para>
/// Phase 1 has a placeholder PEM that does NOT match the multi-key
/// convention; <see cref="LoadEmbeddedTrustStore"/> therefore finds zero
/// keys and fails closed. Comp 2.2 ships a real key ceremony that
/// produces <c>ruleset-signing-key-&lt;key_id&gt;.pub.pem</c> resources
/// before the OTA wire path goes live.
/// </para>
/// <para>
/// Canonicalization is RFC 8785 ONLY in v1.0 (Codex chunk A CRITICAL
/// RESOLVED — no custom fallback; drift between cloud + agent produces
/// signed-but-rejected bundles). Comp 2.1 ships a placeholder canonicaliser
/// that is stable for self-roundtrip tests; Comp 2.2 swaps in the real
/// RFC 8785 implementation with the Appendix B golden vectors as a gate.
/// </para>
/// </remarks>
/// <summary>
/// Abstraction over <see cref="RulesetSignatureVerifier.Verify"/> so workers
/// can be unit-tested with a recording fake without standing up a real
/// trust store + signing roundtrip.
/// </summary>
public interface IRulesetVerifier
{
    RulesetVerificationResult Verify(RulesetBundle bundle);
}

public sealed class RulesetSignatureVerifier : IRulesetVerifier
{
    /// <summary>Embedded-resource prefix for trust-store keys.</summary>
    internal const string KeyResourcePrefix = "ruleset-signing-key-";

    /// <summary>Embedded-resource suffix for trust-store keys.</summary>
    internal const string KeyResourceSuffix = ".pub.pem";

    private readonly IReadOnlyDictionary<string, ECDsa> _trustStore;

    /// <summary>
    /// Construct directly from a pre-built trust store. Used by tests to
    /// inject generated keys without going through embedded-resource load.
    /// Production code calls <see cref="LoadEmbeddedTrustStore"/> instead.
    /// </summary>
    public RulesetSignatureVerifier(IReadOnlyDictionary<string, ECDsa> trustStore)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        if (trustStore.Count == 0)
        {
            throw new ArgumentException(
                "Trust store must contain at least one key (fail-closed; zero keys = no OTA verification possible).",
                nameof(trustStore));
        }
        _trustStore = trustStore;
    }

    /// <summary>
    /// Eager-load every embedded <c>ruleset-signing-key-&lt;key_id&gt;.pub.pem</c>
    /// resource into the trust store. Fail-closed on every error path
    /// (Codex chunk 2 MEDIUM): zero keys, malformed PEM, missing key_id
    /// segment, and unreadable stream all throw <see cref="InvalidOperationException"/>.
    /// </summary>
    /// <remarks>
    /// Lazy load is the wrong default for a signature trust boundary —
    /// keys must be verified at process boot, not at first verification
    /// (Codex chunk 2 MEDIUM RESOLVED). The caller
    /// (<c>ConfigSyncWorker.InitializeAsync</c> in Comp 2.2) wraps this
    /// call with an allowlisted catch:
    /// <list type="bullet">
    /// <item><c>ArgumentException</c> — malformed key_id segment</item>
    /// <item><c>InvalidOperationException</c> — zero keys or eager-fail</item>
    /// <item><c>CryptographicException</c> — <c>ImportFromPem</c> rejected</item>
    /// <item><c>IOException</c> — embedded resource stream unreadable</item>
    /// </list>
    /// All other exceptions escape and crash the process; supervisor
    /// restart is the right recovery path for process-corruption signals.
    /// </remarks>
    public static RulesetSignatureVerifier LoadEmbeddedTrustStore()
    {
        var asm = typeof(RulesetSignatureVerifier).Assembly;
        var dict = new Dictionary<string, ECDsa>(StringComparer.Ordinal);

        foreach (var resName in asm.GetManifestResourceNames()
                                   .Where(n => n.EndsWith(KeyResourceSuffix, StringComparison.Ordinal)
                                            && n.Contains(KeyResourcePrefix, StringComparison.Ordinal)))
        {
            var keyId = ExtractKeyId(resName);

            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException(
                    $"Embedded resource {resName} reported by GetManifestResourceNames but stream is null.");
            using var reader = new StreamReader(stream);
            var pem = reader.ReadToEnd();

            try
            {
                var ecdsa = ECDsa.Create();
                ecdsa.ImportFromPem(pem);
                dict[keyId] = ecdsa;
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException or FormatException)
            {
                // Fail-CLOSED on bad PEM — never silently drop a key from
                // the trust store; that creates a verification gap.
                throw new InvalidOperationException(
                    $"Ruleset trust store: failed to import key '{keyId}' from {resName}.",
                    ex);
            }
        }

        if (dict.Count == 0)
        {
            throw new InvalidOperationException(
                $"Ruleset trust store contains zero embedded keys (looked for *{KeyResourcePrefix}<keyId>{KeyResourceSuffix}).");
        }

        return new RulesetSignatureVerifier(dict);
    }

    /// <summary>
    /// Extract the <c>key_id</c> segment from an embedded-resource name like
    /// <c>SuavoAgent.Diagnostics.Resources.ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem</c>
    /// → <c>ruleset-v1-key-2026-05-13</c>.
    /// </summary>
    /// <remarks>
    /// Hyphen-safe: uses <see cref="string.LastIndexOf(string, StringComparison)"/>
    /// on the prefix marker + suffix strip rather than <c>split('-')</c>.
    /// Split-based parsing breaks rotation because key_ids contain hyphens
    /// (Codex chunk 2 MEDIUM RESOLVED).
    /// </remarks>
    internal static string ExtractKeyId(string resName)
    {
        var lastPrefix = resName.LastIndexOf(KeyResourcePrefix, StringComparison.Ordinal);
        if (lastPrefix < 0)
        {
            throw new ArgumentException(
                $"Resource '{resName}' missing prefix '{KeyResourcePrefix}'.",
                nameof(resName));
        }

        var keyIdStart = lastPrefix + KeyResourcePrefix.Length;
        var keyIdEnd = resName.Length - KeyResourceSuffix.Length;
        if (keyIdEnd <= keyIdStart)
        {
            throw new ArgumentException(
                $"Resource '{resName}' has empty key_id segment between '{KeyResourcePrefix}' and '{KeyResourceSuffix}'.",
                nameof(resName));
        }

        return resName.Substring(keyIdStart, keyIdEnd - keyIdStart);
    }

    /// <summary>
    /// Verify a fetched OTA bundle. Returns
    /// <see cref="RulesetVerificationResult.IsValid"/>=<c>false</c> with a
    /// non-null <see cref="RulesetVerificationResult.FailureReason"/> on every
    /// rejection path (no exception escape on routine verification fails).
    /// </summary>
    /// <remarks>
    /// Order of checks: presence of signature → key_id known → expiry →
    /// canonical-bytes signature match. Fail-fast on each — no later
    /// check should depend on earlier check's state, but ordering keeps
    /// the cheapest checks first.
    /// </remarks>
    public RulesetVerificationResult Verify(RulesetBundle bundle)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        if (bundle.SignatureBytes is null || bundle.SignatureBytes.Length == 0)
        {
            return RulesetVerificationResult.Fail("Missing signature");
        }
        if (bundle.Ruleset is null)
        {
            return RulesetVerificationResult.Fail("Missing ruleset");
        }

        if (!_trustStore.TryGetValue(bundle.Ruleset.KeyId, out var key))
        {
            return RulesetVerificationResult.Fail($"Unknown key_id: {bundle.Ruleset.KeyId}");
        }

        if (!string.IsNullOrEmpty(bundle.Ruleset.ExpiresAt)
            && DateTimeOffset.TryParse(bundle.Ruleset.ExpiresAt, out var exp)
            && exp < DateTimeOffset.UtcNow)
        {
            return RulesetVerificationResult.Fail("Ruleset expired");
        }

        var canonicalJson = CanonicalizeForSigning(bundle.Ruleset);
        var data = Encoding.UTF8.GetBytes(canonicalJson);

        return key.VerifyData(data, bundle.SignatureBytes, HashAlgorithmName.SHA256)
            ? RulesetVerificationResult.Ok()
            : RulesetVerificationResult.Fail("Signature mismatch");
    }

    /// <summary>
    /// RFC 8785 (JSON Canonicalization Scheme) for signature input.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Comp 2.2 part 5 — Codex chunk A CRITICAL RESOLVED. The placeholder
    /// is gone; this now invokes the <c>JsonCanonicalizer</c> reference
    /// implementation (NuGet, v1.0.0). Cloud-side (Comp 2A) uses the
    /// matching <c>canonicalize</c> npm package; both consume the same
    /// RFC 8785 Appendix B test vectors as a wire-compat gate.
    /// </para>
    /// <para>
    /// Serialisation pipeline:
    /// <list type="number">
    /// <item><see cref="JsonSerializer.Serialize"/> emits the bundle's
    /// payload-shape JSON (property names per
    /// <see cref="JsonPropertyNameAttribute"/>, null-omitting).</item>
    /// <item><c>JsonCanonicalizer.GetEncodedString()</c> applies RFC 8785:
    /// sorts object members by UTF-16 code-unit order, normalises numbers
    /// via ECMAScript ToString, escapes strings per RFC 8259 §7.</item>
    /// </list>
    /// The output is what both ends sign / verify.
    /// </para>
    /// </remarks>
    internal static string CanonicalizeForSigning(RulesetV1 ruleset)
    {
        var payloadJson = JsonSerializer.Serialize(ruleset, PayloadJsonOptions);
        return new global::Org.Webpki.JsonCanonicalizer.JsonCanonicalizer(payloadJson).GetEncodedString();
    }

    /// <summary>
    /// RFC 8785 canonicalisation applied to a JSON string (used by golden
    /// vector tests). Production code calls
    /// <see cref="CanonicalizeForSigning(RulesetV1)"/> instead.
    /// </summary>
    internal static string CanonicalizeJsonString(string json)
        => new global::Org.Webpki.JsonCanonicalizer.JsonCanonicalizer(json).GetEncodedString();

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

/// <summary>Outcome of a single ruleset signature verification.</summary>
public readonly record struct RulesetVerificationResult(bool IsValid, string? FailureReason)
{
    public static RulesetVerificationResult Ok() => new(true, null);
    public static RulesetVerificationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// OTA bundle returned by <c>IRulesetClient.FetchAsync</c> (Comp 2.2).
/// </summary>
/// <param name="Ruleset">Deserialised ruleset payload.</param>
/// <param name="SignatureBytes">Detached ECDsa P-256 signature over the
/// canonicalised <paramref name="Ruleset"/> bytes (RFC 8785 in v1.0).</param>
/// <param name="ETag">Cloud-emitted ETag for <c>If-None-Match</c> caching.
/// Empty string when the bundle was loaded from local cache, not from the
/// network.</param>
public sealed record RulesetBundle(
    RulesetV1 Ruleset,
    byte[] SignatureBytes,
    string ETag);
