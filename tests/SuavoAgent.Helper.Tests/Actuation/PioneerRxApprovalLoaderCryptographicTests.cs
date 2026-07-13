using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Helper.Actuation;
using Xunit;

namespace SuavoAgent.Helper.Tests.Actuation;

public sealed class PioneerRxApprovalLoaderCryptographicTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PharmacyId = "11111111-1111-1111-1111-111111111111";
    private const string MachineId = "22222222-2222-2222-2222-222222222222";
    private const string ReceiptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string CatalogId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string CommandId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-pioneerrx-loader-" + Guid.NewGuid().ToString("N"));
    private readonly ECDsa _maintenance = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _cloud = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Public_signed_projection_authorizes_without_reading_protected_appsettings_and_refreshes_revocation()
    {
        Directory.CreateDirectory(_root);
        var catalog = Catalog();
        var receipt = Receipt(catalog);
        var authority = Authority(receipt, revoked: false, Now.AddMinutes(-1));
        WriteInstalled(receipt, authority, catalog);

        var approved = Load();

        Assert.True(approved.Approved, approved.Code);
        Assert.Equal("approved", approved.Code);
        Assert.Equal(receipt.ReceiptId, approved.Receipt!.ReceiptId);

        var revoked = Authority(receipt, revoked: true, Now.AddMinutes(1));
        WriteInstalled(receipt, revoked, catalog);
        var denied = Load();

        Assert.False(denied.Approved);
        Assert.Equal("approval_revoked", denied.Code);
    }

    private PioneerRxApprovalLoadResult Load() => PioneerRxProcessApprovalLoader.Load(
        receiptPath: ReceiptPath,
        appSettingsPath: Path.Combine(_root, "deliberately-unreadable-appsettings.json"),
        now: Now,
        authoritativeFingerprint: () => MachineId,
        authorityStatePath: AuthorityPath,
        vendorCatalogPath: CatalogPath,
        highWaterProjectionPath: ProjectionPath,
        verifyExecutable: false,
        trustedCloudKeys: CloudKeys);

    private void WriteInstalled(
        PioneerRxProcessApprovalReceipt receipt,
        PioneerRxApprovalAuthorityState authority,
        PioneerRxVendorIdentityCatalog catalog)
    {
        Write(ReceiptPath, receipt);
        Write(AuthorityPath, authority);
        Write(CatalogPath, catalog);
        var payloadDigest = PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
            CommandId,
            receipt,
            authority,
            catalog);
        var state = new PioneerRxApprovalHighWaterState(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            receipt.ApprovalCounter,
            receipt.ReceiptId,
            CommandId,
            payloadDigest,
            catalog.CatalogId,
            authority.IssuedAtUtc,
            PioneerRxApprovalMaintenanceContract.ComputeAuthorityDigest(authority),
            PioneerRxProcessApprovalContract.IsReceiptRevoked(authority, receipt.ReceiptId),
            Utc(Now));
        var projection = new PioneerRxApprovalHighWaterProjection(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            state,
            MaintenanceKeyId,
            Sign(_maintenance, PioneerRxApprovalMaintenanceContract.Canonical(state)));
        Write(ProjectionPath, projection);
    }

    private PioneerRxVendorIdentityCatalog Catalog()
    {
        var unsigned = new PioneerRxVendorIdentityCatalog(
            PioneerRxVendorIdentityCatalogContract.SchemaVersion,
            CatalogId,
            new[]
            {
                new PioneerRxVendorIdentityEntry(
                    "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
                    "PioneerPharmacy.exe",
                    "PioneerRx",
                    "CN=New Tech Computer Systems",
                    new string('c', 64),
                    new[] { @"C:\Program Files\PioneerRx\" },
                    new[] { "1.2.3.4" }),
            },
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
            new[] { "read" },
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
        PioneerRxProcessApprovalReceipt receipt,
        bool revoked,
        DateTimeOffset issuedAt)
    {
        var unsigned = new PioneerRxApprovalAuthorityState(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            PharmacyId,
            MachineId,
            receipt.ReceiptId,
            receipt.ApprovalCounter,
            revoked ? new[] { receipt.ReceiptId } : Array.Empty<string>(),
            Utc(issuedAt),
            Utc(issuedAt.AddHours(1)),
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
        _root,
        PioneerRxApprovalMaintenanceContract.ReceiptFileName);
    private string AuthorityPath => Path.Combine(
        _root,
        PioneerRxApprovalMaintenanceContract.AuthorityFileName);
    private string CatalogPath => Path.Combine(
        _root,
        PioneerRxVendorIdentityCatalogContract.InstalledFileName);
    private string ProjectionPath => Path.Combine(
        _root,
        PioneerRxApprovalMaintenanceContract.HighWaterProjectionFileName);

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
