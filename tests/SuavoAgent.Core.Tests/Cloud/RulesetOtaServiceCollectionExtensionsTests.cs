using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

/// <summary>
/// DI helper smoke tests — verifies <see cref="RulesetOtaServiceCollectionExtensions.AddDiagnosticMeshOta"/>
/// resolves all four ruleset OTA dependencies cleanly so a host's Program.cs
/// can wire them in one line.
/// </summary>
public sealed class RulesetOtaServiceCollectionExtensionsTests
{
    private static AgentOptions ValidAgentOptions => new()
    {
        ApiKey = "test-api-key-deadbeef",
        CloudUrl = "https://test.suavollc.com",
    };

    private static IServiceCollection MinimalServices()
    {
        var services = new ServiceCollection();
        // Logger factory so ILogger<T> resolves without a full host.
        services.AddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        return services;
    }

    [Fact]
    public void AddDiagnosticMeshOta_registers_all_four_ruleset_dependencies()
    {
        var services = MinimalServices();
        services.AddDiagnosticMeshOta(ValidAgentOptions);

        using var sp = services.BuildServiceProvider();
        Assert.NotNull(sp.GetRequiredService<IRulesetSwapper>());
        Assert.IsType<WireRulesetSwapper>(sp.GetRequiredService<IRulesetSwapper>());
        Assert.NotNull(sp.GetRequiredService<RulesetSyncStore>());
        Assert.NotNull(sp.GetRequiredService<IRulesetClient>());
        Assert.IsType<AgentRulesetClient>(sp.GetRequiredService<IRulesetClient>());
    }

    [Fact]
    public void AddDiagnosticMeshOta_resolves_IRulesetVerifier_from_embedded_trust_store()
    {
        // The embedded pubkey ships in the Diagnostics resource manifest
        // (Resources/ruleset-signing-key-ruleset-v1-key-2026-05-13.pub.pem).
        // If LoadEmbeddedTrustStore can't import that key, this throws.
        var services = MinimalServices();
        services.AddDiagnosticMeshOta(ValidAgentOptions);

        using var sp = services.BuildServiceProvider();
        var verifier = sp.GetRequiredService<IRulesetVerifier>();

        Assert.NotNull(verifier);
        Assert.IsType<RulesetSignatureVerifier>(verifier);
    }

    [Fact]
    public void AddDiagnosticMeshOta_uses_default_cache_directory_when_unspecified()
    {
        var services = MinimalServices();
        services.AddDiagnosticMeshOta(ValidAgentOptions);

        using var sp = services.BuildServiceProvider();
        var store = sp.GetRequiredService<RulesetSyncStore>();

        Assert.NotNull(store);
        Assert.Equal(RulesetOtaServiceCollectionExtensions.DefaultCacheDirectory,
            Path.GetDirectoryName(store.CurrentPath));
    }

    [Fact]
    public void AddDiagnosticMeshOta_honours_custom_cache_directory()
    {
        var custom = Path.Combine(Path.GetTempPath(), $"mesh-cache-{Guid.NewGuid():N}");
        try
        {
            var services = MinimalServices();
            services.AddDiagnosticMeshOta(ValidAgentOptions, cacheDirectory: custom);

            using var sp = services.BuildServiceProvider();
            var store = sp.GetRequiredService<RulesetSyncStore>();
            Assert.Equal(custom, Path.GetDirectoryName(store.CurrentPath));
        }
        finally
        {
            if (Directory.Exists(custom)) Directory.Delete(custom, recursive: true);
        }
    }

    [Fact]
    public void AddDiagnosticMeshOta_with_missing_api_key_throws()
    {
        var services = MinimalServices();
        var opts = new AgentOptions { ApiKey = null, CloudUrl = "https://x" };

        Assert.Throws<InvalidOperationException>(
            () => services.AddDiagnosticMeshOta(opts));
    }

    [Fact]
    public void AddDiagnosticMeshOta_with_null_services_throws()
    {
        Assert.Throws<ArgumentNullException>(
            () => RulesetOtaServiceCollectionExtensions.AddDiagnosticMeshOta(
                services: null!, ValidAgentOptions));
    }

    [Fact]
    public void AddDiagnosticMeshOta_with_null_agent_options_throws()
    {
        var services = MinimalServices();
        Assert.Throws<ArgumentNullException>(
            () => services.AddDiagnosticMeshOta(agentOptions: null!));
    }
}
