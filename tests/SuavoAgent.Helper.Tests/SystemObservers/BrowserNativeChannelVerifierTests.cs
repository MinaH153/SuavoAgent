using Serilog;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserNativeChannelVerifierTests
{
    private static readonly DateTimeOffset Now =
        DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    [Fact]
    public async Task MatchingPipePeers_RequireTrustedBrowserAndSameUserSession()
    {
        var system = new FakeChannelSystem
        {
            InputPeer = 42,
            OutputPeer = 42,
        };
        var verifier = new WindowsBrowserNativeChannelVerifier(system);

        var result = await verifier.VerifyAsync(
            Authorization(BrowserFamily.Chrome),
            CancellationToken.None);

        Assert.True(result.Trusted);
        Assert.Equal(BrowserConnectorReasonCodes.Ready, result.ReasonCode);
        Assert.Equal(1, system.InputQueries);
        Assert.Equal(1, system.OutputQueries);
        Assert.Equal(1, system.BrowserTrustChecks);
        Assert.Equal(1, system.IdentityChecks);
        Assert.Equal((uint)42, system.LastVerifiedProcessId);
        Assert.Equal(BrowserFamily.Chrome, system.LastBrowser);
        Assert.Equal(BrowserConnectorAuthorityTests.ChromePath, system.LastAuthorizedPath);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task MissingStandardPipeEvidence_FailsClosedWithStableReason(
        bool inputAvailable,
        bool outputAvailable)
    {
        var system = new FakeChannelSystem
        {
            InputAvailable = inputAvailable,
            OutputAvailable = outputAvailable,
            InputPeer = 42,
            OutputPeer = 42,
        };
        var verifier = new WindowsBrowserNativeChannelVerifier(system);

        var result = await verifier.VerifyAsync(
            Authorization(BrowserFamily.Chrome),
            CancellationToken.None);

        Assert.False(result.Trusted);
        Assert.Equal(
            BrowserConnectorReasonCodes.NativeChannelUntrusted,
            result.ReasonCode);
        Assert.Equal(0, system.BrowserTrustChecks);
        Assert.Equal(0, system.IdentityChecks);
    }

    [Theory]
    [InlineData(42u, 43u)]
    [InlineData(0u, 0u)]
    public async Task DifferentOrZeroStdioServerPids_FailBeforeProcessTrust(
        uint inputPeer,
        uint outputPeer)
    {
        var system = new FakeChannelSystem
        {
            InputPeer = inputPeer,
            OutputPeer = outputPeer,
        };
        var verifier = new WindowsBrowserNativeChannelVerifier(system);

        var result = await verifier.VerifyAsync(
            Authorization(BrowserFamily.Edge),
            CancellationToken.None);

        Assert.False(result.Trusted);
        Assert.Equal(
            BrowserConnectorReasonCodes.NativeChannelUntrusted,
            result.ReasonCode);
        Assert.Equal(0, system.BrowserTrustChecks);
        Assert.Equal(0, system.IdentityChecks);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task BrowserTrustOrUserSessionFailure_FailsClosed(
        bool browserTrusted,
        bool identityMatches)
    {
        var system = new FakeChannelSystem
        {
            InputPeer = 42,
            OutputPeer = 42,
            BrowserTrusted = browserTrusted,
            IdentityMatches = identityMatches,
        };
        var verifier = new WindowsBrowserNativeChannelVerifier(system);

        var result = await verifier.VerifyAsync(
            Authorization(BrowserFamily.Chrome),
            CancellationToken.None);

        Assert.False(result.Trusted);
        Assert.Equal(
            BrowserConnectorReasonCodes.NativeChannelUntrusted,
            result.ReasonCode);
    }

    [Theory]
    [InlineData(false, 42u, 42u)]
    [InlineData(true, 42u, 43u)]
    public async Task SpoofedTrustedParentAndOrigin_CannotReachRelayBeforeChannelProof(
        bool inputAvailable,
        uint inputPeer,
        uint outputPeer)
    {
        using var logger = new LoggerConfiguration().CreateLogger();
        var system = new FakeChannelSystem
        {
            InputAvailable = inputAvailable,
            InputPeer = inputPeer,
            OutputPeer = outputPeer,
        };
        var channel = new WindowsBrowserNativeChannelVerifier(system);
        var parent = new AlwaysTrustedParentVerifier();
        var relay = new CountingRelayTransport();

        var result = await BrowserNativeMessagingEntryPoint.RunVerifiedAsync(
            new BrowserHostLaunchContext(
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                0),
            BrowserConnectorAuthorityTests.VerifiedAuthority(),
            channel,
            parent,
            relay,
            _ => null,
            logger,
            new MemoryStream(),
            new MemoryStream(),
            CancellationToken.None,
            new FixedTimeProvider(Now));

        Assert.Equal(4, result);
        Assert.Equal(0, parent.CallCount);
        Assert.Equal(0, relay.ConnectCount);
    }

    [Fact]
    public async Task WrongSignedBrowserPath_IsRejectedBeforeParentOrRelay()
    {
        const string wrongPath =
            @"C:\Program Files\Google\Chrome Beta\Application\chrome.exe";
        using var logger = new LoggerConfiguration().CreateLogger();
        var system = new FakeChannelSystem
        {
            InputPeer = 42,
            OutputPeer = 42,
            RequiredAuthorizedPath = BrowserConnectorAuthorityTests.ChromePath,
        };
        var parent = new AlwaysTrustedParentVerifier();
        var relay = new CountingRelayTransport();

        var result = await BrowserNativeMessagingEntryPoint.RunVerifiedAsync(
            new BrowserHostLaunchContext(
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                0),
            BrowserConnectorAuthorityTests.VerifiedAuthority(wrongPath),
            new WindowsBrowserNativeChannelVerifier(system),
            parent,
            relay,
            _ => null,
            logger,
            new MemoryStream(),
            new MemoryStream(),
            CancellationToken.None,
            new FixedTimeProvider(Now));

        Assert.Equal(4, result);
        Assert.Equal(wrongPath, system.LastAuthorizedPath);
        Assert.Equal(0, parent.CallCount);
        Assert.Equal(0, relay.ConnectCount);
    }

    [Fact]
    public void ProductionSurface_AcceptsNoCallerSuppliedHandleOrProcessId()
    {
        var constructor = Assert.Single(
            typeof(WindowsBrowserNativeChannelVerifier).GetConstructors());
        Assert.Empty(constructor.GetParameters());

        var source = File.ReadAllText(FindRepositoryFile(
            "src/SuavoAgent.Helper/SystemObservers/BrowserConnector/" +
            "WindowsBrowserNativeChannelVerifier.cs"));
        Assert.Contains("GetStdHandle", source, StringComparison.Ordinal);
        Assert.Contains("GetFileType", source, StringComparison.Ordinal);
        Assert.Contains("GetNamedPipeServerProcessId", source, StringComparison.Ordinal);
        Assert.DoesNotContain("parentWindowHandle", source, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(string relativePath)
    {
        for (var cursor = new DirectoryInfo(AppContext.BaseDirectory);
             cursor is not null;
             cursor = cursor.Parent)
        {
            var candidate = Path.Combine(cursor.FullName, relativePath);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException(relativePath);
    }

    private static BrowserConnectorAuthorityEntry Authorization(BrowserFamily browser) =>
        browser == BrowserFamily.Chrome
            ? new(
                browser,
                "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/",
                BrowserConnectorAuthorityTests.ChromePath)
            : new(
                browser,
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                "chrome-extension://bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/",
                BrowserConnectorAuthorityTests.EdgePath);

    private sealed class FakeChannelSystem : IWindowsBrowserNativeChannelSystem
    {
        public bool IsSupportedPlatform { get; init; } = true;
        public bool InputAvailable { get; init; } = true;
        public bool OutputAvailable { get; init; } = true;
        public uint InputPeer { get; init; }
        public uint OutputPeer { get; init; }
        public bool BrowserTrusted { get; init; } = true;
        public string? RequiredAuthorizedPath { get; init; }
        public bool IdentityMatches { get; init; } = true;
        public int InputQueries { get; private set; }
        public int OutputQueries { get; private set; }
        public int BrowserTrustChecks { get; private set; }
        public int IdentityChecks { get; private set; }
        public uint LastVerifiedProcessId { get; private set; }
        public BrowserFamily LastBrowser { get; private set; }
        public string? LastAuthorizedPath { get; private set; }

        public bool TryGetPipeServerProcessId(
            BrowserNativeStandardChannel channel,
            out uint processId)
        {
            if (channel == BrowserNativeStandardChannel.Input)
            {
                InputQueries++;
                processId = InputPeer;
                return InputAvailable;
            }

            OutputQueries++;
            processId = OutputPeer;
            return OutputAvailable;
        }

        public bool IsExpectedBrowserProcess(
            uint processId,
            BrowserConnectorAuthorityEntry authorization)
        {
            BrowserTrustChecks++;
            LastVerifiedProcessId = processId;
            LastBrowser = authorization.Browser;
            LastAuthorizedPath = authorization.BrowserExecutablePath;
            return BrowserTrusted &&
                   (RequiredAuthorizedPath is null ||
                    string.Equals(
                        RequiredAuthorizedPath,
                        authorization.BrowserExecutablePath,
                        StringComparison.OrdinalIgnoreCase));
        }

        public bool IsSameUserAndSession(uint processId)
        {
            IdentityChecks++;
            LastVerifiedProcessId = processId;
            return IdentityMatches;
        }
    }

    private sealed class AlwaysTrustedParentVerifier : IBrowserParentVerifier
    {
        public int CallCount { get; private set; }

        public ValueTask<BrowserParentVerification> VerifyAsync(
            BrowserConnectorAuthorityEntry authorization,
            nint parentWindowHandle,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(BrowserParentVerification.Allow());
        }
    }

    private sealed class CountingRelayTransport : IBrowserRelayClientTransport
    {
        public int ConnectCount { get; private set; }

        public Task<IBrowserRelayDuplex> ConnectAsync(
            CancellationToken cancellationToken)
        {
            ConnectCount++;
            throw new InvalidOperationException("Relay must not be reached.");
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
