using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Serilog;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

public sealed record BrowserHostLaunchContext(string Origin, nint ParentWindowHandle)
{
    public static bool TryParse(IReadOnlyList<string> arguments, out BrowserHostLaunchContext context)
    {
        context = default!;
        if (arguments is null || arguments.Count is < 1 or > 2)
            return false;

        var origin = arguments[0];
        if (string.IsNullOrWhiteSpace(origin) || origin.Length > 96 || !origin.All(char.IsAscii))
            return false;

        nint parentWindow = 0;
        if (arguments.Count == 2)
        {
            const string prefix = "--parent-window=";
            if (!arguments[1].StartsWith(prefix, StringComparison.Ordinal) ||
                !ulong.TryParse(
                    arguments[1].AsSpan(prefix.Length),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var parsed) ||
                parsed > long.MaxValue)
            {
                return false;
            }

            parentWindow = checked((nint)(long)parsed);
        }

        context = new BrowserHostLaunchContext(origin, parentWindow);
        return true;
    }
}

public sealed record BrowserNativeHostOptions
{
    public const string Protocol = "suavo-native-messaging-v1";
    public const int ProtocolVersion = 1;
    public const int DefaultMaximumFrameBytes = 4096;
    public static readonly TimeSpan DefaultSessionLifetime = TimeSpan.FromMinutes(30);

    public int MaximumFrameBytes { get; init; } = DefaultMaximumFrameBytes;

    public TimeSpan SessionLifetime { get; init; } = DefaultSessionLifetime;

    internal bool IsValid =>
        MaximumFrameBytes is >= 512 and <= 16_384 &&
        SessionLifetime >= TimeSpan.FromMinutes(1) &&
        SessionLifetime <= TimeSpan.FromHours(1);
}

public readonly record struct BrowserNativeHostRunResult(bool Connected, string ReasonCode, int AcceptedMessages);

internal interface IBrowserSessionEntropy
{
    void Fill(Span<byte> destination);
}

internal sealed class CryptographicBrowserSessionEntropy : IBrowserSessionEntropy
{
    public void Fill(Span<byte> destination) => RandomNumberGenerator.Fill(destination);
}

