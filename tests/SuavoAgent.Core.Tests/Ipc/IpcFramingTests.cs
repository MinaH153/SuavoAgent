using SuavoAgent.Contracts.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IpcFramingTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        using var ms = new MemoryStream();
        var json = """{"command":"ping","id":"123"}""";
        await IpcFraming.WriteFrameAsync(ms, json);
        ms.Position = 0;
        var result = await IpcFraming.ReadFrameAsync(ms);
        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadFrame_EmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        Assert.Null(await IpcFraming.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task WriteFrame_OversizedPayload_Throws()
    {
        using var ms = new MemoryStream();
        var huge = new string('x', IpcFraming.MaxPayloadSize + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => IpcFraming.WriteFrameAsync(ms, huge));
    }

    [Fact]
    public async Task ReadFrame_OversizedHeader_Throws()
    {
        using var ms = new MemoryStream();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(IpcFraming.MaxPayloadSize + 1));
        ms.Write(header);
        ms.Write(new byte[100]);
        ms.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => IpcFraming.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task MultipleFrames_ReadSequentially()
    {
        using var ms = new MemoryStream();
        await IpcFraming.WriteFrameAsync(ms, "frame1");
        await IpcFraming.WriteFrameAsync(ms, "frame2");
        await IpcFraming.WriteFrameAsync(ms, "frame3");
        ms.Position = 0;
        Assert.Equal("frame1", await IpcFraming.ReadFrameAsync(ms));
        Assert.Equal("frame2", await IpcFraming.ReadFrameAsync(ms));
        Assert.Equal("frame3", await IpcFraming.ReadFrameAsync(ms));
        Assert.Null(await IpcFraming.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task EmptyPayload_RoundTrips()
    {
        using var ms = new MemoryStream();
        await IpcFraming.WriteFrameAsync(ms, "");
        ms.Position = 0;
        Assert.Equal("", await IpcFraming.ReadFrameAsync(ms));
    }
}
