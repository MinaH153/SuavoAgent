using System.Security.Cryptography;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// Native-host-side relay sink. Its only post-handshake methods accept the
/// already privacy-reduced browser contracts; no URL or hostname field exists
/// on this boundary.
/// </summary>
internal sealed class BrowserObservationRelayClient : IBrowserConnectorSink, IAsyncDisposable
{
    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(3);
    private readonly IBrowserRelayDuplex _connection;
    private readonly BrowserRelayClientGrant _grant;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private long _counter;
    private int _disposed;

    private BrowserObservationRelayClient(
        IBrowserRelayDuplex connection,
        BrowserRelayClientGrant grant,
        TimeProvider timeProvider)
    {
        _connection = connection;
        _grant = grant;
        _timeProvider = timeProvider;
    }

    public ReadOnlyMemory<byte> DomainHashKey => _grant.DomainHashKey;

    public DateTimeOffset LeaseExpiresAtUtc => _grant.ExpiresAtUtc;

    public static async Task<BrowserObservationRelayClient> ConnectAsync(
        IBrowserRelayClientTransport transport,
        CancellationToken cancellationToken,
        TimeProvider? timeProvider = null,
        IBrowserRelayEntropy? entropy = null)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var clock = timeProvider ?? TimeProvider.System;
        var random = entropy ?? new CryptographicBrowserRelayEntropy();
        var connection = await transport.ConnectAsync(cancellationToken).ConfigureAwait(false);
        BrowserRelayClientGrant? grant = null;
        using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        handshakeDeadline.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var clientNonce = BrowserRelayProtocol.NewToken(random, 32);
            await BrowserRelayProtocol.WriteClientHelloAsync(
                connection.Stream,
                clientNonce,
                handshakeDeadline.Token).ConfigureAwait(false);
            grant = await BrowserRelayProtocol.ReadServerGrantAsync(
                connection.Stream,
                clientNonce,
                clock.GetUtcNow(),
                handshakeDeadline.Token).ConfigureAwait(false);
            await BrowserRelayProtocol.WriteClientProofAsync(
                connection.Stream,
                grant,
                handshakeDeadline.Token).ConfigureAwait(false);
            await BrowserRelayProtocol.VerifyServerAcceptedAsync(
                connection.Stream,
                grant,
                handshakeDeadline.Token).ConfigureAwait(false);
            var result = new BrowserObservationRelayClient(connection, grant, clock);
            grant = null;
            connection = null!;
            return result;
        }
        finally
        {
            grant?.Dispose();
            if (connection is not null)
                await connection.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void OnStatus(BrowserConnectorStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (!Enum.IsDefined(status.State) ||
            !BrowserConnectorReasonCodes.IsSafe(status.ReasonCode))
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.MessageInvalid);
        SendAsync(
            (counter, cancellationToken) => BrowserRelayProtocol.WriteStatusAsync(
                _connection.Stream,
                _grant,
                counter,
                status,
                cancellationToken));
    }

    public void OnObservation(BrowserDomainObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!IsSafeObservation(observation))
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.MessageInvalid);
        SendAsync(
            (counter, cancellationToken) => BrowserRelayProtocol.WriteObservationAsync(
                _connection.Stream,
                _grant,
                counter,
                observation,
                cancellationToken));
    }

    private void SendAsync(Func<long, CancellationToken, Task> write)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_grant.ExpiresAtUtc <= _timeProvider.GetUtcNow())
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.SessionExpired);
        using var deadline = new CancellationTokenSource(WriteTimeout);
        var acquired = false;
        try
        {
            _writeLock.Wait(deadline.Token);
            acquired = true;
            var counter = checked(_counter + 1);
            write(counter, deadline.Token).GetAwaiter().GetResult();
            _counter = counter;
        }
        catch (OperationCanceledException)
        {
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.Disconnected);
        }
        catch (BrowserRelayProtocolException)
        {
            throw;
        }
        catch
        {
            throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.Disconnected);
        }
        finally
        {
            if (acquired)
                _writeLock.Release();
        }
    }

    private static bool IsSafeObservation(BrowserDomainObservation observation)
    {
        var category = observation.Category;
        var safeCategory = category.Length is >= 1 and <= 64 &&
            char.IsAsciiLetterLower(category[0]) &&
            category.All(character =>
                char.IsAsciiLetterLower(character) ||
                char.IsAsciiDigit(character) ||
                character is '_' or ':' or '-');
        var hash = observation.HostnameHash;
        var safeHash = hash is null ||
            hash.Length == 64 && hash.All(character =>
                char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');
        return safeCategory &&
               safeHash &&
               (string.Equals(category, "unknown", StringComparison.Ordinal) == (hash is not null)) &&
               Enum.IsDefined(observation.Browser) &&
               observation.Counter > 0;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            await _writeLock.WaitAsync(deadline.Token).ConfigureAwait(false);
            try
            {
                if (_grant.ExpiresAtUtc > _timeProvider.GetUtcNow())
                {
                    await BrowserRelayProtocol.WriteStatusAsync(
                        _connection.Stream,
                        _grant,
                        checked(_counter + 1),
                        new BrowserConnectorStatus(
                            BrowserConnectorState.Disconnected,
                            BrowserConnectorReasonCodes.Disconnected,
                            _timeProvider.GetUtcNow()),
                        deadline.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                _writeLock.Release();
            }
        }
        catch
        {
            // Closing the authenticated pipe is itself a fail-closed disconnect.
        }
        await _connection.DisposeAsync().ConfigureAwait(false);
        _grant.Dispose();
        _writeLock.Dispose();
    }
}
