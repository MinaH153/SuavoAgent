using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// Main-Helper relay endpoint. Only a mutually verified second instance of the
/// exact installed Helper can connect. The server receives only privacy-safe
/// status/observation contracts and maps them into the live observer sink.
/// </summary>
internal sealed class BrowserObservationRelayServer : IAsyncDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LeaseCheckInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);

    private readonly IBrowserRelayServerTransport _transport;
    private readonly IBrowserConnectorSink _sink;
    private readonly Func<ObservationKeyLease?> _currentLease;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;
    private readonly IBrowserRelayEntropy _entropy;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _runTask;
    private int _started;

    public BrowserObservationRelayServer(
        IBrowserRelayServerTransport transport,
        IBrowserConnectorSink sink,
        Func<ObservationKeyLease?> currentLease,
        ILogger logger,
        TimeProvider? timeProvider = null,
        IBrowserRelayEntropy? entropy = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _currentLease = currentLease ?? throw new ArgumentNullException(nameof(currentLease));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entropy = entropy ?? new CryptographicBrowserRelayEntropy();
    }

    public static BrowserObservationRelayServer CreateProduction(
        IBrowserConnectorSink sink,
        Func<ObservationKeyLease?> currentLease,
        ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Browser relay is Windows-only.");
        var verifier = new WindowsBrowserRelayPeerIdentityVerifier();
        var transport = new NamedPipeBrowserRelayServerTransport(
            BrowserRelayPipeName.ForCurrentProcess(),
            verifier);
        return new BrowserObservationRelayServer(transport, sink, currentLease, logger);
    }

    public void Start(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            throw new InvalidOperationException("browser_relay_already_started");
        var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        _runTask = Task.Run(async () =>
        {
            try
            {
                await RunAsync(linked.Token).ConfigureAwait(false);
            }
            finally
            {
                linked.Dispose();
            }
        }, CancellationToken.None);
    }

    internal async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var connection = await _transport.AcceptAsync(cancellationToken)
                    .ConfigureAwait(false);
                await RunConnectionAsync(connection.Stream, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (BrowserRelayProtocolException ex)
            {
                Report(BrowserConnectorState.Degraded, ex.ReasonCode);
                _logger.Warning("Browser relay rejected ({ReasonCode})", ex.ReasonCode);
            }
            catch (Exception ex)
            {
                Report(BrowserConnectorState.Degraded, BrowserConnectorReasonCodes.InternalFailure);
                _logger.Warning(
                    "Browser relay failed closed ({ExceptionType})",
                    ex.GetType().Name);
            }

            Report(BrowserConnectorState.Disconnected, BrowserConnectorReasonCodes.Disconnected);
            try
            {
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunConnectionAsync(Stream stream, CancellationToken cancellationToken)
    {
        using var lease = BrowserRelayLeaseSnapshot.TryCreate(
            _currentLease(),
            _timeProvider.GetUtcNow())
            ?? throw new BrowserRelayProtocolException(BrowserConnectorReasonCodes.SessionExpired);
        var relayKey = BrowserRelayProtocol.NewSecret(_entropy, 32);
        var domainHashKey = BrowserDomainHashKeyDerivation.Derive(
            lease.KeyMaterial,
            lease.LeaseId,
            lease.SessionBinding,
            lease.Epoch);
        try
        {
            using var handshakeDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeDeadline.CancelAfter(HandshakeTimeout);
            Report(BrowserConnectorState.HandshakePending, BrowserConnectorReasonCodes.HandshakePending);
            var clientNonce = await BrowserRelayProtocol.ReadClientHelloAsync(
                stream,
                handshakeDeadline.Token).ConfigureAwait(false);
            var grant = new BrowserRelayServerGrant(
                BrowserRelayProtocol.NewToken(_entropy, 16),
                clientNonce,
                BrowserRelayProtocol.NewToken(_entropy, 32),
                lease.LeaseId,
                lease.SessionBinding,
                lease.Epoch,
                lease.ExpiresAtUtc,
                domainHashKey,
                relayKey);
            await BrowserRelayProtocol.WriteServerGrantAsync(
                stream,
                grant,
                handshakeDeadline.Token).ConfigureAwait(false);
            await BrowserRelayProtocol.VerifyClientProofAsync(
                stream,
                grant,
                handshakeDeadline.Token).ConfigureAwait(false);
            await BrowserRelayProtocol.WriteServerAcceptedAsync(
                stream,
                grant,
                handshakeDeadline.Token).ConfigureAwait(false);

            using var sessionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            var leaseRotated = 0;
            var leaseMonitor = Task.Run(async () =>
            {
                try
                {
                    using var timer = new PeriodicTimer(LeaseCheckInterval);
                    while (await timer.WaitForNextTickAsync(sessionCancellation.Token)
                               .ConfigureAwait(false))
                    {
                        if (lease.Matches(_currentLease(), _timeProvider.GetUtcNow()))
                            continue;
                        Volatile.Write(ref leaseRotated, 1);
                        sessionCancellation.Cancel();
                        break;
                    }
                }
                catch (OperationCanceledException) when (sessionCancellation.IsCancellationRequested)
                {
                    // Normal connection shutdown or key/session rotation.
                }
            }, CancellationToken.None);

            long expectedCounter = 1;
            try
            {
                while (!sessionCancellation.IsCancellationRequested)
                {
                    var message = await BrowserRelayProtocol.ReadAuthenticatedMessageAsync(
                        stream,
                        grant,
                        expectedCounter,
                        sessionCancellation.Token).ConfigureAwait(false);
                    if (message is null)
                        break;
                    if (!lease.Matches(_currentLease(), _timeProvider.GetUtcNow()))
                    {
                        Volatile.Write(ref leaseRotated, 1);
                        break;
                    }

                    var receivedAt = _timeProvider.GetUtcNow();
                    if (message.Status is { } status)
                        _sink.OnStatus(status with { Timestamp = receivedAt });
                    else if (message.Observation is { } observation)
                        _sink.OnObservation(observation with { Timestamp = receivedAt });
                    else
                        throw new BrowserRelayProtocolException(
                            BrowserConnectorReasonCodes.MessageInvalid);
                    expectedCounter = checked(expectedCounter + 1);
                }
            }
            catch (OperationCanceledException) when (Volatile.Read(ref leaseRotated) != 0)
            {
                // Rotation is surfaced below with a closed-vocabulary status.
            }
            finally
            {
                sessionCancellation.Cancel();
                await leaseMonitor.ConfigureAwait(false);
            }

            if (Volatile.Read(ref leaseRotated) != 0)
                Report(BrowserConnectorState.Degraded, BrowserConnectorReasonCodes.SessionExpired);
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(domainHashKey);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(relayKey);
        }
    }

    private void Report(BrowserConnectorState state, string reasonCode)
    {
        var safeReason = BrowserConnectorReasonCodes.IsSafe(reasonCode)
            ? reasonCode
            : BrowserConnectorReasonCodes.InternalFailure;
        try
        {
            _sink.OnStatus(new BrowserConnectorStatus(
                state,
                safeReason,
                _timeProvider.GetUtcNow()));
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Browser relay status sink failed ({ExceptionType})",
                ex.GetType().Name);
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_runTask is not null)
        {
            try
            {
                await _runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
        }
        _shutdown.Dispose();
    }
}
