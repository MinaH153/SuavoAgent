using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Reasoning;

public sealed class BrainCohortContractTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ModelKeyId = "brain-model-test-v1";
    private const string NativeKeyId = "brain-native-test-v1";
    private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private IReadOnlyDictionary<string, string> Keys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
        };

    [Fact]
    public void Valid_exact_manifest_round_trips_publisher_signature()
    {
        var manifest = Signed();

        var result = BrainCohortContract.Validate(manifest, Keys, Now);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(BrainCohortContract.BuildCanonical(manifest), result.Canonical);
        Assert.StartsWith(
            $"suavo-brain-cohort-v3|3|{manifest.CohortId}|test-model|https://assets.example/model.gguf|",
            result.Canonical);
    }

    [Fact]
    public void Shared_cross_language_golden_fixture_matches_canonical_signature_and_rejections()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "brain-cohort-contract-v3.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var manifest = JsonSerializer.Deserialize<BrainCohortPublisherManifest>(
            root.GetProperty("manifest").GetRawText())!;
        var fixtureKeys = root.GetProperty("testOnlyTrustedKeys");
        var modelKey = fixtureKeys.GetProperty("model");
        var nativeKey = fixtureKeys.GetProperty("native");
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [modelKey.GetProperty("keyId").GetString()!] =
                modelKey.GetProperty("publicKeySpki").GetString()!,
            [nativeKey.GetProperty("keyId").GetString()!] =
                nativeKey.GetProperty("publicKeySpki").GetString()!,
        };
        var now = DateTimeOffset.Parse(root.GetProperty("nowUtc").GetString()!);

        Assert.Equal(
            root.GetProperty("canonical").GetString(),
            BrainCohortContract.BuildCanonical(manifest));
        Assert.Equal(
            root.GetProperty("modelCanonical").GetString(),
            BrainCohortContract.BuildModelCanonical(manifest));
        Assert.Equal(
            root.GetProperty("nativeCanonical").GetString(),
            BrainCohortContract.BuildNativeCanonical(manifest));
        Assert.Equal(manifest.CohortId, BrainCohortContract.ComputeCohortId(manifest));
        var valid = BrainCohortContract.Validate(manifest, keys, now);
        Assert.True(valid.IsValid, valid.Code);

        Assert.Equal(
            "publisher_native_package_kind_invalid",
            BrainCohortContract.Validate(
                manifest with { NativePackageKind = "zip-flat-v1" },
                keys,
                now).Code);
    }

    [Fact]
    public void Legacy_v1_fixture_remains_development_verify_only()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "brain-cohort-contract-v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var manifest = JsonSerializer.Deserialize<BrainCohortPublisherManifest>(
            root.GetProperty("manifest").GetRawText())!;
        var key = root.GetProperty("testOnlyTrustedKey");
        var keys = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [key.GetProperty("keyId").GetString()!] =
                key.GetProperty("publicKeySpki").GetString()!,
        };
        var now = DateTimeOffset.Parse(root.GetProperty("nowUtc").GetString()!);

        Assert.Equal(
            root.GetProperty("canonical").GetString(),
            BrainCohortContract.BuildCanonical(manifest));
        Assert.True(BrainCohortContract.ValidateLegacyDevelopmentManifest(
            manifest,
            keys,
            now).IsValid);
        Assert.Equal(
            "publisher_schema_mismatch",
            BrainCohortContract.Validate(manifest, keys, now).Code);
    }

    [Fact]
    public void Retired_schema_v2_is_verify_only_and_cannot_alias_schema_v3_identity()
    {
        var current = Signed();
        var retired = SignWithFreshIdentity(Unsigned() with
        {
            SchemaVersion = BrainCohortContract.RetiredInstalledSchemaVersion,
            NativePackageKind = string.Empty,
        });

        Assert.Equal(
            "publisher_schema_mismatch",
            BrainCohortContract.Validate(retired, Keys, Now).Code);
        Assert.True(BrainCohortContract.ValidateRetiredSchemaV2InstalledCohort(
            retired,
            Keys,
            Now).IsValid);
        Assert.NotEqual(current.CohortId, retired.CohortId);
        Assert.Equal(
            "publisher_retired_package_kind_forbidden",
            BrainCohortContract.ValidateRetiredSchemaV2InstalledCohort(
                retired with
                {
                    NativePackageKind =
                        BrainNativePackageExtractor.OfficialNuGetPackageKind,
                },
                Keys,
                Now).Code);
    }

    [Fact]
    public void Production_registry_refuses_update_key_reuse_until_split_roots_are_pinned()
    {
        Assert.Empty(BrainCohortContract.ProductionTrustedPublisherKeys);
        Assert.NotEqual("update-v1", BrainCohortContract.ProductionModelKeyId);
        Assert.NotEqual("update-v1", BrainCohortContract.ProductionNativeKeyId);
        Assert.NotEqual(
            BrainCohortContract.ProductionModelKeyId,
            BrainCohortContract.ProductionNativeKeyId);
    }

    [Fact]
    public void Legacy_production_manifest_is_fail_closed_after_authority_split()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "brain-cohort-production-manifest-v1.json");
        var manifest = JsonSerializer.Deserialize<BrainCohortPublisherManifest>(
            File.ReadAllText(path))!;

        var validation = BrainCohortContract.Validate(
            manifest,
            BrainCohortContract.ProductionTrustedPublisherKeys,
            DateTimeOffset.Parse("2026-07-11T16:00:00.000Z"));
        Assert.False(validation.IsValid);
        Assert.Equal("publisher_schema_mismatch", validation.Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong")]
    [InlineData("malformed")]
    public void Missing_malformed_or_forged_signature_is_rejected(string mode)
    {
        var valid = Signed();
        var candidate = mode switch
        {
            "missing" => valid with { ModelSignature = "" },
            "wrong" => valid with { ModelSignature = new string('0', 128) },
            _ => valid with { ModelSignature = "abcd" },
        };

        var result = BrainCohortContract.Validate(candidate, Keys, Now);

        Assert.False(result.IsValid);
        Assert.StartsWith("publisher_model_signature_", result.Code);
    }

    [Fact]
    public void Unknown_key_is_rejected_even_when_signature_bytes_are_well_formed()
    {
        var result = BrainCohortContract.Validate(
            Signed() with { ModelKeyId = "brain-model-unknown-v1" },
            Keys,
            Now);

        Assert.False(result.IsValid);
        Assert.Equal("publisher_model_key_unknown", result.Code);
    }

    [Fact]
    public void Model_and_native_roles_cannot_reuse_one_public_key()
    {
        var reused = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
        };

        var result = BrainCohortContract.Validate(Signed(), reused, Now);

        Assert.Equal("publisher_role_key_reuse_forbidden", result.Code);
    }

    [Fact]
    public void Schema_v3_rejects_ambiguous_legacy_authority_fields()
    {
        var result = BrainCohortContract.Validate(
            Signed() with { KeyId = "update-v1", Signature = new string('0', 128) },
            Keys,
            Now);

        Assert.Equal("publisher_legacy_authority_forbidden", result.Code);
    }

    [Theory]
    [InlineData(-1, "publisher_manifest_expired")]
    [InlineData(2, "publisher_validity_window_invalid")]
    public void Expired_or_future_issued_manifest_is_rejected(
        int mode,
        string expectedCode)
    {
        var unsigned = Unsigned();
        unsigned = mode < 0
            ? unsigned with
            {
                IssuedAtUtc = Utc(Now.AddDays(-2)),
                ExpiresAtUtc = Utc(Now.AddDays(-1)),
            }
            : unsigned with
            {
                IssuedAtUtc = Utc(Now.AddHours(mode)),
                ExpiresAtUtc = Utc(Now.AddHours(mode + 1)),
            };
        var candidate = SignWithFreshIdentity(unsigned);

        var result = BrainCohortContract.Validate(candidate, Keys, Now);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.Code);
    }

    [Fact]
    public void Any_signed_metadata_tamper_breaks_cohort_or_signature_binding()
    {
        var valid = Signed();
        var candidates = new[]
        {
            valid with { ModelUrl = "https://assets.example/other.gguf" },
            valid with { ModelSha256 = new string('c', 64) },
            valid with { ModelSizeBytes = valid.ModelSizeBytes + 1 },
            valid with { NativeLibsUrl = "https://assets.example/other.zip" },
            valid with { NativeLibsSha256 = new string('d', 64) },
            valid with { NativeLibsSizeBytes = valid.NativeLibsSizeBytes + 1 },
            valid with { NativePackageKind = "nuget-other-v1" },
            valid with { ContextSize = 8192 },
            valid with { MaxOutputTokens = 1024 },
            valid with { ModelId = "other-model" },
            valid with { ExpiresAtUtc = Utc(Now.AddDays(3)) },
        };

        foreach (var candidate in candidates)
        {
            var result = BrainCohortContract.Validate(candidate, Keys, Now);
            Assert.False(result.IsValid);
        }
    }

    [Fact]
    public void Cohort_identity_changes_for_every_runtime_relevant_field()
    {
        var baseline = Unsigned();
        var baselineId = BrainCohortContract.ComputeCohortId(baseline);
        var variants = new[]
        {
            baseline with { ModelId = "model-b" },
            baseline with { ModelUrl = "https://assets.example/b.gguf" },
            baseline with { ModelSha256 = new string('c', 64) },
            baseline with { ModelSizeBytes = baseline.ModelSizeBytes + 1 },
            baseline with { NativeLibsUrl = "https://assets.example/b.zip" },
            baseline with { NativeLibsSha256 = new string('d', 64) },
            baseline with { NativeLibsSizeBytes = baseline.NativeLibsSizeBytes + 1 },
            baseline with { NativePackageKind = "nuget-other-v1" },
            baseline with { ContextSize = 8192 },
            baseline with { MaxOutputTokens = 1024 },
        };

        Assert.All(variants, variant =>
            Assert.NotEqual(baselineId, BrainCohortContract.ComputeCohortId(variant)));
    }

    [Theory]
    [InlineData(511, 1)]
    [InlineData(32769, 1)]
    [InlineData(4096, 0)]
    [InlineData(8192, 4097)]
    [InlineData(4096, 4097)]
    public void Unsafe_tuning_is_rejected(int context, int output)
    {
        var result = BrainCohortContract.Validate(
            SignWithFreshIdentity(Unsigned() with
            {
                ContextSize = context,
                MaxOutputTokens = output,
            }),
            Keys,
            Now);

        Assert.False(result.IsValid);
        Assert.Equal("publisher_tuning_bounds_invalid", result.Code);
    }

    [Fact]
    public void Exact_context_and_output_maxima_are_inclusive()
    {
        var manifest = SignWithFreshIdentity(Unsigned() with
        {
            ContextSize = 32_768,
            MaxOutputTokens = 4_096,
        });

        var result = BrainCohortContract.Validate(manifest, Keys, Now);

        Assert.True(result.IsValid, result.Code);
    }

    [Fact]
    public void Artifact_urls_with_query_credentials_are_rejected()
    {
        var candidate = SignWithFreshIdentity(Unsigned() with
        {
            ModelUrl = "https://assets.example/model.gguf?token=secret",
        });

        var result = BrainCohortContract.Validate(candidate, Keys, Now);

        Assert.False(result.IsValid);
        Assert.Equal("publisher_artifact_metadata_invalid", result.Code);
    }

    [Theory]
    [InlineData("model id")]
    [InlineData("model-µ")]
    public void Model_id_requires_exact_ascii_wire_token(string modelId)
    {
        var result = BrainCohortContract.Validate(
            SignWithFreshIdentity(Unsigned() with { ModelId = modelId }),
            Keys,
            Now);

        Assert.Equal("publisher_artifact_metadata_invalid", result.Code);
    }

    [Fact]
    public void Model_id_rejects_129_ascii_characters()
    {
        var result = BrainCohortContract.Validate(
            SignWithFreshIdentity(Unsigned() with { ModelId = new string('a', 129) }),
            Keys,
            Now);

        Assert.Equal("publisher_artifact_metadata_invalid", result.Code);
    }

    [Theory]
    [InlineData("2026-07-11T11:00:00Z")]
    [InlineData("2026-07-11T11:00:00.0000000Z")]
    [InlineData("2026-07-11T11:00:00.00Z")]
    public void Validity_timestamp_requires_exact_three_millisecond_digits(string timestamp)
    {
        var result = BrainCohortContract.Validate(
            SignWithFreshIdentity(Unsigned() with { IssuedAtUtc = timestamp }),
            Keys,
            Now);

        Assert.Equal("publisher_validity_window_invalid", result.Code);
    }

    [Fact]
    public void Authorization_renewal_cannot_validate_one_manifest_then_persist_another()
    {
        var authorized = Signed();
        var local = new InstalledBrainCohortManifest(
            InstalledBrainCohortVerifier.ManifestSchemaVersion,
            authorized.CohortId,
            BrainCohortContract.BuildCanonical(authorized),
            authorized,
            "model.gguf",
            authorized.ModelSizeBytes,
            authorized.ModelSha256,
            authorized.NativeLibsSizeBytes,
            authorized.NativeLibsSha256,
            [new InstalledBrainFileManifest("llama.dll", 1, new string('c', 64))],
            authorized.NativePackageKind);
        var verified = new InstalledBrainCohortVerification(
            true,
            "valid",
            local,
            AuthorizationRefreshRequired: true,
            RequestedCanonical: BrainCohortContract.BuildCanonical(authorized),
            RequestedManifest: authorized);

        Assert.Throws<InvalidOperationException>(() =>
            InstalledBrainCohortVerifier.RenewAuthorization(
                verified,
                authorized with { NativeSignature = new string('0', 128) }));
    }

    private BrainCohortPublisherManifest Signed() => SignWithFreshIdentity(Unsigned());

    private BrainCohortPublisherManifest SignWithFreshIdentity(
        BrainCohortPublisherManifest manifest)
    {
        var identified = manifest with
        {
            CohortId = BrainCohortContract.ComputeCohortId(manifest),
            ModelSignature = string.Empty,
            NativeSignature = string.Empty,
        };
        var modelSignature = _modelKey.SignData(
            Encoding.UTF8.GetBytes(BrainCohortContract.BuildModelCanonical(identified)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var nativeSignature = _nativeKey.SignData(
            Encoding.UTF8.GetBytes(BrainCohortContract.BuildNativeCanonical(identified)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return identified with
        {
            ModelSignature = Convert.ToHexString(modelSignature).ToLowerInvariant(),
            NativeSignature = Convert.ToHexString(nativeSignature).ToLowerInvariant(),
        };
    }

    private static BrainCohortPublisherManifest Unsigned() => new(
        BrainCohortContract.SchemaVersion,
        new string('0', 64),
        "test-model",
        "https://assets.example/model.gguf",
        new string('a', 64),
        1_024,
        "https://assets.example/native.nupkg",
        new string('b', 64),
        512,
        4_096,
        512,
        Utc(Now.AddHours(-1)),
        Utc(Now.AddDays(1)),
        string.Empty,
        string.Empty,
        ModelKeyId,
        string.Empty,
        NativeKeyId,
        string.Empty,
        BrainNativePackageExtractor.OfficialNuGetPackageKind);

    public void Dispose()
    {
        _modelKey.Dispose();
        _nativeKey.Dispose();
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
