using SuavoAgent.Setup;
using SuavoAgent.Setup.Gui.ViewModels;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class ConnectingViewModelTests
{
    private sealed class FakeTokenService : IInstallTokenService
    {
        private readonly InstallTokenExchangeResult? _result;
        public FakeTokenService(InstallTokenExchangeResult? result) => _result = result;

        // Null result → faulted task (simulates expired token / network failure).
        public Task<InstallTokenExchangeResult> ExchangeAsync(
            string token, string machineName, string fingerprint, string version, CancellationToken ct)
            => _result is null
                ? Task.FromException<InstallTokenExchangeResult>(new HttpRequestException("network down"))
                : Task.FromResult(_result);
    }

    private static ConnectingViewModel Build(
        IInstallTokenService svc, Action<SetupConfig> onConnected, Action onFallback)
        => new(svc, "sai_token123456", "https://suavollc.com", "fp", "PC-1", "3.77.0", onConnected, onFallback);

    [Fact]
    public async Task StartAsync_Success_CallsOnConnectedWithMappedConfig()
    {
        var svc = new FakeTokenService(new InstallTokenExchangeResult("sagent_live", "a-1", "p-1", "Queen"));
        SetupConfig? connected = null;
        var fellBack = false;
        var vm = Build(svc, c => connected = c, () => fellBack = true);

        await vm.StartAsync();

        Assert.False(fellBack);
        Assert.NotNull(connected);
        Assert.Equal("p-1", connected!.PharmacyId);
        Assert.Equal("sagent_live", connected.ApiKey);
        Assert.Equal("a-1", connected.AgentId);
        Assert.Equal("https://suavollc.com", connected.CloudUrl);
        Assert.Equal("v3.77.0", connected.ReleaseTag);
        Assert.False(connected.LearningMode);
    }

    [Fact]
    public async Task StartAsync_ExchangeFails_FallsBackNeverConnects()
    {
        var svc = new FakeTokenService(null);   // throws
        SetupConfig? connected = null;
        var fellBack = false;
        var vm = Build(svc, c => connected = c, () => fellBack = true);

        await vm.StartAsync();

        Assert.True(fellBack);
        Assert.Null(connected);
    }
}
