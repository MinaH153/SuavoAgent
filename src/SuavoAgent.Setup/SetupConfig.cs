using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuavoAgent.Setup;

/// <summary>
/// Configuration provided by the dashboard via setup.json or command-line args.
/// RepoOwner/RepoName removed — hardcoded in BinaryDownloader (C-1).
/// </summary>
public sealed record SetupConfig(
    [property: JsonPropertyName("pharmacy_id")] string PharmacyId,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("cloud_url")] string CloudUrl,
    [property: JsonPropertyName("release_tag")] string ReleaseTag,
    [property: JsonPropertyName("learning_mode")] bool LearningMode)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    // H-5: Format validation patterns
    private static readonly Regex PharmacyIdPattern = new(@"^[A-Za-z0-9_\-]{1,64}$", RegexOptions.Compiled);
    private static readonly Regex ApiKeyPattern = new(@"^[A-Za-z0-9_\-\.]{0,256}$", RegexOptions.Compiled);

    /// <summary>
    /// Load config from setup.json next to the EXE, or fall back to command-line args.
    /// Validates CloudUrl is HTTPS (C-2) and PharmacyId/ApiKey format (H-5).
    /// </summary>
    public static SetupConfig? Load(string[] args)
    {
        // Try setup.json in the same directory as the EXE
        var exeDir = AppContext.BaseDirectory;
        var jsonPath = Path.Combine(exeDir, "setup.json");

        SetupConfig? config;
        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            config = JsonSerializer.Deserialize<SetupConfig>(json, JsonOpts);
        }
        else
        {
            config = ParseArgs(args);
        }

        if (config != null)
            Validate(config);

        return config;
    }

    /// <summary>
    /// Post-load validation: HTTPS CloudUrl (C-2), PharmacyId/ApiKey format (H-5).
    /// </summary>
    private static void Validate(SetupConfig config)
    {
        // C-2: CloudUrl must be absolute HTTPS
        if (!Uri.TryCreate(config.CloudUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https")
            throw new Exception($"CloudUrl must be HTTPS — got: {config.CloudUrl}");

        // H-5: PharmacyId format
        if (!PharmacyIdPattern.IsMatch(config.PharmacyId))
            throw new Exception($"PharmacyId must match [A-Za-z0-9_-]{{1,64}} — got: {config.PharmacyId}");

        // H-5: ApiKey format (empty allowed for cloud-optional)
        if (!ApiKeyPattern.IsMatch(config.ApiKey))
            throw new Exception($"ApiKey must match [A-Za-z0-9_\\-.] up to 256 chars — got length {config.ApiKey.Length}");
    }

    private static SetupConfig? ParseArgs(string[] args)
    {
        string? pharmacyId = null, apiKey = null, cloudUrl = null;
        string? releaseTag = null;
        bool learningMode = false;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pharmacy-id": pharmacyId = args[++i]; break;
                case "--api-key": apiKey = args[++i]; break;
                case "--cloud-url": cloudUrl = args[++i]; break;
                case "--release-tag": releaseTag = args[++i]; break;
                case "--learning-mode": learningMode = true; break;
            }
        }

        // Check last arg for --learning-mode (no value)
        if (args.Length > 0 && args[^1].Equals("--learning-mode", StringComparison.OrdinalIgnoreCase))
            learningMode = true;

        if (string.IsNullOrEmpty(pharmacyId) || string.IsNullOrEmpty(apiKey))
            return null;

        return new SetupConfig(
            PharmacyId: pharmacyId,
            ApiKey: apiKey,
            CloudUrl: cloudUrl ?? "https://suavollc.com",
            ReleaseTag: releaseTag ?? "v3.0.0",
            LearningMode: learningMode);
    }
}
