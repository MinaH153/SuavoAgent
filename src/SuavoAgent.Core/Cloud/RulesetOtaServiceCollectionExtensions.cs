using Microsoft.Extensions.DependencyInjection;
using SuavoAgent.Core.Config;
using SuavoAgent.Diagnostics;

namespace SuavoAgent.Core.Cloud;

/// <summary>
/// One-line DI registration for the Comp 2 ruleset OTA pipeline. Wires
/// <see cref="IRulesetSwapper"/>, <see cref="IRulesetVerifier"/>,
/// <see cref="RulesetSyncStore"/>, and <see cref="IRulesetClient"/> so
/// the existing <see cref="Workers.ConfigSyncWorker"/> auto-picks them up
/// through its optional-with-default-null constructor params.
/// </summary>
/// <remarks>
/// <para>
/// Host wiring: <c>services.AddDiagnosticMeshOta(cacheDir, agentOptions);</c>
/// before <c>services.AddHostedService&lt;ConfigSyncWorker&gt;()</c>.
/// </para>
/// <para>
/// HttpClient construction mirrors the existing <see cref="AgentConfigClient"/>
/// idiom (manual <see cref="HttpClient"/>, no IHttpClientFactory) so the
/// codebase doesn't pull in Microsoft.Extensions.Http transitively.
/// </para>
/// <para>
/// <see cref="RulesetSignatureVerifier.LoadEmbeddedTrustStore"/> is called
/// lazily on first <see cref="IRulesetVerifier"/> resolution. A missing
/// or malformed trust store fails fast at that point — the worker's own
/// exception handling treats this as "OTA disabled" + alarm + continue
/// on the embedded Phase 1 ruleset.
/// </para>
/// </remarks>
public static class RulesetOtaServiceCollectionExtensions
{
    /// <summary>Default cache directory under %ProgramData%.</summary>
    public static string DefaultCacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "SuavoAgent", "diagnostics", "ruleset-cache");

    public static IServiceCollection AddDiagnosticMeshOta(
        this IServiceCollection services,
        AgentOptions agentOptions,
        string? cacheDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(agentOptions);
        if (string.IsNullOrWhiteSpace(agentOptions.ApiKey))
        {
            // Ruleset OTA requires an API key for HMAC auth. Without one
            // the AgentRulesetClient constructor throws — surface here
            // with a clearer error.
            throw new InvalidOperationException(
                "AddDiagnosticMeshOta requires Agent.ApiKey to be configured.");
        }

        var dir = cacheDirectory ?? DefaultCacheDirectory;

        // Manual HttpClient — matches the existing AgentConfigClient idiom.
        // PooledConnectionLifetime caps the DNS pin: without it, this singleton
        // HttpClient holds the first-resolved IP for process lifetime; if Vercel
        // rotates the routing IP the agent talks to a dead endpoint until restart.
        var rulesetHandler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
        };
        var rulesetHttp = new HttpClient(rulesetHandler, disposeHandler: true)
        {
            BaseAddress = new Uri(agentOptions.CloudUrl),
            Timeout = TimeSpan.FromSeconds(10),
        };

        services.AddSingleton<IRulesetSwapper, WireRulesetSwapper>();
        services.AddSingleton<IRulesetVerifier>(_ =>
            RulesetSignatureVerifier.LoadEmbeddedTrustStore());
        services.AddSingleton(sp => new RulesetSyncStore(
            dir,
            sp.GetService<ILogger<RulesetSyncStore>>()));
        services.AddSingleton<IRulesetClient>(sp => new AgentRulesetClient(
            rulesetHttp,
            agentOptions,
            sp.GetService<ILogger<AgentRulesetClient>>()));

        return services;
    }
}
