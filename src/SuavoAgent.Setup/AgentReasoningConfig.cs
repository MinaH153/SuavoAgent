using System.Text.Json.Serialization;

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
    [property: JsonPropertyName("maxOutputTokens")] int MaxOutputTokens)
{
    /// <summary>True only when enabled AND both assets have a URL + SHA to provision from.</summary>
    public bool IsProvisionable =>
        Enabled
        && !string.IsNullOrWhiteSpace(ModelUrl) && !string.IsNullOrWhiteSpace(ModelSha256)
        && !string.IsNullOrWhiteSpace(NativeLibsUrl) && !string.IsNullOrWhiteSpace(NativeLibsSha256);
}
