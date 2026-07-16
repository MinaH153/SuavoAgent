using Serilog;
using SuavoAgent.Contracts.Ipc;
using SuavoAgent.Contracts.Security;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Config;
using SuavoAgent.Core.Vision;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class VisionBootstrapTests
{
    [Fact]
    public void TryBuild_MissingRegistryState_ReturnsNull_AndSurvives()
    {
        // Missing machine state is explicitly disabled, never inferred enabled.
        var logger = new LoggerConfiguration().CreateLogger();
        var loaded = VisionBootstrap.LoadConfiguration(
            logger,
            new StubStore(new(
                VisionRegistryReadStatus.Missing,
                "vision_registry_state_missing")),
            Path.GetTempPath());
        var result = VisionBootstrap.TryBuild(logger, loaded);

        // On CI / test runner, we expect vision to be disabled → null.
        Assert.Null(result);
    }

    [Fact]
    public void BuildRuntime_MissingRegistryState_ReportsHealthyDefaultDisabled()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var loaded = VisionBootstrap.LoadConfiguration(
            logger,
            new StubStore(new(
                VisionRegistryReadStatus.Missing,
                "vision_registry_state_missing")),
            Path.GetTempPath());

        var result = VisionBootstrap.BuildRuntime(logger, loaded);
        var status = result.RuntimeStatus.Snapshot();

        Assert.Null(result.CaptureController);
        Assert.Null(result.PricingReader);
        Assert.False(status.VisionEnabled);
        Assert.False(status.RequiresAttention);
        Assert.Equal(VisionRuntimeCodes.VisionDisabled, status.Code);
    }

    [Fact]
    public void BuildRuntime_ConfiguredOcrWithoutApprovedInstall_FailsVisibly()
    {
        var defaults = VisionOptionsSnapshot.DisabledDefault();
        var enabled = defaults with
        {
            Enabled = true,
            Tesseract = defaults.Tesseract with
            {
                Enabled = true,
                CohortId = "unapproved",
                BundleSha256 = new string('a', 64),
                ManifestSha256 = new string('b', 64),
                NativeLibraryPath = Path.Combine(Path.GetTempPath(), "missing-cohort"),
                TessdataPath = Path.Combine(Path.GetTempPath(), "missing-cohort", "tessdata"),
            },
        };
        var loaded = new VisionConfigurationLoadResult(
            true,
            false,
            "active",
            enabled);
        var logger = new LoggerConfiguration().CreateLogger();

        // Force the production branch without constructing Windows-only
        // capture components: exact-cohort verification fails first.
        var result = VisionBootstrap.BuildRuntime(logger, loaded, isWindows: true);
        var status = result.RuntimeStatus.Snapshot();

        Assert.Null(result.CaptureController);
        Assert.Null(result.PricingReader);
        Assert.True(status.VisionEnabled);
        Assert.True(status.OcrConfigured);
        Assert.False(status.Ready);
        Assert.True(status.RequiresAttention);
        Assert.Equal(VisionRuntimeCodes.OcrCohortVerificationFailed, status.Code);
    }

    [Fact]
    public void VisionOptions_Defaults_DisabledByDefault()
    {
        var opts = new VisionOptions();
        Assert.False(opts.Enabled);
        Assert.Equal(24, opts.RetentionHours);
        Assert.Equal(500, opts.MaxStoredScreens);
        Assert.Equal(1000, opts.MinIntervalMs);
    }

    [Fact]
    public void LoadConfiguration_InvalidState_ThrowsInsteadOfSilentlyDisabling()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var store = new StubStore(new(
            VisionRegistryReadStatus.Present,
            "present",
            "{}"));

        var error = Assert.Throws<InvalidDataException>(() =>
            VisionBootstrap.LoadConfiguration(logger, store, Path.GetTempPath()));

        Assert.Contains("vision_state_missing_field", error.Message);
    }

    [Fact]
    public void LoadConfiguration_MissingState_IsExplicitDisabledStatus()
    {
        var logger = new LoggerConfiguration().CreateLogger();
        var store = new StubStore(new(
            VisionRegistryReadStatus.Missing,
            "vision_registry_state_missing"));

        var loaded = VisionBootstrap.LoadConfiguration(
            logger,
            store,
            Path.GetTempPath());

        Assert.True(loaded.IsMissing);
        Assert.False(loaded.EffectiveOptions.Enabled);
        Assert.Equal(0, loaded.EffectiveGeneration);
    }

    private sealed class StubStore(VisionRegistryReadResult result)
        : IVisionConfigurationStore
    {
        public VisionRegistryReadResult Read() => result;
        public void Write(string value) => throw new NotSupportedException();
    }
}
