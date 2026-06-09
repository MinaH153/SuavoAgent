using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Core.Agentic.Adapters;
using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Pins the capture_screen IPC contract change that routes explore_sandbox to the Helper's
/// WINDOW-SCOPED sandbox capture path: a sandbox perceiver embeds {targetProcess:"sandbox"} in the
/// request Data; every other caller (PMS cadence / navigate / replay) sends null Data, byte-for-byte
/// as before. The fail-closed-on-error contract is unchanged for both.
/// </summary>
public class HelperPerceiverTargetProcessTests
{
    private sealed class CapturingIpcClient : IIpcCommandClient
    {
        public IpcRequest? LastRequest;
        public int Status = 500; // non-200 by default → PerceiveAsync returns null; we assert on the request
        public bool IsConnected => true;
        public Task<bool> ConnectAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<IpcResponse?> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult<IpcResponse?>(new IpcResponse(request.Id, Status, request.Command, null, null));
        }
    }

    [Fact]
    public async Task PerceiveAsync_NullTargetProcess_SendsNullData()
    {
        var ipc = new CapturingIpcClient();
        var perceiver = new HelperPerceiver(ipc);

        await perceiver.PerceiveAsync(CancellationToken.None);

        Assert.NotNull(ipc.LastRequest);
        Assert.Equal(IpcCommands.CaptureScreen, ipc.LastRequest!.Command);
        Assert.Null(ipc.LastRequest.Data); // PMS path: contract unchanged
    }

    [Fact]
    public async Task PerceiveAsync_SandboxTargetProcess_EmbedsSandboxInData()
    {
        var ipc = new CapturingIpcClient();
        var perceiver = new HelperPerceiver(ipc, targetProcess: "sandbox");

        await perceiver.PerceiveAsync(CancellationToken.None);

        Assert.NotNull(ipc.LastRequest?.Data);
        Assert.Equal("sandbox", ipc.LastRequest!.Data!.Value.GetProperty("targetProcess").GetString());
    }

    [Fact]
    public async Task PerceiveAsync_SandboxTargetProcess_StillFailsClosedOnNon200()
    {
        var ipc = new CapturingIpcClient { Status = 500 };
        var perceiver = new HelperPerceiver(ipc, targetProcess: "sandbox");

        var screen = await perceiver.PerceiveAsync(CancellationToken.None);

        Assert.Null(screen); // any non-200 → null (no-perception strike), unchanged for the sandbox path
    }
}
