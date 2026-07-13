using System.Collections.ObjectModel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Reasoning;

/// <summary>
/// Publisher-authorized identity of one immutable local-reasoning cohort. The
/// cloud may select one of these manifests, but it cannot mint or alter one:
/// only the offline release-signing key can produce <see cref="Signature"/>.
/// </summary>
public sealed record BrainCohortPublisherManifest(
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion,
    [property: JsonPropertyName("cohortId")] string CohortId,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("modelUrl")] string ModelUrl,
    [property: JsonPropertyName("modelSha256")] string ModelSha256,
    [property: JsonPropertyName("modelSizeBytes")] long ModelSizeBytes,
    [property: JsonPropertyName("nativeLibsUrl")] string NativeLibsUrl,
    [property: JsonPropertyName("nativeLibsSha256")] string NativeLibsSha256,
    [property: JsonPropertyName("nativeLibsSizeBytes")] long NativeLibsSizeBytes,
    [property: JsonPropertyName("contextSize")] int ContextSize,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("issuedAtUtc")] string IssuedAtUtc,
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc,
    [property: JsonPropertyName("keyId")] string KeyId,
    [property: JsonPropertyName("signature")] string Signature,
    [property: JsonPropertyName("modelKeyId")] string ModelKeyId = "",
    [property: JsonPropertyName("modelSignature")] string ModelSignature = "",
    [property: JsonPropertyName("nativeKeyId")] string NativeKeyId = "",
    [property: JsonPropertyName("nativeSignature")] string NativeSignature = "",
    [property: JsonPropertyName("nativePackageKind")] string NativePackageKind = "");

public sealed record BrainCohortValidationResult(
    bool IsValid,
    string Code,
    string? Canonical = null)
{
    internal static BrainCohortValidationResult Valid(string canonical) =>
        new(true, "valid", canonical);

    internal static BrainCohortValidationResult Reject(string code) =>
        new(false, code);
}

/// <summary>
/// Exact, cross-language publisher signature contract for model weights,
/// executable native libraries, exact sizes, and inference resource bounds.
/// </summary>
public static class BrainCohortContract
{
    public const int SchemaVersion = 3;
    public const int RetiredInstalledSchemaVersion = 2;
    public const int LegacySchemaVersion = 1;
    public const string ProductionModelKeyId = "brain-model-v1";
    public const string ProductionNativeKeyId = "brain-native-v1";
    public const long MaxModelBytes = 8L * 1024 * 1024 * 1024;
    public const long MaxNativePackageBytes = 256L * 1024 * 1024;
    public const int MinContextSize = 512;
    public const int MaxContextSize = 32_768;
    public const int MaxOutputTokenLimit = 4_096;
    public static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MaximumAuthorizationLifetime = TimeSpan.FromDays(90);

    private const string SignatureDomain = "suavo-brain-cohort-v3";
    private const string RetiredSignatureDomain = "suavo-brain-cohort-v2";
    private const string ModelSignatureDomain = "suavo-brain-model-v1";
    private const string NativeSignatureDomain = "suavo-brain-native-v1";
    private const string LegacySignatureDomain = "suavo-brain-cohort-v1";
    private const string IdentityDomain = "suavo-brain-cohort-id-v2";
    private const string RetiredIdentityDomain = "suavo-brain-cohort-id-v1";

    // Deliberately empty until independently generated model/native public keys
    // are reviewed and pinned. Reusing update-v1 here would restore a cross-role
    // compromise path, so production reasoning fails closed in the interim.
    public static readonly IReadOnlyDictionary<string, string> ProductionTrustedPublisherKeys =
        new ReadOnlyDictionary<string, string>(
            new Dictionary<string, string>(StringComparer.Ordinal));

    public static BrainCohortValidationResult Validate(
        BrainCohortPublisherManifest? manifest,
        DateTimeOffset now) =>
        Validate(manifest, ProductionTrustedPublisherKeys, now);

