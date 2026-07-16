using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests.Maintenance;

public sealed class MaintenanceHostTrustVerifierTests
{
    [Fact]
    public void Signed_release_checksum_binding_accepts_exact_staged_setup_bytes()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();

        var result = fixture.Verify();

        Assert.True(result.IsTrusted);
        Assert.Equal(MaintenanceTrustSource.SignedReleaseChecksums, result.Source);
        Assert.Equal(fixture.HostSha256, result.ExecutableSha256);
    }

    [Fact]
    public void Signed_13_field_ota_binding_accepts_exact_maintenance_hash()
    {
        using var fixture = new TrustFixture();
        fixture.WriteOtaReceipt();

        var result = fixture.Verify();

        Assert.True(result.IsTrusted);
        Assert.Equal(MaintenanceTrustSource.SignedOtaManifest, result.Source);
        Assert.Equal(fixture.HostSha256, result.ExecutableSha256);
    }

    [Fact]
    public void Missing_all_signed_receipts_rejects()
    {
        using var fixture = new TrustFixture();

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Equal("signed_receipt_missing", result.Code);
        Assert.Null(result.ExecutableSha256);
    }

    [Fact]
    public void Release_receipt_rejects_missing_signature()
    {
        using var fixture = new TrustFixture();
        File.WriteAllText(
            Path.Combine(fixture.Root, MaintenanceContract.ReleaseChecksumsFileName),
            $"{fixture.HostSha256}  {MaintenanceContract.SignedSetupArtifactName}\n");

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("release_receipt_incomplete", result.Code);
    }

    [Fact]
    public void Release_receipt_rejects_malformed_or_wrong_key_signature()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();
        var signaturePath = Path.Combine(
            fixture.Root,
            MaintenanceContract.ReleaseChecksumsSignatureFileName);
        File.WriteAllBytes(signaturePath, [1, 2, 3, 4]);

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("release_signature_invalid", result.Code);
    }

    [Fact]
    public void Injected_wrong_public_key_rejects_otherwise_valid_receipt()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var result = MaintenanceHostTrustVerifier.Verify(
            fixture.HostPath,
            Convert.ToBase64String(otherKey.ExportSubjectPublicKeyInfo()),
            _ => AuthenticodePublisherTrust.Trusted(
                AuthenticodePublisherVerifier.ExpectedPublisher));

        Assert.False(result.IsTrusted);
        Assert.Contains("release_signature_invalid", result.Code);
    }

    [Fact]
    public void Rotation_registry_accepts_v2_signed_maintenance_receipt()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();
        using var unrelatedV1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var roots = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [OtaUpdateTrust.LegacyV1KeyId] =
                Convert.ToBase64String(unrelatedV1.ExportSubjectPublicKeyInfo()),
            [OtaUpdateTrust.CurrentV2KeyId] = fixture.PublicKeyDer,
        };

        var result = MaintenanceHostTrustVerifier.Verify(
            fixture.HostPath,
            roots,
            _ => AuthenticodePublisherTrust.Trusted(
                AuthenticodePublisherVerifier.ExpectedPublisher));

        Assert.True(result.IsTrusted, result.Code);
        Assert.Equal(MaintenanceTrustSource.SignedReleaseChecksums, result.Source);
    }

    [Fact]
    public void Release_receipt_rejects_signed_wrong_setup_hash()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt(new string('a', 64));

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("release_setup_hash_mismatch", result.Code);
    }

    [Fact]
    public void Release_receipt_rejects_signed_malformed_checksum_file()
    {
        using var fixture = new TrustFixture();
        fixture.WriteSignedReleaseText("not-a-sha  SuavoSetup.exe\n");

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("release_checksums_malformed", result.Code);
    }

    [Fact]
    public void Ota_receipt_rejects_wrong_field_count_even_when_signature_is_valid()
    {
        using var fixture = new TrustFixture();
        fixture.WriteOtaReceipt(fieldCount: 11);

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("ota_manifest_wrong_field_count", result.Code);
    }

    [Fact]
    public void Ota_receipt_rejects_unsigned_or_malformed_signature()
    {
        using var fixture = new TrustFixture();
        fixture.WriteOtaReceipt();
        File.WriteAllText(
            Path.Combine(fixture.Root, MaintenanceContract.CurrentOtaManifestSignatureFileName),
            "not-p1363-hex");

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("ota_signature_malformed", result.Code);
    }

    [Fact]
    public void Ota_receipt_rejects_signed_wrong_maintenance_hash()
    {
        using var fixture = new TrustFixture();
        fixture.WriteOtaReceipt(maintenanceHash: new string('b', 64));

        var result = fixture.Verify();

        Assert.False(result.IsTrusted);
        Assert.Contains("ota_maintenance_hash_mismatch", result.Code);
    }

    [Fact]
    public void Valid_ota_receipt_can_recover_from_invalid_stale_release_receipt()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt(new string('c', 64));
        fixture.WriteOtaReceipt();

        var result = fixture.Verify();

        Assert.True(result.IsTrusted);
        Assert.Equal(MaintenanceTrustSource.SignedOtaManifest, result.Source);
    }

    [Fact]
    public void Valid_ota_receipt_is_still_accepted_when_release_receipt_is_unreadable()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();
        File.WriteAllBytes(
            Path.Combine(fixture.Root, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            [0x30, 0xFF]);
        fixture.WriteOtaReceipt();

        var result = fixture.Verify();

        Assert.True(result.IsTrusted);
        Assert.Equal(MaintenanceTrustSource.SignedOtaManifest, result.Source);
    }

    [Fact]
    public void Relative_or_wrong_named_host_is_rejected_before_receipt_read()
    {
        using var fixture = new TrustFixture();
        fixture.WriteReleaseReceipt();

        Assert.Equal(
            "maintenance_host_invalid",
            MaintenanceHostTrustVerifier.Verify(
                MaintenanceContract.ExecutableName,
                fixture.PublicKeyDer).Code);
        Assert.Equal(
            "maintenance_host_invalid",
            MaintenanceHostTrustVerifier.Verify(
                Path.Combine(fixture.Root, "renamed.exe"),
                fixture.PublicKeyDer).Code);
    }
}

