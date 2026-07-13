using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

/// <summary>
/// One exact browser-store identity authorized to start the native host. Edge
/// and Chrome both use the chrome-extension origin scheme, so browser family
/// and the device's canonical protected browser executable path remain
/// independently signed fields checked against the stdio peer, parent, and
/// parent-window processes at connection time.
/// </summary>
public sealed record BrowserConnectorAuthorityEntry(
    BrowserFamily Browser,
    string ExtensionId,
    string Origin,
    string BrowserExecutablePath);

public sealed record BrowserConnectorAuthorityDocument(
    int SchemaVersion,
    long Revision,
    DateTimeOffset IssuedAt,
    DateTimeOffset ExpiresAt,
    string KeyId,
    IReadOnlyList<BrowserConnectorAuthorityEntry> AllowedExtensions,
    string Signature);

public readonly record struct BrowserConnectorAuthorityValidation(
    bool Valid,
    string ReasonCode,
    VerifiedBrowserConnectorAuthority? Authority);

/// <summary>
/// Capability produced only after the signed extension allowlist validates.
/// Native-host code accepts this type instead of an arbitrary list so a local
/// JSON edit cannot silently authorize a different extension.
/// </summary>
public sealed class VerifiedBrowserConnectorAuthority
{
    private readonly IReadOnlyList<BrowserConnectorAuthorityEntry> _allowedExtensions;

    internal VerifiedBrowserConnectorAuthority(
        long revision,
        DateTimeOffset expiresAt,
        IReadOnlyList<BrowserConnectorAuthorityEntry> allowedExtensions)
    {
        Revision = revision;
        ExpiresAt = expiresAt;
        _allowedExtensions = allowedExtensions;
    }

    public long Revision { get; }

    public DateTimeOffset ExpiresAt { get; }

    public bool TryAuthorize(string origin, out BrowserConnectorAuthorityEntry entry)
    {
        entry = default!;
        if (string.IsNullOrEmpty(origin) || origin.Length > 96)
            return false;

        foreach (var candidate in _allowedExtensions)
        {
            if (!FixedAsciiEquals(candidate.Origin, origin))
                continue;

            entry = candidate;
            return true;
        }

        return false;
    }

    private static bool FixedAsciiEquals(string expected, string actual)
    {
        if (expected.Length != actual.Length || !expected.All(char.IsAscii) || !actual.All(char.IsAscii))
            return false;

        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected),
            Encoding.ASCII.GetBytes(actual));
    }
}

public static class BrowserConnectorAuthorityVerifier
{
    public const int CurrentSchemaVersion = 2;
    private static readonly TimeSpan MaximumAuthorityLifetime = TimeSpan.FromDays(31);
    private static readonly TimeSpan MaximumFutureSkew = TimeSpan.FromMinutes(5);
    private static readonly Regex ExtensionIdPattern = new(
        "^[a-p]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex KeyIdPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static BrowserConnectorAuthorityValidation Verify(
        BrowserConnectorAuthorityDocument? document,
        IReadOnlyDictionary<string, string> trustedPublicKeys,
        DateTimeOffset now)
    {
        if (document is null || trustedPublicKeys is null)
            return Deny();
        if (document.SchemaVersion != CurrentSchemaVersion ||
            document.Revision <= 0 ||
            document.AllowedExtensions is null or { Count: 0 } ||
            document.AllowedExtensions.Count > 4 ||
            !KeyIdPattern.IsMatch(document.KeyId ?? string.Empty) ||
            document.IssuedAt > now + MaximumFutureSkew ||
            document.ExpiresAt <= now ||
            document.ExpiresAt <= document.IssuedAt ||
            document.ExpiresAt - document.IssuedAt > MaximumAuthorityLifetime ||
            !TryValidateEntries(document.AllowedExtensions, out var normalized) ||
            !trustedPublicKeys.TryGetValue(document.KeyId!, out var publicKeyBase64) ||
            !TryDecodeBase64Url(document.Signature, 64, out var signature) ||
            !TryDecodeBase64(publicKeyBase64, out var publicKey))
        {
            return Deny();
        }

        try
        {
            using var verifier = ECDsa.Create();
            verifier.ImportSubjectPublicKeyInfo(publicKey, out var consumed);
            if (consumed != publicKey.Length ||
                !verifier.VerifyData(
                    Encoding.UTF8.GetBytes(BuildCanonical(document with
                    {
                        AllowedExtensions = normalized,
                    })),
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                return Deny();
            }

            return new(
                true,
                BrowserConnectorReasonCodes.Ready,
                new VerifiedBrowserConnectorAuthority(
                    document.Revision,
                    document.ExpiresAt,
                    normalized));
        }
        catch (CryptographicException)
        {
            return Deny();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(signature);
            CryptographicOperations.ZeroMemory(publicKey);
        }
    }

    internal static string BuildCanonical(BrowserConnectorAuthorityDocument document)
    {
        var entries = document.AllowedExtensions
            .OrderBy(entry => entry.Browser)
            .ThenBy(entry => entry.ExtensionId, StringComparer.Ordinal)
            .Select(entry => string.Join(
                ',',
                entry.Browser.ToString().ToLowerInvariant(),
                entry.ExtensionId,
                entry.Origin,
                Base64UrlEncode(Encoding.UTF8.GetBytes(
                    entry.BrowserExecutablePath))));

        return string.Join(
            '|',
            document.SchemaVersion.ToString(CultureInfo.InvariantCulture),
            document.Revision.ToString(CultureInfo.InvariantCulture),
            document.IssuedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            document.ExpiresAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
            document.KeyId,
            string.Join(';', entries));
    }

    internal static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    internal static bool TryDecodeBase64Url(string? value, int exactBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var exactCharacters = checked((exactBytes * 8 + 5) / 6);
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length != exactCharacters ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-' && character != '_'))
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

    private static bool TryValidateEntries(
        IReadOnlyList<BrowserConnectorAuthorityEntry> entries,
        out IReadOnlyList<BrowserConnectorAuthorityEntry> normalized)
    {
        normalized = Array.Empty<BrowserConnectorAuthorityEntry>();
        var origins = new HashSet<string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BrowserConnectorAuthorityEntry>(entries.Count);

        foreach (var entry in entries)
        {
            if (!Enum.IsDefined(entry.Browser) ||
                !ExtensionIdPattern.IsMatch(entry.ExtensionId ?? string.Empty) ||
                !BrowserExecutablePathPolicy.IsValidAuthorityPath(
                    entry.BrowserExecutablePath,
                    entry.Browser))
                return false;

            var exactOrigin = $"chrome-extension://{entry.ExtensionId}/";
            if (!string.Equals(entry.Origin, exactOrigin, StringComparison.Ordinal) ||
                !origins.Add(entry.Origin) ||
                !identities.Add($"{entry.Browser}:{entry.ExtensionId}"))
                return false;

            result.Add(entry with { Origin = exactOrigin });
        }

        normalized = result
            .OrderBy(entry => entry.Browser)
            .ThenBy(entry => entry.ExtensionId, StringComparer.Ordinal)
            .ToArray();
        return true;
    }

    private static bool TryDecodeBase64(string? value, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(value) || value.Length > 1024)
            return false;
        try
        {
            bytes = Convert.FromBase64String(value);
            return bytes.Length is >= 64 and <= 512;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static BrowserConnectorAuthorityValidation Deny() =>
        new(false, BrowserConnectorReasonCodes.AuthorityInvalid, null);
}
