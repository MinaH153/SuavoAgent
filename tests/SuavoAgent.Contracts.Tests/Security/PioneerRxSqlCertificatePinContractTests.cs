using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class PioneerRxSqlCertificatePinContractTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "suavo-sql-pin-contract-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ExactDerCertificate_VerifiesByRawCertificateDigest()
    {
        var (certificate, key) = CreateCertificate();
        using (certificate)
        using (key)
        {
            var path = Write("server.cer", certificate.Export(X509ContentType.Cert));
            var digest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(path);

            Assert.True(PioneerRxSqlCertificatePinContract.TryVerifyFile(
                path, digest, DateTimeOffset.UtcNow, out var code));
            Assert.Equal("sql_certificate_pin_valid", code);
        }
    }

    [Fact]
    public void PemEncoding_ResolvesToSameRawDerDigest()
    {
        var (certificate, key) = CreateCertificate();
        using (certificate)
        using (key)
        {
            var derPath = Write("server.cer", certificate.Export(X509ContentType.Cert));
            var pem = certificate.ExportCertificatePem();
            var pemPath = Write("server.pem", Encoding.ASCII.GetBytes(pem));

            Assert.Equal(
                PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(derPath),
                PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(pemPath));
        }
    }

    [Fact]
    public void DigestMismatchAndPrivateKeyContainer_FailClosed()
    {
        var (certificate, key) = CreateCertificate();
        using (certificate)
        using (key)
        {
            var derPath = Write("server.cer", certificate.Export(X509ContentType.Cert));
            Assert.False(PioneerRxSqlCertificatePinContract.TryVerifyFile(
                derPath, new string('0', 64), DateTimeOffset.UtcNow, out var mismatch));
            Assert.Equal("sql_certificate_digest_mismatch", mismatch);

            var pfxPath = Write("server.pfx", certificate.Export(X509ContentType.Pfx));
            var digest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(derPath);
            Assert.False(PioneerRxSqlCertificatePinContract.TryVerifyFile(
                pfxPath, digest, DateTimeOffset.UtcNow, out var privateKey));
            Assert.Equal("sql_certificate_private_key_forbidden", privateKey);
        }
    }

    [Fact]
    public void CaAndClientAuthOnlyCertificates_AreRejected()
    {
        using var caKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var caRequest = new CertificateRequest("CN=CA", caKey, HashAlgorithmName.SHA256);
        caRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var ca = caRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var caPath = Write("ca.cer", ca.Export(X509ContentType.Cert));
        var caDigest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(caPath);
        Assert.False(PioneerRxSqlCertificatePinContract.TryVerifyFile(
            caPath, caDigest, DateTimeOffset.UtcNow, out var caCode));
        Assert.Equal("sql_certificate_ca_forbidden", caCode);

        using var clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var clientRequest = new CertificateRequest("CN=Client", clientKey, HashAlgorithmName.SHA256);
        var clientEku = new OidCollection { new("1.3.6.1.5.5.7.3.2") };
        clientRequest.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(clientEku, true));
        using var client = clientRequest.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var clientPath = Write("client.cer", client.Export(X509ContentType.Cert));
        var clientDigest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(clientPath);
        Assert.False(PioneerRxSqlCertificatePinContract.TryVerifyFile(
            clientPath, clientDigest, DateTimeOffset.UtcNow, out var clientCode));
        Assert.Equal("sql_certificate_server_auth_required", clientCode);
    }

    [Fact]
    public void ExpiredCertificate_IsRejected()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Expired", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-3), DateTimeOffset.UtcNow.AddDays(-2));
        var path = Write("expired.cer", certificate.Export(X509ContentType.Cert));
        var digest = PioneerRxSqlCertificatePinContract.ComputeRawDerSha256(path);

        Assert.False(PioneerRxSqlCertificatePinContract.TryVerifyFile(
            path, digest, DateTimeOffset.UtcNow, out var code));
        Assert.Equal("sql_certificate_time_invalid", code);
    }

    private static (X509Certificate2 Certificate, ECDsa Key) CreateCertificate()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest(
            "CN=PioneerRx SQL Test",
            key,
            HashAlgorithmName.SHA256);
        return (request.CreateSelfSigned(
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1)), key);
    }

    private string Write(string name, byte[] bytes)
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true); } catch { }
    }
}
