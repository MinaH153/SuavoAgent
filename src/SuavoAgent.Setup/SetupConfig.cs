using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Setup;

/// <summary>
/// Configuration provided by the dashboard via setup.json or command-line args.
/// </summary>
public sealed record SetupConfig(
    [property: JsonPropertyName("pharmacy_id")] string PharmacyId,
    [property: JsonPropertyName("api_key")] string ApiKey,
    [property: JsonPropertyName("cloud_url")] string CloudUrl,
    [property: JsonPropertyName("release_tag")] string ReleaseTag,
    [property: JsonPropertyName("repo_owner")] string RepoOwner,
    [property: JsonPropertyName("repo_name")] string RepoName,
    [property: JsonPropertyName("learning_mode")] bool LearningMode)
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Load config from setup.json next to the EXE, or fall back to command-line args.
    /// </summary>
    public static SetupConfig? Load(string[] args)
    {
        // Try setup.json in the same directory as the EXE
        var exeDir = AppContext.BaseDirectory;
        var jsonPath = Path.Combine(exeDir, "setup.json");

        if (File.Exists(jsonPath))
        {
            var json = File.ReadAllText(jsonPath);
            var config = JsonSerializer.Deserialize<SetupConfig>(json, JsonOpts);
            return config;
        }

        // Fall back to command-line args
        return ParseArgs(args);
    }

    private static SetupConfig? ParseArgs(string[] args)
    {
        string? pharmacyId = null, apiKey = null, cloudUrl = null;
        string? releaseTag = null, repoOwner = null, repoName = null;
        bool learningMode = false;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pharmacy-id": pharmacyId = args[++i]; break;
                case "--api-key": apiKey = args[++i]; break;
                case "--cloud-url": cloudUrl = args[++i]; break;
                case "--release-tag": releaseTag = args[++i]; break;
                case "--repo-owner": repoOwner = args[++i]; break;
                case "--repo-name": repoName = args[++i]; break;
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
            RepoOwner: repoOwner ?? "MinaH153",
            RepoName: repoName ?? "SuavoAgent",
            LearningMode: learningMode);
    }
}