/// <summary>
/// Chrome/Edge native-messaging host protocol. The entry point establishes the
/// caller from signed origin authority and kernel-backed stdio pipe peers; this
/// host re-verifies the signed parent only as corroboration, then creates a fresh
/// in-memory HMAC key and one-time challenge; every active-tab hostname must
/// carry the exact next counter, current challenge, and HMAC. Challenges rotate
/// after every accepted message. Any ambiguity terminates the connection.
/// </summary>
public sealed class BrowserNativeMessagingHost : IDisposable
{
    private static readonly Regex SafeCategory = new(
        "^[a-z][a-z0-9_:-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly VerifiedBrowserConnectorAuthority _authority;
    private readonly IBrowserParentVerifier _parentVerifier;
    private readonly IBrowserConnectorSink _sink;
    private readonly Func<string, string?> _domainClassifier;
    private readonly byte[] _observationHmacKey;
    private readonly BrowserNativeHostOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IBrowserSessionEntropy _entropy;
    private readonly ILogger _logger;
    private int _disposed;

    public BrowserNativeMessagingHost(
        VerifiedBrowserConnectorAuthority authority,
        IBrowserParentVerifier parentVerifier,
        IBrowserConnectorSink sink,
        Func<string, string?> domainClassifier,
        ReadOnlySpan<byte> observationHmacKey,
        ILogger logger,
        BrowserNativeHostOptions? options = null,
        TimeProvider? timeProvider = null)
        : this(
            authority,
            parentVerifier,
            sink,
            domainClassifier,
            observationHmacKey,
            logger,
            options,
            timeProvider,
            new CryptographicBrowserSessionEntropy())
    {
    }

    internal BrowserNativeMessagingHost(
        VerifiedBrowserConnectorAuthority authority,
        IBrowserParentVerifier parentVerifier,
        IBrowserConnectorSink sink,
        Func<string, string?> domainClassifier,
        ReadOnlySpan<byte> observationHmacKey,
        ILogger logger,
        BrowserNativeHostOptions? options,
        TimeProvider? timeProvider,
        IBrowserSessionEntropy entropy)
    {
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _parentVerifier = parentVerifier ?? throw new ArgumentNullException(nameof(parentVerifier));
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _domainClassifier = domainClassifier ?? throw new ArgumentNullException(nameof(domainClassifier));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = options ?? new BrowserNativeHostOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _entropy = entropy ?? throw new ArgumentNullException(nameof(entropy));
        if (observationHmacKey.Length < 16)
            throw new ArgumentException("Observation HMAC key must contain at least 128 bits.", nameof(observationHmacKey));
        if (!_options.IsValid)
            throw new ArgumentException("Browser native-host options exceed the fail-closed protocol bounds.", nameof(options));

        _observationHmacKey = observationHmacKey.ToArray();
    }

    public async Task<BrowserNativeHostRunResult> RunAsync(
        Stream nativeInput,
        Stream nativeOutput,
        BrowserHostLaunchContext launchContext,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(nativeInput);
        ArgumentNullException.ThrowIfNull(nativeOutput);
        ArgumentNullException.ThrowIfNull(launchContext);

        var now = _timeProvider.GetUtcNow();
        if (_authority.ExpiresAt <= now)
            return Reject(BrowserConnectorReasonCodes.AuthorityInvalid, 0);
        if (!_authority.TryAuthorize(launchContext.Origin, out var authorization))
            return Reject(BrowserConnectorReasonCodes.OriginRejected, 0);

        BrowserParentVerification parent;
        try
        {
            parent = await _parentVerifier.VerifyAsync(
                authorization,
                launchContext.ParentWindowHandle,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Reject(BrowserConnectorReasonCodes.ParentBrowserUntrusted, 0);
        }

        if (!parent.Trusted)
        {
            var code = BrowserConnectorReasonCodes.IsSafe(parent.ReasonCode)
                ? parent.ReasonCode
                : BrowserConnectorReasonCodes.ParentBrowserUntrusted;
            return Reject(code, 0);
        }

        var sessionIdBytes = NewSecret(16);
        var sessionKey = NewSecret(32);
        var challengeBytes = NewSecret(32);
        try
        {
            var sessionId = BrowserConnectorAuthorityVerifier.Base64UrlEncode(sessionIdBytes);
            var challenge = BrowserConnectorAuthorityVerifier.Base64UrlEncode(challengeBytes);
            var expiresAt = Min(
                _authority.ExpiresAt,
                now + _options.SessionLifetime);

            Report(BrowserConnectorState.HandshakePending, BrowserConnectorReasonCodes.HandshakePending);
            var hello = new NativeHello(
                BrowserNativeHostOptions.ProtocolVersion,
                "hello",
                BrowserNativeHostOptions.Protocol,
                sessionId,
                BrowserConnectorAuthorityVerifier.Base64UrlEncode(sessionKey),
                challenge,
                0,
                expiresAt.ToUnixTimeMilliseconds());
            await NativeMessagingFraming.WriteJsonAsync(
                nativeOutput,
                hello,
                _options.MaximumFrameBytes,
                cancellationToken).ConfigureAwait(false);

            var acceptedMessages = 0;
            long lastCounter = 0;
            string? lastObservationFingerprint = null;
            while (true)
            {
                byte[]? payload;
                try
                {
                    payload = await NativeMessagingFraming.ReadFrameAsync(
                        nativeInput,
                        _options.MaximumFrameBytes,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (NativeMessagingProtocolException ex)
                {
                    Report(BrowserConnectorState.Degraded, ex.ReasonCode);
                    await TryWriteFatalAsync(
                        nativeOutput,
                        sessionId,
                        lastCounter,
                        ex.ReasonCode,
                        sessionKey,
                        cancellationToken).ConfigureAwait(false);
                    return new(false, ex.ReasonCode, acceptedMessages);
                }

                if (payload is null)
                {
                    Report(BrowserConnectorState.Disconnected, BrowserConnectorReasonCodes.Disconnected);
                    return new(acceptedMessages > 0, BrowserConnectorReasonCodes.Disconnected, acceptedMessages);
                }

                ParsedNativeMessage parsed;
                try
                {
                    var parse = ParseAndAuthenticate(
                        payload,
                        sessionId,
                        checked(lastCounter + 1),
                        challenge,
                        sessionKey);
                    if (!parse.Success)
                    {
                        Report(BrowserConnectorState.Degraded, parse.ReasonCode);
                        await TryWriteFatalAsync(
                            nativeOutput,
                            sessionId,
                            lastCounter,
                            parse.ReasonCode,
                            sessionKey,
                            cancellationToken).ConfigureAwait(false);
                        return new(false, parse.ReasonCode, acceptedMessages);
                    }
                    parsed = parse.Message!;
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(payload);
                }

                now = _timeProvider.GetUtcNow();
                if (now >= expiresAt)
                {
                    Report(BrowserConnectorState.Degraded, BrowserConnectorReasonCodes.SessionExpired);
                    await TryWriteFatalAsync(
                        nativeOutput,
                        sessionId,
                        lastCounter,
                        BrowserConnectorReasonCodes.SessionExpired,
                        sessionKey,
                        cancellationToken).ConfigureAwait(false);
                    return new(false, BrowserConnectorReasonCodes.SessionExpired, acceptedMessages);
                }

                string? category;
                try
                {
                    category = _domainClassifier(parsed.Hostname);
                }
                catch
                {
                    return Reject(BrowserConnectorReasonCodes.CategoryRejected, acceptedMessages);
                }

                if (category is not null && !SafeCategory.IsMatch(category))
                    return Reject(BrowserConnectorReasonCodes.CategoryRejected, acceptedMessages);

                var hostnameBytes = Encoding.UTF8.GetBytes(parsed.Hostname);
                string hostnameHash;
                try
                {
                    var digest = HMACSHA256.HashData(_observationHmacKey, hostnameBytes);
                    try
                    {
                        hostnameHash = Convert.ToHexString(digest).ToLowerInvariant();
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(digest);
                    }
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(hostnameBytes);
                }
                var safeCategory = category ?? "unknown";
                var fingerprint = category is null ? hostnameHash : safeCategory;
                if (!string.Equals(lastObservationFingerprint, fingerprint, StringComparison.Ordinal))
                {
                    try
                    {
                        _sink.OnObservation(new BrowserDomainObservation(
                            safeCategory,
                            category is null ? hostnameHash : null,
                            authorization.Browser,
                            parsed.Counter,
                            now));
                    }
                    catch
                    {
                        return Reject(BrowserConnectorReasonCodes.InternalFailure, acceptedMessages);
                    }
                    lastObservationFingerprint = fingerprint;
                }

                lastCounter = parsed.Counter;
                acceptedMessages++;
                CryptographicOperations.ZeroMemory(challengeBytes);
                challengeBytes = NewSecret(32);
                challenge = BrowserConnectorAuthorityVerifier.Base64UrlEncode(challengeBytes);
                var ack = BuildAcknowledgement(sessionId, lastCounter, challenge, sessionKey);
                await NativeMessagingFraming.WriteJsonAsync(
                    nativeOutput,
                    ack,
                    _options.MaximumFrameBytes,
                    cancellationToken).ConfigureAwait(false);
                Report(BrowserConnectorState.Ready, BrowserConnectorReasonCodes.Ready);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Reject(BrowserConnectorReasonCodes.InternalFailure, 0);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sessionIdBytes);
            CryptographicOperations.ZeroMemory(sessionKey);
            CryptographicOperations.ZeroMemory(challengeBytes);
        }
    }

    internal static string ComputeClientMac(
        ReadOnlySpan<byte> sessionKey,
        string sessionId,
        long counter,
        string challenge,
        string hostname)
    {
        var canonical = string.Join(
            '\n',
            BrowserNativeHostOptions.Protocol,
            sessionId,
            counter.ToString(CultureInfo.InvariantCulture),
            challenge,
            hostname);
        return ComputeCanonicalMac(sessionKey, canonical);
    }

    internal static string ComputeHostMac(
        ReadOnlySpan<byte> sessionKey,
        string sessionId,
        string type,
        long counter,
        string value,
        string status)
    {
        var canonical = string.Join(
            '\n',
            BrowserNativeHostOptions.Protocol,
            sessionId,
            type,
            counter.ToString(CultureInfo.InvariantCulture),
            value,
            status);
        return ComputeCanonicalMac(sessionKey, canonical);
    }

    internal static string? NormalizeHostname(string? input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.Length > 253)
            return null;
        var candidate = input.Trim().TrimEnd('.');
        if (candidate.Length == 0 ||
            candidate.Any(char.IsWhiteSpace) ||
            candidate.IndexOfAny(['/','\\','@','?','#','%']) >= 0)
            return null;

        if (candidate.StartsWith("[", StringComparison.Ordinal) &&
            candidate.EndsWith("]", StringComparison.Ordinal))
            candidate = candidate[1..^1];

        if (IPAddress.TryParse(candidate, out var address))
            return address.ToString().ToLowerInvariant();
        if (candidate.Contains(':', StringComparison.Ordinal))
            return null;
        if (Uri.CheckHostName(candidate) != UriHostNameType.Dns)
            return null;

        try
        {
            var ascii = new IdnMapping().GetAscii(candidate).ToLowerInvariant();
            return ascii.Length <= 253 && Uri.CheckHostName(ascii) == UriHostNameType.Dns
                ? ascii
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private ParsedNativeMessageResult ParseAndAuthenticate(
        ReadOnlyMemory<byte> payload,
        string expectedSessionId,
        long expectedCounter,
        string expectedChallenge,
        ReadOnlySpan<byte> sessionKey)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);

            var allowed = new HashSet<string>(StringComparer.Ordinal)
            {
                "version", "type", "protocol", "sessionId", "counter", "challenge", "hostname", "mac",
            };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                    return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);
            }
            if (seen.Count != allowed.Count)
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);

            var root = document.RootElement;
            if (!root.GetProperty("version").TryGetInt32(out var version) ||
                version != BrowserNativeHostOptions.ProtocolVersion ||
                !string.Equals(root.GetProperty("type").GetString(), "active_tab_hostname", StringComparison.Ordinal) ||
                !string.Equals(root.GetProperty("protocol").GetString(), BrowserNativeHostOptions.Protocol, StringComparison.Ordinal))
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);

