using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Produces SuavoAgent exact-request HMAC v2 authentication. Each physical
/// HTTP attempt receives a fresh 256-bit nonce; application idempotency is a
/// separate concern and must never reuse the transport nonce.
/// </summary>
public sealed partial class AgentRequestSigner
{
    public const string AuthVersion = "2";
    public const string CanonicalPrefix = "suavo-agent-request-v2";

    private readonly byte[] _keyBytes;

    public AgentRequestSigner(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _keyBytes = Encoding.UTF8.GetBytes(apiKey);
        ApiKey = apiKey;
    }

    public string ApiKey { get; }

    public AgentRequestAuthorization ApplyHeaders(HttpRequestMessage request, string rawBody)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(rawBody);
        if (request.RequestUri is null)
            throw new InvalidOperationException("A request URI is required before signing.");

        var pathAndQuery = GetExactPathAndQuery(request.RequestUri);
        var timestamp = DateTimeOffset.UtcNow
            .ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var nonce = CreateNonce();
        var bodySha256 = ComputeBodySha256(rawBody);
        var signature = Sign(
            request.Method.Method,
            pathAndQuery,
            timestamp,
            nonce,
            bodySha256);

        AddHeader(request, "x-agent-auth-version", AuthVersion);
        AddHeader(request, "x-agent-api-key", ApiKey);
        AddHeader(request, "x-agent-timestamp", timestamp);
        AddHeader(request, "x-agent-nonce", nonce);
        AddHeader(request, "x-agent-content-sha256", bodySha256);
        AddHeader(request, "x-agent-signature", signature);

        return new AgentRequestAuthorization(timestamp, nonce, bodySha256, signature);
    }

    public string Sign(
        string method,
        string pathAndQuery,
        string timestamp,
        string nonce,
        string bodySha256)
    {
        var canonical = BuildCanonicalEnvelope(
            method,
            pathAndQuery,
            timestamp,
            nonce,
            bodySha256);
        return Convert.ToHexString(
                HMACSHA256.HashData(_keyBytes, Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    public static string BuildCanonicalEnvelope(
        string method,
        string pathAndQuery,
        string timestamp,
        string nonce,
        string bodySha256)
    {
        if (string.IsNullOrEmpty(method) || !HttpMethodShape().IsMatch(method))
            throw new ArgumentException("HTTP method has an invalid shape.", nameof(method));
        if (string.IsNullOrEmpty(pathAndQuery) ||
            pathAndQuery[0] != '/' ||
            pathAndQuery.Contains('\r') ||
            pathAndQuery.Contains('\n') ||
            pathAndQuery.Contains('#'))
            throw new ArgumentException("Request target has an invalid shape.", nameof(pathAndQuery));
        if (!EpochMillisecondsShape().IsMatch(timestamp))
            throw new ArgumentException("Timestamp has an invalid shape.", nameof(timestamp));
        if (!IsCanonicalNonce(nonce))
            throw new ArgumentException("Nonce has an invalid shape.", nameof(nonce));
        if (!Sha256Shape().IsMatch(bodySha256))
            throw new ArgumentException("Body digest has an invalid shape.", nameof(bodySha256));

        return string.Join(
            "\n",
            CanonicalPrefix,
            method,
            pathAndQuery,
            timestamp,
            nonce,
            bodySha256);
    }

    public static string ComputeBodySha256(string rawBody)
    {
        ArgumentNullException.ThrowIfNull(rawBody);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawBody)))
            .ToLowerInvariant();
    }

    public static string CreateNonce()
    {
        return Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static string GetExactPathAndQuery(Uri requestUri)
    {
        ArgumentNullException.ThrowIfNull(requestUri);
        var target = requestUri.IsAbsoluteUri
            ? requestUri.PathAndQuery
            : requestUri.OriginalString;
        if (string.IsNullOrEmpty(target) ||
            target[0] != '/' ||
            target.Contains('\r') ||
            target.Contains('\n') ||
            target.Contains('#'))
            throw new ArgumentException("Request URI must be an origin-form path without a fragment.", nameof(requestUri));
        return target;
    }

    public static bool IsWithinReplayWindow(string timestamp, TimeSpan window)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var milliseconds))
            return false;
        DateTimeOffset parsed;
        try
        {
            parsed = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
        var age = DateTimeOffset.UtcNow - parsed;
        return age >= TimeSpan.Zero && age <= window;
    }

    private static bool IsCanonicalNonce(string nonce)
    {
        if (!NonceShape().IsMatch(nonce)) return false;
        try
        {
            var padded = nonce.Replace('-', '+').Replace('_', '/') + "=";
            var decoded = Convert.FromBase64String(padded);
            return decoded.Length == 32 && string.Equals(
                Convert.ToBase64String(decoded).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
                nonce,
                StringComparison.Ordinal);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static void AddHeader(HttpRequestMessage request, string name, string value)
    {
        if (request.Headers.Contains(name) || !request.Headers.TryAddWithoutValidation(name, value))
            throw new InvalidOperationException($"Request authentication header already exists: {name}");
    }

    [GeneratedRegex("^[A-Z]{3,10}$", RegexOptions.CultureInvariant)]
    private static partial Regex HttpMethodShape();

    [GeneratedRegex("^\\d{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex EpochMillisecondsShape();

    [GeneratedRegex("^[A-Za-z0-9_-]{43}$", RegexOptions.CultureInvariant)]
    private static partial Regex NonceShape();

    [GeneratedRegex("^[a-f0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Shape();
}

public sealed record AgentRequestAuthorization(
    string Timestamp,
    string Nonce,
    string ContentSha256,
    string Signature);
