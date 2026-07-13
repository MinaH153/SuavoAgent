using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal sealed class BrowserRelayJsonDocument : IDisposable
{
    private byte[]? _payload;
    private JsonDocument? _document;

    public BrowserRelayJsonDocument(byte[] payload)
    {
        _payload = payload ?? throw new ArgumentNullException(nameof(payload));
        _document = JsonDocument.Parse(
            payload,
            new JsonDocumentOptions { MaxDepth = 16 });
    }

    public JsonElement RootElement =>
        _document?.RootElement ?? throw new ObjectDisposedException(nameof(BrowserRelayJsonDocument));

    public void Dispose()
    {
        _document?.Dispose();
        _document = null;
        var payload = Interlocked.Exchange(ref _payload, null);
        if (payload is not null)
            CryptographicOperations.ZeroMemory(payload);
    }
}

internal sealed record BrowserRelayServerGrant(
    string SessionId,
    string ClientNonce,
    string ServerNonce,
    string LeaseId,
    string SessionBinding,
    long LeaseEpoch,
    DateTimeOffset ExpiresAtUtc,
    byte[] DomainHashKey,
    byte[] RelayKey);

internal sealed class BrowserRelayClientGrant : IDisposable
{
    private int _disposed;

    public BrowserRelayClientGrant(
        string sessionId,
        string clientNonce,
        string serverNonce,
        string leaseId,
        string sessionBinding,
        long leaseEpoch,
        DateTimeOffset expiresAtUtc,
        byte[] domainHashKey,
        byte[] relayKey)
    {
        SessionId = sessionId;
        ClientNonce = clientNonce;
        ServerNonce = serverNonce;
        LeaseId = leaseId;
        SessionBinding = sessionBinding;
        LeaseEpoch = leaseEpoch;
        ExpiresAtUtc = expiresAtUtc;
        DomainHashKey = domainHashKey;
        RelayKey = relayKey;
    }

    public string SessionId { get; }
    public string ClientNonce { get; }
    public string ServerNonce { get; }
    public string LeaseId { get; }
    public string SessionBinding { get; }
    public long LeaseEpoch { get; }
    public DateTimeOffset ExpiresAtUtc { get; }
    public byte[] DomainHashKey { get; }
    public byte[] RelayKey { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CryptographicOperations.ZeroMemory(DomainHashKey);
        CryptographicOperations.ZeroMemory(RelayKey);
    }
}

internal sealed record BrowserRelayMessage(
    BrowserConnectorStatus? Status,
    BrowserDomainObservation? Observation)
{
    public static BrowserRelayMessage ForStatus(BrowserConnectorStatus status) => new(status, null);
    public static BrowserRelayMessage ForObservation(BrowserDomainObservation observation) => new(null, observation);
}

internal static class BrowserRelayFraming
{
    public static async Task<byte[]?> ReadFrameAsync(
        Stream stream,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadExactOrEofAsync(stream, header, cancellationToken).ConfigureAwait(false);
        if (headerRead == 0)
            return null;
        if (headerRead != header.Length)
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.FrameTruncated);
        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maximumBytes)
            throw new BrowserRelayProtocolException(
                length > maximumBytes
                    ? BrowserConnectorReasonCodes.FrameOversize
                    : BrowserConnectorReasonCodes.FrameInvalid);
        var payload = new byte[length];
        var payloadRead = await ReadExactOrEofAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        if (payloadRead == payload.Length)
            return payload;
        CryptographicOperations.ZeroMemory(payload);
        throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.FrameTruncated);
    }

    public static async Task WriteFrameAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (payload.Length == 0 || payload.Length > maximumBytes)
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.FrameOversize);
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<int> ReadExactOrEofAsync(
        Stream stream,
        Memory<byte> destination,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < destination.Length)
        {
            var read = await stream.ReadAsync(destination[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                return total;
            total += read;
        }
        return total;
    }
}
