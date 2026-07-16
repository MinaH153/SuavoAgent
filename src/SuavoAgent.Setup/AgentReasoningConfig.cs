using System.IO;
using System.Text.Json.Serialization;
using SuavoAgent.Contracts.Reasoning;

namespace SuavoAgent.Setup;

/// <summary>
/// The on-device brain (Qwen3) provisioning config the cloud hands the installer
/// at device-code / install-context time. The installer bakes this into the
/// agent's appsettings (Agent:Reasoning) so a fresh install boots reasoning-
/// enabled and self-provisions the model + native libs on first run — no restart,
/// no cloud command. The installer computes the on-box ModelPath / NativeLibraryPath
/// from %PROGRAMDATA%; the cloud owns the URLs + SHAs + identity + tuning.
///
/// Property names match the cloud `reasoning` JSON block (camelCase).
/// </summary>
public sealed record AgentReasoningConfig(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("modelId")] string ModelId,
    [property: JsonPropertyName("modelUrl")] string ModelUrl,
    [property: JsonPropertyName("modelSha256")] string ModelSha256,
    [property: JsonPropertyName("modelSizeBytes")] long? ModelSizeBytes,
    [property: JsonPropertyName("nativeLibsUrl")] string NativeLibsUrl,
    [property: JsonPropertyName("nativeLibsSha256")] string NativeLibsSha256,
    [property: JsonPropertyName("nativeLibsSizeBytes")] long? NativeLibsSizeBytes,
    [property: JsonPropertyName("contextSize")] int ContextSize,
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens,
    [property: JsonPropertyName("schemaVersion")] int SchemaVersion = 0,
    [property: JsonPropertyName("cohortId")] string CohortId = "",
    [property: JsonPropertyName("issuedAtUtc")] string IssuedAtUtc = "",
    [property: JsonPropertyName("expiresAtUtc")] string ExpiresAtUtc = "",
    [property: JsonPropertyName("keyId")] string KeyId = "",
    [property: JsonPropertyName("signature")] string Signature = "",
    [property: JsonPropertyName("modelKeyId")] string ModelKeyId = "",
    [property: JsonPropertyName("modelSignature")] string ModelSignature = "",
    [property: JsonPropertyName("nativeKeyId")] string NativeKeyId = "",
    [property: JsonPropertyName("nativeSignature")] string NativeSignature = "",
    [property: JsonPropertyName("nativePackageKind")] string NativePackageKind = "")
{
    /// <summary>
    /// Shape-only gate. Cryptographic authorization is independently checked by
    /// <see cref="ValidatePublisher"/> immediately before any filesystem or
    /// network mutation.
    /// </summary>
    public bool IsProvisionable =>
        Enabled
        && !string.IsNullOrWhiteSpace(ModelUrl) && !string.IsNullOrWhiteSpace(ModelSha256)
        && ModelSizeBytes is > 0
        && !string.IsNullOrWhiteSpace(NativeLibsUrl) && !string.IsNullOrWhiteSpace(NativeLibsSha256)
        && NativeLibsSizeBytes is > 0
        && SchemaVersion == BrainCohortContract.SchemaVersion
        && NativePackageKind == BrainNativePackageExtractor.OfficialNuGetPackageKind
        && !string.IsNullOrWhiteSpace(CohortId)
        && !string.IsNullOrWhiteSpace(IssuedAtUtc)
        && !string.IsNullOrWhiteSpace(ExpiresAtUtc)
        && string.IsNullOrEmpty(KeyId)
        && string.IsNullOrEmpty(Signature)
        && !string.IsNullOrWhiteSpace(ModelKeyId)
        && !string.IsNullOrWhiteSpace(ModelSignature)
        && !string.IsNullOrWhiteSpace(NativeKeyId)
        && !string.IsNullOrWhiteSpace(NativeSignature);

    public BrainCohortPublisherManifest PublisherManifest() => new(
        SchemaVersion,
        CohortId,
        ModelId,
        ModelUrl,
        ModelSha256,
        ModelSizeBytes ?? 0,
        NativeLibsUrl,
        NativeLibsSha256,
        NativeLibsSizeBytes ?? 0,
        ContextSize,
        MaxOutputTokens,
        IssuedAtUtc,
        ExpiresAtUtc,
        KeyId,
        Signature,
        ModelKeyId,
        ModelSignature,
        NativeKeyId,
        NativeSignature,
        NativePackageKind);

    public BrainCohortValidationResult ValidatePublisher(DateTimeOffset now) =>
        BrainCohortContract.Validate(PublisherManifest(), now);

    internal BrainCohortValidationResult ValidatePublisher(
        IReadOnlyDictionary<string, string> trustedPublisherKeys,
        DateTimeOffset now) =>
        BrainCohortContract.Validate(PublisherManifest(), trustedPublisherKeys, now);

    /// <summary>
    /// On-box model path inside an immutable content-addressed cohort. A newly
    /// downloaded model/native pair can therefore be prepared while the prior
    /// Core remains online without overwriting files loaded by that Core.
    /// </summary>
    public string GetModelPath(string dataDir) =>
        Path.Combine(
            GetBrainCohortRoot(dataDir),
            "model",
            SafeFileNameFromUrl(ModelUrl, "model.gguf"));

    /// <summary>Content-addressed native-libs directory for this exact brain pair.</summary>
    public string GetNativeLibsDir(string dataDir) =>
        Path.Combine(GetBrainCohortRoot(dataDir), "native");

    internal string GetBrainCohortRoot(string dataDir)
        => BrainCohortContract.GetCohortRoot(dataDir, BrainCohortId());

    internal string BrainCohortId()
        => BrainCohortContract.ComputeCohortId(PublisherManifest());

    /// <summary>Last path segment of a URL, sanitized to a safe filename; fallback on anything odd.</summary>
    internal static string SafeFileNameFromUrl(string url, string fallback)
        => BrainCohortContract.SafeFileNameFromUrl(url, fallback);
}
