using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using SuavoAgent.Setup.Security;
using Xunit;

namespace SuavoAgent.Setup.Tests.Security;

public sealed class SqlServerCertificateEnrollmentTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-sql-cert-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("cer")]
    [InlineData("der")]
    [InlineData("pem")]
    public void Public_certificate_is_canonicalized_atomically_and_digest_is_der_sha256(
        string format)
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-a");
        var source = Path.Combine(_root, "server." + format);
        if (format == "pem")
            File.WriteAllText(source, certificate.ExportCertificatePem());
        else
            File.WriteAllBytes(source, certificate.Export(X509ContentType.Cert));

        var result = SqlServerCertificateEnrollment.Enroll(
            source,
            data,
            isAdministrator: () => true);

        var expected = certificate.Export(X509ContentType.Cert);
        Assert.Equal(
            Path.Combine(data, SqlServerCertificateEnrollment.InstalledFileName),
            result.InstalledPath);
        Assert.Equal(expected, File.ReadAllBytes(result.InstalledPath));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant(),
            result.Digest);
        Assert.Equal(64, result.Digest.Length);
        Assert.Equal(result.Digest.ToLowerInvariant(), result.Digest);
        Assert.Empty(Directory.GetFiles(data, "*.tmp-*"));
    }

    [Fact]
    public void Private_key_container_is_rejected_even_when_disguised_as_cer()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-private");
        var source = Path.Combine(_root, "server.cer");
        File.WriteAllBytes(source, certificate.Export(X509ContentType.Pfx));

        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => true));
        Assert.False(File.Exists(Path.Combine(
            data,
            SqlServerCertificateEnrollment.InstalledFileName)));
    }

    [Fact]
    public void Multi_object_pem_and_non_administrator_are_rejected_without_mutation()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var first = Certificate("sql-first");
        using var second = Certificate("sql-second");
        var source = Path.Combine(_root, "server.pem");
        File.WriteAllText(
            source,
            first.ExportCertificatePem() + second.ExportCertificatePem());

        Assert.Throws<UnauthorizedAccessException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => false));
        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => true));
        Assert.Empty(Directory.GetFiles(data));
    }

    [Fact]
    public void Explicit_reenrollment_replaces_whole_certificate_and_digest_together()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var first = Certificate("sql-first");
        using var second = Certificate("sql-second");
        var firstPath = Path.Combine(_root, "first.cer");
        var secondPath = Path.Combine(_root, "second.cer");
        File.WriteAllBytes(firstPath, first.Export(X509ContentType.Cert));
        File.WriteAllBytes(secondPath, second.Export(X509ContentType.Cert));

        var initial = SqlServerCertificateEnrollment.Enroll(
            firstPath,
            data,
            isAdministrator: () => true);
        var replacement = SqlServerCertificateEnrollment.Enroll(
            secondPath,
            data,
            isAdministrator: () => true);

        Assert.Equal(initial.InstalledPath, replacement.InstalledPath);
        Assert.NotEqual(initial.Digest, replacement.Digest);
        Assert.Equal(
            second.Export(X509ContentType.Cert),
            File.ReadAllBytes(replacement.InstalledPath));
    }

    [Fact]
    public void Upgrade_without_new_selection_revalidates_and_preserves_existing_pin()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-upgrade");
        var source = Path.Combine(_root, "server.cer");
        File.WriteAllBytes(source, certificate.Export(X509ContentType.Cert));
        var initial = SqlServerCertificateEnrollment.Enroll(
            source,
            data,
            isAdministrator: () => true);

        var preserved = SqlServerCertificateEnrollment.EnrollSelectedOrExisting(
            selectedSourcePath: null,
            data,
            isAdministrator: () => true);

        Assert.NotNull(preserved);
        Assert.Equal(initial, preserved);
        Assert.Equal(
            certificate.Export(X509ContentType.Cert),
            File.ReadAllBytes(preserved!.InstalledPath));
        Assert.Empty(Directory.GetFiles(data, "*.tmp-*"));
    }

    [Fact]
    public void Upgrade_without_existing_pin_remains_observe_only()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);

        var result = SqlServerCertificateEnrollment.EnrollSelectedOrExisting(
            selectedSourcePath: null,
            data,
            isAdministrator: () => true);

        Assert.Null(result);
        Assert.Empty(Directory.GetFiles(data));
    }

    [Fact]
    public void Upgrade_never_silently_drops_an_invalid_existing_pin()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        File.WriteAllText(
            Path.Combine(data, SqlServerCertificateEnrollment.InstalledFileName),
            "not-a-certificate");

        Assert.ThrowsAny<CryptographicException>(() =>
            SqlServerCertificateEnrollment.EnrollSelectedOrExisting(
                selectedSourcePath: null,
                data,
                isAdministrator: () => true));
    }

    [Fact]
    public void Upgrade_rejects_reserved_certificate_path_when_it_is_not_a_regular_file()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        Directory.CreateDirectory(Path.Combine(
            data,
            SqlServerCertificateEnrollment.InstalledFileName));

        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.EnrollSelectedOrExisting(
                selectedSourcePath: null,
                data,
                isAdministrator: () => true));
    }

    [Fact]
    public void Ca_certificate_is_rejected_before_installation()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-ca", isCertificateAuthority: true);
        var source = Path.Combine(_root, "ca.cer");
        File.WriteAllBytes(source, certificate.Export(X509ContentType.Cert));

        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => true));
        Assert.Empty(Directory.GetFiles(data));
    }

    [Fact]
    public void Client_auth_only_certificate_is_rejected_before_installation()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-client", clientAuthOnly: true);
        var source = Path.Combine(_root, "client.cer");
        File.WriteAllBytes(source, certificate.Export(X509ContentType.Cert));

        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => true));
        Assert.Empty(Directory.GetFiles(data));
    }

    [Fact]
    public void Expired_certificate_is_rejected_before_installation()
    {
        Directory.CreateDirectory(_root);
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var certificate = Certificate("sql-expired", expired: true);
        var source = Path.Combine(_root, "expired.cer");
        File.WriteAllBytes(source, certificate.Export(X509ContentType.Cert));

        Assert.Throws<InvalidDataException>(() =>
            SqlServerCertificateEnrollment.Enroll(
                source,
                data,
                isAdministrator: () => true));
        Assert.Empty(Directory.GetFiles(data));
    }

    private static X509Certificate2 Certificate(
        string name,
        bool isCertificateAuthority = false,
        bool clientAuthOnly = false,
        bool expired = false)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            $"CN={name}",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        if (isCertificateAuthority)
            request.CertificateExtensions.Add(new X509BasicConstraintsExtension(
                certificateAuthority: true,
                hasPathLengthConstraint: false,
                pathLengthConstraint: 0,
                critical: true));
        if (clientAuthOnly)
        {
            var usages = new OidCollection
            {
                new("1.3.6.1.5.5.7.3.2"),
            };
            request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
                usages,
                critical: true));
        }
        var notBefore = expired
            ? DateTimeOffset.UtcNow.AddDays(-30)
            : DateTimeOffset.UtcNow.AddDays(-1);
        var notAfter = expired
            ? DateTimeOffset.UtcNow.AddDays(-1)
            : DateTimeOffset.UtcNow.AddDays(30);
        return request.CreateSelfSigned(
            notBefore,
            notAfter);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
