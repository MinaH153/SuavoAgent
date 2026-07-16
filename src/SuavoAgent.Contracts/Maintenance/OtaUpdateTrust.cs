using System.Collections.ObjectModel;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Contracts.Maintenance;

/// <summary>
/// Reviewed OTA signing roots embedded into every native trust boundary. The v2 entry remains an
/// inert marker until its exact AWS KMS P-256 SPKI DER is reviewed and committed. No private key is
/// read, generated, or persisted here.
/// </summary>
public static class OtaUpdateTrust
{
    public const string LegacyV1KeyId = "ota-update-v1";
    public const string CurrentV2KeyId = "ota-update-v2";
    public const string PendingV2PublicKeyMarker =
        "REPLACE_WITH_REVIEWED_AWS_KMS_P256_SPKI_DER_BASE64";

    private const string ResourceName =
        "SuavoAgent.Contracts.Maintenance.ota-update-trust-roots.json";

    private static readonly Lazy<TrustConfiguration> ProductionConfiguration =
        new(LoadProductionConfiguration, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Exact configured verification roots. The explicit v2 marker is never returned as a key.
    /// Malformed or unexpected source data throws during initialization instead of weakening trust.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ProductionTrustedPublicKeys =>
        ProductionConfiguration.Value.PublicKeys;

    /// <summary>The only root the source-controlled release tooling may use to sign.</summary>
    public static string ProductionSigningKeyId =>
        ProductionConfiguration.Value.SigningKeyId;

    public static bool IsProductionKeyConfigured(string keyId) =>
        ProductionTrustedPublicKeys.ContainsKey(keyId);

    public static bool VerifyP1363Hex(
        IReadOnlyDictionary<string, string> trustedRoots,
        string canonical,
        string? signatureHex)
    {
        if (string.IsNullOrEmpty(canonical) ||
            signatureHex is not { Length: 128 } ||
            !signatureHex.All(Uri.IsHexDigit))
            return false;

        byte[]? signature = null;
        try
        {
            signature = Convert.FromHexString(signatureHex);
            return Verify(
                trustedRoots,
                Encoding.UTF8.GetBytes(canonical),
                signature,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation,
                requiredKeyId: null);
        }
        catch (FormatException)
        {
            return false;
        }
        finally
        {
            if (signature is not null) CryptographicOperations.ZeroMemory(signature);
        }
    }

    public static bool VerifyDer(
        IReadOnlyDictionary<string, string> trustedRoots,
        byte[] payload,
        byte[] derSignature)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(derSignature);
        if (derSignature.Length is < 8 or > 72) return false;
        return Verify(
            trustedRoots,
            payload,
            derSignature,
            DSASignatureFormat.Rfc3279DerSequence,
            requiredKeyId: null);
    }

