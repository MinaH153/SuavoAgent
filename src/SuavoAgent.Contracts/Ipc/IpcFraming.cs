using System.Buffers.Binary;
using System.Text;

namespace SuavoAgent.Contracts.Ipc;

public static class IpcFraming
{
    public const int MaxPayloadSize = 65536;
    public const int HeaderSize = 4;

    public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxPayloadSize)
            throw new InvalidOperationException($"Payload {payload.Length} bytes exceeds max {MaxPayloadSize}");
        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        var read = await ReadExactAsync(stream, header, ct);
        if (read < HeaderSize) return null;
        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxPayloadSize)
            throw new InvalidOperationException($"Frame size {length} exceeds max {MaxPayloadSize}");
        if (length == 0) return "";
        var payload = new byte[length];
        read = await ReadExactAsync(stream, payload, ct);
        if (read < (int)length) return null;
        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) return offset;
            offset += n;
        }
        return offset;
    }
}
