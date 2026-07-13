using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Integration;

public sealed class CoreRuntimeServiceSelectionTests
{
    [Theory]
    [InlineData(PricingExecutorMode.UiaFirst)]
    [InlineData(PricingExecutorMode.VisionFirst)]
    public void DesktopPricingModes_SelectUiaExecutorWithoutSqlFallback(PricingExecutorMode mode)
    {
        using var runtime = Runtime(options => options.PricingExecutor = mode, addIpcFake: true);

        var selected = runtime.Services.GetRequiredService<IPricingJobExecutor>();

        Assert.IsType<UiaFirstPricingJobExecutor>(selected);
    }

    [Theory]
    [InlineData(PricingExecutorMode.SqlFirst)]
    [InlineData((PricingExecutorMode)999)]
    public void ReadOnlyOrUnknownPricingMode_SelectsSqlFirstFailClosed(PricingExecutorMode mode)
    {
        using var runtime = Runtime(options => options.PricingExecutor = mode);

        var selected = runtime.Services.GetRequiredService<IPricingJobExecutor>();

        Assert.IsType<SqlFirstPricingJobExecutor>(selected);
    }

    [Fact]
    public void PricingBrainEnabled_ConstructsRunnerWithTieredEvaluator()
    {
        using var runtime = Runtime(options => options.Reasoning.PricingBrainEnabled = true);

        Assert.NotNull(runtime.Services.GetRequiredService<PricingJobRunner>());
    }

    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(true, "")]
    [InlineData(true, "   ")]
    public void CloudReasoning_MissingOptInOrCredentialUsesNullObject(bool enabled, string? apiKey)
    {
        using var runtime = Runtime(options =>
        {
            options.Reasoning.CloudEnabled = enabled;
            options.ApiKey = apiKey;
        });

        Assert.IsType<NullCloudReasoning>(runtime.Services.GetRequiredService<ICloudReasoning>());
    }

    [Fact]
    public void CloudReasoning_EnabledWithoutSignerStillFailsClosed()
    {
        using var runtime = Runtime(options =>
        {
            options.Reasoning.CloudEnabled = true;
            options.ApiKey = "test-api-key";
        });

        Assert.IsType<NullCloudReasoning>(runtime.Services.GetRequiredService<ICloudReasoning>());
    }

    [Fact]
    public void CloudReasoning_EnabledWithSignerSelectsSignedClaudeClient()
    {
        using var runtime = Runtime(
            options =>
            {
                options.Reasoning.CloudEnabled = true;
                options.ApiKey = "test-api-key";
            },
            addSigner: true);

        Assert.IsType<ClaudeCloudReasoning>(runtime.Services.GetRequiredService<ICloudReasoning>());
    }

    [Fact]
    public void LocalReasoning_EnabledWithoutPublisherAuthorizationUsesNullObject()
    {
        using var runtime = Runtime(options => options.Reasoning.Enabled = true);

        Assert.IsType<NullLocalInference>(runtime.Services.GetRequiredService<ILocalInference>());
    }

    private static RuntimeHarness Runtime(
        Action<AgentOptions> configure,
        bool addSigner = false,
        bool addIpcFake = false)
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "suavo-core-selection-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var db = new AgentStateDb(Path.Combine(directory, "state.db"));
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = Array.Empty<string>(),
            DisableDefaults = true,
        });
        builder.Services.AddLogging();
        builder.Services.AddOptions<AgentOptions>().Configure(options =>
        {
            options.AgentId = "11111111-1111-4111-8111-111111111111";
            options.MachineFingerprint = "selection-machine";
            options.PharmacyId = "22222222-2222-4222-8222-222222222222";
            options.Reasoning.Enabled = false;
            options.Reasoning.CloudEnabled = false;
            configure(options);
        });
        builder.Services.AddSingleton(db);
        CoreRuntimeServiceRegistration.Register(
            builder,
            "selection-command",
            "selection-events",
            "selection-nonce");
        if (addIpcFake)
            builder.Services.AddSingleton<IIpcCommandClient>(new NoOpIpcClient());
        if (addSigner)
            builder.Services.AddSingleton<IPostSigner>(new NoOpPostSigner());
        return new RuntimeHarness(builder.Build(), db, directory);
    }

    private sealed class RuntimeHarness(IHost host, AgentStateDb db, string directory) : IDisposable
    {
        internal IServiceProvider Services => host.Services;

        public void Dispose()
        {
            host.Dispose();
            db.Dispose();
            try { Directory.Delete(directory, recursive: true); } catch { }
        }
    }

    private sealed class NoOpIpcClient : IIpcCommandClient
    {
        public bool IsConnected => false;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(false);
        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct) =>
            Task.FromResult<IpcResponse?>(null);
    }

    private sealed class NoOpPostSigner : IPostSigner
    {
        public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);

        public Task<JsonElement?> PostSignedVerifiedAsync(
            string path,
            object payload,
            string publicKeyDer,
            CancellationToken ct) =>
            Task.FromResult<JsonElement?>(null);
    }
}
