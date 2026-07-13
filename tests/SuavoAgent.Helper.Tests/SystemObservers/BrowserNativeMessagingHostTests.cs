using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SuavoAgent.Helper.SystemObservers.BrowserConnector;
using Xunit;

namespace SuavoAgent.Helper.Tests.SystemObservers;

public sealed class BrowserNativeMessagingHostTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    private const string ChromeOrigin = "chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/";

    [Fact]
    public async Task ValidActiveHostname_IsClassifiedLocallyAndNeverLeavesSinkRaw()
    {
        using var fixture = new HostFixture(hostname => hostname == "example.com" ? "business_portal" : null);
        await using var input = await fixture.InputWithMessagesAsync("example.com");
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(1, result.AcceptedMessages);
        var observation = Assert.Single(fixture.Sink.Observations);
        Assert.Equal("business_portal", observation.Category);
        Assert.Null(observation.HostnameHash);
        Assert.Equal(BrowserFamily.Chrome, observation.Browser);
        Assert.Contains(fixture.Sink.Statuses, status =>
            status.State == BrowserConnectorState.Ready &&
            status.ReasonCode == BrowserConnectorReasonCodes.Ready);

        var frames = await ReadOutputFramesAsync(output);
        Assert.Equal(2, frames.Count);
        Assert.Equal("hello", frames[0].RootElement.GetProperty("type").GetString());
        Assert.Equal("accepted", frames[1].RootElement.GetProperty("type").GetString());
        Assert.DoesNotContain(
            "example.com",
            System.Text.Encoding.UTF8.GetString(output.ToArray()),
            StringComparison.Ordinal);
        foreach (var frame in frames) frame.Dispose();
    }

    [Fact]
    public async Task UnknownHostname_LeavesOnlyKeyedHash()
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = await fixture.InputWithMessagesAsync("unknown.example");
        await using var output = new MemoryStream();

        await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        var observation = Assert.Single(fixture.Sink.Observations);
        Assert.Equal("unknown", observation.Category);
        Assert.Matches("^[0-9a-f]{64}$", observation.HostnameHash);
        Assert.DoesNotContain("unknown.example", observation.ToString(), StringComparison.Ordinal);
        Assert.All(fixture.Sink.Statuses, status =>
            Assert.DoesNotContain("unknown.example", status.ToString(), StringComparison.Ordinal));
        Assert.All(fixture.LogSink.Messages, message =>
            Assert.DoesNotContain("unknown.example", message, StringComparison.Ordinal));
        Assert.DoesNotContain(
            "unknown.example",
            Encoding.UTF8.GetString(output.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Replay_IsRejectedAfterOneAcceptedMessage()
    {
        using var fixture = new HostFixture(_ => null);
        var message = fixture.BuildMessage("example.com", 1, fixture.InitialChallenge);
        await using var input = await FramedInputAsync(message, message);
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Equal(1, result.AcceptedMessages);
        Assert.Equal(BrowserConnectorReasonCodes.ReplayRejected, result.ReasonCode);
        Assert.Single(fixture.Sink.Observations);
        Assert.Contains(fixture.Sink.Statuses, status =>
            status.ReasonCode == BrowserConnectorReasonCodes.ReplayRejected);
    }

    [Theory]
    [InlineData("mac", BrowserConnectorReasonCodes.AuthenticationRejected)]
    [InlineData("challenge", BrowserConnectorReasonCodes.ChallengeRejected)]
    [InlineData("counter", BrowserConnectorReasonCodes.ReplayRejected)]
    public async Task TamperedAuthenticationField_TerminatesConnection(
        string field,
        string expectedReason)
    {
        using var fixture = new HostFixture(_ => null);
        var message = field switch
        {
            "challenge" => fixture.BuildMessage(
                "example.com",
                1,
                BrowserConnectorAuthorityVerifier.Base64UrlEncode(
                    Enumerable.Repeat((byte)8, 32).ToArray())),
            "counter" => fixture.BuildMessage("example.com", 2, fixture.InitialChallenge),
            _ => fixture.BuildMessage("example.com", 1, fixture.InitialChallenge),
        };
        if (field == "mac")
            message["mac"] = new string('A', 43);
        await using var input = await FramedInputAsync(message);
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.False(result.Connected);
        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Equal(0, result.AcceptedMessages);
        Assert.Empty(fixture.Sink.Observations);
    }

    [Fact]
    public async Task AcceptedOutput_IsAuthenticatedWithRotatedChallenge()
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = await fixture.InputWithMessagesAsync("example.com");
        await using var output = new MemoryStream();

        await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        var frames = await ReadOutputFramesAsync(output);
        try
        {
            var hello = frames[0].RootElement;
            var acknowledgement = frames[1].RootElement;
            Assert.True(BrowserConnectorAuthorityVerifier.TryDecodeBase64Url(
                hello.GetProperty("sessionKey").GetString(),
                32,
                out var sessionKey));
            try
            {
                var expectedMac = BrowserNativeMessagingHost.ComputeHostMac(
                    sessionKey,
                    hello.GetProperty("sessionId").GetString()!,
                    "accepted",
                    acknowledgement.GetProperty("counter").GetInt64(),
                    acknowledgement.GetProperty("nextChallenge").GetString()!,
                    "ready");
                Assert.Equal(expectedMac, acknowledgement.GetProperty("mac").GetString());
                Assert.NotEqual(
                    hello.GetProperty("challenge").GetString(),
                    acknowledgement.GetProperty("nextChallenge").GetString());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    [Fact]
    public async Task FatalOutput_IsAuthenticatedWithoutEchoingRejectedValue()
    {
        const string sensitive = "patient-123.example";
        using var fixture = new HostFixture(_ => null);
        var message = fixture.BuildMessage(sensitive, 1, fixture.InitialChallenge);
        message["mac"] = new string('A', 43);
        await using var input = await FramedInputAsync(message);
        await using var output = new MemoryStream();

        await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        var frames = await ReadOutputFramesAsync(output);
        try
        {
            var hello = frames[0].RootElement;
            var fatal = frames[1].RootElement;
            Assert.Equal("fatal", fatal.GetProperty("type").GetString());
            Assert.True(BrowserConnectorAuthorityVerifier.TryDecodeBase64Url(
                hello.GetProperty("sessionKey").GetString(),
                32,
                out var sessionKey));
            try
            {
                var reason = fatal.GetProperty("reason").GetString()!;
                var expectedMac = BrowserNativeMessagingHost.ComputeHostMac(
                    sessionKey,
                    hello.GetProperty("sessionId").GetString()!,
                    "fatal",
                    fatal.GetProperty("counter").GetInt64(),
                    reason,
                    "degraded");
                Assert.Equal(expectedMac, fatal.GetProperty("mac").GetString());
            }
            finally
            {
                CryptographicOperations.ZeroMemory(sessionKey);
            }
            Assert.DoesNotContain(
                sensitive,
                Encoding.UTF8.GetString(output.ToArray()),
                StringComparison.Ordinal);
        }
        finally
        {
            foreach (var frame in frames) frame.Dispose();
        }
    }

    [Theory]
    [InlineData("https://example.com/rx?patient=1", BrowserConnectorReasonCodes.HostnameRejected)]
    [InlineData("Prescription Queue - example.com", BrowserConnectorReasonCodes.HostnameRejected)]
    public async Task FullUrlOrTitle_IsRejected(string value, string expectedReason)
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = await fixture.InputWithMessagesAsync(value);
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(expectedReason, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
    }

    [Fact]
    public async Task UnknownJsonProperty_IsRejected()
    {
        using var fixture = new HostFixture(_ => null);
        var valid = fixture.BuildMessage("example.com", 1, fixture.InitialChallenge);
        var expanded = new Dictionary<string, object?>(valid, StringComparer.Ordinal)
        {
            ["title"] = "sensitive",
        };
        await using var input = await FramedInputAsync(expanded);
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.MessageInvalid, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
    }

    [Fact]
    public async Task DuplicateJsonProperty_IsRejectedBeforeAuthentication()
    {
        using var fixture = new HostFixture(_ => null);
        var mac = fixture.ComputeMac("example.com", 1, fixture.InitialChallenge);
        var rawJson = $$"""
            {
              "version": 1,
              "type": "active_tab_hostname",
              "protocol": "{{BrowserNativeHostOptions.Protocol}}",
              "sessionId": "{{fixture.SessionId}}",
              "counter": 1,
              "challenge": "{{fixture.InitialChallenge}}",
              "hostname": "example.com",
              "hostname": "patient-duplicate.example",
              "mac": "{{mac}}"
            }
            """;
        await using var input = RawFramedInput(Encoding.UTF8.GetBytes(rawJson));
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.MessageInvalid, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
        Assert.DoesNotContain(
            "patient-duplicate.example",
            Encoding.UTF8.GetString(output.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizeFrame_IsRejectedBeforePayloadAllocation()
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, 4097);
        await input.WriteAsync(header);
        input.Position = 0;
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.FrameOversize, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TruncatedHeaderOrPayload_IsRejected(bool truncateHeader)
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = new MemoryStream();
        if (truncateHeader)
        {
            await input.WriteAsync(new byte[] { 20, 0 });
        }
        else
        {
            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, 20);
            await input.WriteAsync(header);
            await input.WriteAsync(Encoding.UTF8.GetBytes("{\"short\":"));
        }
        input.Position = 0;
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.FrameTruncated, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
    }

    [Fact]
    public async Task SessionExpiry_RejectsAuthenticatedMessageBeforeObservation()
    {
        var time = new SequenceTimeProvider(
            Now,
            Now,
            Now.AddMinutes(31),
            Now.AddMinutes(31));
        using var fixture = new HostFixture(_ => null, timeProvider: time);
        await using var input = await fixture.InputWithMessagesAsync("example.com");
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.SessionExpired, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
        Assert.Contains(fixture.Sink.Statuses, status =>
            status.ReasonCode == BrowserConnectorReasonCodes.SessionExpired);
    }

    [Fact]
    public async Task RejectedRawUrl_NeverAppearsInStatusLogOutputOrObservation()
    {
        const string sensitive = "https://patient-987.example/rx/123?token=secret";
        using var fixture = new HostFixture(_ => null);
        await using var input = await fixture.InputWithMessagesAsync(sensitive);
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.HostnameRejected, result.ReasonCode);
        Assert.Empty(fixture.Sink.Observations);
        Assert.All(fixture.Sink.Statuses, status =>
            Assert.DoesNotContain(sensitive, status.ToString(), StringComparison.Ordinal));
        Assert.All(fixture.LogSink.Messages, message =>
            Assert.DoesNotContain(sensitive, message, StringComparison.Ordinal));
        Assert.DoesNotContain(
            sensitive,
            Encoding.UTF8.GetString(output.ToArray()),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownOrigin_ProducesNoHandshake()
    {
        using var fixture = new HostFixture(_ => null);
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(
                "chrome-extension://cccccccccccccccccccccccccccccccc/",
                0),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.OriginRejected, result.ReasonCode);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public async Task ParentBrowserMismatch_ProducesNoHandshake()
    {
        using var fixture = new HostFixture(
            _ => null,
            new FakeParentVerifier(BrowserParentVerification.Deny(
                BrowserConnectorReasonCodes.ParentBrowserMismatch)));
        await using var input = new MemoryStream();
        await using var output = new MemoryStream();

        var result = await fixture.Host.RunAsync(
            input,
            output,
            new BrowserHostLaunchContext(ChromeOrigin, 99),
            CancellationToken.None);

        Assert.Equal(BrowserConnectorReasonCodes.ParentBrowserMismatch, result.ReasonCode);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=0", true)]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=42", true)]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "--parent-window=-1", false)]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/", "unexpected", false)]
    public void LaunchContext_ParsesOnlyChromiumArguments(string origin, string second, bool expected)
    {
        Assert.Equal(expected, BrowserHostLaunchContext.TryParse([origin, second], out _));
    }

    [Fact]
    public void LaunchContext_RejectsMissingExtraOrNonAsciiArguments()
    {
        Assert.False(BrowserHostLaunchContext.TryParse([], out _));
        Assert.True(BrowserHostLaunchContext.TryParse([ChromeOrigin], out var serviceWorker));
        Assert.Equal(0, serviceWorker.ParentWindowHandle);
        Assert.False(BrowserHostLaunchContext.TryParse(
            [ChromeOrigin, "--parent-window=1", "extra"],
            out _));
        Assert.False(BrowserHostLaunchContext.TryParse(
            ["chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaé/"],
            out _));
    }

    [Theory]
    [InlineData(BrowserFamily.Chrome, "CN=Google LLC, O=Google LLC, C=US", true)]
    [InlineData(BrowserFamily.Chrome, "CN=Google Tools, O=Google Tools LLC, C=US", false)]
    [InlineData(BrowserFamily.Edge, "CN=Microsoft Windows, O=Microsoft Corporation, C=US", true)]
    [InlineData(BrowserFamily.Edge, "CN=Microsoft Corporation Tools, O=Other, C=US", false)]
    public void ParentPublisher_RequiresExactOrganization(
        BrowserFamily browser,
        string subject,
        bool expected)
    {
        Assert.Equal(expected, WindowsBrowserParentVerifier.IsExpectedPublisherSubject(browser, subject));
    }

    [Theory]
    [InlineData("example.com", "example.com")]
    [InlineData("EXAMPLE.com", "example.com")]
    [InlineData("example.com.", "example.com")]
    [InlineData("https://example.com/path", null)]
    [InlineData("example.com:443", null)]
    [InlineData("example.com?rx=1", null)]
    public void HostnameNormalization_NeverAcceptsUrlComponents(string input, string? expected)
    {
        Assert.Equal(expected, BrowserNativeMessagingHost.NormalizeHostname(input));
    }

    private static async Task<MemoryStream> FramedInputAsync(params object[] messages)
    {
        var stream = new MemoryStream();
        foreach (var message in messages)
        {
            await NativeMessagingFraming.WriteJsonAsync(
                stream,
                message,
                BrowserNativeHostOptions.DefaultMaximumFrameBytes,
                CancellationToken.None);
        }
        stream.Position = 0;
        return stream;
    }

    private static MemoryStream RawFramedInput(byte[] payload, uint? declaredLength = null)
    {
        var stream = new MemoryStream();
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(
            header,
            declaredLength ?? checked((uint)payload.Length));
        stream.Write(header);
        stream.Write(payload);
        stream.Position = 0;
        return stream;
    }

    private static async Task<List<JsonDocument>> ReadOutputFramesAsync(MemoryStream output)
    {
        output.Position = 0;
        var frames = new List<JsonDocument>();
        while (await NativeMessagingFraming.ReadFrameAsync(
                   output,
                   BrowserNativeHostOptions.DefaultMaximumFrameBytes,
                   CancellationToken.None) is { } payload)
        {
            try
            {
                frames.Add(JsonDocument.Parse(Encoding.UTF8.GetString(payload)));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        return frames;
    }

    private sealed class HostFixture : IDisposable
    {
        private readonly byte[] _sessionKey = Enumerable.Repeat((byte)2, 32).ToArray();
        private readonly string _sessionId = BrowserConnectorAuthorityVerifier.Base64UrlEncode(
            Enumerable.Repeat((byte)1, 16).ToArray());
        private readonly byte[] _observationKey = Enumerable.Repeat((byte)9, 32).ToArray();
        private readonly ILogger _logger;

        public HostFixture(
            Func<string, string?> classifier,
            IBrowserParentVerifier? parentVerifier = null,
            TimeProvider? timeProvider = null)
        {
            Sink = new RecordingSink();
            LogSink = new RecordingLogSink();
            _logger = new LoggerConfiguration().WriteTo.Sink(LogSink).CreateLogger();
            Host = new BrowserNativeMessagingHost(
                BrowserConnectorAuthorityTests.VerifiedAuthority(),
                parentVerifier ?? new FakeParentVerifier(BrowserParentVerification.Allow()),
                Sink,
                classifier,
                _observationKey,
                _logger,
                new BrowserNativeHostOptions(),
                timeProvider ?? new FixedTimeProvider(Now),
                new SequentialEntropy());
        }

        public RecordingSink Sink { get; }
        public RecordingLogSink LogSink { get; }
        public BrowserNativeMessagingHost Host { get; }
        public string SessionId => _sessionId;
        public string InitialChallenge => BrowserConnectorAuthorityVerifier.Base64UrlEncode(
            Enumerable.Repeat((byte)3, 32).ToArray());

        public async Task<MemoryStream> InputWithMessagesAsync(string hostname) =>
            await FramedInputAsync(BuildMessage(hostname, 1, InitialChallenge));

        public Dictionary<string, object?> BuildMessage(string hostname, long counter, string challenge)
        {
            var mac = ComputeMac(hostname, counter, challenge);
            return new(StringComparer.Ordinal)
            {
                ["version"] = BrowserNativeHostOptions.ProtocolVersion,
                ["type"] = "active_tab_hostname",
                ["protocol"] = BrowserNativeHostOptions.Protocol,
                ["sessionId"] = _sessionId,
                ["counter"] = counter,
                ["challenge"] = challenge,
                ["hostname"] = hostname,
                ["mac"] = mac,
            };
        }

        public string ComputeMac(string hostname, long counter, string challenge) =>
            BrowserNativeMessagingHost.ComputeClientMac(
                _sessionKey,
                _sessionId,
                counter,
                challenge,
                hostname);

        public void Dispose()
        {
            Host.Dispose();
            (_logger as IDisposable)?.Dispose();
            CryptographicOperations.ZeroMemory(_sessionKey);
            CryptographicOperations.ZeroMemory(_observationKey);
        }
    }

    private sealed class SequentialEntropy : IBrowserSessionEntropy
    {
        private byte _next = 1;

        public void Fill(Span<byte> destination)
        {
            destination.Fill(_next);
            _next++;
        }
    }

    private sealed class FakeParentVerifier(BrowserParentVerification result) : IBrowserParentVerifier
    {
        public ValueTask<BrowserParentVerification> VerifyAsync(
            BrowserConnectorAuthorityEntry authorization,
            nint parentWindowHandle,
            CancellationToken cancellationToken) => ValueTask.FromResult(result);
    }

    private sealed class RecordingSink : IBrowserConnectorSink
    {
        public List<BrowserConnectorStatus> Statuses { get; } = [];
        public List<BrowserDomainObservation> Observations { get; } = [];

        public void OnStatus(BrowserConnectorStatus status) => Statuses.Add(status);
        public void OnObservation(BrowserDomainObservation observation) => Observations.Add(observation);
    }

    private sealed class RecordingLogSink : ILogEventSink
    {
        public List<string> Messages { get; } = [];

        public void Emit(LogEvent logEvent) =>
            Messages.Add(logEvent.RenderMessage(CultureInfo.InvariantCulture));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] values) : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            var index = Math.Min(Interlocked.Increment(ref _index) - 1, values.Length - 1);
            return values[index];
        }
    }
}
