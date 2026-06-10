using System.IO;
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

    /// <summary>On-box model file path — SINGLE source of truth shared by the appsettings
    /// bake and the installer's brain phase so they can never disagree.</summary>
    public string GetModelPath(string dataDir) =>
        Path.Combine(dataDir, "models", SafeFileNameFromUrl(ModelUrl, "model.gguf"));

    /// <summary>On-box native-libs dir (llama.cpp DLLs extract here).</summary>
    public string GetNativeLibsDir(string dataDir) => Path.Combine(dataDir, "native");

    /// <summary>Last path segment of a URL, sanitized to a safe filename; fallback on anything odd.</summary>
    internal static string SafeFileNameFromUrl(string url, string fallback)
    {
        try
        {
            var path = new Uri(url).AbsolutePath;
            var name = Path.GetFileName(path);
            if (string.IsNullOrWhiteSpace(name)) return fallback;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
        catch
        {
            return fallback;
        }
    }
}