    /// <summary>Key-injectable seam for rotation rehearsals and generated-key tests.</summary>
    public static BrainCohortValidationResult Validate(
        BrainCohortPublisherManifest? manifest,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now)
    {
        if (manifest is null || trustedPublisherKeys is null)
            return BrainCohortValidationResult.Reject("publisher_manifest_missing");
        if (manifest.SchemaVersion != SchemaVersion)
            return BrainCohortValidationResult.Reject("publisher_schema_mismatch");
        if (!string.Equals(
                manifest.NativePackageKind,
                BrainNativePackageExtractor.OfficialNuGetPackageKind,
                StringComparison.Ordinal))
            return BrainCohortValidationResult.Reject("publisher_native_package_kind_invalid");
        return ValidateRoleSeparatedManifest(manifest, trustedPublisherKeys, now);
    }

    /// <summary>
    /// Re-verifies an already-installed schema-v2 cohort during upgrade. This
    /// is intentionally internal and is used only by
    /// <see cref="InstalledBrainCohortVerifier"/>; normal selection and
    /// provisioning remain schema-v3-only.
    /// </summary>
    internal static BrainCohortValidationResult ValidateRetiredSchemaV2InstalledCohort(
        BrainCohortPublisherManifest? manifest,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now)
    {
        if (manifest is null || trustedPublisherKeys is null)
            return BrainCohortValidationResult.Reject("publisher_manifest_missing");
        if (manifest.SchemaVersion != RetiredInstalledSchemaVersion)
            return BrainCohortValidationResult.Reject("publisher_retired_schema_required");
        if (!string.IsNullOrEmpty(manifest.NativePackageKind))
            return BrainCohortValidationResult.Reject("publisher_retired_package_kind_forbidden");
        return ValidateRoleSeparatedManifest(manifest, trustedPublisherKeys, now);
    }

    private static BrainCohortValidationResult ValidateRoleSeparatedManifest(
        BrainCohortPublisherManifest manifest,
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now)
    {
        if (!IsSafeText(manifest.CohortId, 64) || !IsLowerSha256(manifest.CohortId) ||
            !IsModelId(manifest.ModelId) ||
            !IsHttpsUrl(manifest.ModelUrl) ||
            !IsLowerSha256(manifest.ModelSha256) ||
            !IsHttpsUrl(manifest.NativeLibsUrl) ||
            !IsLowerSha256(manifest.NativeLibsSha256))
            return BrainCohortValidationResult.Reject("publisher_artifact_metadata_invalid");
        if (manifest.ModelSizeBytes is <= 0 or > MaxModelBytes ||
            manifest.NativeLibsSizeBytes is <= 0 or > MaxNativePackageBytes)
            return BrainCohortValidationResult.Reject("publisher_artifact_size_invalid");
        if (manifest.ContextSize is < MinContextSize or > MaxContextSize ||
            manifest.MaxOutputTokens is <= 0 or > MaxOutputTokenLimit ||
            manifest.MaxOutputTokens > manifest.ContextSize)
            return BrainCohortValidationResult.Reject("publisher_tuning_bounds_invalid");
        if (!string.IsNullOrEmpty(manifest.KeyId) || !string.IsNullOrEmpty(manifest.Signature))
            return BrainCohortValidationResult.Reject("publisher_legacy_authority_forbidden");
        if (!IsRoleKeyId(manifest.ModelKeyId, "brain-model-") ||
            !IsRoleKeyId(manifest.NativeKeyId, "brain-native-") ||
            string.Equals(manifest.ModelKeyId, manifest.NativeKeyId, StringComparison.Ordinal))
            return BrainCohortValidationResult.Reject("publisher_role_key_id_invalid");
        if (!trustedPublisherKeys.TryGetValue(manifest.ModelKeyId, out var modelPublicKey))
            return BrainCohortValidationResult.Reject("publisher_model_key_unknown");
        if (!trustedPublisherKeys.TryGetValue(manifest.NativeKeyId, out var nativePublicKey))
            return BrainCohortValidationResult.Reject("publisher_native_key_unknown");
        if (AreSamePublicKeys(modelPublicKey, nativePublicKey))
            return BrainCohortValidationResult.Reject("publisher_role_key_reuse_forbidden");

        if (!TryParseUtc(manifest.IssuedAtUtc, out var issuedAt) ||
            !TryParseUtc(manifest.ExpiresAtUtc, out var expiresAt) ||
            issuedAt > now.ToUniversalTime() + MaximumFutureSkew ||
            expiresAt <= issuedAt ||
            expiresAt - issuedAt > MaximumAuthorizationLifetime)
            return BrainCohortValidationResult.Reject("publisher_validity_window_invalid");
        if (expiresAt <= now.ToUniversalTime())
            return BrainCohortValidationResult.Reject("publisher_manifest_expired");

        var expectedCohortId = ComputeCohortId(manifest);
        if (!FixedAsciiEquals(expectedCohortId, manifest.CohortId))
            return BrainCohortValidationResult.Reject("publisher_cohort_id_mismatch");
        if (string.IsNullOrWhiteSpace(manifest.ModelSignature))
            return BrainCohortValidationResult.Reject("publisher_model_signature_missing");
        if (manifest.ModelSignature.Length != 128 || !IsLowerHex(manifest.ModelSignature))
            return BrainCohortValidationResult.Reject("publisher_model_signature_format_invalid");
        if (string.IsNullOrWhiteSpace(manifest.NativeSignature))
            return BrainCohortValidationResult.Reject("publisher_native_signature_missing");
        if (manifest.NativeSignature.Length != 128 || !IsLowerHex(manifest.NativeSignature))
            return BrainCohortValidationResult.Reject("publisher_native_signature_format_invalid");

        var canonical = BuildCanonical(manifest);
        if (!VerifySignature(
                modelPublicKey,
                BuildModelCanonical(manifest),
                manifest.ModelSignature))
            return BrainCohortValidationResult.Reject("publisher_model_signature_invalid");
        if (!VerifySignature(
                nativePublicKey,
                BuildNativeCanonical(manifest),
                manifest.NativeSignature))
            return BrainCohortValidationResult.Reject("publisher_native_signature_invalid");
        return BrainCohortValidationResult.Valid(canonical);
    }

