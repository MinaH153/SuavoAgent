using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Reasoning;
using Xunit;

namespace SuavoAgent.Setup.Tests;

public sealed class BrainDiskSpaceGateTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private const string ModelKeyId = "brain-model-disk-test-v1";
    private const string NativeKeyId = "brain-native-disk-test-v1";
    private readonly ECDsa _modelKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ECDsa _nativeKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private IReadOnlyDictionary<string, string> TrustedKeys =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ModelKeyId] = Convert.ToBase64String(_modelKey.ExportSubjectPublicKeyInfo()),
            [NativeKeyId] = Convert.ToBase64String(_nativeKey.ExportSubjectPublicKeyInfo()),
        };

    [Fact]
    public void Signed_sizes_bounded_extraction_and_reserve_all_count_before_download()
    {
        var reasoning = SignedReasoning(
            modelBytes: 2L * 1024 * 1024 * 1024,
            nativeBytes: 128L * 1024 * 1024);

        var result = BrainDiskSpaceGate.Evaluate(
            Path.Combine(Path.GetTempPath(), "install"),
            Path.Combine(Path.GetTempPath(), "data"),
            reasoning,
            _ => long.MaxValue,
            _ => false,
            TrustedKeys,
            Now);

        Assert.True(result.IsSufficient, result.Detail);
        Assert.Equal(
            BrainDiskSpaceGate.DataSafetyReserveBytes +
            reasoning.ModelSizeBytes!.Value +
            reasoning.NativeLibsSizeBytes!.Value +
            InstalledBrainCohortVerifier.MaxNativeUncompressedBytes,
            result.DataRequiredBytes);
    }

    [Fact]
    public void Static_two_gib_that_cannot_stage_valid_brain_fails_actionably()
    {
        var reasoning = SignedReasoning(
            modelBytes: 2L * 1024 * 1024 * 1024,
            nativeBytes: 128L * 1024 * 1024);

        var result = BrainDiskSpaceGate.Evaluate(
            Path.Combine(Path.GetTempPath(), "install"),
            Path.Combine(Path.GetTempPath(), "data"),
            reasoning,
            _ => 2L * 1024 * 1024 * 1024,
            _ => false,
            TrustedKeys,
            Now);

        Assert.False(result.IsSufficient);
        Assert.Contains("Free at least", result.Detail);
        Assert.Contains("old signed cohorts", result.Detail);
    }

    [Fact]
    public void Fully_verified_content_addressed_cohort_needs_only_recovery_reserve()
    {
        var reasoning = SignedReasoning(
            modelBytes: 2L * 1024 * 1024 * 1024,
            nativeBytes: 128L * 1024 * 1024);

        var result = BrainDiskSpaceGate.Evaluate(
            Path.Combine(Path.GetTempPath(), "install"),
            Path.Combine(Path.GetTempPath(), "data"),
            reasoning,
            _ => 2L * 1024 * 1024 * 1024,
            _ => true,
            TrustedKeys,
            Now);

        Assert.True(result.IsSufficient, result.Detail);
        Assert.Equal(BrainDiskSpaceGate.DataSafetyReserveBytes, result.DataRequiredBytes);
    }

    private AgentReasoningConfig SignedReasoning(long modelBytes, long nativeBytes)
    {
        var unsigned = new BrainCohortPublisherManifest(
            BrainCohortContract.SchemaVersion,
            new string('0', 64),
            "disk-test-model",
            "https://assets.example/model.gguf",
            new string('a', 64),
            modelBytes,
            "https://assets.example/native.nupkg",
            new string('b', 64),
            nativeBytes,
            4_096,
            512,
            Utc(Now.AddMinutes(-1)),
            Utc(Now.AddDays(1)),
            string.Empty,
            string.Empty,
            ModelKeyId,
            string.Empty,
            NativeKeyId,
            string.Empty,
            BrainNativePackageExtractor.OfficialNuGetPackageKind);
        var identified = unsigned with
        {
            CohortId = BrainCohortContract.ComputeCohortId(unsigned),
        };
        var modelSignature = Convert.ToHexString(_modelKey.SignData(
            Encoding.UTF8.GetBytes(BrainCohortContract.BuildModelCanonical(identified)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant();
        var nativeSignature = Convert.ToHexString(_nativeKey.SignData(
            Encoding.UTF8.GetBytes(BrainCohortContract.BuildNativeCanonical(identified)),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)).ToLowerInvariant();
        var signed = identified with
        {
            ModelSignature = modelSignature,
            NativeSignature = nativeSignature,
        };
        // Disk calculation validates through the production registry in normal
        // use. This test seam uses a structurally identical signed config whose
        // sizes are the values under test; the preflight evaluator separately
        // enforces all contract bounds below.
        return new AgentReasoningConfig(
            true,
            signed.ModelId,
            signed.ModelUrl,
            signed.ModelSha256,
            signed.ModelSizeBytes,
            signed.NativeLibsUrl,
            signed.NativeLibsSha256,
            signed.NativeLibsSizeBytes,
            signed.ContextSize,
            signed.MaxOutputTokens,
            signed.SchemaVersion,
            signed.CohortId,
            signed.IssuedAtUtc,
            signed.ExpiresAtUtc,
            signed.KeyId,
            signed.Signature,
            signed.ModelKeyId,
            signed.ModelSignature,
            signed.NativeKeyId,
            signed.NativeSignature,
            signed.NativePackageKind);
    }

    private static string Utc(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public void Dispose()
    {
        _modelKey.Dispose();
        _nativeKey.Dispose();
    }
}
