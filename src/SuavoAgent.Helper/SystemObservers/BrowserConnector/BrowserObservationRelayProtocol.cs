using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal static class BrowserRelayProtocol
{
    private static readonly Regex OpaqueIdentifier = new(
        "^[A-Za-z0-9_-]{16,128}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public static bool IsOpaqueIdentifier(string? value) =>
        value is not null && OpaqueIdentifier.IsMatch(value);

    public static string NewToken(IBrowserRelayEntropy entropy, int byteLength)
    {
        var bytes = new byte[byteLength];
        try
        {
            entropy.Fill(bytes);
            return Base64Url(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    public static byte[] NewSecret(IBrowserRelayEntropy entropy, int byteLength)
    {
        var bytes = new byte[byteLength];
        entropy.Fill(bytes);
        return bytes;
    }

    public static async Task WriteClientHelloAsync(
        Stream stream,
        string clientNonce,
        CancellationToken cancellationToken) =>
        await WriteAsync(
            stream,
            new ClientHello(
                BrowserObservationRelayConstants.ProtocolVersion,
                "client_hello",
                BrowserObservationRelayConstants.Protocol,
                clientNonce),
            cancellationToken).ConfigureAwait(false);

    public static async Task<string> ReadClientHelloAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        RequireExactProperties(root, "version", "type", "protocol", "clientNonce");
        RequireProtocol(root, "client_hello");
        var nonce = RequiredString(root, "clientNonce");
        if (!IsBase64UrlToken(nonce, 32))
            throw Reject(BrowserConnectorReasonCodes.ChallengeRejected);
        return nonce;
    }

    public static async Task WriteServerGrantAsync(
        Stream stream,
        BrowserRelayServerGrant grant,
        CancellationToken cancellationToken)
    {
        var domainHashKey = Convert.ToBase64String(grant.DomainHashKey);
        var relayKey = Base64Url(grant.RelayKey);
        var expires = grant.ExpiresAtUtc.ToUnixTimeMilliseconds();
        var mac = ComputeMac(
            grant.RelayKey,
            "server_grant",
            grant.SessionId,
            grant.ClientNonce,
            grant.ServerNonce,
            grant.LeaseId,
            grant.SessionBinding,
            grant.LeaseEpoch.ToString(CultureInfo.InvariantCulture),
            expires.ToString(CultureInfo.InvariantCulture),
            domainHashKey,
            relayKey);
        await WriteAsync(
            stream,
            new ServerGrant(
                BrowserObservationRelayConstants.ProtocolVersion,
                "server_grant",
                BrowserObservationRelayConstants.Protocol,
                grant.SessionId,
                grant.ClientNonce,
                grant.ServerNonce,
                grant.LeaseId,
                grant.SessionBinding,
                grant.LeaseEpoch,
                expires,
                domainHashKey,
                relayKey,
                mac),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BrowserRelayClientGrant> ReadServerGrantAsync(
        Stream stream,
        string expectedClientNonce,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        RequireExactProperties(
            root,
            "version", "type", "protocol", "sessionId", "clientNonce", "serverNonce",
            "leaseId", "sessionBinding", "leaseEpoch", "expiresAtUnixMs",
            "domainHashKey", "relayKey", "mac");
        RequireProtocol(root, "server_grant");

        var sessionId = RequiredString(root, "sessionId");
        var clientNonce = RequiredString(root, "clientNonce");
        var serverNonce = RequiredString(root, "serverNonce");
        var leaseId = RequiredString(root, "leaseId");
        var sessionBinding = RequiredString(root, "sessionBinding");
        var domainHashKeyText = RequiredString(root, "domainHashKey");
        var relayKeyText = RequiredString(root, "relayKey");
        var mac = RequiredString(root, "mac");
        if (!FixedAsciiEquals(expectedClientNonce, clientNonce) ||
            !IsBase64UrlToken(sessionId, 16) ||
            !IsBase64UrlToken(serverNonce, 32) ||
            !IsOpaqueIdentifier(leaseId) ||
            !IsOpaqueIdentifier(sessionBinding) ||
            !root.GetProperty("leaseEpoch").TryGetInt64(out var leaseEpoch) ||
            leaseEpoch <= 0 ||
            !root.GetProperty("expiresAtUnixMs").TryGetInt64(out var expiresUnixMs) ||
            !TryFromUnixMilliseconds(expiresUnixMs, out var expiresAt) ||
            expiresAt <= now + BrowserObservationRelayConstants.MinimumLeaseRemaining ||
            !TryDecodeBase64(domainHashKeyText, 32, 32, out var domainHashKey) ||
            !TryDecodeBase64Url(relayKeyText, 32, out var relayKey))
        {
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
        }

        try
        {
            var expectedMac = ComputeMac(
                relayKey,
                "server_grant",
                sessionId,
                clientNonce,
                serverNonce,
                leaseId,
                sessionBinding,
                leaseEpoch.ToString(CultureInfo.InvariantCulture),
                expiresUnixMs.ToString(CultureInfo.InvariantCulture),
                domainHashKeyText,
                relayKeyText);
            if (!FixedAsciiEquals(expectedMac, mac))
                throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);

            var result = new BrowserRelayClientGrant(
                sessionId,
                clientNonce,
                serverNonce,
                leaseId,
                sessionBinding,
                leaseEpoch,
                expiresAt,
                domainHashKey,
                relayKey);
            domainHashKey = Array.Empty<byte>();
            relayKey = Array.Empty<byte>();
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(domainHashKey);
            CryptographicOperations.ZeroMemory(relayKey);
        }
    }

    public static async Task WriteClientProofAsync(
        Stream stream,
        BrowserRelayClientGrant grant,
        CancellationToken cancellationToken)
    {
        var mac = ComputeHandshakeMac(
            grant.RelayKey,
            "client_proof",
            grant.SessionId,
            grant.ClientNonce,
            grant.ServerNonce,
            grant.LeaseId,
            grant.LeaseEpoch);
        await WriteAsync(
            stream,
            new HandshakeProof(
                BrowserObservationRelayConstants.ProtocolVersion,
                "client_proof",
                BrowserObservationRelayConstants.Protocol,
                grant.SessionId,
                grant.ClientNonce,
                grant.ServerNonce,
                grant.LeaseId,
                grant.LeaseEpoch,
                mac),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task VerifyClientProofAsync(
        Stream stream,
        BrowserRelayServerGrant grant,
        CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        VerifyHandshakeProof(document.RootElement, "client_proof", grant);
    }

    public static async Task WriteServerAcceptedAsync(
        Stream stream,
        BrowserRelayServerGrant grant,
        CancellationToken cancellationToken)
    {
        var mac = ComputeHandshakeMac(
            grant.RelayKey,
            "server_accepted",
            grant.SessionId,
            grant.ClientNonce,
            grant.ServerNonce,
            grant.LeaseId,
            grant.LeaseEpoch);
        await WriteAsync(
            stream,
            new HandshakeProof(
                BrowserObservationRelayConstants.ProtocolVersion,
                "server_accepted",
                BrowserObservationRelayConstants.Protocol,
                grant.SessionId,
                grant.ClientNonce,
                grant.ServerNonce,
                grant.LeaseId,
                grant.LeaseEpoch,
                mac),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task VerifyServerAcceptedAsync(
        Stream stream,
        BrowserRelayClientGrant grant,
        CancellationToken cancellationToken)
    {
        using var document = await ReadDocumentAsync(stream, cancellationToken).ConfigureAwait(false);
        VerifyHandshakeProof(document.RootElement, "server_accepted", grant);
    }

    public static async Task WriteStatusAsync(
        Stream stream,
        BrowserRelayClientGrant grant,
        long counter,
        BrowserConnectorStatus status,
        CancellationToken cancellationToken)
    {
        var timestamp = status.Timestamp.ToUnixTimeMilliseconds();
        var state = (int)status.State;
        var mac = ComputeMac(
            grant.RelayKey,
            "status",
            grant.SessionId,
            counter.ToString(CultureInfo.InvariantCulture),
            state.ToString(CultureInfo.InvariantCulture),
            status.ReasonCode,
            timestamp.ToString(CultureInfo.InvariantCulture));
        await WriteAsync(
            stream,
            new StatusFrame(
                BrowserObservationRelayConstants.ProtocolVersion,
                "status",
                BrowserObservationRelayConstants.Protocol,
                grant.SessionId,
                counter,
                state,
                status.ReasonCode,
                timestamp,
                mac),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task WriteObservationAsync(
        Stream stream,
        BrowserRelayClientGrant grant,
        long counter,
        BrowserDomainObservation observation,
        CancellationToken cancellationToken)
    {
        var timestamp = observation.Timestamp.ToUnixTimeMilliseconds();
        var browser = (int)observation.Browser;
        var mac = ComputeMac(
            grant.RelayKey,
            "observation",
            grant.SessionId,
            counter.ToString(CultureInfo.InvariantCulture),
            observation.Category,
            observation.HostnameHash ?? string.Empty,
            browser.ToString(CultureInfo.InvariantCulture),
            observation.Counter.ToString(CultureInfo.InvariantCulture),
            timestamp.ToString(CultureInfo.InvariantCulture));
        await WriteAsync(
            stream,
            new ObservationFrame(
                BrowserObservationRelayConstants.ProtocolVersion,
                "observation",
                BrowserObservationRelayConstants.Protocol,
                grant.SessionId,
                counter,
                observation.Category,
                observation.HostnameHash,
                browser,
                observation.Counter,
                timestamp,
                mac),
            cancellationToken).ConfigureAwait(false);
    }

    public static async Task<BrowserRelayMessage?> ReadAuthenticatedMessageAsync(
        Stream stream,
        BrowserRelayServerGrant grant,
        long expectedCounter,
        CancellationToken cancellationToken)
    {
        var payload = await BrowserRelayFraming.ReadFrameAsync(
            stream,
            BrowserObservationRelayConstants.MaximumFrameBytes,
            cancellationToken).ConfigureAwait(false);
        if (payload is null)
            return null;
        try
        {
            using var document = JsonDocument.Parse(payload, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var typeProperty) ||
                typeProperty.ValueKind != JsonValueKind.String)
                throw Reject(BrowserConnectorReasonCodes.FrameInvalid);
            return typeProperty.GetString() switch
            {
                "status" => ParseStatus(root, grant, expectedCounter),
                "observation" => ParseObservation(root, grant, expectedCounter),
                _ => throw Reject(BrowserConnectorReasonCodes.MessageInvalid),
            };
        }
        catch (JsonException)
        {
            throw Reject(BrowserConnectorReasonCodes.FrameInvalid);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    internal static string ComputeHandshakeMacForTest(
        ReadOnlySpan<byte> key,
        string type,
        string sessionId,
        string clientNonce,
        string serverNonce,
        string leaseId,
        long epoch) =>
        ComputeHandshakeMac(key, type, sessionId, clientNonce, serverNonce, leaseId, epoch);

    private static BrowserRelayMessage ParseStatus(
        JsonElement root,
        BrowserRelayServerGrant grant,
        long expectedCounter)
    {
        RequireExactProperties(
            root,
            "version", "type", "protocol", "sessionId", "counter", "state",
            "reasonCode", "timestampUnixMs", "mac");
        RequireProtocol(root, "status");
        var sessionId = RequiredString(root, "sessionId");
        var reason = RequiredString(root, "reasonCode");
        var mac = RequiredString(root, "mac");
        if (!FixedAsciiEquals(grant.SessionId, sessionId))
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
        if (!root.GetProperty("counter").TryGetInt64(out var counter) ||
            counter != expectedCounter)
            throw Reject(BrowserConnectorReasonCodes.ReplayRejected);
        if (!root.GetProperty("state").TryGetInt32(out var stateValue) ||
            !Enum.IsDefined(typeof(BrowserConnectorState), stateValue) ||
            !BrowserConnectorReasonCodes.IsSafe(reason) ||
            !root.GetProperty("timestampUnixMs").TryGetInt64(out var timestamp) ||
            !TryFromUnixMilliseconds(timestamp, out var at))
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        var expectedMac = ComputeMac(
            grant.RelayKey,
            "status",
            sessionId,
            counter.ToString(CultureInfo.InvariantCulture),
            stateValue.ToString(CultureInfo.InvariantCulture),
            reason,
            timestamp.ToString(CultureInfo.InvariantCulture));
        if (!FixedAsciiEquals(expectedMac, mac))
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
        return BrowserRelayMessage.ForStatus(
            new BrowserConnectorStatus((BrowserConnectorState)stateValue, reason, at));
    }

    private static BrowserRelayMessage ParseObservation(
        JsonElement root,
        BrowserRelayServerGrant grant,
        long expectedCounter)
    {
        RequireExactProperties(
            root,
            "version", "type", "protocol", "sessionId", "counter", "category",
            "hostnameHash", "browser", "sourceCounter", "timestampUnixMs", "mac");
        RequireProtocol(root, "observation");
        var sessionId = RequiredString(root, "sessionId");
        var category = RequiredString(root, "category");
        var hash = OptionalString(root, "hostnameHash");
        var mac = RequiredString(root, "mac");
        if (!FixedAsciiEquals(grant.SessionId, sessionId))
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
        if (!root.GetProperty("counter").TryGetInt64(out var counter) ||
            counter != expectedCounter)
            throw Reject(BrowserConnectorReasonCodes.ReplayRejected);
        if (!IsSafeCategory(category) ||
            (hash is not null && !IsLowerHexSha256(hash)) ||
            (string.Equals(category, "unknown", StringComparison.Ordinal) != (hash is not null)) ||
            !root.GetProperty("browser").TryGetInt32(out var browserValue) ||
            !Enum.IsDefined(typeof(BrowserFamily), browserValue) ||
            !root.GetProperty("sourceCounter").TryGetInt64(out var sourceCounter) ||
            sourceCounter <= 0 ||
            !root.GetProperty("timestampUnixMs").TryGetInt64(out var timestamp) ||
            !TryFromUnixMilliseconds(timestamp, out var at))
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        var expectedMac = ComputeMac(
            grant.RelayKey,
            "observation",
            sessionId,
            counter.ToString(CultureInfo.InvariantCulture),
            category,
            hash ?? string.Empty,
            browserValue.ToString(CultureInfo.InvariantCulture),
            sourceCounter.ToString(CultureInfo.InvariantCulture),
            timestamp.ToString(CultureInfo.InvariantCulture));
        if (!FixedAsciiEquals(expectedMac, mac))
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
        return BrowserRelayMessage.ForObservation(
            new BrowserDomainObservation(
                category,
                hash,
                (BrowserFamily)browserValue,
                sourceCounter,
                at));
    }

    private static void VerifyHandshakeProof(
        JsonElement root,
        string expectedType,
        BrowserRelayServerGrant grant)
    {
        RequireExactProperties(
            root,
            "version", "type", "protocol", "sessionId", "clientNonce", "serverNonce",
            "leaseId", "leaseEpoch", "mac");
        RequireProtocol(root, expectedType);
        var sessionId = RequiredString(root, "sessionId");
        var clientNonce = RequiredString(root, "clientNonce");
        var serverNonce = RequiredString(root, "serverNonce");
        var leaseId = RequiredString(root, "leaseId");
        var mac = RequiredString(root, "mac");
        if (!root.GetProperty("leaseEpoch").TryGetInt64(out var leaseEpoch) ||
            !FixedAsciiEquals(grant.SessionId, sessionId) ||
            !FixedAsciiEquals(grant.ClientNonce, clientNonce) ||
            !FixedAsciiEquals(grant.ServerNonce, serverNonce) ||
            !FixedAsciiEquals(grant.LeaseId, leaseId) ||
            leaseEpoch != grant.LeaseEpoch)
            throw Reject(BrowserConnectorReasonCodes.ChallengeRejected);
        var expectedMac = ComputeHandshakeMac(
            grant.RelayKey,
            expectedType,
            sessionId,
            clientNonce,
            serverNonce,
            leaseId,
            leaseEpoch);
        if (!FixedAsciiEquals(expectedMac, mac))
            throw Reject(BrowserConnectorReasonCodes.AuthenticationRejected);
    }

    private static void VerifyHandshakeProof(
        JsonElement root,
        string expectedType,
        BrowserRelayClientGrant grant)
    {
        var serverGrant = new BrowserRelayServerGrant(
            grant.SessionId,
            grant.ClientNonce,
            grant.ServerNonce,
            grant.LeaseId,
            grant.SessionBinding,
            grant.LeaseEpoch,
            grant.ExpiresAtUtc,
            grant.DomainHashKey,
            grant.RelayKey);
        VerifyHandshakeProof(root, expectedType, serverGrant);
    }

    private static string ComputeHandshakeMac(
        ReadOnlySpan<byte> key,
        string type,
        string sessionId,
        string clientNonce,
        string serverNonce,
        string leaseId,
        long epoch) =>
        ComputeMac(
            key,
            type,
            sessionId,
            clientNonce,
            serverNonce,
            leaseId,
            epoch.ToString(CultureInfo.InvariantCulture));

    private static string ComputeMac(ReadOnlySpan<byte> key, params string[] fields)
    {
        var canonical = string.Join('\n',
            new[] { BrowserObservationRelayConstants.Protocol }.Concat(fields));
        var bytes = Encoding.UTF8.GetBytes(canonical);
        try
        {
            return Base64Url(HMACSHA256.HashData(key, bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static async Task<BrowserRelayJsonDocument> ReadDocumentAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        var payload = await BrowserRelayFraming.ReadFrameAsync(
            stream,
            BrowserObservationRelayConstants.MaximumFrameBytes,
            cancellationToken).ConfigureAwait(false)
            ?? throw Reject(BrowserConnectorReasonCodes.Disconnected);
        try
        {
            return new BrowserRelayJsonDocument(payload);
        }
        catch (JsonException)
        {
            CryptographicOperations.ZeroMemory(payload);
            throw Reject(BrowserConnectorReasonCodes.FrameInvalid);
        }
    }

    private static async Task WriteAsync<T>(
        Stream stream,
        T value,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            await BrowserRelayFraming.WriteFrameAsync(
                stream,
                payload,
                BrowserObservationRelayConstants.MaximumFrameBytes,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    private static void RequireProtocol(JsonElement root, string type)
    {
        if (!root.GetProperty("version").TryGetInt32(out var version) ||
            version != BrowserObservationRelayConstants.ProtocolVersion ||
            !string.Equals(root.GetProperty("type").GetString(), type, StringComparison.Ordinal) ||
            !string.Equals(
                root.GetProperty("protocol").GetString(),
                BrowserObservationRelayConstants.Protocol,
                StringComparison.Ordinal))
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
    }

    private static void RequireExactProperties(JsonElement root, params string[] expected)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        }
        if (seen.Count != allowed.Count)
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
    }

    private static string RequiredString(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind != JsonValueKind.String)
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        return property.GetString()
            ?? throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        var property = root.GetProperty(name);
        if (property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind != JsonValueKind.String)
            throw Reject(BrowserConnectorReasonCodes.MessageInvalid);
        return property.GetString();
    }

    private static bool IsSafeCategory(string value) =>
        value.Length is >= 1 and <= 64 &&
        char.IsAsciiLetterLower(value[0]) &&
        value.All(character =>
            char.IsAsciiLetterLower(character) ||
            char.IsAsciiDigit(character) ||
            character is '_' or ':' or '-');

    private static bool IsLowerHexSha256(string value) =>
        value.Length == 64 && value.All(character =>
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f');

    private static bool IsBase64UrlToken(string value, int exactBytes) =>
        TryDecodeBase64Url(value, exactBytes, out var bytes) && ZeroAndTrue(bytes);

    private static bool ZeroAndTrue(byte[] bytes)
    {
        CryptographicOperations.ZeroMemory(bytes);
        return true;
    }

    private static bool TryDecodeBase64Url(string value, int exactBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var exactCharacters = checked((exactBytes * 8 + 5) / 6);
        if (value.Length != exactCharacters ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
            return false;
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        try
        {
            bytes = Convert.FromBase64String(normalized);
            if (bytes.Length == exactBytes)
                return true;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = Array.Empty<byte>();
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool TryDecodeBase64(
        string value,
        int minimumBytes,
        int maximumBytes,
        out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (value.Length > 128)
            return false;
        try
        {
            bytes = Convert.FromBase64String(value);
            if (bytes.Length >= minimumBytes && bytes.Length <= maximumBytes)
                return true;
            CryptographicOperations.ZeroMemory(bytes);
            bytes = Array.Empty<byte>();
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedAsciiEquals(string expected, string? actual)
    {
        if (actual is null || expected.Length != actual.Length ||
            !expected.All(char.IsAscii) || !actual.All(char.IsAscii))
            return false;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }

    private static bool TryFromUnixMilliseconds(long value, out DateTimeOffset timestamp)
    {
        try
        {
            timestamp = DateTimeOffset.FromUnixTimeMilliseconds(value);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            timestamp = default;
            return false;
        }
    }

    private static BrowserRelayProtocolException Reject(string reasonCode) => new(reasonCode);

    private sealed record ClientHello(int Version, string Type, string Protocol, string ClientNonce);

    private sealed record ServerGrant(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        string ClientNonce,
        string ServerNonce,
        string LeaseId,
        string SessionBinding,
        long LeaseEpoch,
        long ExpiresAtUnixMs,
        string DomainHashKey,
        string RelayKey,
        string Mac);

    private sealed record HandshakeProof(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        string ClientNonce,
        string ServerNonce,
        string LeaseId,
        long LeaseEpoch,
        string Mac);

    private sealed record StatusFrame(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        long Counter,
        int State,
        string ReasonCode,
        long TimestampUnixMs,
        string Mac);

    private sealed record ObservationFrame(
        int Version,
        string Type,
        string Protocol,
        string SessionId,
        long Counter,
        string Category,
        string? HostnameHash,
        int Browser,
        long SourceCounter,
        long TimestampUnixMs,
        string Mac);
}
