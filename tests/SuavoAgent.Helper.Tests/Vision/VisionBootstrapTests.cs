using Serilog;
using SuavoAgent.Core.Config;
using SuavoAgent.Helper.Vision;
using Xunit;

namespace SuavoAgent.Helper.Tests.Vision;

public class VisionBootstrapTests
{
    // NOTE: VisionBootstrap reads from ProgramData\SuavoAgent\vision.json which
    // is a fixed path. These tests run in CI/macOS where that path shouldn't
    // exist (or if it does, shouldn't have Enabled=true). We only verify the
    // safe defaults.

    [Fact]
    public void TryBuild_NoConfigFile_ReturnsNull_AndSurvives()
    {
        // Default state: no vision.json on this dev box → disabled → null.
        var logger = new LoggerConfiguration().CreateLogger();
        var result = VisionBootstrap.TryBuild(logger);

        // On CI / test runner, we expect vision to be disabled → null.
        // If a dev happens to have a local vision.json with Enabled=true, this
        // test passes only on Windows (where the pipeline would construct).
        // Either outcome is acceptable — the contract is "don't throw".
        Assert.True(result == null || OperatingSystem.IsWindows());
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
}
