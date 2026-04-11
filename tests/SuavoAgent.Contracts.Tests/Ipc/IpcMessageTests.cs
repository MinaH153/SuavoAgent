using System.Text.Json;
using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Ipc;

public class IpcMessageTests
{
    [Fact]
    public void IpcRequest_CreatesWithFields()
    {
        var data = JsonSerializer.SerializeToElement(new { rx = "123" });
        var req = new IpcRequest("req-001", IpcCommands.WritebackDelivery, 1, data);
        Assert.Equal("req-001", req.Id);
        Assert.Equal(1, req.Version);
        Assert.Equal(IpcCommands.WritebackDelivery, req.Command);
        Assert.NotNull(req.Data);
    }

    [Fact]
    public void IpcResponse_SuccessCase()
    {
        var resp = new IpcResponse("req-001", IpcStatus.Ok, IpcCommands.Ping, null, null);
        Assert.Equal(IpcStatus.Ok, resp.Status);
        Assert.Null(resp.Error);
    }

    [Fact]
    public void IpcResponse_ErrorCase()
    {
        var err = new IpcError("NOT_FOUND", "Resource not found", false, 1);
        var resp = new IpcResponse("req-002", IpcStatus.NotFound, IpcCommands.WritebackDelivery, null, err);
        Assert.Equal(IpcStatus.NotFound, resp.Status);
        Assert.NotNull(resp.Error);
        Assert.Equal("NOT_FOUND", resp.Error.Code);
        Assert.False(resp.Error.Retryable);
    }

    [Fact]
    public void IpcCommands_HasExpectedValues()
    {
        Assert.Equal("ping", IpcCommands.Ping);
        Assert.Equal("writeback_delivery", IpcCommands.WritebackDelivery);
        Assert.Equal("drain", IpcCommands.Drain);
        Assert.Equal("helper_status", IpcCommands.HelperStatus);
        Assert.Equal("attach_pioneerrx", IpcCommands.AttachPioneerRx);
    }

    [Fact]
    public void IpcStatus_HasExpectedValues()
    {
        Assert.Equal(200, IpcStatus.Ok);
        Assert.Equal(400, IpcStatus.BadRequest);
        Assert.Equal(404, IpcStatus.NotFound);
        Assert.Equal(408, IpcStatus.Timeout);
        Assert.Equal(500, IpcStatus.InternalError);
    }
}