    /// <summary>
    /// Verifies a DER checksum signature under one declared OTA root only. The entire
    /// supplied registry is still validated first, so a malformed extra root cannot be
    /// hidden behind an otherwise valid exact-root signature.
    /// </summary>
    public static bool VerifyDerForKeyId(
        IReadOnlyDictionary<string, string> trustedRoots,
        string requiredKeyId,
        byte[] payload,
        byte[] derSignature)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(derSignature);
        if (derSignature.Length is < 8 or > 72) return false;
        return Verify(
            trustedRoots,
            payload,
            derSignature,
            DSASignatureFormat.Rfc3279DerSequence,
            requiredKeyId);
    }

    private static bool Verify(
        IReadOnlyDictionary<string, string>? trustedRoots,
        byte[] payload,
        byte[] signature,
        DSASignatureFormat signatureFormat,
        string? requiredKeyId)
    {
        if (trustedRoots is null || trustedRoots.Count is < 1 or > 2) return false;
        if (requiredKeyId is not null and not (LegacyV1KeyId or CurrentV2KeyId))
            return false;

        // Validate the complete registry before accepting any signature. A malformed extra root
        // cannot be silently skipped while another root succeeds.
        var validated = new List<(string KeyId, byte[] Bytes)>(trustedRoots.Count);
        var distinctPublicKeys = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var (keyId, publicKey) in trustedRoots)
            {
                if (keyId is not (LegacyV1KeyId or CurrentV2KeyId) ||
                    !distinctPublicKeys.Add(publicKey) ||
                    !TryDecodeP256Spki(publicKey, out var keyBytes))
                    return false;
                validated.Add((keyId, keyBytes));
            }

            foreach (var (keyId, keyBytes) in validated)
            {
                if (requiredKeyId is not null &&
                    !string.Equals(keyId, requiredKeyId, StringComparison.Ordinal))
                    continue;
                using var verifier = ECDsa.Create();
                verifier.ImportSubjectPublicKeyInfo(keyBytes, out var consumed);
                if (consumed == keyBytes.Length &&
                    verifier.VerifyData(
                        payload,
                        signature,
                        HashAlgorithmName.SHA256,
                        signatureFormat))
                    return true;
            }
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            foreach (var (_, keyBytes) in validated)
                CryptographicOperations.ZeroMemory(keyBytes);
        }
    }

    private static TrustConfiguration LoadProductionConfiguration()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException(
                               "The reviewed OTA update trust registry is missing.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 4,
        });
        var root = document.RootElement;
        RequireExactProperties(root, "schemaVersion", "signingKeyId", "roots");
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
            throw new InvalidOperationException("The OTA update trust registry schema is unsupported.");
        var signingKeyId = root.GetProperty("signingKeyId").GetString();
        if (signingKeyId is not (LegacyV1KeyId or CurrentV2KeyId))
            throw new InvalidOperationException("The OTA signing root selection is invalid.");

        var entries = root.GetProperty("roots");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() != 2)
            throw new InvalidOperationException("The OTA update trust registry must contain v1 and v2.");

        var configured = new Dictionary<string, string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var publicKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            RequireExactProperties(entry, "keyId", "publicKeyDerBase64");
            var keyId = entry.GetProperty("keyId").GetString();
            var publicKey = entry.GetProperty("publicKeyDerBase64").GetString();
            if (keyId is not (LegacyV1KeyId or CurrentV2KeyId) || !seen.Add(keyId))
                throw new InvalidOperationException("The OTA update trust registry contains an invalid key id.");
            if (keyId == CurrentV2KeyId &&
                string.Equals(publicKey, PendingV2PublicKeyMarker, StringComparison.Ordinal))
                continue;
            if (!TryDecodeP256Spki(publicKey, out var keyBytes))
                throw new InvalidOperationException("An OTA update trust root is not exact P-256 SPKI DER.");
            try
            {
                if (!publicKeys.Add(publicKey!))
                    throw new InvalidOperationException("OTA update trust roots must be distinct.");
                configured.Add(keyId, publicKey!);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(keyBytes);
            }
        }

        if (!seen.SetEquals([LegacyV1KeyId, CurrentV2KeyId]) ||
            !configured.ContainsKey(LegacyV1KeyId))
            throw new InvalidOperationException("The legacy OTA bridge root is missing.");
        if (!configured.ContainsKey(signingKeyId))
            throw new InvalidOperationException("The selected OTA signing root is not configured.");
        return new TrustConfiguration(
            signingKeyId,
            new ReadOnlyDictionary<string, string>(configured));
    }

    private static bool TryDecodeP256Spki(string? value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        try
        {
            bytes = Convert.FromBase64String(value);
            if (!string.Equals(Convert.ToBase64String(bytes), value, StringComparison.Ordinal))
            {
                CryptographicOperations.ZeroMemory(bytes);
                bytes = [];
                return false;
            }
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(bytes, out var consumed);
            if (consumed == bytes.Length && key.KeySize == 256)
                return true;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            if (bytes.Length > 0) CryptographicOperations.ZeroMemory(bytes);
            bytes = [];
            return false;
        }
    }

    private static void RequireExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("The OTA update trust registry shape is invalid.");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != expected.Length ||
            expected.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
            throw new InvalidOperationException("The OTA update trust registry has unknown fields.");
    }

    private sealed record TrustConfiguration(
        string SigningKeyId,
        IReadOnlyDictionary<string, string> PublicKeys);
}
