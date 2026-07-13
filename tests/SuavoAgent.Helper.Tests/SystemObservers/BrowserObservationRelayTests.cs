using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Threading.Channels;
using Serilog;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserObservationRelayTests
{
    [Fact]
    public async Task UntrustedParent_IsRejectedBeforeRelayRequestsAnyKey()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var transport = new CountingRejectedTransport();
        var parent = new StubParentVerifier(trusted: false);
        var result = await BrowserNativeMessagingEntryPoint.RunVerifiedAsync(
            new BrowserHostLaunchContext(
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                0),
            BrowserConnectorAuthorityTests.VerifiedAuthority(),
            new StubChannelVerifier(trusted: true),
            parent,
            transport,
            _ => null,
            logger,
            new MemoryStream(),
            new MemoryStream(),
            CancellationToken.None,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));

        Assert.Equal(4, result);
        Assert.Equal(1, parent.CallCount);
        Assert.Equal(BrowserConnectorAuthorityTests.ChromePath, parent.LastAuthorizedPath);
        Assert.Equal(0, transport.ConnectCount);
    }

    [Fact]
    public async Task UnauthorizedOrigin_IsRejectedBeforeParentOrRelay()
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var transport = new CountingRejectedTransport();
        var channel = new StubChannelVerifier(trusted: true);
        var parent = new StubParentVerifier(trusted: true);
        var result = await BrowserNativeMessagingEntryPoint.RunVerifiedAsync(
            new BrowserHostLaunchContext(
                "chrome-extension://cccccccccccccccccccccccccccccccc/",
                0),
            BrowserConnectorAuthorityTests.VerifiedAuthority(),
            channel,
            parent,
            transport,
            _ => null,
            logger,
            new MemoryStream(),
            new MemoryStream(),
            CancellationToken.None,
            new FixedTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_800_000_000)));

        Assert.Equal(4, result);
        Assert.Equal(0, channel.CallCount);
        Assert.Equal(0, parent.CallCount);
        Assert.Equal(0, transport.ConnectCount);
    }

    [Fact]
    public void DomainHashSubkey_DiffersAndCannotAuthenticateObservationBatches()
    {
        var lease = Lease(1);
        var leaseKey = Convert.FromBase64String(lease.KeyMaterial);
        var domainKey = BrowserDomainHashKeyDerivation.Derive(
            leaseKey,
            lease.LeaseId,
            lease.SessionBinding,
            lease.Epoch);
        try
        {
            Assert.Equal(32, domainKey.Length);
            Assert.False(CryptographicOperations.FixedTimeEquals(leaseKey, domainKey));
            var batch = ObservationBatchAuthentication.Seal(new BehavioralEventBatch
            {
                BatchId = "browser-key-separation-batch",
                StreamId = "browser-key-separation-stream",
                Channel = BehavioralEventChannels.System,
                FirstSequence = 0,
                LastSequence = 0,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                Events = Array.Empty<BehavioralEvent>(),
            }, lease);
            Assert.True(ObservationBatchAuthentication.Verify(batch, lease.KeyMaterial));
            Assert.False(ObservationBatchAuthentication.Verify(
                batch,
                Convert.ToBase64String(domainKey)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(leaseKey);
            CryptographicOperations.ZeroMemory(domainKey);
        }
    }

    [Fact]
    public void NativeMessagingBranch_PrecedesEveryConsoleWriterAndLogger()
    {
        var source = File.ReadAllText(FindRepositoryFile("src/SuavoAgent.Helper/Program.cs"));
        var branch = source.IndexOf(
            "BrowserNativeMessagingEntryPoint.IsCandidate(args)",
            StringComparison.Ordinal);
        var workerConsole = source.IndexOf(
            "UiaSnapshotWorkerMode.TryRun(args, Console.Out",
            StringComparison.Ordinal);
        var consoleLogger = source.IndexOf(
            ".WriteTo.Console()",
            StringComparison.Ordinal);

        Assert.True(branch >= 0);
        Assert.True(workerConsole > branch);
        Assert.True(consoleLogger > branch);
    }

    [Fact]
    public async Task InjectedDuplexTransport_RelaysOnlyReducedObservationAndStatus()
    {
        var lease = Lease(1);
        var transport = new InMemoryRelayTransport();
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().CreateLogger();
        var server = new BrowserObservationRelayServer(
            transport,
            sink,
            () => lease,
            logger);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await using var client = await BrowserObservationRelayClient.ConnectAsync(
            transport,
            CancellationToken.None);

        client.OnStatus(new BrowserConnectorStatus(
            BrowserConnectorState.Ready,
            BrowserConnectorReasonCodes.Ready,
            DateTimeOffset.UtcNow));
        client.OnObservation(new BrowserDomainObservation(
            "business_portal",
            null,
            BrowserFamily.Chrome,
            1,
            DateTimeOffset.UtcNow));
        await WaitUntilAsync(() => sink.Observations.Count == 1);

        var observation = Assert.Single(sink.Observations);
        Assert.Equal("business_portal", observation.Category);
        Assert.Null(observation.HostnameHash);
        Assert.Contains(sink.Statuses, status =>
            status.State == BrowserConnectorState.Ready &&
            status.ReasonCode == BrowserConnectorReasonCodes.Ready);

        stop.Cancel();
        await serverTask;
        await server.DisposeAsync();
    }

    [Fact]
    public async Task LeaseRotation_CancelsRelayAndReadinessFailsClosed()
    {
        var lease = Lease(1);
        var transport = new InMemoryRelayTransport();
        var sink = new CapturingSink();
        using var logger = new LoggerConfiguration().CreateLogger();
        var server = new BrowserObservationRelayServer(
            transport,
            sink,
            () => lease,
            logger);
        using var stop = new CancellationTokenSource();
        var serverTask = server.RunAsync(stop.Token);
        await using var client = await BrowserObservationRelayClient.ConnectAsync(
            transport,
            CancellationToken.None);
        client.OnStatus(new BrowserConnectorStatus(
            BrowserConnectorState.Ready,
            BrowserConnectorReasonCodes.Ready,
            DateTimeOffset.UtcNow));

        lease = Lease(2);
        await WaitUntilAsync(() => sink.Statuses.Any(status =>
            status.ReasonCode is BrowserConnectorReasonCodes.SessionExpired or
                BrowserConnectorReasonCodes.Disconnected));
        Assert.Throws<BrowserRelayProtocolException>(() =>
            client.OnObservation(new BrowserDomainObservation(
                "business_portal",
                null,
                BrowserFamily.Edge,
                2,
                DateTimeOffset.UtcNow)));

        stop.Cancel();
        await serverTask;
        await server.DisposeAsync();
    }

    [Fact]
    public void HandshakeMac_BindsBothFreshChallengesAndRejectsReplayMaterial()
    {
        var key = Enumerable.Repeat((byte)0x41, 32).ToArray();
        try
        {
            var original = BrowserRelayProtocol.ComputeHandshakeMacForTest(
                key,
                "client_proof",
                "aaaaaaaaaaaaaaaaaaaaaa",
                new string('a', 43),
                new string('b', 43),
                "cccccccccccccccc",
                1);
            var replayed = BrowserRelayProtocol.ComputeHandshakeMacForTest(
                key,
                "client_proof",
                "aaaaaaaaaaaaaaaaaaaaaa",
                new string('a', 43),
                new string('d', 43),
                "cccccccccccccccc",
                1);

            Assert.NotEqual(original, replayed);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    [Fact]
    public async Task OversizedRelayFrame_IsRejectedBeforeAllocationOfPayload()
    {
        var header = BitConverter.GetBytes(
            BrowserObservationRelayConstants.MaximumFrameBytes + 1);
        await using var stream = new MemoryStream(header);

        var error = await Assert.ThrowsAsync<BrowserRelayProtocolException>(() =>
            BrowserRelayFraming.ReadFrameAsync(
                stream,
                BrowserObservationRelayConstants.MaximumFrameBytes,
                CancellationToken.None));

        Assert.Equal(BrowserConnectorReasonCodes.FrameOversize, error.ReasonCode);
    }

    [Fact]
    public async Task NamedPipeTransports_ConsultBothInjectedIdentityChecksOnWindows()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var verifier = new RecordingIdentityVerifier();
        var pipeName = "SuavoAgent-BrowserRelay-Test-" + Guid.NewGuid().ToString("N");
        var serverTransport = new NamedPipeBrowserRelayServerTransport(pipeName, verifier);
        var clientTransport = new NamedPipeBrowserRelayClientTransport(pipeName, verifier);

        var accepting = serverTransport.AcceptAsync(CancellationToken.None);
        await using var client = await clientTransport.ConnectAsync(CancellationToken.None);
        await using var server = await accepting;

        Assert.Equal(1, verifier.ClientChecks);
        Assert.Equal(1, verifier.ServerChecks);
    }

    private static ObservationKeyLease Lease(long epoch) => new()
    {
        LeaseId = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        SessionBinding = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_'),
        Epoch = epoch,
        IssuedAtUtc = DateTimeOffset.UtcNow,
        ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(20),
        KeyMaterial = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    };

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        var cursor = new DirectoryInfo(AppContext.BaseDirectory);
        while (cursor is not null)
        {
            var candidate = Path.Combine(cursor.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
            cursor = cursor.Parent;
        }
        throw new FileNotFoundException(relativePath);
    }

    private sealed class CapturingSink : IBrowserConnectorSink
    {
        public ConcurrentQueue<BrowserConnectorStatus> Statuses { get; } = new();
        public ConcurrentQueue<BrowserDomainObservation> Observations { get; } = new();
        public void OnStatus(BrowserConnectorStatus status) => Statuses.Enqueue(status);
        public void OnObservation(BrowserDomainObservation observation) => Observations.Enqueue(observation);
    }

    private sealed class RecordingIdentityVerifier : IBrowserRelayPeerIdentityVerifier
    {
        public int ClientChecks;
        public int ServerChecks;

        public ValueTask<BrowserRelayPeerVerification> VerifyClientAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ClientChecks);
            return ValueTask.FromResult(BrowserRelayPeerVerification.Allow());
        }

        public ValueTask<BrowserRelayPeerVerification> VerifyServerAsync(
            NamedPipeClientStream pipe,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ServerChecks);
            return ValueTask.FromResult(BrowserRelayPeerVerification.Allow());
        }
    }

    private sealed class CountingRejectedTransport : IBrowserRelayClientTransport
    {
        public int ConnectCount;

        public Task<IBrowserRelayDuplex> ConnectAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ConnectCount);
            throw new InvalidOperationException("The relay must not be reached.");
        }
    }

    private sealed class StubParentVerifier(bool trusted) : IBrowserParentVerifier
    {
        public int CallCount;
        public string? LastAuthorizedPath;

        public ValueTask<BrowserParentVerification> VerifyAsync(
            BrowserConnectorAuthorityEntry authorization,
            nint parentWindowHandle,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            LastAuthorizedPath = authorization.BrowserExecutablePath;
            return ValueTask.FromResult(trusted
                ? BrowserParentVerification.Allow()
                : BrowserParentVerification.Deny(
                    BrowserConnectorReasonCodes.ParentBrowserUntrusted));
        }
    }

    private sealed class StubChannelVerifier(bool trusted) : IBrowserNativeChannelVerifier
    {
        public int CallCount;

        public ValueTask<BrowserNativeChannelVerification> VerifyAsync(
            BrowserConnectorAuthorityEntry authorization,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            return ValueTask.FromResult(trusted
                ? BrowserNativeChannelVerification.Allow()
                : BrowserNativeChannelVerification.Deny());
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class InMemoryRelayTransport :
        IBrowserRelayServerTransport,
        IBrowserRelayClientTransport
    {
        private readonly TaskCompletionSource<IBrowserRelayDuplex> _server =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly IBrowserRelayDuplex _serverEndpoint;
        private readonly IBrowserRelayDuplex _clientEndpoint;
        private int _connected;
        private int _accepted;

        public InMemoryRelayTransport()
        {
            (_serverEndpoint, _clientEndpoint) = ChannelDuplex.CreatePair();
        }

        public async Task<IBrowserRelayDuplex> AcceptAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _accepted, 1) == 0)
                return await _server.Task.WaitAsync(cancellationToken);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new OperationCanceledException(cancellationToken);
        }

        public Task<IBrowserRelayDuplex> ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Exchange(ref _connected, 1) != 0)
                throw new InvalidOperationException("Only one in-memory relay connection is supported.");
            _server.TrySetResult(_serverEndpoint);
            return Task.FromResult(_clientEndpoint);
        }
    }

    private sealed class ChannelDuplex : Stream, IBrowserRelayDuplex
    {
        private readonly ChannelReader<byte[]> _incoming;
        private readonly ChannelWriter<byte[]> _outgoing;
        private readonly CancellationTokenSource _closed;
        private byte[]? _current;
        private int _offset;
        private int _disposed;

        private ChannelDuplex(
            ChannelReader<byte[]> incoming,
            ChannelWriter<byte[]> outgoing,
            CancellationTokenSource closed)
        {
            _incoming = incoming;
            _outgoing = outgoing;
            _closed = closed;
        }

        public static (IBrowserRelayDuplex Server, IBrowserRelayDuplex Client) CreatePair()
        {
            var serverToClient = Channel.CreateUnbounded<byte[]>();
            var clientToServer = Channel.CreateUnbounded<byte[]>();
            var closed = new CancellationTokenSource();
            return (
                new ChannelDuplex(clientToServer.Reader, serverToClient.Writer, closed),
                new ChannelDuplex(serverToClient.Reader, clientToServer.Writer, closed));
        }

        public Stream Stream => this;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_current is null || _offset == _current.Length)
            {
                if (!await _incoming.WaitToReadAsync(cancellationToken))
                    return 0;
                if (!_incoming.TryRead(out _current))
                    return 0;
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }

        public override async ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (_closed.IsCancellationRequested)
                throw new IOException("The in-memory relay is closed.");
            await _outgoing.WriteAsync(buffer.ToArray(), cancellationToken);
        }

        public override async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            _closed.Cancel();
            _outgoing.TryComplete();
            await base.DisposeAsync();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
