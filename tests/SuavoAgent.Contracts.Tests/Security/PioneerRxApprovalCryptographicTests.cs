using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Contracts.Security;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Security;

public sealed class PioneerRxApprovalCryptographicTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string PharmacyId = "11111111-1111-1111-1111-111111111111";
    private const string MachineId = "22222222-2222-2222-2222-222222222222";
    private const string ReceiptId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
    private const string CatalogId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
    private const string CommandId = "cccccccc-cccc-4ccc-8ccc-cccccccccccc";
    private const string Path = @"C:\Program Files\PioneerRx\PioneerPharmacy.exe";
    private const string Root = @"C:\Program Files\PioneerRx\";
    private readonly ECDsa _maintenance = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _cloud = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    [Fact]
    public void Expired_authentic_receipt_and_catalog_are_accepted_only_for_deny_installation()
    {
        var artifacts = Artifacts(receiptExpired: true, catalogExpired: true, revoked: true);

        Assert.False(PioneerRxProcessApprovalContract.TryValidate(
            artifacts.Receipt,
            artifacts.Catalog,
            PharmacyId,
            MachineId,
            MaintenanceKeyId,
            Now,
            CloudKeys,
            out var activeCode));
        Assert.Equal("approval_expired_or_time_invalid", activeCode);

        Assert.True(PioneerRxProcessApprovalContract.TryValidateHistoricalForRevocation(
            artifacts.Receipt,
            artifacts.Catalog,
            PharmacyId,
            MachineId,
            MaintenanceKeyId,
            Now,
            CloudKeys,
            out var historicalCode));
        Assert.Equal("approved", historicalCode);
    }

    [Fact]
    public void Fresh_signed_revocation_is_installable_but_never_runtime_authority()
    {
        var artifacts = Artifacts(revoked: true);

        Assert.True(PioneerRxProcessApprovalContract.TryValidateAuthorityDocument(
            artifacts.Authority,
            PharmacyId,
            MachineId,
            Now,
            CloudKeys,
            out var documentCode));
        Assert.Equal("authority_document_valid", documentCode);

        Assert.False(PioneerRxProcessApprovalContract.TryValidateAuthorityState(
            artifacts.Authority,
            artifacts.Receipt,
            PharmacyId,
            MachineId,
            Now,
            CloudKeys,
            out var runtimeCode));
        Assert.Equal("approval_revoked", runtimeCode);
    }

    [Fact]
    public void Maintenance_signed_high_water_projection_binds_every_installed_artifact()
    {
        var artifacts = Artifacts(revoked: true);
        var payloadDigest = PioneerRxApprovalMaintenanceContract.ComputePayloadDigest(
            CommandId,
            artifacts.Receipt,
            artifacts.Authority,
            artifacts.Catalog);
        var state = new PioneerRxApprovalHighWaterState(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            PioneerRxApprovalMaintenanceContract.CurrentProtocolEpoch,
            artifacts.Receipt.ApprovalCounter,
            artifacts.Receipt.ReceiptId,
            CommandId,
            payloadDigest,
            artifacts.Catalog.CatalogId,
            artifacts.Authority.IssuedAtUtc,
            PioneerRxApprovalMaintenanceContract.ComputeAuthorityDigest(artifacts.Authority),
            true,
            Utc(Now));
        var projection = new PioneerRxApprovalHighWaterProjection(
            PioneerRxApprovalMaintenanceContract.SchemaVersion,
            state,
            MaintenanceKeyId,
            SignMaintenance(PioneerRxApprovalMaintenanceContract.Canonical(state)));

        Assert.True(PioneerRxApprovalMaintenanceContract.TryValidateProjection(
            projection,
            artifacts.Receipt,
            artifacts.Authority,
            artifacts.Catalog,
            Now,
            out var code));
        Assert.Equal("approval_revoked", code);

        var tampered = projection with
        {
            State = state with { Revoked = false },
        };
        Assert.False(PioneerRxApprovalMaintenanceContract.TryValidateProjection(
            tampered,
            artifacts.Receipt,
            artifacts.Authority,
            artifacts.Catalog,
            Now,
            out _));
    }

    [Fact]
    public void Signed_vendor_catalog_allows_only_exact_root_signer_and_version_evidence()
    {
        var artifacts = Artifacts();

        Assert.True(PioneerRxVendorIdentityCatalogContract.TryValidate(
            artifacts.Catalog,
            Now,
            CloudKeys,
            out _));
        Assert.True(PioneerRxVendorIdentityCatalogContract.TryMatchEvidence(
            artifacts.Catalog,
            "PioneerPharmacy.exe",
            "PioneerRx",
            "CN=New Tech Computer Systems",
            new string('c', 64),
            Path,
            "1.2.3.4",
            out _));
        Assert.False(PioneerRxVendorIdentityCatalogContract.TryMatchEvidence(
            artifacts.Catalog,
            "PioneerPharmacy.exe",
            "PioneerRx",
            "CN=New Tech Computer Systems",
            new string('c', 64),
            @"C:\Users\Public\PioneerPharmacy.exe",
            "1.2.3.4",
            out _));
        Assert.False(PioneerRxVendorIdentityCatalogContract.TryMatchEvidence(
            artifacts.Catalog,
            "PioneerPharmacy.exe",
            "PioneerRx",
            "CN=New Tech Computer Systems",
            new string('c', 64),
            Path,
            "9.9.9.9",
            out _));
    }

    private (PioneerRxProcessApprovalReceipt Receipt,
        PioneerRxApprovalAuthorityState Authority,
        PioneerRxVendorIdentityCatalog Catalog) Artifacts(
        bool receiptExpired = false,
        bool catalogExpired = false,
        bool revoked = false)
    {
        var entry = new PioneerRxVendorIdentityEntry(
            "dddddddd-dddd-4ddd-8ddd-dddddddddddd",
            "PioneerPharmacy.exe",
            "PioneerRx",
            "CN=New Tech Computer Systems",
            new string('c', 64),
            new[] { Root },
            new[] { "1.2.3.4" });
        var unsignedCatalog = new PioneerRxVendorIdentityCatalog(
            PioneerRxVendorIdentityCatalogContract.SchemaVersion,
            CatalogId,
            new[] { entry },
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
            MaintenanceKeyId,
            Convert.ToBase64String(_maintenance.ExportSubjectPublicKeyInfo()),
            "PioneerPharmacy.exe",
            Path,
            new string('b', 64),
            "CN=New Tech Computer Systems",
            new string('c', 64),
            "PioneerRx",
            "1.2.3.4",
            CatalogId,
            new string('d', 64),
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
        var canonicalReceipt = PioneerRxProcessApprovalContract.Canonical(unsignedReceipt);
        var receipt = unsignedReceipt with
        {
            CloudCoApprovalSignature = SignCloud(canonicalReceipt),
            MaintenanceSignature = SignMaintenance(canonicalReceipt),
        };

        var unsignedAuthority = new PioneerRxApprovalAuthorityState(
            PioneerRxProcessApprovalContract.CurrentSchemaVersion,
            PharmacyId,
            MachineId,
            ReceiptId,
            receipt.ApprovalCounter,
            revoked ? new[] { ReceiptId } : Array.Empty<string>(),
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddHours(1)),
            RemoteCommandTrust.CommandV1KeyId,
            string.Empty);
        var authority = unsignedAuthority with
        {
            CloudSignature = SignCloud(
                PioneerRxProcessApprovalContract.Canonical(unsignedAuthority)),
        };
        return (receipt, authority, catalog);
    }

    private string MaintenanceKeyId => Convert.ToHexString(
        SHA256.HashData(_maintenance.ExportSubjectPublicKeyInfo())).ToLowerInvariant();

    private IReadOnlyDictionary<string, string> CloudKeys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [RemoteCommandTrust.CommandV1KeyId] = Convert.ToBase64String(
                _cloud.ExportSubjectPublicKeyInfo()),
        };

    private string SignMaintenance(string canonical) => Sign(_maintenance, canonical);
    private string SignCloud(string canonical) => Sign(_cloud, canonical);

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
    }
}
