using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class PioneerRxApprovalInstallCoordinatorTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PharmacyId = "11111111-1111-1111-1111-111111111111";
    private const string MachineId = "22222222-2222-2222-2222-222222222222";
    private const string ReceiptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string CatalogId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string FirstCommand = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string SecondCommand = "dddddddd-dddd-4ddd-8ddd-dddddddddddd";
    private const string ThirdCommand = "eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneerrx-install-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _cloud = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly FakeMaintenanceKeys _maintenance = new();
    private readonly string _certificateDigest;

    public PioneerRxApprovalInstallCoordinatorTests()
    {
        Directory.CreateDirectory(_root);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=pioneerrx-sql",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") },
            critical: true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
            critical: true));
        using var certificate = request.CreateSelfSigned(
            Now.AddDays(-1),
            Now.AddDays(30));
        var der = certificate.Export(X509ContentType.Cert);
        File.WriteAllBytes(CertificatePath, der);
        _certificateDigest = Convert.ToHexString(SHA256.HashData(der)).ToLowerInvariant();
        WriteAppSettings();
    }

    [Fact]
    public void Exact_artifact_redelivery_gets_new_completion_and_newer_revocation_blocks_old_replay()
    {
        var approved = Artifacts();
        var first = Request(FirstCommand, approved);
        WriteRequest(first);

        var firstResult = Install();
        Assert.True(firstResult.Succeeded);
        Assert.Equal(PioneerRxApprovalMaintenanceContract.InstalledOutcome, firstResult.Outcome);
        Assert.False(File.Exists(RequestPath));

        var redelivery = Request(SecondCommand, approved);
        WriteRequest(redelivery);
        var replayResult = Install();
        Assert.True(replayResult.Succeeded);
        var replayCompletion = Read<PioneerRxApprovalInstallCompletion>(CompletionPath);
        Assert.Equal(SecondCommand, replayCompletion.CommandId);
        Assert.Equal(redelivery.PayloadDigest, replayCompletion.PayloadDigest);
        Assert.Equal(SecondCommand, Read<PioneerRxApprovalHighWaterState>(HighWaterPath).CommandId);

        var revoked = Artifacts(
            revoked: true,
            authorityIssuedAt: Now.AddMinutes(2));
        var revocation = Request(ThirdCommand, revoked);
        WriteRequest(revocation);
        var revokedResult = Install();
        Assert.True(revokedResult.Succeeded);
        Assert.Equal(PioneerRxApprovalMaintenanceContract.RevokedOutcome, revokedResult.Outcome);
        Assert.Equal(
            PioneerRxApprovalMaintenanceContract.RevokedOutcome,
            Read<PioneerRxApprovalInstallCompletion>(CompletionPath).Outcome);

        WriteRequest(redelivery);
        var rollback = Install();
        Assert.False(rollback.Succeeded);
        Assert.Equal("approval_authority_rollback", rollback.Code);
        var installedAuthority = Read<PioneerRxApprovalAuthorityState>(AuthorityPath);
        Assert.Contains(ReceiptId, installedAuthority.RevokedReceiptIds);
    }

    [Fact]
    public void Expired_receipt_and_catalog_can_install_only_a_fresh_signed_revocation()
    {
        var expiredRevocation = Artifacts(
            revoked: true,
            receiptExpired: true,
            catalogExpired: true,
            authorityIssuedAt: Now.AddMinutes(-1));
        WriteRequest(Request(FirstCommand, expiredRevocation));

        var result = Install(certificatePath: Path.Combine(_root, "missing.cer"));

        Assert.True(result.Succeeded);
        Assert.Equal(PioneerRxApprovalMaintenanceContract.RevokedOutcome, result.Outcome);
        Assert.Equal(
            PioneerRxApprovalMaintenanceContract.RevokedOutcome,
            Read<PioneerRxApprovalInstallCompletion>(CompletionPath).Outcome);
    }

    [Theory]
    [InlineData((int)PioneerRxApprovalInstallPhase.AuthorityInvalidated)]
    [InlineData((int)PioneerRxApprovalInstallPhase.HighWaterCommitted)]
    [InlineData((int)PioneerRxApprovalInstallPhase.CatalogInstalled)]
    [InlineData((int)PioneerRxApprovalInstallPhase.ReceiptInstalled)]
    [InlineData((int)PioneerRxApprovalInstallPhase.ProjectionInstalled)]
    public void Crash_before_authority_publication_stays_denied_and_same_request_recovers(
        int crashPhaseValue)
    {
        var crashPhase = (PioneerRxApprovalInstallPhase)crashPhaseValue;
        var request = Request(FirstCommand, Artifacts());
        WriteRequest(request);

        Assert.Throws<InjectedCrash>(() => Install(afterPhase: phase =>
        {
            if (phase == crashPhase) throw new InjectedCrash();
        }));
        Assert.False(File.Exists(AuthorityPath));
        Assert.False(File.Exists(CompletionPath));
        Assert.True(File.Exists(RequestPath));

        var recovered = Install();
        Assert.True(recovered.Succeeded);
        Assert.True(File.Exists(AuthorityPath));
        var completion = Read<PioneerRxApprovalInstallCompletion>(CompletionPath);
        Assert.True(PioneerRxApprovalMaintenanceContract.CompletionMatches(
            completion,
            request.CommandId,
            request.PayloadDigest,
            request.Receipt,
            request.Authority));
    }

    private PioneerRxApprovalInstallExecutionResult Install(
        string? certificatePath = null,
        Action<PioneerRxApprovalInstallPhase>? afterPhase = null) =>
        PioneerRxApprovalInstallCoordinator.Install(
            RequestPath,
            AppSettingsPath,
            certificatePath ?? CertificatePath,
            AuthorityDirectory,
            Now,
            CloudKeys,
            _maintenance,
            protectDirectory: _ => { },
            protectMetadata: _ => { },
            validateMetadata: File.Exists,
            protectHighWater: _ => { },
            validateHighWater: File.Exists,
            validateAppSettings: File.Exists,
            validateCertificate: File.Exists,
            afterPhase: afterPhase);

    private (PioneerRxProcessApprovalReceipt Receipt,
        PioneerRxApprovalAuthorityState Authority,
        PioneerRxVendorIdentityCatalog Catalog) Artifacts(
        bool revoked = false,
        bool receiptExpired = false,
        bool catalogExpired = false,
        DateTimeOffset? authorityIssuedAt = null)
    {
        var unsignedCatalog = new PioneerRxVendorIdentityCatalog(
            PioneerRxVendorIdentityCatalogContract.SchemaVersion,
            CatalogId,
            new[]
            {
                new PioneerRxVendorIdentityEntry(
                    "ffffffff-ffff-4fff-8fff-ffffffffffff",
                    "PioneerPharmacy.exe",
                    "PioneerRx",
                    "CN=New Tech Computer Systems",
                    new string('c', 64),
                    new[] { @"C:\Program Files\PioneerRx\" },
                    new[] { "1.2.3.4" }),
            },
            Utc(Now.AddDays(-2)),
            Utc(catalogExpired ? Now.AddHours(-1) : Now.AddDays(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        var catalog = unsignedCatalog with
        {
            CloudSignature = SignCloud(
                PioneerRxVendorIdentityCatalogContract.Canonical(unsignedCatalog)),
        };
        var unsignedReceipt = new PioneerRxProcessApprovalReceipt(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            ReceiptId,
            PharmacyId,
            MachineId,
            _maintenance.KeyId,
            _maintenance.PublicKeySpki,
            "PioneerPharmacy.exe",
            @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
            new string('b', 64),
            "CN=New Tech Computer Systems",
            new string('c', 64),
            "PioneerRx",
            "1.2.3.4",
            CatalogId,
            _certificateDigest,
            "S-1-5-21-1-2-3-1001",
            new string('e', 64),
            new string('f', 64),
            7,
            new[] { "read" },
            Utc(Now.AddDays(-2)),
            Utc(receiptExpired ? Now.AddHours(-1) : Now.AddDays(1)),
            null,
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty,
            string.Empty);
        var receiptCanonical = PioneerRxProcessApprovalContract.Canonical(unsignedReceipt);
        var receipt = unsignedReceipt with
        {
            CloudCoApprovalSignature = SignCloud(receiptCanonical),
            MaintenanceSignature = _maintenance.SignCanonical(receiptCanonical),
        };
        var issuedAt = authorityIssuedAt ?? Now.AddMinutes(-1);
        var unsignedAuthority = new PioneerRxApprovalAuthorityState(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            PharmacyId,
            MachineId,
            ReceiptId,
            receipt.ApprovalCounter,
            revoked ? new[] { ReceiptId } : Array.Empty<string>(),
            Utc(issuedAt),
            Utc(issuedAt.AddHours(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        var authority = unsignedAuthority with
        {
            CloudSignature = SignCloud(
                PioneerRxProcessApprovalContract.Canonical(unsignedAuthority)),
        };
        return (receipt, authority, catalog);
    }

    private static PioneerRxApprovalInstallRequest Request(
        string commandId,
        (PioneerRxProcessApprovalReceipt Receipt,
            PioneerRxApprovalAuthorityState Authority,
            PioneerRxVendorIdentityCatalog Catalog) artifacts) => new(
        PioneerRxApprovalMaintenanceContract.SchemaVersion,
        PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
        commandId,
        PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
            commandId,
            artifacts.Receipt,
            artifacts.Authority,
            artifacts.Catalog),
        artifacts.Receipt,
        artifacts.Authority,
        artifacts.Catalog,
        Utc(Now));

    private void WriteRequest(PioneerRxApprovalInstallRequest request) => File.WriteAllBytes(
        RequestPath,
        JsonSerializer.SerializeToUtf8Bytes(
            request,
            PioneerRxApprovalMaintenanceContract.JsonOptions));

    private void WriteAppSettings() => File.WriteAllText(
        AppSettingsPath,
        JsonSerializer.Serialize(new
        {
            Agent = new
            {
                PharmacyId,
                MachineFingerprint = MachineId,
                MaintenanceAttestationKeyId = _maintenance.KeyId,
                SqlServerCertificateSha256 = _certificateDigest,
            },
        }));

    private T Read<T>(string path) => JsonSerializer.Deserialize<T>(
        File.ReadAllBytes(path),
        PioneerRxApprovalMaintenanceContract.JsonOptions)!;

    private IReadOnlyDictionary<string, string> CloudKeys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RemoteCommandTrust.CommandV1KeyId] = Convert.ToBase64String(
                _cloud.ExportSubjectPublicKeyInfo()),
        };

    private string SignCloud(string canonical) =>
        PioneerRxProcessApprovalContract.Base64UrlEncode(_cloud.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private string RequestPath => Path.Combine(_root, "request.json");
    private string AppSettingsPath => Path.Combine(_root, "appsettings.json");
    private string CertificatePath => Path.Combine(_root, "server.cer");
    private string AuthorityDirectory => Path.Combine(_root, "authority");
    private string AuthorityPath => Path.Combine(
        AuthorityDirectory,
        PioneerRxApprovalMaintenanceContract.AuthorityFileName);
    private string HighWaterPath => Path.Combine(
        AuthorityDirectory,
        PioneerRxApprovalMaintenanceContract.HighWaterFileName);
    private string CompletionPath => Path.Combine(
        AuthorityDirectory,
        PioneerRxApprovalMaintenanceContract.CompletionFileName);

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _cloud.Dispose();
        _maintenance.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class InjectedCrash : Exception;

    private sealed class FakeMaintenanceKeys : IMaintenanceAttestationKeyProvider, IDisposable
    {
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        internal string PublicKeySpki => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());
        internal string KeyId => Convert.ToHexString(
            SHA256.HashData(_key.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
        private DeviceKeyEnrollment Enrollment => new("ES256", KeyId, PublicKeySpki);

        internal string SignCanonical(string canonical) =>
            PioneerRxProcessApprovalContract.Base64UrlEncode(_key.SignData(
                Encoding.UTF8.GetBytes(canonical),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public MaintenanceKeyRegistration OpenOrCreate(string authoritativeFingerprint) =>
            new(Enrollment, new string('p', 86));

        public MaintenanceKeyRegistration OpenExisting(string authoritativeFingerprint) =>
            new(Enrollment, new string('p', 86));

        public DeviceMaintenanceSignature Sign(
            string authoritativeFingerprint,
            string expectedKeyId,
            ReadOnlyMemory<byte> canonicalBytes)
        {
            Assert.Equal(KeyId, expectedKeyId);
            return new(
                Enrollment,
                _key.SignData(
                    canonicalBytes.Span,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        }

        public void DestroyForUninstall(string authoritativeFingerprint, string expectedKeyId) =>
            throw new NotSupportedException();

        public void Dispose() => _key.Dispose();
    }
}
