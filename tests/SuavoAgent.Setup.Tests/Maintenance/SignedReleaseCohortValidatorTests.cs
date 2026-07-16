using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Maintenance;
using SuavoAgent.Diagnostics.Maintenance;
using SuavoAgent.Setup.Maintenance;
using Xunit;

namespace SuavoAgent.Setup.Tests.Maintenance;

public sealed class SignedReleaseCohortValidatorTests
{
    [Fact]
    public void Exact_five_member_signed_release_cohort_is_accepted()
    {
        using var fixture = SignedCohortFixture.Create();

        var result = fixture.Validate();

        Assert.True(result.IsValid, result.Code);
        Assert.NotNull(result.Evidence);
        Assert.Equal("v9.9.9", result.Evidence.ReleaseTag);
        Assert.Equal(new string('a', 40), result.Evidence.SourceCommit);
        Assert.Equal(OtaUpdateTrust.LegacyV1KeyId, result.Evidence.OtaSigningKeyId);
        Assert.All(
            new[]
            {
                result.Evidence.MsiArtifactSha256,
                result.Evidence.ReleaseReceiptSha256,
                result.Evidence.ChecksumsSha256,
                result.Evidence.ChecksumsSignatureSha256,
                result.Evidence.MaintenanceHostSha256,
            },
            digest => Assert.Matches("^[a-f0-9]{64}$", digest));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(
                    Encoding.UTF8.GetBytes("signed-msi-installer")))
                .ToLowerInvariant(),
            result.Evidence.MsiArtifactSha256);
        Assert.Equal(
            new[]
            {
                "SuavoAgent.Broker.exe",
                "SuavoAgent.Core.exe",
                "SuavoAgent.Helper.exe",
                "SuavoAgent.Watchdog.exe",
                MaintenanceContract.SignedSetupArtifactName,
            },
            result.Evidence.InstalledCohort.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Signed_release_without_exact_msi_digest_is_rejected()
    {
        using var fixture = SignedCohortFixture.Create();
        var msiName = Release1ConvergenceContract.ReleaseMsiArtifactName("v9.9.9");
        fixture.RewriteReceipt(lines => lines
            .Where(line => !line.EndsWith("  " + msiName, StringComparison.Ordinal))
            .ToArray());

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("release_entry_missing:" + msiName, result.Code);
    }

    [Theory]
    [InlineData("SuavoAgent.Core.exe")]
    [InlineData("SuavoAgent.Broker.exe")]
    [InlineData("SuavoAgent.Helper.exe")]
    [InlineData("SuavoAgent.Watchdog.exe")]
    [InlineData(MaintenanceContract.ExecutableName)]
    public void Missing_or_tampered_member_is_rejected(string fileName)
    {
        using var fixture = SignedCohortFixture.Create();
        File.AppendAllText(Path.Combine(fixture.Directory, fileName), "tampered");

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("cohort_hash_mismatch:" + fileName, result.Code);
    }

    [Fact]
    public void Maintenance_must_match_signed_setup_artifact_not_an_unsigned_alias()
    {
        using var fixture = SignedCohortFixture.Create(
            maintenanceBytes: "different-maintenance");

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(
            "cohort_hash_mismatch:" + MaintenanceContract.ExecutableName,
            result.Code);
    }

    [Fact]
    public void Extra_executable_is_rejected_even_when_other_members_are_valid()
    {
        using var fixture = SignedCohortFixture.Create();
        File.WriteAllText(Path.Combine(fixture.Directory, "surprise.exe"), "unsigned");

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("cohort_executable_set_not_exact", result.Code);
    }

    [Fact]
    public void Invalid_receipt_signature_is_rejected_before_hash_acceptance()
    {
        using var fixture = SignedCohortFixture.Create();
        File.WriteAllBytes(
            Path.Combine(fixture.Directory, MaintenanceContract.ReleaseChecksumsSignatureFileName),
            [1, 2, 3, 4]);

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("release_signature_invalid", result.Code);
    }

    [Fact]
    public void Reparse_point_staging_root_is_rejected_before_any_artifact_read()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "suavo-release-reparse-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var link = Path.Combine(root, "stage");
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       IOException or
                                       PlatformNotSupportedException)
            {
                return;
            }

            var result = SignedReleaseCohortValidator.Validate(
                link,
                "v9.9.9",
                (_, _, _) => throw new InvalidOperationException("must not read artifacts"),
                _ => throw new InvalidOperationException("must not inspect publisher"));

            Assert.False(result.IsValid);
            Assert.Equal("staging_directory_reparse_point", result.Code);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(root, true); } catch { }
        }
    }

    [Fact]
    public void Nested_reparse_point_is_rejected_even_when_signed_members_are_valid()
    {
        using var fixture = SignedCohortFixture.Create();
        var target = Path.Combine(
            Path.GetTempPath(),
            "suavo-release-link-target-" + Guid.NewGuid().ToString("N"));
        var link = Path.Combine(fixture.Directory, "unexpected-link");
        Directory.CreateDirectory(target);
        try
        {
            try { Directory.CreateSymbolicLink(link, target); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or
                                       IOException or
                                       PlatformNotSupportedException)
            {
                return;
            }

            var result = fixture.Validate();

            Assert.False(result.IsValid);
            Assert.Equal("cohort_contains_reparse_point", result.Code);
        }
        finally
        {
            try { if (Directory.Exists(link)) Directory.Delete(link); } catch { }
            try { Directory.Delete(target, true); } catch { }
        }
    }

    [Fact]
    public void Duplicate_artifact_name_in_signed_receipt_is_rejected()
    {
        using var fixture = SignedCohortFixture.Create();
        fixture.RewriteReceipt(lines => [.. lines, lines[0]]);

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("release_checksums_unsafe_or_duplicate_name", result.Code);
    }

    [Fact]
    public void Prerelease_cohort_accepts_lower_stable_rollback()
    {
        using var fixture = SignedCohortFixture.Create(
            releaseTag: "v9.9.9-rc.1",
            rollbackTag: "v9.9.8");

        var result = fixture.Validate();

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Prerelease_cohort_rejects_same_core_stable_as_rollback()
    {
        using var fixture = SignedCohortFixture.Create(
            releaseTag: "v9.9.9-rc.1",
            rollbackTag: "v9.9.9");

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("field_release_rollback_invalid", result.Code);
    }

    [Fact]
    public void Release_1_accepts_legacy_zip_bound_to_exact_rollback_tag()
    {
        using var fixture = SignedCohortFixture.Create(
            rollbackTag: "v3.92.1",
            rollbackArtifact: "suavoagent-v3.92.1-win-x64.zip");

        var result = fixture.Validate();

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Legacy_zip_for_another_tag_is_rejected()
    {
        using var fixture = SignedCohortFixture.Create(
            rollbackTag: "v3.92.1",
            rollbackArtifact: "suavoagent-v3.92.0-win-x64.zip");

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("field_release_rollback_invalid", result.Code);
    }

    [Fact]
    public void Field_receipt_artifact_hash_must_match_canonical_installer_entry()
    {
        using var fixture = SignedCohortFixture.Create(
            artifactShaOverride: new string('f', 64));

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("field_release_receipt_binding_invalid", result.Code);
    }

    [Fact]
    public void Field_receipt_rejects_legacy_setup_alias_as_release_installer()
    {
        using var fixture = SignedCohortFixture.Create(
            artifactName: MaintenanceContract.SignedSetupArtifactName);

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("field_release_receipt_binding_invalid", result.Code);
    }

    [Theory]
    [InlineData(OtaUpdateTrust.LegacyV1KeyId)]
    [InlineData(OtaUpdateTrust.CurrentV2KeyId)]
    public void Field_receipt_accepts_only_the_root_that_signed_checksums(
        string signingKeyId)
    {
        using var fixture = SignedCohortFixture.Create(
            actualSigningKeyId: signingKeyId,
            receiptSigningKeyId: signingKeyId);

        var result = fixture.Validate();

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Field_receipt_cannot_declare_v2_for_v1_signed_checksums()
    {
        using var fixture = SignedCohortFixture.Create(
            actualSigningKeyId: OtaUpdateTrust.LegacyV1KeyId,
            receiptSigningKeyId: OtaUpdateTrust.CurrentV2KeyId);

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("release_signature_invalid", result.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("attacker-root")]
    public void Field_receipt_missing_or_unknown_signing_root_is_rejected(
        string? receiptSigningKeyId)
    {
        using var fixture = SignedCohortFixture.Create(
            receiptSigningKeyId: receiptSigningKeyId);

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal(
            receiptSigningKeyId is null
                ? "field_release_receipt_schema_invalid"
                : "field_release_receipt_binding_invalid",
            result.Code);
    }

    private sealed class SignedCohortFixture : IDisposable
    {
        private readonly ECDsa _signer;
        private readonly ECDsa _verifier;
        private readonly string _releaseTag;
        private readonly string _rollbackTag;
        private readonly string _rollbackArtifact;
        private readonly string? _artifactShaOverride;
        private readonly string _artifactName;
        private readonly string _actualSigningKeyId;
        private readonly string? _receiptSigningKeyId;
        public string Directory { get; }

        private SignedCohortFixture(
            string? maintenanceBytes,
            string releaseTag,
            string rollbackTag,
            string rollbackArtifact,
            string? artifactShaOverride,
            string artifactName,
            string actualSigningKeyId,
            string? receiptSigningKeyId)
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "suavo-signed-cohort-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _verifier = ECDsa.Create();
            _releaseTag = releaseTag;
            _rollbackTag = rollbackTag;
            _rollbackArtifact = rollbackArtifact;
            _artifactShaOverride = artifactShaOverride;
            _artifactName = artifactName;
            _actualSigningKeyId = actualSigningKeyId;
            _receiptSigningKeyId = receiptSigningKeyId;
            _verifier.ImportSubjectPublicKeyInfo(
                _signer.ExportSubjectPublicKeyInfo(),
                out _);

            foreach (var fileName in BinaryDownloader.InstalledCohort)
            {
                var content = string.Equals(
                    fileName,
                    MaintenanceContract.ExecutableName,
                    StringComparison.OrdinalIgnoreCase)
                    ? maintenanceBytes ?? "signed-setup"
                    : "signed-" + fileName;
                File.WriteAllText(Path.Combine(Directory, fileName), content);
            }
            WriteSignedReceipt();
        }

        public static SignedCohortFixture Create(
            string? maintenanceBytes = null,
            string releaseTag = "v9.9.9",
            string rollbackTag = "v9.9.8",
            string rollbackArtifact = MaintenanceContract.SignedSetupArtifactName,
            string? artifactShaOverride = null,
            string artifactName = MaintenanceContract.CanonicalInstallerArtifactName,
            string actualSigningKeyId = OtaUpdateTrust.LegacyV1KeyId,
            string? receiptSigningKeyId = OtaUpdateTrust.LegacyV1KeyId) =>
            new(
                maintenanceBytes,
                releaseTag,
                rollbackTag,
                rollbackArtifact,
                artifactShaOverride,
                artifactName,
                actualSigningKeyId,
                receiptSigningKeyId);

        public SignedReleaseCohortValidation Validate() =>
            SignedReleaseCohortValidator.Validate(
                Directory,
                _releaseTag,
                (keyId, data, signature) =>
                    string.Equals(keyId, _actualSigningKeyId, StringComparison.Ordinal) &&
                    _verifier.VerifyData(
                        data,
                        signature,
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.Rfc3279DerSequence),
                _ => AuthenticodePublisherTrust.Trusted(
                    AuthenticodePublisherVerifier.ExpectedPublisher),
                (keyId, canonical, signature) =>
                    string.Equals(keyId, _actualSigningKeyId, StringComparison.Ordinal) &&
                    _verifier.VerifyData(
                        Encoding.ASCII.GetBytes(canonical),
                        Convert.FromHexString(signature),
                        HashAlgorithmName.SHA256,
                        DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        public void RewriteReceipt(Func<string[], string[]> mutate)
        {
            var path = Path.Combine(Directory, MaintenanceContract.ReleaseChecksumsFileName);
            var lines = File.ReadAllLines(path);
            WriteSignedReceipt(mutate(lines));
        }

        private void WriteSignedReceipt(string[]? overrideLines = null)
        {
            var numericVersion = _releaseTag.TrimStart('v', 'V').Split('-', 2)[0];
            var expectedInstallerHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("signed-burn-installer"))).ToLowerInvariant();
            var expectedMsiHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("signed-msi-installer"))).ToLowerInvariant();
            var artifactSha = _artifactShaOverride ?? expectedInstallerHash;
            var rollbackArtifact = new
            {
                releaseTag = _rollbackTag,
                artifact = _rollbackArtifact,
                artifactSha256 = new string('c', 64),
                releaseUrl =
                    $"https://github.com/MinaH153/SuavoAgent/releases/download/{_rollbackTag}/{_rollbackArtifact}",
            };
            object fieldReceipt = _receiptSigningKeyId is null
                ? new
                {
                    releaseTag = _releaseTag,
                    version = numericVersion,
                    sourceCommit = new string('a', 40),
                    artifact = _artifactName,
                    artifactSha256 = artifactSha,
                    authenticode = "required-valid",
                    checksumSignature = MaintenanceContract.ReleaseChecksumsSignatureFileName,
                    manifestSignature = $"update-manifest-{_releaseTag}.sig",
                    track2QueenValidation = "do-not-run-against-older-tags",
                    rollbackArtifact,
                }
                : new
                {
                    releaseTag = _releaseTag,
                    version = numericVersion,
                    sourceCommit = new string('a', 40),
                    artifact = _artifactName,
                    artifactSha256 = artifactSha,
                    authenticode = "required-valid",
                    checksumSignature = MaintenanceContract.ReleaseChecksumsSignatureFileName,
                    manifestSignature = $"update-manifest-{_releaseTag}.sig",
                    otaSigningKeyId = _receiptSigningKeyId,
                    track2QueenValidation = "do-not-run-against-older-tags",
                    rollbackArtifact,
                };
            File.WriteAllText(
                Path.Combine(Directory, MaintenanceContract.FieldReleaseReceiptFileName),
                JsonSerializer.Serialize(
                    fieldReceipt,
                    new JsonSerializerOptions { WriteIndented = true }) + "\n");
            var baseUrl =
                $"https://github.com/MinaH153/SuavoAgent/releases/download/{_releaseTag}";
            string Hash(string fileName) => Convert.ToHexString(SHA256.HashData(
                    File.ReadAllBytes(Path.Combine(Directory, fileName))))
                .ToLowerInvariant();
            var manifestCanonical = string.Join('|',
                $"{baseUrl}/SuavoAgent.Core.exe",
                Hash("SuavoAgent.Core.exe"),
                $"{baseUrl}/SuavoAgent.Broker.exe",
                Hash("SuavoAgent.Broker.exe"),
                $"{baseUrl}/SuavoAgent.Helper.exe",
                Hash("SuavoAgent.Helper.exe"),
                numericVersion,
                "net8.0",
                "win-x64",
                $"{baseUrl}/SuavoAgent.Watchdog.exe",
                Hash("SuavoAgent.Watchdog.exe"));
            var manifestName = $"update-manifest-{_releaseTag}.txt";
            var manifestSignatureName = $"update-manifest-{_releaseTag}.sig";
            File.WriteAllText(
                Path.Combine(Directory, manifestName),
                manifestCanonical,
                new UTF8Encoding(false));
            var manifestSignature = Convert.ToHexString(_signer.SignData(
                    Encoding.ASCII.GetBytes(manifestCanonical),
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                .ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(Directory, manifestSignatureName),
                manifestSignature,
                new UTF8Encoding(false));
            var lines = overrideLines ??
                BinaryDownloader.InstalledCohort.Select(fileName =>
                {
                    var signedName = string.Equals(
                        fileName,
                        MaintenanceContract.ExecutableName,
                        StringComparison.OrdinalIgnoreCase)
                        ? MaintenanceContract.SignedSetupArtifactName
                        : fileName;
                    var signedPath = string.Equals(
                        fileName,
                        MaintenanceContract.ExecutableName,
                        StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(Directory, MaintenanceContract.ExecutableName)
                        : Path.Combine(Directory, fileName);
                    // A mismatched-maintenance fixture models a Setup hash that
                    // differs from the staged renamed Maintenance bytes.
                    var bytes = signedName == MaintenanceContract.SignedSetupArtifactName
                        ? Encoding.UTF8.GetBytes("signed-setup")
                        : File.ReadAllBytes(signedPath);
                    var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                    return hash + "  " + signedName;
                }).Append(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(
                        Directory,
                        MaintenanceContract.FieldReleaseReceiptFileName)))).ToLowerInvariant() +
                    "  " + MaintenanceContract.FieldReleaseReceiptFileName)
                .Append(
                    expectedInstallerHash + "  " +
                    MaintenanceContract.CanonicalInstallerArtifactName)
                .Append(
                    expectedMsiHash + "  " +
                    Release1ConvergenceContract.ReleaseMsiArtifactName(_releaseTag))
                .Append(Hash(manifestName) + "  " + manifestName)
                .Append(Hash(manifestSignatureName) + "  " + manifestSignatureName)
                .ToArray();
            var receiptBytes = Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n");
            File.WriteAllBytes(
                Path.Combine(Directory, MaintenanceContract.ReleaseChecksumsFileName),
                receiptBytes);
            File.WriteAllBytes(
                Path.Combine(Directory, MaintenanceContract.ReleaseChecksumsSignatureFileName),
                _signer.SignData(
                    receiptBytes,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence));
        }

        public void Dispose()
        {
            _signer.Dispose();
            _verifier.Dispose();
            try { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
            catch { }
        }
    }
}
