using System.Text.Json;
using System.Text.Json.Nodes;
using SuavoAgent.Core.Vision;
using Xunit;

namespace SuavoAgent.Core.Tests.Vision;

public sealed class VisionConfigurationBoundaryMatrixTests
{
    private const string CommandId = "11111111-1111-4111-8111-111111111111";
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "suavo-vision-boundary-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("retentionHours", 0L)]
    [InlineData("retentionHours", 168L)]
    [InlineData("maxStoredScreens", 1L)]
    [InlineData("maxStoredScreens", 5000L)]
    [InlineData("minIntervalMs", 250L)]
    [InlineData("minIntervalMs", 60000L)]
    [InlineData("periodicCaptureIntervalSeconds", 5L)]
    [InlineData("periodicCaptureIntervalSeconds", 3600L)]
    [InlineData("cloudSamplingInterval", 1L)]
    [InlineData("cloudSamplingInterval", 1000L)]
    [InlineData("idleUnloadSeconds", 0L)]
    [InlineData("idleUnloadSeconds", 3600L)]
    [InlineData("memoryHeadroomBytes", 0L)]
    [InlineData("memoryHeadroomBytes", 67108864L)]
    [InlineData("memoryHeadroomBytes", 4294967296L)]
    [InlineData("extractionTimeoutSeconds", 1L)]
    [InlineData("extractionTimeoutSeconds", 120L)]
    public void EffectiveOption_InclusiveBoundsAreAccepted(string field, long value)
    {
        var command = DisabledOcr();
        command[field] = value;

        var result = Parse(command);

        Assert.True(result.IsValid, result.Code);
    }

    [Theory]
    [InlineData("retentionHours", -1L)]
    [InlineData("retentionHours", 169L)]
    [InlineData("maxStoredScreens", 0L)]
    [InlineData("maxStoredScreens", 5001L)]
    [InlineData("minIntervalMs", 249L)]
    [InlineData("minIntervalMs", 60001L)]
    [InlineData("periodicCaptureIntervalSeconds", 4L)]
    [InlineData("periodicCaptureIntervalSeconds", 3601L)]
    [InlineData("cloudSamplingInterval", 0L)]
    [InlineData("cloudSamplingInterval", 1001L)]
    [InlineData("idleUnloadSeconds", -1L)]
    [InlineData("idleUnloadSeconds", 3601L)]
    [InlineData("memoryHeadroomBytes", -1L)]
    [InlineData("memoryHeadroomBytes", 1L)]
    [InlineData("memoryHeadroomBytes", 67108863L)]
    [InlineData("memoryHeadroomBytes", 4294967297L)]
    [InlineData("extractionTimeoutSeconds", 0L)]
    [InlineData("extractionTimeoutSeconds", 121L)]
    public void EffectiveOption_OutsideBoundsIsRejected(string field, long value)
    {
        var command = DisabledOcr();
        command[field] = value;

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_option_bounds_invalid", result.Code);
    }

    [Theory]
    [InlineData("retentionHours")]
    [InlineData("maxStoredScreens")]
    [InlineData("minIntervalMs")]
    [InlineData("periodicCaptureIntervalSeconds")]
    [InlineData("cloudSamplingInterval")]
    [InlineData("idleUnloadSeconds")]
    [InlineData("memoryHeadroomBytes")]
    [InlineData("extractionTimeoutSeconds")]
    public void EffectiveOption_NonNumericShapeIsRejected(string field)
    {
        var command = DisabledOcr();
        command[field] = "1";

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_option_bounds_invalid", result.Code);
    }

    [Theory]
    [InlineData("periodicCaptureEnabled")]
    [InlineData("requireForegroundMatch")]
    [InlineData("shadowReasoningEnabled")]
    [InlineData("cloudFrameUploadEnabled")]
    public void OptionalBoolean_RejectsEveryNonBooleanShape(string field)
    {
        var command = DisabledOcr();
        command[field] = "false";

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_optional_boolean_invalid", result.Code);
    }

    [Theory]
    [InlineData("periodicCaptureEnabled")]
    [InlineData("shadowReasoningEnabled")]
    [InlineData("cloudFrameUploadEnabled")]
    public void DisabledMasterRejectsEachEnabledSubfeature(string field)
    {
        var command = DisabledOcr();
        command["enabled"] = false;
        command[field] = true;

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_disabled_subfeature_enabled", result.Code);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("fra")]
    [InlineData("ENG")]
    [InlineData("eng\n")]
    public void LanguageIsClosedToEnglish(string? value)
    {
        var command = DisabledOcr();
        command["language"] = value;

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_language_not_approved", result.Code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("contains space")]
    [InlineData("contains/slash")]
    [InlineData("contains\ncontrol")]
    public void ShadowSkillIdRejectsFreeFormOrPathLikeValues(string value)
    {
        var command = DisabledOcr();
        command["shadowSkillId"] = value;

        var result = Parse(command);

        Assert.False(result.IsValid);
        Assert.Equal("vision_config_shadow_skill_invalid", result.Code);
    }

    [Fact]
    public void ShadowSkillIdAllowsOnlyBoundedAsciiIdentifierCharacters()
    {
        var command = DisabledOcr();
        command["shadowSkillId"] = "vision.observe_1-test";
        Assert.True(Parse(command).IsValid);

        command["shadowSkillId"] = new string('a', 129);
        var tooLong = Parse(command);
        Assert.False(tooLong.IsValid);
        Assert.Equal("vision_config_shadow_skill_invalid", tooLong.Code);
    }

    [Fact]
    public void BundleMetadataIsForbiddenWhenOcrIsDisabledEvenIfOtherwiseWellShaped()
    {
        foreach (var field in new[] { "bundleUrl", "bundleSha256" })
        {
            var command = DisabledOcr();
            command[field] = field == "bundleUrl"
                ? "https://assets.example/tesseract.zip"
                : new string('a', 64);
            var result = Parse(command);
            Assert.False(result.IsValid);
            Assert.Equal("vision_bundle_metadata_forbidden_when_ocr_disabled", result.Code);
        }
    }

    [Theory]
    [InlineData("bundleUrl")]
    [InlineData("bundleSha256")]
    public void BundleMetadataRejectsNonStringBlankControlAndOversizeShapes(string field)
    {
        foreach (var value in new JsonNode?[]
                 {
                     JsonValue.Create(1),
                     JsonValue.Create(""),
                     JsonValue.Create("bad\nvalue"),
                     JsonValue.Create(new string('a', 2049)),
                 })
        {
            var command = DisabledOcr();
            command[field] = value?.DeepClone();
            var result = Parse(command);
            Assert.False(result.IsValid);
            Assert.Equal("vision_bundle_metadata_invalid", result.Code);
        }
    }

    [Fact]
    public void ObjectAndDataDirectoryGuardsFailBeforePathDerivation()
    {
        var nonObject = JsonSerializer.Deserialize<JsonElement>("[]");
        Assert.Equal(
            "vision_config_object_required",
            VisionConfigurationCommandContract.Parse(nonObject, _root).Code);

        var command = JsonSerializer.Deserialize<JsonElement>(DisabledOcr().ToJsonString());
        Assert.Equal(
            "vision_data_directory_invalid",
            VisionConfigurationCommandContract.Parse(command, " ").Code);
    }

    private VisionConfigurationCommandResult Parse(JsonObject command)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(command.ToJsonString());
        return VisionConfigurationCommandContract.Parse(element, _root);
    }

    private static JsonObject DisabledOcr() => new()
    {
        ["commandId"] = CommandId,
        ["enabled"] = true,
        ["tesseractEnabled"] = false,
    };
}
