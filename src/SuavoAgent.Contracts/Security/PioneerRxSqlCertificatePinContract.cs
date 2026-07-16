using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace SuavoAgent.Contracts.Security;

/// <summary>
/// Exact-certificate pin contract for local PioneerRx SQL Server deployments.
/// The pin is SHA-256 over X.509 RawData (DER), so PEM/DER source encoding cannot
/// change the enrolled identity. Private-key containers are never accepted.
/// </summary>
public static class PioneerRxSqlCertificatePinContract
{
    public const string InstalledFileName = "pioneerrx-sql-server.cer";
    public const int MaximumCertificateFileBytes = 64 * 1024;

    public static bool TryVerifyFile(
        string path,
        string expectedRawDerSha256,
        DateTimeOffset now,
        out string code)
    {
        code = "sql_certificate_pin_invalid";
        if (!IsLowerHex64(expectedRawDerSha256))
        {
            code = "sql_certificate_digest_invalid";
            return false;
        }

        byte[]? fileBytes = null;
        byte[]? rawDer = null;
        byte[]? expected = null;
        byte[]? actual = null;
        try
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                code = "sql_certificate_file_untrusted";
                return false;
            }
            using (var stream = new FileStream(
                       path,
                       FileMode.Open,
                       FileAccess.Read,
                       FileShare.Read,
                       4096,
                       FileOptions.SequentialScan))
            {
                if (stream.Length is <= 0 or > MaximumCertificateFileBytes)
                {
                    code = "sql_certificate_file_size_invalid";
                    return false;
                }
                fileBytes = new byte[checked((int)stream.Length)];
                stream.ReadExactly(fileBytes);
            }

            using var certificate = LoadPublicCertificate(fileBytes);
            if (!TryValidatePublicLeafCertificate(certificate, now, out code)) return false;

            rawDer = certificate.RawData;
            expected = Convert.FromHexString(expectedRawDerSha256);
            actual = SHA256.HashData(rawDer);
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                code = "sql_certificate_digest_mismatch";
                return false;
            }

            code = "sql_certificate_pin_valid";
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                                   CryptographicException or FormatException or ArgumentException)
        {
            code = "sql_certificate_file_unreadable";
            return false;
        }
        finally
        {
            if (fileBytes is not null) CryptographicOperations.ZeroMemory(fileBytes);
            if (rawDer is not null) CryptographicOperations.ZeroMemory(rawDer);
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }

    /// <summary>
    /// Shared enrollment/runtime leaf policy. Setup calls this before persisting bytes and Core
    /// calls it on every activation, so a CA, client-only, weak, or expired certificate can never
    /// be accepted during install and deferred to a later startup failure.
    /// </summary>
    public static bool TryValidatePublicLeafCertificate(
        X509Certificate2 certificate,
        DateTimeOffset now,
        out string code)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        code = "sql_certificate_pin_invalid";
        if (certificate.HasPrivateKey)
        {
            code = "sql_certificate_private_key_forbidden";
            return false;
        }
        var basicConstraints = certificate.Extensions
            .OfType<X509BasicConstraintsExtension>()
            .SingleOrDefault();
        if (basicConstraints?.CertificateAuthority == true)
        {
            code = "sql_certificate_ca_forbidden";
            return false;
        }
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().SingleOrDefault();
        if (eku is not null && !eku.EnhancedKeyUsages.Cast<Oid>().Any(oid =>
                string.Equals(oid.Value, "1.3.6.1.5.5.7.3.1", StringComparison.Ordinal)))
        {
            code = "sql_certificate_server_auth_required";
            return false;
        }
        var keyUsage = certificate.Extensions.OfType<X509KeyUsageExtension>().SingleOrDefault();
        const X509KeyUsageFlags tlsCompatible =
            X509KeyUsageFlags.DigitalSignature |
            X509KeyUsageFlags.KeyEncipherment |
            X509KeyUsageFlags.KeyAgreement;
        if (keyUsage is not null && (keyUsage.KeyUsages & tlsCompatible) == 0)
        {
            code = "sql_certificate_key_usage_incompatible";
            return false;
        }
        using var rsa = certificate.GetRSAPublicKey();
        using var ecdsa = certificate.GetECDsaPublicKey();
        if ((rsa is null && ecdsa is null) ||
            (rsa is not null && rsa.KeySize < 2048) ||
            (ecdsa is not null && ecdsa.KeySize < 256))
        {
            code = "sql_certificate_public_key_invalid";
            return false;
        }
        var notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
        var notAfter = new DateTimeOffset(certificate.NotAfter.ToUniversalTime());
        if (now < notBefore || now >= notAfter)
        {
            code = "sql_certificate_time_invalid";
            return false;
        }
        code = "sql_certificate_leaf_valid";
        return true;
    }

    public static string ComputeRawDerSha256(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            using var certificate = LoadPublicCertificate(bytes);
            if (certificate.HasPrivateKey)
                throw new InvalidDataException("SQL certificate enrollment cannot contain a private key.");
            var rawDer = certificate.RawData;
            try
            {
                var digest = SHA256.HashData(rawDer);
                try
                {
                    return Convert.ToHexString(digest).ToLowerInvariant();
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(digest);
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawDer);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }

    private static X509Certificate2 LoadPublicCertificate(byte[] bytes)
    {
        var text = Encoding.ASCII.GetString(bytes);
        return text.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
            ? X509Certificate2.CreateFromPem(text)
            : new X509Certificate2(bytes);
    }

    private static bool IsLowerHex64(string? value) =>
        value is { Length: 64 } &&
        value.All(ch => ch is >= '0' and <= '9' or >= 'a' and <= 'f');
}
