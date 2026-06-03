using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Pricing;
using Xunit;

namespace SuavoAgent.Core.Tests.Pricing;

/// <summary>
/// The pricing pre-flight gate is the safety wall that stops a UIA pricing job from
/// driving the live PMS screen when the Helper can't actually see or act on it — the
/// silent "commands sent but never actuate" failure when the Helper is stranded in
/// Session 0 or the Core→Broker pipe is dead. It fails closed: if interactivity can't
/// be proven, the job must not run.
/// </summary>
public class HelperInteractivePreflightTests
{
    private static readonly TimeSpan Connect = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Ping = TimeSpan.FromSeconds(5);

    private static JsonElement PingPayload(uint helperSession, uint activeConsole, bool isInteractive) =>
        JsonSerializer.SerializeToElement(
            new HelperPingInfo(1234, helperSession, activeConsole, isInteractive));

    private static IpcResponse PingOk(JsonElement data) =>
        new("id", IpcStatus.Ok, IpcCommands.Ping, data, null);

    [Fact]
    public async Task NullClient_FailsClosed()
    {
        var result = await HelperInteractivePreflight.CheckAsync(null, Connect, Ping, default);

        Assert.False(result.Ok);
        Assert.False(result.IsInteractive);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task ConnectFails_FailsClosed()
    {
        var ipc = new PreflightFakeIpc { IsConnectedValue = false, ConnectShouldSucceed = false };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.False(result.Ok);
        Assert.Contains("unreachable", result.Error);
        Assert.Equal(0, ipc.SendCount); // never got far enough to ping
    }

    [Fact]
    public async Task NoPingResponse_FailsClosed_AsStranded()
    {
        var ipc = new PreflightFakeIpc { NextResponse = _ => null };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.False(result.Ok);
        Assert.Contains("stranded", result.Error);
    }

    [Fact]
    public async Task NonOkStatus_FailsClosed()
    {
        var ipc = new PreflightFakeIpc
        {
            NextResponse = _ => new IpcResponse("id", IpcStatus.InternalError, IpcCommands.Ping, null, null),
        };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.False(result.Ok);
    }

    [Fact]
    public async Task MissingSessionDiagnostics_FailsClosed_AsOutOfDate()
    {
        // An old Helper answers ping with null Data (no session info). Fail closed:
        // if we can't PROVE the Helper can see the screen, we don't drive it.
        var ipc = new PreflightFakeIpc { NextResponse = _ => PingOk(default) };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.False(result.Ok);
        Assert.Contains("date", result.Error);
    }

    [Fact]
    public async Task Session0_FailsClosed_AsBlind()
    {
        // Helper alive and answering, but in the non-interactive services session.
        var ipc = new PreflightFakeIpc
        {
            NextResponse = _ => PingOk(PingPayload(helperSession: 0, activeConsole: 1, isInteractive: false)),
        };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.False(result.Ok);
        Assert.False(result.IsInteractive);
        Assert.Contains("Session 0", result.Error);
        Assert.Contains("Broker", result.Error); // tells the operator how to recover
    }

    [Fact]
    public async Task InteractiveHelper_Passes()
    {
        var ipc = new PreflightFakeIpc
        {
            NextResponse = _ => PingOk(PingPayload(helperSession: 1, activeConsole: 1, isInteractive: true)),
        };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.True(result.Ok);
        Assert.True(result.IsInteractive);
        Assert.Null(result.Error);
        Assert.Equal(1u, result.HelperSessionId);
    }

    [Fact]
    public async Task NotConnected_ButConnectSucceeds_AndInteractive_Passes()
    {
        var ipc = new PreflightFakeIpc
        {
            IsConnectedValue = false,
            ConnectShouldSucceed = true,
            NextResponse = _ => PingOk(PingPayload(helperSession: 2, activeConsole: 2, isInteractive: true)),
        };

        var result = await HelperInteractivePreflight.CheckAsync(ipc, Connect, Ping, default);

        Assert.True(result.Ok);
        Assert.Equal(1, ipc.ConnectCount);
        Assert.Equal(1, ipc.SendCount);
    }

    private sealed class PreflightFakeIpc : IIpcCommandClient
    {
        public bool IsConnectedValue { get; set; } = true;
        public bool ConnectShouldSucceed { get; set; } = true;
        public Func<IpcRequest, IpcResponse?> NextResponse { get; set; } =
            _ => new IpcResponse("id", IpcStatus.Ok, IpcCommands.Ping, null, null);
        public int SendCount { get; private set; }
        public int ConnectCount { get; private set; }

        public bool IsConnected => IsConnectedValue;

        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct)
        {
            ConnectCount++;
            if (ConnectShouldSucceed) IsConnectedValue = true;
            return Task.FromResult(ConnectShouldSucceed);
        }

        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            SendCount++;
            return Task.FromResult(NextResponse(request));
        }
    }
}
