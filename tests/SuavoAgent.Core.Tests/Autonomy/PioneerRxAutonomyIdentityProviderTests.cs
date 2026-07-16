using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Core.Autonomy;
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Autonomy;

public sealed class PioneerRxAutonomyIdentityProviderTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 12, 12, 0, 0, TimeSpan.Zero);
    private const string PharmacyId = "11111111-1111-4111-8111-111111111111";
    private const string MachineId = "22222222-2222-4222-8222-222222222222";
    private const string ReceiptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string CatalogId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string CommandId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "suavo-autonomy-identity-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _maintenance = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _cloud = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Current_RequiresSignedActiveApprovalHighWaterAndExactLiveExecutableIdentity()
    {
        Directory.CreateDirectory(_root);
        var catalog = Catalog();
        var receipt = Receipt(catalog);
        var authority = Authority(receipt);
        WriteInstalled(receipt, authority, catalog);
        var liveVerified = false;
        var provider = Provider((path, sha, version, signer) =>
        {
            liveVerified = true;
            return path == receipt.CanonicalExecutablePath &&
                sha == receipt.ExecutableSha256 &&
                version == receipt.FileVersion &&
                signer == receipt.SignerCertificateSha256;
        });

        var identity = provider.Current(Now);

        Assert.True(liveVerified);
        Assert.NotNull(identity);
        Assert.Equal("1.2.3.4", identity!.FileVersion);
        Assert.Equal(receipt.ExecutableSha256, identity.ExecutableSha256);
        Assert.Equal(receipt.SignerCertificateSha256, identity.SignerCertificateSha256);
        Assert.Equal(receipt.ApprovalCounter, identity.ApprovalCounter);

        Assert.Null(Provider((_, _, _, _) => false).Current(Now));

        var staleHighWater = HighWater(receipt, authority, catalog) with
        {
            ApprovalCounter = receipt.ApprovalCounter - 1,
        };
        Write(HighWaterPath, staleHighWater);
        Assert.Null(provider.Current(Now));
    }

    private PioneerRxAutonomyIdentityProvider Provider(
        Func<string, string, string, string, bool> verifyLive) => new(
            new AgentOptions
            {
                PharmacyId = PharmacyId,
                MachineFingerprint = MachineId,
                MaintenanceAttestationKeyId = MaintenanceKeyId,
            },
            _root,
            CloudKeys,
            verifyLive,
            _ => true,
            _ => true);

    private void WriteInstalled(
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog catalog)
    {
        Write(ReceiptPath, receipt);
        Write(AuthorityPath, authority);
        Write(CatalogPath, catalog);
        Write(HighWaterPath, HighWater(receipt, authority, catalog));
    }

    private PioneerRxApprovalHighWaterState HighWater(
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog catalog) => new(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            receipt.ApprovalCounter,
            receipt.ReceiptId,
            CommandId,
            PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
                CommandId, receipt, authority, catalog),
            catalog.CatalogId,
            authority.IssuedAtUtc,
            PioneerRxApprovalMaintenanceContract.ComputeAuthorityDigest(authority),
            false,
            Utc(Now));

    private PioneerRxVendorIdentityCatalog Catalog()
    {
        var unsigned = new PioneerRxVendorIdentityCatalog(
            PioneerRxVendorIdentityCatalogContract.SchemaVersion,
            CatalogId,
            [new PioneerRxVendorIdentityEntry(
                "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                "PioneerPharmacy.exe",
                "PioneerRx",
                "CN=New Tech Computer Systems",
                new string('c', 64),
                [@"C:\Program Files\PioneerRx\"],
                ["1.2.3.4"])],
            Utc(Now.AddMinutes(-2)),
            Utc(Now.AddDays(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        return unsigned with
        {
            CloudSignature = Sign(
                _cloud,
                PioneerRxVendorIdentityCatalogContract.Canonical(unsigned)),
        };
    }

    private PioneerRxProcessApprovalReceipt Receipt(PioneerRxVendorIdentityCatalog catalog)
    {
        var unsigned = new PioneerRxProcessApprovalReceipt(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            ReceiptId,
            PharmacyId,
            MachineId,
            MaintenanceKeyId,
            Convert.ToBase64String(_maintenance.ExportSubjectPublicKeyInfo()),
            "PioneerPharmacy.exe",
            @"C:\Program Files\PioneerRx\PioneerPharmacy.exe",
            new string('b', 64),
            "CN=New Tech Computer Systems",
            new string('c', 64),
            "PioneerRx",
            "1.2.3.4",
            catalog.CatalogId,
            new string('d', 64),
            "S-1-5-21-1-2-3-1001",
            new string('e', 64),
            new string('f', 64),
            7,
            ["read"],
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddDays(1)),
            null,
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty,
            string.Empty);
        var canonical = PioneerRxProcessApprovalContract.Canonical(unsigned);
        return unsigned with
        {
            CloudCoApprovalSignature = Sign(_cloud, canonical),
            MaintenanceSignature = Sign(_maintenance, canonical),
        };
    }

    private PioneerRxApprovalAuthorityState Authority(
        PioneerRxProcessApprovalReceipt receipt)
    {
        var unsigned = new PioneerRxApprovalAuthorityState(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            PharmacyId,
            MachineId,
            receipt.ReceiptId,
            receipt.ApprovalCounter,
            [],
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddHours(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        return unsigned with
        {
            CloudSignature = Sign(
                _cloud,
                PioneerRxProcessApprovalContract.Canonical(unsigned)),
        };
    }

    private void Write<T>(string path, T value) => File.WriteAllBytes(
        path,
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            PioneerRxApprovalMaintenanceContract.JsonOptions));

    private string ReceiptPath => Path.Combine(
        _root, PioneerRxApprovalMaintenanceContract.ReceiptFileName);
    private string AuthorityPath => Path.Combine(
        _root, PioneerRxApprovalMaintenanceContract.AuthorityFileName);
    private string CatalogPath => Path.Combine(
        _root, PioneerRxVendorIdentityCatalogContract.InstalledFileName);
    private string HighWaterPath => Path.Combine(
        _root, PioneerRxApprovalMaintenanceContract.HighWaterFileName);

    private string MaintenanceKeyId => Convert.ToHexString(
        SHA256.HashData(_maintenance.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private IReadOnlyDictionary<string, string> CloudKeys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RemoteCommandTrust.CommandV1KeyId] = Convert.ToBase64String(
                _cloud.ExportSubjectPublicKeyInfo()),
        };

    private static string Sign(ECDsa key, string canonical) =>
        PioneerRxProcessApprovalContract.Base64UrlEncode(key.SignData(
            Encoding.UTF8.GetBytes(canonical),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString(
            PioneerRxProcessApprovalContract.UtcTimestampFormat,
            System.Globalization.CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _maintenance.Dispose();
        _cloud.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
