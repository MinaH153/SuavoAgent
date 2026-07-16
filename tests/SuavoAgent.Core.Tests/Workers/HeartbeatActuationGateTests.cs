using System.Diagnostics;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.ActionGrammarV1.Verbs.Actuation;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.State;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class HeartbeatActuationGateTests : IDisposable
{
    private readonly AgentStateDb _db = new(":memory:");

    [Fact]
    public async Task ReadHelperActuationGate_UnconfiguredGateway_ReturnsUnavailable()
    {
        using var services = new ServiceCollection().BuildServiceProvider();
        var worker = Worker(services);

        var state = await ReadAsync(worker, CancellationToken.None);

        Assert.Null(state);
    }

    [Fact]
    public async Task ReadHelperActuationGate_GatewayFailure_ReturnsUnavailable()
    {
        var gateway = new ScriptedGateway(_ => throw new IOException("helper unavailable"));
        using var services = new ServiceCollection()
            .AddSingleton<IActuationGateway>(gateway)
            .BuildServiceProvider();
        var worker = Worker(services);

        var state = await ReadAsync(worker, CancellationToken.None);

        Assert.Null(state);
        Assert.Equal(1, gateway.StateReads);
    }

    [Fact]
    public async Task ReadHelperActuationGate_HungGateway_TimesOutAndReturnsUnavailable()
    {
        var gateway = new ScriptedGateway(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var services = new ServiceCollection()
            .AddSingleton<IActuationGateway>(gateway)
            .BuildServiceProvider();
        var worker = Worker(services);
        var stopwatch = Stopwatch.StartNew();

        var state = await ReadAsync(worker, CancellationToken.None);

        Assert.Null(state);
        Assert.Equal(1, gateway.StateReads);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(4.5), TimeSpan.FromSeconds(8));
    }

    [Fact]
    public async Task ReadHelperActuationGate_CallerCancellation_Propagates()
    {
        var gateway = new ScriptedGateway(async ct =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("unreachable");
        });
        using var services = new ServiceCollection()
            .AddSingleton<IActuationGateway>(gateway)
            .BuildServiceProvider();
        var worker = Worker(services);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ReadAsync(worker, cancellation.Token));
    }

    private HeartbeatWorker Worker(IServiceProvider services) => new(
        NullLogger<HeartbeatWorker>.Instance,
        Options.Create(new AgentOptions
        {
            AgentId = "gate-test-agent",
            MachineFingerprint = "gate-test-machine",
            PharmacyId = "gate-test-pharmacy",
        }),
        services,
        _db);

    private static Task<ActuationGateState?> ReadAsync(
        HeartbeatWorker worker,
        CancellationToken cancellationToken)
    {
        var method = typeof(HeartbeatWorker).GetMethod(
            "ReadHelperActuationGateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return Assert.IsAssignableFrom<Task<ActuationGateState?>>(
            method!.Invoke(worker, new object[] { "factory_regression", cancellationToken }));
    }

    public void Dispose() => _db.Dispose();

    private sealed class ScriptedGateway(
        Func<CancellationToken, Task<ActuationGateState>> getState) : IActuationGateway
    {
        public int StateReads { get; private set; }

        public Task<ActuationGateState> GetStateAsync(CancellationToken ct)
        {
            StateReads++;
            return getState(ct);
        }

        public Task<ActuationResult> ClickByLabelAsync(ClickByLabelRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> ClickBySignatureAsync(ClickBySignatureRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> TypeTextAsync(TypeTextRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> PressKeysAsync(PressKeysRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> LaunchSandboxAppAsync(LaunchSandboxAppRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> ReloadAllowlistAsync(CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> AssertElementAsync(AssertElementRequest req, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<ActuationResult> DiscoverElementsAsync(DiscoverElementsRequest req, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
