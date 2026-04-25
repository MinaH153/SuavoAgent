using SuavoAgent.Core.Config;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class TemplateLearningModeTests
{
    [Fact]
    public void Defaults_AreObserveOnlyCapture()
    {
        var options = new TemplateLearningOptions();

        Assert.False(options.Enabled);
        Assert.Equal("capture", options.Mode);
        Assert.False(options.RuleGeneration);
        Assert.False(options.AutoApproveOnFingerprintMatch);
    }

    [Fact]
    public void CaptureMode_DoesNotEmitRules_EvenWhenEnabled()
    {
        var options = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = "capture",
            RuleGeneration = true,
        };

        Assert.False(LearningWorker.ShouldEmitTemplateRules(options));
    }

    [Fact]
    public void RuleEmission_RequiresEnabledNonCaptureModeAndRuleGeneration()
    {
        var options = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = "generate",
            RuleGeneration = true,
        };

        Assert.True(LearningWorker.ShouldEmitTemplateRules(options));
    }

    [Theory]
    [InlineData("capture")]
    [InlineData("CAPTURE")]
    [InlineData("Capture")]
    [InlineData("cApTuRe")]
    public void CaptureMode_ComparisonIsCaseInsensitive(string mode)
    {
        // Regression guard: if the comparator is changed from OrdinalIgnoreCase
        // to Ordinal, "CAPTURE"/"Capture" would silently fall through the gate
        // and emit rules.
        var options = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = mode,
            RuleGeneration = true,
        };

        Assert.False(LearningWorker.ShouldEmitTemplateRules(options));
    }

    [Fact]
    public void RuleEmission_BlockedWhenAnyGateFalse()
    {
        // All three gates must pass — verify each individual gate failure
        // independently blocks emission.
        var enabledOff = new TemplateLearningOptions
        {
            Enabled = false,
            Mode = "generate",
            RuleGeneration = true,
        };
        Assert.False(LearningWorker.ShouldEmitTemplateRules(enabledOff));

        var ruleGenOff = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = "generate",
            RuleGeneration = false,
        };
        Assert.False(LearningWorker.ShouldEmitTemplateRules(ruleGenOff));

        var captureMode = new TemplateLearningOptions
        {
            Enabled = true,
            Mode = "capture",
            RuleGeneration = true,
        };
        Assert.False(LearningWorker.ShouldEmitTemplateRules(captureMode));
    }
}
