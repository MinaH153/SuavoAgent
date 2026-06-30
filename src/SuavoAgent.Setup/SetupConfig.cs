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
    [property: JsonPropertyName("learning_mode")] bool LearningMode,
    [property: JsonPropertyName("agent_id")] string AgentId = "",
    // Not from setup.json — set by the device-code/install-context flow so the
    // installer can bake the on-device brain config (Agent:Reasoning). Null =
    // no brain config (agent installs rules-only — fail-soft).
    [property: JsonIgnore] AgentReasoningConfig? Reasoning = null,
    // Vertical-config fields — never persisted to appsettings.json directly;
    // resolved to ComplianceMode + connector during install.
    [property: JsonIgnore] string? VerticalConfigRaw = null,
    [property: JsonIgnore] VerticalConfigDto? VerticalConfig = null,
    [property: JsonIgnore] string? VerticalConfigSignature = null,
    [property: JsonIgnore] string? VerticalConfigKeyId = null)
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
    private static readonly Regex AgentIdPattern = new(
        @"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        if (!AgentIdPattern.IsMatch(config.AgentId))
            throw new Exception("AgentId must be a cloud UUID. Legacy local setup.json installs are disabled; use the dashboard bootstrap installer.");
    }

    private static SetupConfig? ParseArgs(string[] args)
    {
        string? pharmacyId = null, apiKey = null, cloudUrl = null, agentId = null;
        string? releaseTag = null;
        bool learningMode = false;

        for (int i = 0; i < args.Length - 1; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--pharmacy-id": pharmacyId = args[++i]; break;
                case "--api-key": apiKey = args[++i]; break;
                case "--agent-id": agentId = args[++i]; break;
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
            LearningMode: learningMode,
            AgentId: agentId ?? "");
    }

    // ── Zero-touch filename token (canonical installer path) ────────────────
    private static readonly Regex DedupSuffix = new(@"\s\(\d+\)$", RegexOptions.Compiled);

    /// <summary>
    /// Detects a single-use install token baked into the EXE's own filename by
    /// the authenticated dashboard download (<c>SuavoSetup-&lt;token&gt;.exe</c>).
    /// Cheap + synchronous — NO I/O, NO network (App.OnFrameworkInitializationCompleted
    /// calls this on the UI thread before the window shows); the actual
    /// /api/agent/register exchange happens later, off the UI thread, in the
    /// Connecting step. Returns the token (begins "sai_") or null. Only meaningful
    /// when there is no setup.json / CLI config.
    /// </summary>
    public static string? DetectInstallTokenFromFilename()
    {
        try
        {
            var path = Environment.ProcessPath;
            return string.IsNullOrEmpty(path)
                ? null
                : ParseInstallToken(Path.GetFileNameWithoutExtension(path));
        }
        catch { return null; }
    }

    /// <summary>
    /// Pure parser for the EXE base name (no extension). Tolerates the browser
    /// download dedup suffix (<c>SuavoSetup-&lt;token&gt; (1)</c>). Returns the
    /// token when the name is <c>SuavoSetup-&lt;token&gt;</c> with a "sai_" token
    /// of length ≥ 12; otherwise null.
    /// </summary>
    internal static string? ParseInstallToken(string? exeBaseName)
    {
        if (string.IsNullOrEmpty(exeBaseName)) return null;
        var name = DedupSuffix.Replace(exeBaseName, "");
        const string prefix = "SuavoSetup-";
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = name[prefix.Length..];
        return token.StartsWith("sai_", StringComparison.Ordinal) && token.Length >= 12
            ? token
            : null;
    }
}
