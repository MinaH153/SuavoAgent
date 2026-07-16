using System.Text.Json;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public sealed class VisionConfigurationCommandContractTests
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "suavo-vision-contract");

    private static TesseractNativeCohort TestCohort(string url, string hash) =>
        TesseractNativeCohort.Create(
            url,
            hash,
            1_024,
            new[]
            {
                new TesseractNativeFile("x64/tesseract50.dll", 10, new string('1', 64)),
                new TesseractNativeFile("x64/leptonica-1.82.0.dll", 11, new string('2', 64)),
                new TesseractNativeFile("tessdata/eng.traineddata", 12, new string('3', 64)),
            });

    [Theory]
    [InlineData("nativeLibraryPath")]
    [InlineData("tessdataPath")]
    [InlineData("unexpected")]
    public void Caller_controlled_or_unknown_fields_are_rejected(string field)
    {
        using var document = JsonDocument.Parse($$"""
            { "commandId": "{{CommandId}}", "enabled": false, "tesseractEnabled": false, "{{field}}": "C:\\ProgramData\\SuavoAgent" }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_unknown_field", result.Code);
    }

    [Fact]
    public void Disabled_ocr_has_no_remote_paths_and_uses_fixed_local_root()
    {
        using var document = JsonDocument.Parse("""
            { "commandId": "11111111-1111-4111-8111-111111111111", "enabled": true, "tesseractEnabled": false }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.True(result.IsValid, result.Code);
        Assert.NotNull(result.Command);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), "vision", "cohorts"),
            result.Command.NativeLibraryPath);
        Assert.Null(result.Command.BundleUrl);
        Assert.Null(result.Command.BundleSha256);
        Assert.Null(result.Command.CohortId);
        Assert.Null(result.Command.ManifestSha256);
    }

    [Theory]
    [InlineData("{}", "vision_command_id_invalid")]
    [InlineData("{\"commandId\":17,\"enabled\":false,\"tesseractEnabled\":false}", "vision_command_id_invalid")]
    [InlineData("{\"commandId\":\"11111111-1111-4111-8111-111111111111\",\"tesseractEnabled\":false}", "vision_config_boolean_invalid")]
    [InlineData("{\"commandId\":\"11111111-1111-4111-8111-111111111111\",\"enabled\":false}", "vision_config_boolean_invalid")]
    public void Command_identity_and_both_boolean_intents_are_required(
        string json,
        string expectedCode)
    {
        using var document = JsonDocument.Parse(json);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal(expectedCode, result.Code);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Duplicate_fields_are_rejected()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": false,
              "enabled": false,
              "tesseractEnabled": false
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_duplicate_field", result.Code);
    }

    [Fact]
    public void Optional_effective_options_are_bounded_and_projected()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": true,
              "tesseractEnabled": false,
              "retentionHours": 12,
              "maxStoredScreens": 100,
              "minIntervalMs": 500,
              "periodicCaptureEnabled": true,
              "periodicCaptureIntervalSeconds": 15,
              "requireForegroundMatch": true,
              "shadowReasoningEnabled": true,
              "shadowSkillId": "vision-observe",
              "cloudFrameUploadEnabled": true,
              "cloudSamplingInterval": 3,
              "idleUnloadSeconds": 30,
              "memoryHeadroomBytes": 367001600,
              "extractionTimeoutSeconds": 8
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.True(result.IsValid, result.Code);
        var options = Assert.IsType<VisionOptionsSnapshot>(result.Command!.EffectiveOptions);
        Assert.True(options.Enabled);
        Assert.Equal(12, options.RetentionHours);
        Assert.Equal(100, options.MaxStoredScreens);
        Assert.Equal(500, options.MinIntervalMs);
        Assert.True(options.PeriodicCapture.Enabled);
        Assert.Equal(15, options.PeriodicCapture.IntervalSeconds);
        Assert.True(options.ShadowReasoning.Enabled);
        Assert.True(options.CloudFrameUpload.Enabled);
        Assert.Equal(3, options.CloudFrameUpload.SamplingInterval);
    }

    [Fact]
    public void Disabled_master_rejects_an_enabled_subfeature()
    {
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": false,
              "tesseractEnabled": false,
              "periodicCaptureEnabled": true
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_disabled_subfeature_enabled", result.Code);
    }

    [Fact]
    public void Approved_ocr_path_is_content_addressed_and_locally_derived()
    {
        var hash = new string('a', 64);
        const string url = "https://assets.example/tesseract.zip";
        var cohort = TestCohort(url, hash);
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": true,
              "tesseractEnabled": true,
              "bundleUrl": "{{url}}",
              "bundleSha256": "{{hash}}"
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(
            document.RootElement,
            _root,
            (candidateUrl, candidateHash) =>
                candidateUrl == url && candidateHash == hash ? cohort : null);

        Assert.True(result.IsValid, result.Code);
        Assert.NotNull(result.Command);
        Assert.Equal(
            Path.Combine(Path.GetFullPath(_root), "vision", "cohorts", hash),
            result.Command.NativeLibraryPath);
        Assert.Equal(
            Path.Combine(result.Command.NativeLibraryPath, "tessdata"),
            result.Command.TessdataPath);
        Assert.Equal(cohort.CohortId, result.Command.CohortId);
        Assert.Equal(
            TesseractNativeCohortPolicy.ComputeManifestSha256(cohort),
            result.Command.ManifestSha256);
    }

    [Fact]
    public void Production_policy_rejects_unapproved_ocr_before_paths_are_returned()
    {
        using var document = JsonDocument.Parse("""
            {
              "commandId": "11111111-1111-4111-8111-111111111111",
              "enabled": true,
              "tesseractEnabled": true,
              "bundleUrl": "https://assets.example/tesseract.zip",
              "bundleSha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal("tesseract_native_cohort_not_release_approved", result.Code);
        Assert.Null(result.Command);
    }

    [Fact]
    public void Production_command_selects_the_exact_shared_preinstalled_cohort()
    {
        var cohort = Assert.Single(ReleaseOcrCohortCatalog.Approved);
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": true,
              "tesseractEnabled": true,
              "bundleUrl": "{{cohort.BundleUrl}}",
              "bundleSha256": "{{cohort.BundleSha256}}"
            }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.True(result.IsValid, result.Code);
        Assert.Equal(cohort.CohortId, result.Command!.CohortId);
        Assert.Equal(
            ReleaseOcrCohortCatalog.ComputeManifestSha256(cohort),
            result.Command.ManifestSha256);
    }

    [Fact]
    public void Resolver_cannot_approve_a_different_url_or_hash()
    {
        var requestedHash = new string('a', 64);
        using var document = JsonDocument.Parse($$"""
            {
              "commandId": "{{CommandId}}",
              "enabled": true,
              "tesseractEnabled": true,
              "bundleUrl": "https://assets.example/requested.zip",
              "bundleSha256": "{{requestedHash}}"
            }
            """);
        var different = TestCohort(
            "https://assets.example/different.zip",
            new string('b', 64));

        var result = VisionConfigurationCommandContract.Parse(
            document.RootElement,
            _root,
            (_, _) => different);

        Assert.False(result.IsValid);
        Assert.Equal("tesseract_native_cohort_not_release_approved", result.Code);
        Assert.Null(result.Command);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void Confidence_outside_tesseract_bounds_is_rejected(int confidence)
    {
        using var document = JsonDocument.Parse($$"""
            { "commandId": "{{CommandId}}", "enabled": true, "tesseractEnabled": false, "minConfidence": {{confidence}} }
            """);

        var result = VisionConfigurationCommandContract.Parse(document.RootElement, _root);

        Assert.False(result.IsValid);
        Assert.Equal("vision_min_confidence_invalid", result.Code);
    }
}
