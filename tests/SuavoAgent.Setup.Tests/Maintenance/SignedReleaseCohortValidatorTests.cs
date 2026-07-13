using System.Security.Cryptography;
using System.Text;
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
                (_, _) => throw new InvalidOperationException("must not read artifacts"),
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
    public void Field_receipt_artifact_hash_must_match_signed_setup_entry()
    {
        using var fixture = SignedCohortFixture.Create(
            artifactShaOverride: new string('f', 64));

        var result = fixture.Validate();

        Assert.False(result.IsValid);
        Assert.Equal("field_release_receipt_binding_invalid", result.Code);
    }

    private sealed class SignedCohortFixture : IDisposable
    {
        private readonly ECDsa _signer;
        private readonly ECDsa _verifier;
        private readonly string _releaseTag;
        private readonly string _rollbackTag;
        private readonly string? _artifactShaOverride;
        public string Directory { get; }

        private SignedCohortFixture(
            string? maintenanceBytes,
            string releaseTag,
            string rollbackTag,
            string? artifactShaOverride)
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                "suavo-signed-cohort-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            _signer = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            _verifier = ECDsa.Create();
            _releaseTag = releaseTag;
            _rollbackTag = rollbackTag;
            _artifactShaOverride = artifactShaOverride;
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
            string? artifactShaOverride = null) =>
            new(maintenanceBytes, releaseTag, rollbackTag, artifactShaOverride);

        public SignedReleaseCohortValidation Validate() =>
            SignedReleaseCohortValidator.Validate(
                Directory,
                _releaseTag,
                (data, signature) => _verifier.VerifyData(
                    data,
                    signature,
                    HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence),
                _ => AuthenticodePublisherTrust.Trusted(
                    AuthenticodePublisherVerifier.ExpectedPublisher));

        public void RewriteReceipt(Func<string[], string[]> mutate)
        {
            var path = Path.Combine(Directory, MaintenanceContract.ReleaseChecksumsFileName);
            var lines = File.ReadAllLines(path);
            WriteSignedReceipt(mutate(lines));
        }

        private void WriteSignedReceipt(string[]? overrideLines = null)
        {
            var numericVersion = _releaseTag.TrimStart('v', 'V').Split('-', 2)[0];
            var expectedSetupHash = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes("signed-setup"))).ToLowerInvariant();
            var artifactSha = _artifactShaOverride ?? expectedSetupHash;
            File.WriteAllText(
                Path.Combine(Directory, MaintenanceContract.FieldReleaseReceiptFileName),
                $$"""
                {
                  "releaseTag": "{{_releaseTag}}",
                  "version": "{{numericVersion}}",
                  "sourceCommit": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                  "artifact": "SuavoSetup.exe",
                  "artifactSha256": "{{artifactSha}}",
                  "authenticode": "required-valid",
                  "checksumSignature": "checksums.sha256.sig",
                  "manifestSignature": "update-manifest-{{_releaseTag}}.sig",
                  "track2QueenValidation": "do-not-run-against-older-tags",
                  "rollbackArtifact": {
                    "releaseTag": "{{_rollbackTag}}",
                    "artifact": "SuavoSetup.exe",
                    "artifactSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                    "releaseUrl": "https://github.com/MinaH153/SuavoAgent/releases/download/{{_rollbackTag}}/SuavoSetup.exe"
                  }
                }
                """);
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
                .Append(new string('d', 64) + "  update-manifest-" + _releaseTag + ".txt")
                .Append(new string('e', 64) + "  update-manifest-" + _releaseTag + ".sig")
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