            var sessionId = root.GetProperty("sessionId").GetString();
            if (!FixedEncodedEquals(expectedSessionId, sessionId))
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.AuthenticationRejected);
            if (!root.GetProperty("counter").TryGetInt64(out var counter) || counter != expectedCounter)
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.ReplayRejected);

            var challenge = root.GetProperty("challenge").GetString();
            if (!FixedEncodedEquals(expectedChallenge, challenge))
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.ChallengeRejected);

            var rawHostname = root.GetProperty("hostname").GetString();
            var hostname = NormalizeHostname(rawHostname);
            if (hostname is null || !string.Equals(hostname, rawHostname, StringComparison.Ordinal))
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.HostnameRejected);

            var providedMac = root.GetProperty("mac").GetString();
            var expectedMac = ComputeClientMac(sessionKey, expectedSessionId, counter, expectedChallenge, hostname);
            if (!FixedEncodedEquals(expectedMac, providedMac))
                return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.AuthenticationRejected);

            return ParsedNativeMessageResult.Allow(new ParsedNativeMessage(counter, hostname));
        }
        catch (JsonException)
        {
            return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);
        }
        catch (InvalidOperationException)
        {
            return ParsedNativeMessageResult.Deny(BrowserConnectorReasonCodes.MessageInvalid);
        }
    }

    private NativeAcknowledgement BuildAcknowledgement(
        string sessionId,
        long counter,
        string nextChallenge,
        ReadOnlySpan<byte> sessionKey) =>
        new(
            BrowserNativeHostOptions.ProtocolVersion,
            "accepted",
            BrowserNativeHostOptions.Protocol,
            sessionId,
            counter,
            nextChallenge,
            BrowserConnectorReasonCodes.Ready,
            ComputeHostMac(
                sessionKey,
                sessionId,
                "accepted",
                counter,
                nextChallenge,
                BrowserConnectorReasonCodes.Ready));

    private async Task TryWriteFatalAsync(
        Stream output,
        string sessionId,
        long counter,
        string reasonCode,
        ReadOnlyMemory<byte> sessionKey,
        CancellationToken cancellationToken)
    {
        try
        {
            var fatal = new NativeFatal(
                BrowserNativeHostOptions.ProtocolVersion,
                "fatal",
                BrowserNativeHostOptions.Protocol,
                sessionId,
                counter,
                reasonCode,
                ComputeHostMac(
                    sessionKey.Span,
                    sessionId,
                    "fatal",
                    counter,
                    reasonCode,
                    "degraded"));
            await NativeMessagingFraming.WriteJsonAsync(
                output,
                fatal,
                _options.MaximumFrameBytes,
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The status sink remains the source of local health truth. A torn
            // browser pipe must never turn a protocol rejection into a crash.
        }
    }

    private byte[] NewSecret(int length)
    {
        var bytes = new byte[length];
        _entropy.Fill(bytes);
        return bytes;
    }

    private BrowserNativeHostRunResult Reject(string reasonCode, int acceptedMessages)
    {
        var safeReason = BrowserConnectorReasonCodes.IsSafe(reasonCode)
            ? reasonCode
            : BrowserConnectorReasonCodes.InternalFailure;
        Report(BrowserConnectorState.Degraded, safeReason);
        _logger.Warning("Browser connector rejected.");
        return new(false, safeReason, acceptedMessages);
    }

    private void Report(BrowserConnectorState state, string reasonCode)
    {
        try
        {
            _sink.OnStatus(new BrowserConnectorStatus(
                state,
                BrowserConnectorReasonCodes.IsSafe(reasonCode)
                    ? reasonCode
                    : BrowserConnectorReasonCodes.InternalFailure,
                _timeProvider.GetUtcNow()));
        }
        catch
        {
            // Status reporting is diagnostic only. Observation delivery still
            // fails closed independently when the observation callback fails.
        }
    }

    private static bool FixedEncodedEquals(string expected, string? actual)
    {
        if (actual is null || expected.Length != actual.Length ||
            !expected.All(char.IsAscii) || !actual.All(char.IsAscii))
            return false;
        var expectedBytes = Encoding.ASCII.GetBytes(expected);
        var actualBytes = Encoding.ASCII.GetBytes(actual);
        try
        {
            return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private static string ComputeCanonicalMac(ReadOnlySpan<byte> sessionKey, string canonical)
    {
        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            var digest = HMACSHA256.HashData(sessionKey, canonicalBytes);
            try
            {
                return BrowserConnectorAuthorityVerifier.Base64UrlEncode(digest);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonicalBytes);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) =>
        left <= right ? left : right;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        CryptographicOperations.ZeroMemory(_observationHmacKey);
    }

    private sealed record NativeHello(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        string SessionKey,
        string Challenge,
        long Counter,
        long ExpiresAtUnixMs);

    private sealed record NativeAcknowledgement(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        long Counter,
        string NextChallenge,
        string Status,
        string Mac);

    private sealed record NativeFatal(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        long Counter,
        string Reason,
        string Mac);

    private sealed record ParsedNativeMessage(long Counter, string Hostname);

    private readonly record struct ParsedNativeMessageResult(
        bool Success,
        string ReasonCode,
        ParsedNativeMessage? Message)
    {
        public static ParsedNativeMessageResult Allow(ParsedNativeMessage message) =>
            new(true, BrowserConnectorReasonCodes.Ready, message);

        public static ParsedNativeMessageResult Deny(string reasonCode) =>
            new(false, reasonCode, null);
    }
}

internal static class NativeMessagingFraming
{
    public static async Task<byte[]?> ReadFrameAsync(
        Stream input,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerBytes = await ReadExactOrEofAsync(input, header, cancellationToken).ConfigureAwait(false);
        if (headerBytes == 0)
            return null;
        if (headerBytes != header.Length)
            throw new NativeMessagingProtocolException(BrowserConnectorReasonCodes.FrameTruncated);

        var length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length == 0 || length > maximumFrameBytes)
            throw new NativeMessagingProtocolException(
                length > maximumFrameBytes
                    ? BrowserConnectorReasonCodes.FrameOversize
                    : BrowserConnectorReasonCodes.FrameInvalid);

        var payload = new byte[length];
        var payloadBytes = await ReadExactOrEofAsync(input, payload, cancellationToken).ConfigureAwait(false);
        if (payloadBytes != payload.Length)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw new NativeMessagingProtocolException(BrowserConnectorReasonCodes.FrameTruncated);
        }
        return payload;
    }

    public static async Task WriteJsonAsync<T>(
        Stream output,
        T value,
        int maximumFrameBytes,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, BrowserProtocolJson.Options);
        try
        {
            if (payload.Length == 0 || payload.Length > maximumFrameBytes)
                throw new NativeMessagingProtocolException(BrowserConnectorReasonCodes.FrameOversize);

            var header = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(header, checked((uint)payload.Length));
            await output.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await output.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static async Task<int> ReadExactOrEofAsync(
        Stream input,
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await input.ReadAsync(buffer[total..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }
        return total;
    }
}

internal sealed class NativeMessagingProtocolException : Exception
{
    public NativeMessagingProtocolException(string reasonCode)
        : base("Native messaging protocol rejected a frame.")
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

internal static class BrowserProtocolJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        MaxDepth = 8,
    };
}
