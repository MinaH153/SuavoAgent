using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Setup;

/// <summary>Outcome of verifying a <see cref="ParsedVerticalConfig"/>.</summary>
public enum VerticalVerificationOutcome
{
    /// <summary>Signed, passes signature check, DTO valid.</summary>
    Verified,
    /// <summary>Blocked: malformed DTO or bad signature.</summary>
    Blocked,
}

public sealed record VerticalVerificationResult(
    VerticalVerificationOutcome Outcome,
    string? FailureReason = null,
    VerticalConfigDto? Config = null)
{
    public bool IsBlocked => Outcome == VerticalVerificationOutcome.Blocked;
    public bool IsVerified => Outcome == VerticalVerificationOutcome.Verified;
}

/// <summary>
/// ECDsa P-256 verifier for server-issued signed verticalConfig payloads.
/// Mirrors <see cref="SuavoAgent.Diagnostics.RulesetSignatureVerifier"/> pattern.
///
/// No legacy lane exists: absent, unsigned, malformed, unknown-key, and bad
/// signatures are all blocked. HIPAA defaults are not a substitute for proving
/// which vertical/connector contract the workstation was authorized to run.
/// </summary>
internal sealed class VerticalConfigVerifier
{
    internal const string KeyResourcePrefix = "vertical-config-signing-key-";
    internal const string KeyResourceSuffix = ".pub.pem";

    private readonly IReadOnlyDictionary<string, ECDsa> _trustStore;

    /// <summary>Test seam: inject an ephemeral key without touching embedded resources.</summary>
    internal VerticalConfigVerifier(IReadOnlyDictionary<string, ECDsa> trustStore)
    {
        ArgumentNullException.ThrowIfNull(trustStore);
        if (trustStore.Count == 0)
            throw new ArgumentException("Trust store must contain at least one key.", nameof(trustStore));
        _trustStore = trustStore;
    }

    /// <summary>Load all embedded vertical-config-signing-key-&lt;keyId&gt;.pub.pem keys.</summary>
    internal static VerticalConfigVerifier LoadEmbeddedTrustStore()
    {
        var asm = typeof(VerticalConfigVerifier).Assembly;
        var dict = new Dictionary<string, ECDsa>(StringComparer.Ordinal);

        foreach (var resName in asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(KeyResourceSuffix, StringComparison.Ordinal)
                     && n.Contains(KeyResourcePrefix, StringComparison.Ordinal)))
        {
            var keyId = ExtractKeyId(resName);
            using var stream = asm.GetManifestResourceStream(resName)
                ?? throw new InvalidOperationException($"Resource {resName} stream is null.");
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
                throw new InvalidOperationException(
                    $"Vertical-config trust store: failed to import key '{keyId}' from {resName}.", ex);
            }
        }

        if (dict.Count == 0)
            throw new InvalidOperationException(
                $"Vertical-config trust store contains zero embedded keys (looked for *{KeyResourcePrefix}<keyId>{KeyResourceSuffix}).");

        return new VerticalConfigVerifier(dict);
    }

    /// <summary>Strict signed-only verification.</summary>
    internal VerticalVerificationResult Verify(ParsedVerticalConfig vc)
    {
        if (vc.Raw is null)
            return new VerticalVerificationResult(
                VerticalVerificationOutcome.Blocked,
                "vertical_config_missing");

        if (vc.Signature is null)
            return new VerticalVerificationResult(
                VerticalVerificationOutcome.Blocked,
                "vertical_config_signature_missing");

        // State (3): signed but DTO is null (malformed JSON)
        if (vc.Dto is null)
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, "malformed_dto");

        // Resolve key — unknown key_id = block
        var keyId = vc.KeyId ?? "vertical-v1";
        if (!_trustStore.TryGetValue(keyId, out var key))
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, $"unknown_key_id:{keyId}");

        // State (4/5): verify signature over RFC-8785 canonical bytes
        return VerifyWithKey(vc, key);
    }

    /// <summary>Inner signature check — separated so tests can inject an ephemeral key.</summary>
    internal VerticalVerificationResult VerifyWithKey(ParsedVerticalConfig vc, ECDsa key)
    {
        if (vc.Dto is null || vc.Signature is null)
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, "missing_dto_or_sig");

        byte[] sigBytes;
        try { sigBytes = Convert.FromBase64String(vc.Signature); }
        catch (FormatException)
        {
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, "invalid_base64_sig");
        }

        var canonical = Canonicalize(vc.Dto);
        var data = Encoding.UTF8.GetBytes(canonical);

        if (!key.VerifyData(data, sigBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, "bad_signature");

        // Signature is good, but a validly-signed payload can still be SEMANTICALLY invalid
        // (unknown complianceMode/connector). Apply the DTO's own IsValid gate — otherwise garbage
        // gets baked verbatim into the install posture. Defense in depth over CompliancePosture's
        // fail-closed default.
        if (!vc.Dto.IsValid)
            return new VerticalVerificationResult(VerticalVerificationOutcome.Blocked, "invalid_dto");

        return new VerticalVerificationResult(VerticalVerificationOutcome.Verified, null, vc.Dto);
    }

    /// <summary>RFC 8785 canonicalize a <see cref="VerticalConfigDto"/>.</summary>
    internal static string Canonicalize(VerticalConfigDto dto)
    {
        var json = JsonSerializer.Serialize(dto, PayloadJsonOptions);
        return CanonicalizeRaw(json);
    }

    /// <summary>RFC 8785 canonicalize a raw JSON string.</summary>
    internal static string CanonicalizeRaw(string json) =>
        Rfc8785Canonicalizer.Canonicalize(json);

    internal static string ExtractKeyId(string resName)
    {
        var lastPrefix = resName.LastIndexOf(KeyResourcePrefix, StringComparison.Ordinal);
        if (lastPrefix < 0)
            throw new ArgumentException($"Resource '{resName}' missing prefix '{KeyResourcePrefix}'.", nameof(resName));
        var start = lastPrefix + KeyResourcePrefix.Length;
        var end = resName.Length - KeyResourceSuffix.Length;
        if (end <= start)
            throw new ArgumentException($"Resource '{resName}' has empty key_id.", nameof(resName));
        return resName.Substring(start, end - start);
    }

    private static readonly JsonSerializerOptions PayloadJsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