internal sealed class TrustFixture : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public string Root { get; } = Path.Combine(
        Path.GetTempPath(),
        "suavo-maintenance-trust-" + Guid.NewGuid().ToString("N"));
    public string HostPath => Path.Combine(Root, MaintenanceContract.ExecutableName);
    public string HostSha256 => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(HostPath))).ToLowerInvariant();
    public string PublicKeyDer => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    public TrustFixture()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(HostPath, "signed-maintenance-host-bytes");
    }

    public MaintenanceHostTrustResult Verify() =>
        MaintenanceHostTrustVerifier.Verify(
            HostPath,
            PublicKeyDer,
            _ => AuthenticodePublisherTrust.Trusted(
                AuthenticodePublisherVerifier.ExpectedPublisher));

    public void WriteReleaseReceipt(string? setupHash = null)
    {
        WriteSignedReleaseText(
            $"{new string('1', 64)}  SuavoAgent.Core.exe\n" +
            $"{setupHash ?? HostSha256}  {MaintenanceContract.SignedSetupArtifactName}\n");
    }

    public void WriteSignedReleaseText(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(
            Path.Combine(Root, MaintenanceContract.ReleaseChecksumsFileName),
            bytes);
        File.WriteAllBytes(
            Path.Combine(Root, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            _key.SignData(
                bytes,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence));
    }

    public void WriteOtaReceipt(string? maintenanceHash = null, int fieldCount = 13)
    {
        var hash = new string('2', 64);
        var baseUrl = "https://github.com/MinaH153/SuavoAgent/releases/download/v9.9.9";
        string[] fields =
        [
            $"{baseUrl}/SuavoAgent.Core.exe", hash,
            $"{baseUrl}/SuavoAgent.Broker.exe", hash,
            $"{baseUrl}/SuavoAgent.Helper.exe", hash,
            "9.9.9", "net8.0", "win-x64",
            $"{baseUrl}/SuavoAgent.Watchdog.exe", hash,
            $"{baseUrl}/{MaintenanceContract.SignedSetupArtifactName}", maintenanceHash ?? HostSha256,
        ];
        var canonical = string.Join('|', fields.Take(fieldCount));
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var signature = _key.SignData(
            bytes,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        File.WriteAllBytes(
            Path.Combine(Root, MaintenanceContract.CurrentOtaManifestFileName),
            bytes);
        File.WriteAllText(
            Path.Combine(Root, MaintenanceContract.CurrentOtaManifestSignatureFileName),
            Convert.ToHexString(signature).ToLowerInvariant());
    }

    public void Dispose()
    {
        _key.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}
