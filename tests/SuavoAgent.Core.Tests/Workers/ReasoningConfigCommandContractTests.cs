using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Workers;

public sealed class ReasoningConfigCommandContractTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
    private const string ModelKeyId = "brain-model-test-v1";
    private const string NativeKeyId = "brain-native-test-v1";
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _data = Path.Combine(
        Path.GetTempPath(),
        "suavo-reasoning-command-" + Guid.NewGuid().ToString("N"));

    private IReadOnlyDictionary<string, string> Keys =>
        new Dictionary<string, string>
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
        };

    [Fact]
    public void Valid_publisher_manifest_derives_paths_and_persists_exact_authority()
    {
        var manifest = Signed();

        var result = ReasoningConfigCommandContract.Parse(
            Data(manifest),
            _data,
            Keys,
            Now);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(CommandId, result.CommandId);
        Assert.Equal(
            BrainCohortContract.GetModelPath(_data, manifest),
            result.Reasoning!["ModelPath"]!.GetValue<string>());
        Assert.Equal(
            BrainCohortContract.GetNativeDirectory(_data, manifest),
            result.Reasoning["NativeLibraryPath"]!.GetValue<string>());
        Assert.Equal(
            manifest.ModelSignature,
            result.Reasoning["ModelSignature"]!.GetValue<string>());
        Assert.Equal(
            manifest.NativeSignature,
            result.Reasoning["NativeSignature"]!.GetValue<string>());
        Assert.Equal(manifest.NativeLibsSizeBytes,
            result.Reasoning["NativeLibsSizeBytes"]!.GetValue<long>());
        Assert.Equal(manifest.NativePackageKind,
            result.Reasoning["NativePackageKind"]!.GetValue<string>());
    }

    [Fact]
    public void Cloud_cannot_supply_unsigned_paths_or_extra_tuning()
    {
        var manifest = Signed();
        using var document = JsonDocument.Parse(Data(manifest).GetRawText());
        var dictionary = document.RootElement.EnumerateObject()
            .ToDictionary(
                property => property.Name,
                property => JsonNodeValue(property.Value));
        dictionary["modelPath"] = @"C:\Windows\System32\evil.gguf";

        var result = ReasoningConfigCommandContract.Parse(
            JsonSerializer.SerializeToElement(dictionary),
            _data,
            Keys,
            Now);

        Assert.False(result.IsValid);
        Assert.Equal("reasoning_command_schema_invalid", result.Code);
    }

    [Fact]
    public void Retired_schema_v2_and_wrong_package_kind_cannot_be_selected()
    {
        var current = Signed();
        var retired = Sign(current with
        {
            SchemaVersion = BrainCohortContract.RetiredInstalledSchemaVersion,
            NativePackageKind = string.Empty,
        });
        var wrongKind = Sign(current with { NativePackageKind = "zip-flat-v1" });

        Assert.False(ReasoningConfigCommandContract.Parse(
            Data(retired), _data, Keys, Now).IsValid);
        Assert.Equal(
            "publisher_native_package_kind_invalid",
            ReasoningConfigCommandContract.Parse(
                Data(wrongKind), _data, Keys, Now).Code);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("tampered")]
    public void Invalid_publisher_authority_is_rejected(string mode)
    {
        var valid = Signed();
        var candidate = mode switch
        {
            "missing" => valid with { NativeSignature = "" },
            "unknown" => valid with { NativeKeyId = "brain-native-unknown-v1" },
            "expired" => Sign(valid with
            {
                IssuedAtUtc = Utc(Now.AddDays(-2)),
                ExpiresAtUtc = Utc(Now.AddDays(-1)),
            }),
            _ => valid with { ModelSha256 = new string('f', 64) },
        };

        var result = ReasoningConfigCommandContract.Parse(
            Data(candidate),
            _data,
            Keys,
            Now);

        Assert.False(result.IsValid);
        Assert.StartsWith("publisher_", result.Code);
    }

    [Fact]
    public void Exact_disable_needs_no_publisher_manifest_but_extra_fields_fail()
    {
        var disabled = JsonSerializer.SerializeToElement(new
        {
            commandId = CommandId,
            enabled = false,
        });
        var valid = ReasoningConfigCommandContract.Parse(disabled, _data, Keys, Now);
        var withExtra = JsonSerializer.SerializeToElement(new
        {
            commandId = CommandId,
            enabled = false,
            modelUrl = "https://assets.example/model.gguf",
        });

        Assert.True(valid.IsValid, valid.Code);
        Assert.False(valid.Reasoning!["Enabled"]!.GetValue<bool>());
        Assert.False(ReasoningConfigCommandContract.Parse(
            withExtra, _data, Keys, Now).IsValid);
    }

    private JsonElement Data(BrainCohortPublisherManifest manifest) =>
        JsonSerializer.SerializeToElement(new
        {
            commandId = CommandId,
            enabled = true,
            schemaVersion = manifest.SchemaVersion,
            cohortId = manifest.CohortId,
            modelId = manifest.ModelId,
            modelUrl = manifest.ModelUrl,
            modelSha256 = manifest.ModelSha256,
            modelSizeBytes = manifest.ModelSizeBytes,
            nativeLibsUrl = manifest.NativeLibsUrl,
            nativeLibsSha256 = manifest.NativeLibsSha256,
            nativeLibsSizeBytes = manifest.NativeLibsSizeBytes,
            nativePackageKind = manifest.NativePackageKind,
            contextSize = manifest.ContextSize,
            maxOutputTokens = manifest.MaxOutputTokens,
            issuedAtUtc = manifest.IssuedAtUtc,
            expiresAtUtc = manifest.ExpiresAtUtc,
            keyId = manifest.KeyId,
            signature = manifest.Signature,
            modelKeyId = manifest.ModelKeyId,
            modelSignature = manifest.ModelSignature,
            nativeKeyId = manifest.NativeKeyId,
            nativeSignature = manifest.NativeSignature,
        });

    private BrainCohortPublisherManifest Signed() => Sign(new(
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
        BrainNativePackageExtractor.OfficialNuGetPackageKind));

    private BrainCohortPublisherManifest Sign(BrainCohortPublisherManifest manifest)
    {
        var identified = manifest with
        {
            CohortId = BrainCohortContract.ComputeCohortId(manifest),
            ModelSignature = string.Empty,
            NativeSignature = string.Empty,
        };
        return identified with
        {
            ModelSignature = Convert.ToHexString(_modelKey.SignData(
                Encoding.UTF8.GetBytes(BrainCohortContract.BuildModelCanonical(identified)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant(),
            NativeSignature = Convert.ToHexString(_nativeKey.SignData(
                Encoding.UTF8.GetBytes(BrainCohortContract.BuildNativeCanonical(identified)),
                HashAlgorithmName.SHA256,
                DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant(),
        };
    }

    private static object? JsonNodeValue(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number when element.TryGetInt64(out var number) => number,
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => element.GetRawText(),
    };

    public void Dispose()
    {
        _modelKey.Dispose();
        _nativeKey.Dispose();
        try { Directory.Delete(_data, recursive: true); } catch { }
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
}
