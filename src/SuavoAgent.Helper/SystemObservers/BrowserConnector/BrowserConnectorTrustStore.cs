using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Helper.SystemObservers.BrowserConnector;

internal interface IBrowserConnectorTrustStoreSource
{
    bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument);
}

internal readonly record struct BrowserConnectorTrustStoreLoadResult(
    bool Valid,
    string ReasonCode,
    VerifiedBrowserConnectorAuthority? Authority);

internal static class BrowserConnectorTrustStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    public static BrowserConnectorTrustStoreLoadResult LoadProduction(DateTimeOffset now) =>
        OperatingSystem.IsWindows()
            ? Load(new WindowsBrowserConnectorTrustStoreSource(), now)
            : Deny();

    internal static BrowserConnectorTrustStoreLoadResult Load(
        IBrowserConnectorTrustStoreSource source,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(source);
        byte[] authorityBytes = Array.Empty<byte>();
        byte[] rootsBytes = Array.Empty<byte>();
        try
        {
            if (!source.TryRead(out authorityBytes, out rootsBytes) ||
                !HasExactAuthorityShape(authorityBytes) ||
                !HasExactRootsShape(rootsBytes))
                return Deny();

            var authority = JsonSerializer.Deserialize<BrowserConnectorAuthorityDocument>(
                authorityBytes,
                JsonOptions);
            var roots = JsonSerializer.Deserialize<BrowserConnectorTrustedRootsDocument>(
                rootsBytes,
                JsonOptions);
            if (authority is null ||
                roots is null ||
                roots.SchemaVersion != 1 ||
                roots.TrustedKeys is null or { Count: 0 } ||
                roots.TrustedKeys.Count > 8)
                return Deny();

            var trustedKeys = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var root in roots.TrustedKeys)
            {
                if (string.IsNullOrWhiteSpace(root.KeyId) ||
                    root.KeyId.Length > 64 ||
                    string.IsNullOrWhiteSpace(root.PublicKeySpkiBase64) ||
                    root.PublicKeySpkiBase64.Length > 1_024 ||
                    !trustedKeys.TryAdd(root.KeyId, root.PublicKeySpkiBase64))
                    return Deny();
            }

            var validation = BrowserConnectorAuthorityVerifier.Verify(authority, trustedKeys, now);
            return validation.Valid && validation.Authority is not null
                ? new(true, BrowserConnectorReasonCodes.Ready, validation.Authority)
                : Deny();
        }
        catch (Exception ex) when (ex is
            JsonException or
            NotSupportedException or
            InvalidOperationException or
            ArgumentException)
        {
            return Deny();
        }
        finally
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorityBytes);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(rootsBytes);
        }
    }

    private static bool HasExactAuthorityShape(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!HasExactProperties(
                    root,
                    "schemaVersion", "revision", "issuedAt", "expiresAt", "keyId",
                    "allowedExtensions", "signature") ||
                root.GetProperty("allowedExtensions").ValueKind != JsonValueKind.Array)
                return false;
            return root.GetProperty("allowedExtensions")
                .EnumerateArray()
                .All(entry => HasExactProperties(
                    entry,
                    "browser",
                    "extensionId",
                    "origin",
                    "browserExecutablePath"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactRootsShape(byte[] json)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            if (!HasExactProperties(root, "schemaVersion", "trustedKeys") ||
                root.GetProperty("trustedKeys").ValueKind != JsonValueKind.Array)
                return false;
            return root.GetProperty("trustedKeys")
                .EnumerateArray()
                .All(entry => HasExactProperties(entry, "keyId", "publicKeySpkiBase64"));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool HasExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return false;
        var allowed = expected.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name) || !seen.Add(property.Name))
                return false;
        }
        return seen.Count == allowed.Count;
    }

    private static BrowserConnectorTrustStoreLoadResult Deny() =>
        new(false, BrowserConnectorReasonCodes.AuthorityInvalid, null);

    private sealed record BrowserConnectorTrustedRootsDocument(
        int SchemaVersion,
        IReadOnlyList<BrowserConnectorTrustedRoot> TrustedKeys);

    private sealed record BrowserConnectorTrustedRoot(
        string KeyId,
        string PublicKeySpkiBase64);
}