    public static string BuildCanonical(BrainCohortPublisherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion == LegacySchemaVersion)
            return BuildLegacyCanonical(manifest);
        if (manifest.SchemaVersion == RetiredInstalledSchemaVersion)
            return BuildRetiredCanonical(manifest);
        return string.Join('|',
            SignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.ModelId,
            manifest.ModelUrl,
            manifest.ModelSha256,
            manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.NativeLibsUrl,
            manifest.NativeLibsSha256,
            manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.NativePackageKind,
            manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
            manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.ModelKeyId,
            manifest.NativeKeyId);
    }

    public static string BuildModelCanonical(BrainCohortPublisherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return string.Join('|',
            ModelSignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.ModelId,
            manifest.ModelUrl,
            manifest.ModelSha256,
            manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
            manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.ModelKeyId);
    }

    public static string BuildNativeCanonical(BrainCohortPublisherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (manifest.SchemaVersion == RetiredInstalledSchemaVersion)
            return BuildRetiredNativeCanonical(manifest);
        return string.Join('|',
            NativeSignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.NativeLibsUrl,
            manifest.NativeLibsSha256,
            manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.NativePackageKind,
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.NativeKeyId);
    }

    /// <summary>
    /// Explicit test/migration seam for old fixtures. Production callers must
    /// use <see cref="Validate(BrainCohortPublisherManifest?,DateTimeOffset)"/>;
    /// schema v1 can never enter the production trust registry.
    /// </summary>
    public static BrainCohortValidationResult ValidateLegacyDevelopmentManifest(
        BrainCohortPublisherManifest? manifest,
        IReadOnlyDictionary<string, string> trustedDevelopmentKeys,
        DateTimeOffset now)
    {
        if (manifest is null || trustedDevelopmentKeys is null)
            return BrainCohortValidationResult.Reject("publisher_manifest_missing");
        if (manifest.SchemaVersion != LegacySchemaVersion)
            return BrainCohortValidationResult.Reject("publisher_legacy_schema_required");
        if (!trustedDevelopmentKeys.TryGetValue(manifest.KeyId, out var publicKey))
            return BrainCohortValidationResult.Reject("publisher_key_unknown");
        if (manifest.Signature.Length != 128 || !IsLowerHex(manifest.Signature))
            return BrainCohortValidationResult.Reject("publisher_signature_format_invalid");
        return VerifySignature(publicKey, BuildLegacyCanonical(manifest), manifest.Signature)
            ? BrainCohortValidationResult.Valid(BuildLegacyCanonical(manifest))
            : BrainCohortValidationResult.Reject("publisher_signature_invalid");
    }

    private static string BuildLegacyCanonical(BrainCohortPublisherManifest manifest) =>
        string.Join('|',
            LegacySignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.ModelId,
            manifest.ModelUrl,
            manifest.ModelSha256,
            manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.NativeLibsUrl,
            manifest.NativeLibsSha256,
            manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
            manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.KeyId);

    private static string BuildRetiredCanonical(BrainCohortPublisherManifest manifest) =>
        string.Join('|',
            RetiredSignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.ModelId,
            manifest.ModelUrl,
            manifest.ModelSha256,
            manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.NativeLibsUrl,
            manifest.NativeLibsSha256,
            manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
            manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture),
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.ModelKeyId,
            manifest.NativeKeyId);

    private static string BuildRetiredNativeCanonical(BrainCohortPublisherManifest manifest) =>
        string.Join('|',
            NativeSignatureDomain,
            manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            manifest.CohortId,
            manifest.NativeLibsUrl,
            manifest.NativeLibsSha256,
            manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.IssuedAtUtc,
            manifest.ExpiresAtUtc,
            manifest.NativeKeyId);

    public static string ComputeCohortId(BrainCohortPublisherManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var retired = manifest.SchemaVersion is LegacySchemaVersion or RetiredInstalledSchemaVersion;
        var identity = retired
            ? string.Join('|',
                RetiredIdentityDomain,
                manifest.ModelId,
                manifest.ModelUrl,
                manifest.ModelSha256,
                manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
                manifest.NativeLibsUrl,
                manifest.NativeLibsSha256,
                manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
                manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
                manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture))
            : string.Join('|',
                IdentityDomain,
                manifest.ModelId,
                manifest.ModelUrl,
                manifest.ModelSha256,
                manifest.ModelSizeBytes.ToString(CultureInfo.InvariantCulture),
                manifest.NativeLibsUrl,
                manifest.NativeLibsSha256,
                manifest.NativeLibsSizeBytes.ToString(CultureInfo.InvariantCulture),
                manifest.NativePackageKind,
                manifest.ContextSize.ToString(CultureInfo.InvariantCulture),
                manifest.MaxOutputTokens.ToString(CultureInfo.InvariantCulture));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))
            .ToLowerInvariant();
    }

    public static string GetCohortRoot(string dataDirectory, string cohortId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!IsLowerSha256(cohortId))
            throw new ArgumentException("Cohort id must be exact lowercase SHA-256.", nameof(cohortId));
        return Path.Combine(
            Path.GetFullPath(dataDirectory),
            "reasoning",
            "cohorts",
            cohortId);
    }

    public static string GetModelPath(string dataDirectory, BrainCohortPublisherManifest manifest) =>
        Path.Combine(
            GetCohortRoot(dataDirectory, manifest.CohortId),
            "model",
            SafeFileNameFromUrl(manifest.ModelUrl, "model.gguf"));

    public static string GetNativeDirectory(string dataDirectory, BrainCohortPublisherManifest manifest) =>
        Path.Combine(GetCohortRoot(dataDirectory, manifest.CohortId), "native");

    public static string SafeFileNameFromUrl(string url, string fallback)
    {
        try
        {
            var name = Path.GetFileName(new Uri(url).AbsolutePath);
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            foreach (var character in Path.GetInvalidFileNameChars())
                name = name.Replace(character, '_');
            return name;
        }
        catch
        {
            return fallback;
        }
    }

    private static bool VerifySignature(string publicKeyDer, string canonical, string signatureHex)
    {
        try
        {
            var keyBytes = Convert.FromBase64String(publicKeyDer);
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(keyBytes, out var read);
            return read == keyBytes.Length && key.KeySize == 256 && key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromHexString(signatureHex),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception exception) when (exception is
            FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    private static bool IsHttpsUrl(string? value)
    {
        if (!IsVisibleAsciiText(value, 2_048) ||
            !value!.StartsWith("https://", StringComparison.Ordinal) ||
            value.Contains('?') ||
            value.Contains('#') ||
            ContainsPercentEncodedControl(value) ||
            !TryReadDnsOrIpv4Authority(value, out var authority) ||
            !Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
               !string.IsNullOrWhiteSpace(uri.Host) &&
               string.Equals(uri.Host, authority, StringComparison.OrdinalIgnoreCase) &&
               uri.IsDefaultPort &&
               string.IsNullOrEmpty(uri.UserInfo) &&
               string.IsNullOrEmpty(uri.Query) &&
               string.IsNullOrEmpty(uri.Fragment);
    }

    private static bool TryReadDnsOrIpv4Authority(
        string value,
        out string authority)
    {
        const int schemeLength = 8;
        var slash = value.IndexOf('/', schemeLength);
        authority = slash < 0 ? value[schemeLength..] : value[schemeLength..slash];
        if (authority.Length is <= 0 or > 253 ||
            authority.Contains('@') ||
            authority.Contains(':') ||
            authority.Contains('[') ||
            authority.Contains(']'))
            return false;

        var labels = authority.Split('.');
        if (labels.Length == 4 && labels.All(IsDecimalLabel))
            return labels.All(label =>
                (label.Length == 1 || label[0] != '0') &&
                byte.TryParse(
                    label,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out _));
        return labels.All(IsRfc1123Label);
    }

    private static bool IsDecimalLabel(string value) =>
        value.Length is > 0 and <= 3 && value.All(char.IsAsciiDigit);

    private static bool IsRfc1123Label(string value) =>
        value.Length is > 0 and <= 63 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        char.IsAsciiLetterOrDigit(value[^1]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool ContainsPercentEncodedControl(string value)
    {
        for (var index = 0; index + 2 < value.Length; index++)
        {
            if (value[index] != '%' ||
                !byte.TryParse(
                    value.AsSpan(index + 1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var decoded))
                continue;
            if (decoded <= 0x1f || decoded == 0x7f) return true;
        }
        return false;
    }

    private static bool IsVisibleAsciiText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        value.All(character => character is >= '!' and <= '~' && character != '|');

    private static bool IsSafeText(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length <= maxLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal) &&
        !value.Any(character => character == '|' || char.IsControl(character));

    private static bool IsLowerSha256(string? value) =>
        value is { Length: 64 } && IsLowerHex(value);

    private static bool IsModelId(string? value) =>
        value is { Length: >= 1 and <= 128 } &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.Skip(1).All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or ':' or '-');

    private static bool IsRoleKeyId(string? value, string requiredPrefix) =>
        IsSafeText(value, 80) &&
        value!.StartsWith(requiredPrefix, StringComparison.Ordinal) &&
        value.Length > requiredPrefix.Length &&
        value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    private static bool IsLowerHex(string value) =>
        value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryParseUtc(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        return value is { Length: 24 } &&
               DateTimeOffset.TryParseExact(
                   value,
                   "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
                   CultureInfo.InvariantCulture,
                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                   out parsed);
    }

    private static bool FixedAsciiEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(left),
            Encoding.ASCII.GetBytes(right));

    private static bool AreSamePublicKeys(string left, string right)
    {
        try
        {
            var leftBytes = Convert.FromBase64String(left);
            var rightBytes = Convert.FromBase64String(right);
            return leftBytes.Length == rightBytes.Length &&
                   CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
