using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Cloud;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public class ConfigSyncWorkerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _path;

    public ConfigSyncWorkerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "suavo-agent-configsync-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _path = Path.Combine(_tempDir, "config-overrides.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ExecuteAsync_Fetches_WritesOverridesToDisk()
    {
        var client = new StubClient(Response(
            Override("Reasoning.PricingBrainEnabled", "true"),
            Override("Reasoning.CloudEnabled", "true")));
        var store = NewStore();
        var worker = NewWorker(client, store);

        using var cts = new CancellationTokenSource();
        await RunOneIterationAsync(worker, cts);

        Assert.True(File.Exists(_path));
        var root = JsonDocument.Parse(File.ReadAllText(_path)).RootElement;
        Assert.True(root.GetProperty("Reasoning").GetProperty("PricingBrainEnabled").GetBoolean());
        Assert.True(root.GetProperty("Reasoning").GetProperty("CloudEnabled").GetBoolean());
        Assert.Equal(1, client.CallCount);
    }

    [Fact]
    public async Task ExecuteAsync_FetchReturnsNull_DoesNotWriteFile()
    {
        var client = new StubClient(null);
        var store = NewStore();
        var worker = NewWorker(client, store);

        using var cts = new CancellationTokenSource();
        await RunOneIterationAsync(worker, cts);

        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task ExecuteAsync_FetchThrows_SwallowsAndContinues()
    {
        var client = new StubClient(throwOnFetch: true);
        var store = NewStore();
        var worker = NewWorker(client, store);

        using var cts = new CancellationTokenSource();
        // Even though the client throws, the worker should not propagate.
        await RunOneIterationAsync(worker, cts);

        Assert.Equal(1, client.CallCount);
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public async Task ExecuteAsync_StoppingCancellation_ExitsPromptly()
    {
        var client = new StubClient(Response(Override("A.B", "true")));
        var store = NewStore();
        var worker = NewWorker(client, store, initialDelay: TimeSpan.FromSeconds(5));

        using var cts = new CancellationTokenSource();
        var task = worker.StartAsync(cts.Token);
        cts.Cancel();

        // Worker should honor the cancellation from inside the initial delay
        // without waiting the full 5 seconds.
        await task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    // --- Helpers -------------------------------------------------------------

    private ConfigOverrideStore NewStore() =>
        new(_path, NullLogger<ConfigOverrideStore>.Instance);

    private ConfigSyncWorker NewWorker(
        IAgentConfigClient client,
        ConfigOverrideStore store,
        TimeSpan? initialDelay = null)
    {
        var opts = new ConfigSyncOptions
        {
            InitialDelay = initialDelay ?? TimeSpan.Zero,
            // Keep the loop from firing more than once inside our test window —
            // each iteration fetch() is followed by a 10 s sleep we cancel out of.
            PollInterval = TimeSpan.FromSeconds(10),
        };
        return new ConfigSyncWorker(client, store, opts, NullLogger<ConfigSyncWorker>.Instance);
    }

    private static async Task RunOneIterationAsync(
        ConfigSyncWorker worker, CancellationTokenSource cts)
    {
        var task = worker.StartAsync(cts.Token);
        // Give the worker one poll tick to fetch + write.
        await Task.Delay(150);
        cts.Cancel();
        try { await task; } catch (OperationCanceledException) { }
    }

    private static ConfigOverrideResponse Response(params ConfigOverride[] overrides) =>
        new()
        {
            Success = true,
            AsOf = "2026-04-19T00:00:00Z",
            Overrides = overrides,
        };

    private static ConfigOverride Override(string path, string rawJson) => new()
    {
        Path = path,
        Value = JsonDocument.Parse(rawJson).RootElement,
        Scope = "pharmacy",
        UpdatedAt = "2026-04-19T00:00:00Z",
    };

    private sealed class StubClient : IAgentConfigClient
    {
        private readonly ConfigOverrideResponse? _response;
        private readonly bool _throwOnFetch;
        public int CallCount { get; private set; }

        public StubClient(ConfigOverrideResponse? response = null, bool throwOnFetch = false)
        {
            _response = response;
            _throwOnFetch = throwOnFetch;
        }

        public Task<ConfigOverrideResponse?> FetchAsync(CancellationToken ct)
        {
            CallCount++;
            if (_throwOnFetch)
                throw new InvalidOperationException("Stub configured to throw");
            return Task.FromResult(_response);
        }
    }
}