/// <summary>
/// Dedicated ProgramData trust store. The root of trust is accepted only when
/// its directory and both exact files are local, non-reparse, protected by a
/// non-inherited DACL, and writable only by SYSTEM/Administrators.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsBrowserConnectorTrustStoreSource : IBrowserConnectorTrustStoreSource
{
    private const int MaximumAuthorityBytes = 32 * 1_024;
    private const int MaximumRootsBytes = 16 * 1_024;
    private static readonly SecurityIdentifier SystemSid =
        new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    public bool TryRead(out byte[] authorityDocument, out byte[] trustedRootsDocument)
    {
        authorityDocument = Array.Empty<byte>();
        trustedRootsDocument = Array.Empty<byte>();
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "SuavoAgent",
                "browser-connector-trust");
            var authorityPath = Path.Combine(root, "authority.json");
            var rootsPath = Path.Combine(root, "trusted-roots.json");
            if (!ValidateDirectory(root) ||
                !ValidateFile(authorityPath) ||
                !ValidateFile(rootsPath) ||
                !TryReadExact(authorityPath, MaximumAuthorityBytes, out authorityDocument) ||
                !TryReadExact(rootsPath, MaximumRootsBytes, out trustedRootsDocument) ||
                !ValidateDirectory(root) ||
                !ValidateFile(authorityPath) ||
                !ValidateFile(rootsPath))
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorityDocument);
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(trustedRootsDocument);
                authorityDocument = Array.Empty<byte>();
                trustedRootsDocument = Array.Empty<byte>();
                return false;
            }
            return true;
        }
        catch
        {
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(authorityDocument);
            System.Security.Cryptography.CryptographicOperations.ZeroMemory(trustedRootsDocument);
            authorityDocument = Array.Empty<byte>();
            trustedRootsDocument = Array.Empty<byte>();
            return false;
        }
    }

    private static bool ValidateDirectory(string path)
    {
        if (!Directory.Exists(path) ||
            Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal) ||
            new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
        {
            if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                return false;
        }
        return ValidateAcl(new DirectoryInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static bool ValidateFile(string path)
    {
        if (!File.Exists(path) ||
            !Path.IsPathFullyQualified(path) ||
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            return false;
        return ValidateAcl(new FileInfo(path).GetAccessControl(
            AccessControlSections.Access | AccessControlSections.Owner));
    }

    private static bool ValidateAcl(FileSystemSecurity security)
    {
        if (!security.AreAccessRulesProtected)
            return false;
        var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        if (owner is null || (!owner.Equals(SystemSid) && !owner.Equals(AdministratorsSid)))
            return false;

        var systemRights = (FileSystemRights)0;
        var administratorRights = (FileSystemRights)0;
        foreach (var rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier))
                     .OfType<FileSystemAccessRule>())
        {
            if (rule.IsInherited)
                return false;
            if (rule.AccessControlType != AccessControlType.Allow)
                continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(SystemSid))
            {
                systemRights |= rule.FileSystemRights;
                continue;
            }
            if (sid.Equals(AdministratorsSid))
            {
                administratorRights |= rule.FileSystemRights;
                continue;
            }
            if ((rule.FileSystemRights & WriteCapableRights) != 0)
                return false;
        }
        return (systemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl &&
               (administratorRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
    }

    private static bool TryReadExact(string path, int maximumBytes, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1_024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            return false;
        bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return stream.Position == stream.Length;
    }

    private const FileSystemRights WriteCapableRights =
        FileSystemRights.WriteData |
        FileSystemRights.AppendData |
        FileSystemRights.WriteExtendedAttributes |
        FileSystemRights.WriteAttributes |
        FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions |
        FileSystemRights.TakeOwnership;
}
