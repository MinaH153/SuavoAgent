using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

public class IpcMessageTests
{
    [Fact]
    public void IpcMessage_CreatesWithFields()
    {
        var msg = new IpcMessage(1, "req-001", IpcCommands.WritebackDelivery, "{\"rx\":\"123\"}");
        Assert.Equal(1, msg.Version);
        Assert.Equal(IpcCommands.WritebackDelivery, msg.Command);
    }

    [Fact]
    public void IpcResponse_SuccessCase()
    {
        var resp = new IpcResponse("req-001", true, "ok", null);
        Assert.True(resp.Success);
        Assert.Null(resp.Error);
    }

    [Fact]
    public void IpcCommands_HasExpectedValues()
    {
        Assert.Equal("ping", IpcCommands.Ping);
        Assert.Equal("writeback_delivery", IpcCommands.WritebackDelivery);
        Assert.Equal("drain", IpcCommands.Drain);
    }
}
