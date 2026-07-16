using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using SuavoAgent.Contracts.Security;

namespace SuavoAgent.Setup.Security;

internal sealed record SqlServerCertificateEnrollmentResult(
    string InstalledPath,
    string Digest);

internal sealed record SqlServerCertificateValidationResult(
    string SourcePath,
    string Digest);

/// <summary>
/// Enrolls only the administrator-selected public SQL Server certificate. The
/// canonical DER certificate is pinned by digest; private-key containers and
/// ambiguous/multi-object PEM input are rejected.
/// </summary>
internal static class SqlServerCertificateEnrollment
{
    internal const string InstalledFileName = PioneerRxSqlCertificatePinContract.InstalledFileName;
    private const int MaxSourceBytes = 1024 * 1024;
    private static readonly UTF8Encoding FatalUtf8 = new(false, true);
    private sealed record ParsedCertificate(string SourcePath, byte[] Der, string Digest);

    internal static SqlServerCertificateValidationResult ValidateSource(string sourcePath)
    {
        var parsed = ParseSource(sourcePath);
        return new(parsed.SourcePath, parsed.Digest);
    }

    /// <summary>
    /// Enrolls an explicitly selected certificate, or revalidates and preserves
    /// the already-enrolled certificate during an upgrade when the operator did
    /// not select a replacement. An existing malformed/expired pin is a hard
    /// failure; silently dropping it would downgrade TLS identity verification.
    /// </summary>
    internal static SqlServerCertificateEnrollmentResult? EnrollSelectedOrExisting(
        string? selectedSourcePath,
        string dataDirectory,
        Func<bool>? isAdministrator = null)
    {
        var data = new DirectoryInfo(Path.GetFullPath(dataDirectory));
        if (!data.Exists || data.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The protected ProgramData directory is unavailable.");
        var existing = Path.Combine(data.FullName, InstalledFileName);
        var nameComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var existingEntryPresent = data.EnumerateFileSystemInfos()
            .Any(entry => string.Equals(entry.Name, InstalledFileName, nameComparison));
        var source = !string.IsNullOrWhiteSpace(selectedSourcePath)
            ? selectedSourcePath
            : existingEntryPresent
                ? existing
                : null;
        if (source is null)
            return null;

        return Enroll(source, data.FullName, isAdministrator);
    }

    internal static SqlServerCertificateEnrollmentResult Enroll(
        string sourcePath,
        string dataDirectory,
        Func<bool>? isAdministrator = null)
    {
        isAdministrator ??= IsAdministrator;
        if (!isAdministrator())
            throw new UnauthorizedAccessException(
                "Administrator authority is required to enroll the SQL Server certificate.");
        var parsed = ParseSource(sourcePath);
        var canonicalDer = parsed.Der;
        var digest = parsed.Digest;

        var data = new DirectoryInfo(Path.GetFullPath(dataDirectory));
        if (!data.Exists || data.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The protected ProgramData directory is unavailable.");
        var destination = Path.Combine(data.FullName, InstalledFileName);
        if (File.Exists(destination) &&
            File.GetAttributes(destination).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The SQL Server certificate destination is untrusted.");
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var output = new FileStream(
                       temporary,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       16 * 1024,
                       FileOptions.WriteThrough))
            {
                output.Write(canonicalDer);
                output.Flush(flushToDisk: true);
            }
            ProtectAndVerify(temporary);
            File.Move(temporary, destination, overwrite: true);
            ProtectAndVerify(destination);
            var persisted = ReadBounded(destination, MaxSourceBytes);
            if (!persisted.AsSpan().SequenceEqual(canonicalDer) ||
                !string.Equals(
                    Convert.ToHexString(SHA256.HashData(persisted)).ToLowerInvariant(),
                    digest,
                    StringComparison.Ordinal))
                throw new IOException("SQL Server certificate read-back proof failed.");
            return new(destination, digest);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    private static ParsedCertificate ParseSource(string sourcePath)
    {
        var source = RequireRegularFile(sourcePath, MaxSourceBytes);
        var extension = Path.GetExtension(source.FullName).ToLowerInvariant();
        if (extension is not (".cer" or ".der" or ".pem"))
            throw new InvalidDataException(
                "Select a public .cer, .der, or .pem certificate. Private-key containers are forbidden.");
        var sourceBytes = ReadBounded(source.FullName, MaxSourceBytes);
        var der = extension == ".pem"
            ? DecodeExactCertificatePem(sourceBytes)
            : sourceBytes;
        using var certificate = new X509Certificate2(der);
        if (certificate.HasPrivateKey ||
            !certificate.RawData.AsSpan().SequenceEqual(der) ||
            !PioneerRxSqlCertificatePinContract.TryValidatePublicLeafCertificate(
                certificate,
                DateTimeOffset.UtcNow,
                out _))
            throw new InvalidDataException(
                "The selected file is not one canonical public TLS certificate.");
        var canonicalDer = certificate.RawData;
        return new(
            source.FullName,
            canonicalDer,
            Convert.ToHexString(SHA256.HashData(canonicalDer)).ToLowerInvariant());
    }

    private static FileInfo RequireRegularFile(string path, int maximumBytes)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new InvalidDataException("The certificate path must be absolute.");
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length <= 0 || file.Length > maximumBytes ||
            file.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("The certificate source is unavailable or untrusted.");
        return file;
    }

    private static byte[] ReadBounded(string path, int maximumBytes)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            16 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException("Certificate file exceeds its safe bound.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static byte[] DecodeExactCertificatePem(byte[] bytes)
    {
        var text = FatalUtf8.GetString(bytes);
        if (!PemEncoding.TryFind(text, out var fields) ||
            !text[fields.Label].SequenceEqual("CERTIFICATE"))
            throw new InvalidDataException("PEM input must contain one CERTIFICATE object.");
        var prefix = text.AsSpan(0, fields.Location.Start.GetOffset(text.Length));
        var end = fields.Location.End.GetOffset(text.Length);
        var suffix = text.AsSpan(end);
        if (!prefix.IsWhiteSpace() || !suffix.IsWhiteSpace())
            throw new InvalidDataException("PEM input contains additional data or objects.");
        return Convert.FromBase64String(text[fields.Base64Data]);
    }

    private static void ProtectAndVerify(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        ProtectAndVerifyWindows(path);
    }

    [SupportedOSPlatform("windows")]
    private static void ProtectAndVerifyWindows(string path)
    {
        var policy = new HandleBoundAclPolicy(
            HandleBoundAcl.SystemSid,
        [
            new(HandleBoundAcl.SystemSid, FileSystemRights.FullControl),
            new(HandleBoundAcl.AdministratorsSid, FileSystemRights.FullControl),
            new(CoreServiceIdentity.ServiceSid, FileSystemRights.ReadAndExecute),
        ]);
        new HandleBoundAcl().ApplyBatch([new(path, IsDirectory: false, policy)]);
    }

    private static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return true;
        return IsWindowsAdministrator();
    }

    [SupportedOSPlatform("windows")]
    private static bool IsWindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
