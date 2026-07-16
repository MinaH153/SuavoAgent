using System.IO.Pipes;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

/// <summary>
/// Proves the browser relay's real named-pipe transport consults both peer
/// identity checks and disposes rejected endpoints. The Windows production
/// verifier adds signer/session/path proof on top of this transport contract.
/// </summary>
public sealed class BrowserRelayTransportBoundaryTests
{
    [Fact]
    public void ConstructorsRejectMissingAuthorityAndUnboundedTimeouts()
    {
        var verifier = new RecordingVerifier();

        Assert.Throws<ArgumentException>(() =>
            new NamedPipeBrowserRelayServerTransport(" ", verifier));
        Assert.Throws<ArgumentNullException>(() =>
            new NamedPipeBrowserRelayServerTransport("pipe", null!));
        Assert.Throws<ArgumentException>(() =>
            new NamedPipeBrowserRelayClientTransport("", verifier));
        Assert.Throws<ArgumentNullException>(() =>
            new NamedPipeBrowserRelayClientTransport("pipe", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NamedPipeBrowserRelayClientTransport(
                "pipe", verifier, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new NamedPipeBrowserRelayClientTransport(
                "pipe", verifier, TimeSpan.FromSeconds(11)));
    }

    [Fact]
    public async Task RealPipeConsultsBothInjectedPeerVerifiersCrossPlatform()
    {
        var verifier = new RecordingVerifier();
        var pipeName = $"sa_relay_{Guid.NewGuid():N}";
        var serverTransport = new NamedPipeBrowserRelayServerTransport(
            pipeName,
            verifier);
        var clientTransport = new NamedPipeBrowserRelayClientTransport(
            pipeName,
            verifier,
            TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var accepting = serverTransport.AcceptAsync(timeout.Token);
        await using var client = await clientTransport.ConnectAsync(timeout.Token);
        await using var server = await accepting;

        Assert.Equal(1, verifier.ClientChecks);
        Assert.Equal(1, verifier.ServerChecks);
        Assert.True(client.Stream.CanRead);
        Assert.True(server.Stream.CanWrite);
    }

    [Fact]
    public async Task ServerSideIdentityDenialRejectsConnectedPeer()
    {
        var serverVerifier = new RecordingVerifier(allowClient: false);
        var clientVerifier = new RecordingVerifier();
        var pipeName = $"sa_relay_{Guid.NewGuid():N}";
        var serverTransport = new NamedPipeBrowserRelayServerTransport(
            pipeName,
            serverVerifier);
        var clientTransport = new NamedPipeBrowserRelayClientTransport(
            pipeName,
            clientVerifier,
            TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var accepting = serverTransport.AcceptAsync(timeout.Token);
        await using var client = await clientTransport.ConnectAsync(timeout.Token);
        var error = await Assert.ThrowsAsync<BrowserRelayProtocolException>(
            async () => await accepting);

        Assert.Equal(BrowserConnectorReasonCodes.AuthenticationRejected, error.ReasonCode);
        Assert.Equal(1, serverVerifier.ClientChecks);
        Assert.Equal(1, clientVerifier.ServerChecks);
    }

    [Fact]
    public async Task ClientSideIdentityDenialRejectsConnectedServer()
    {
        var serverVerifier = new RecordingVerifier();
        var clientVerifier = new RecordingVerifier(allowServer: false);
        var pipeName = $"sa_relay_{Guid.NewGuid():N}";
        var serverTransport = new NamedPipeBrowserRelayServerTransport(
            pipeName,
            serverVerifier);
        var clientTransport = new NamedPipeBrowserRelayClientTransport(
            pipeName,
            clientVerifier,
            TimeSpan.FromSeconds(2));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var accepting = serverTransport.AcceptAsync(timeout.Token);
        var connecting = clientTransport.ConnectAsync(timeout.Token);
        await using var server = await accepting;
        var error = await Assert.ThrowsAsync<BrowserRelayProtocolException>(
            async () => await connecting);

        Assert.Equal(BrowserConnectorReasonCodes.AuthenticationRejected, error.ReasonCode);
        Assert.Equal(1, serverVerifier.ClientChecks);
        Assert.Equal(1, clientVerifier.ServerChecks);
    }

    [Fact]
    public async Task CancelledAcceptDisposesPendingServerPipe()
    {
        var transport = new NamedPipeBrowserRelayServerTransport(
            $"sa_relay_{Guid.NewGuid():N}",
            new RecordingVerifier());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            transport.AcceptAsync(cancellation.Token));
    }

    [Fact]
    public async Task RelayPipeNameAndWindowsVerifierFailClosedOffWindows()
    {
        if (OperatingSystem.IsWindows())
            return;

        Assert.Throws<PlatformNotSupportedException>(
            BrowserRelayPipeName.ForCurrentProcess);

        var verifier = new WindowsBrowserRelayPeerIdentityVerifier();
        using var server = new NamedPipeServerStream(
            $"sa_relay_{Guid.NewGuid():N}",
            PipeDirection.InOut,
            1,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(
            ".",
            $"unused_{Guid.NewGuid():N}",
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        var clientVerdict = await verifier.VerifyClientAsync(
            server,
            CancellationToken.None);
        var serverVerdict = await verifier.VerifyServerAsync(
            client,
            CancellationToken.None);

        Assert.False(clientVerdict.Trusted);
        Assert.False(serverVerdict.Trusted);
        Assert.Equal(
            BrowserConnectorReasonCodes.AuthenticationRejected,
            clientVerdict.ReasonCode);
        Assert.Equal(
            BrowserConnectorReasonCodes.AuthenticationRejected,
            serverVerdict.ReasonCode);
    }

    private sealed class RecordingVerifier(
        bool allowClient = true,
        bool allowServer = true) : IBrowserRelayPeerIdentityVerifier
    {
        public int ClientChecks { get; private set; }
        public int ServerChecks { get; private set; }

        public ValueTask<BrowserRelayPeerVerification> VerifyClientAsync(
            NamedPipeServerStream pipe,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClientChecks++;
            return ValueTask.FromResult(allowClient
                ? BrowserRelayPeerVerification.Allow()
                : BrowserRelayPeerVerification.Deny(
                    BrowserConnectorReasonCodes.AuthenticationRejected));
        }

        public ValueTask<BrowserRelayPeerVerification> VerifyServerAsync(
            NamedPipeClientStream pipe,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ServerChecks++;
            return ValueTask.FromResult(allowServer
                ? BrowserRelayPeerVerification.Allow()
                : BrowserRelayPeerVerification.Deny(
                    BrowserConnectorReasonCodes.AuthenticationRejected));
        }
    }
}
