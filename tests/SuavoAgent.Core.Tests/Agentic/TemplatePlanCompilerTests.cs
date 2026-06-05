using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Core.Agentic;
using SuavoAgent.Core.Agentic.Replication;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// The watch-&-learn replication core: learned template → executable plan. Asserts structural
/// (signature-based) grounding, PHI-free Type compilation, check-vs-actuate classification, and order.
/// </summary>
public sealed class TemplatePlanCompilerTests
{
    private static ElementSignature Sig(string control, string autoId, string? cls = null) =>
        new(control, autoId, cls);

    private static TemplateStep Step(
        int ordinal, TemplateStepKind kind, ElementSignature? target = null, KeyHint? hint = null,
        bool isWrite = false)
    {
        var t = target ?? Sig("Button", "saveBtn");
        var visible = new[] { t };
        var after = isWrite ? new[] { Sig("Text", "confirm") } : null;
        return new TemplateStep(ordinal, kind, t, visible, MinElementsRequired: 1, ExpectedAfter: after,
            IsWrite: isWrite, CorrelatedQueryShapeHash: null, StepConfidence: 0.9, Hint: hint);
    }

    [Fact]
    public void Compile_OrdersByOrdinal()
    {
        var steps = new[]
        {
            Step(2, TemplateStepKind.PressKey, hint: new KeyHint("Enter", null)),
            Step(0, TemplateStepKind.Click),
            Step(1, TemplateStepKind.Type, hint: new KeyHint(null, KeyHintPlaceholder.RxNumberEchoed)),
        };

        var plan = TemplatePlanCompiler.Compile(steps);

        Assert.Equal(new[] { 0, 1, 2 }, plan.Select(s => s.Ordinal));
    }

    [Fact]
    public void CompileStep_Click_EmitsClickBySignature_WithStructuralParams_NotName()
    {
        var step = Step(0, TemplateStepKind.Click, target: Sig("Button", "saveBtn", "WinButton"));
        var compiled = TemplatePlanCompiler.CompileStep(step);

        Assert.False(compiled.IsCheck);
        Assert.NotNull(compiled.Action);
        Assert.Equal(NextActionKind.Act, compiled.Action!.Kind);
        Assert.Equal("click_by_signature", compiled.Action.Verb);
        Assert.Equal("saveBtn", compiled.Action.Parameters!["automationId"]);
        Assert.Equal("Button", compiled.Action.Parameters!["controlType"]);
        Assert.Equal("WinButton", compiled.Action.Parameters!["className"]);
    }

    [Fact]
    public void CompileStep_Type_EmitsTypeIntoField_WithWhitelistedPlaceholder_NeverPlaintext()
    {
        var step = Step(0, TemplateStepKind.Type, hint: new KeyHint(null, KeyHintPlaceholder.RxNumberEchoed));
        var compiled = TemplatePlanCompiler.CompileStep(step);

        Assert.Equal("type_into_field", compiled.Action!.Verb);
        Assert.Equal("RxNumberEchoed", compiled.Action.Parameters!["placeholder"]);
        Assert.False(compiled.Action.Parameters!.ContainsKey("text")); // no plaintext ever
    }

    [Fact]
    public void CompileStep_PressKey_EmitsPressKeys_WithKeyName()
    {
        var step = Step(0, TemplateStepKind.PressKey, hint: new KeyHint("Enter", null));
        var compiled = TemplatePlanCompiler.CompileStep(step);

        Assert.Equal("press_keys", compiled.Action!.Verb);
        Assert.Equal("Enter", compiled.Action.Parameters!["chords"]);
    }

    [Theory]
    [InlineData(TemplateStepKind.WaitForElement)]
    [InlineData(TemplateStepKind.VerifyElement)]
    public void CompileStep_CheckKinds_AreChecks_WithNoActuation(TemplateStepKind kind)
    {
        var compiled = TemplatePlanCompiler.CompileStep(Step(0, kind));

        Assert.True(compiled.IsCheck);
        Assert.Null(compiled.Action); // perceive-and-assert, never actuate
        Assert.NotEmpty(compiled.ExpectedVisible);
    }

    [Fact]
    public void CompileStep_WritebackStep_CarriesExpectedAfter_AndIsWriteFlag()
    {
        var compiled = TemplatePlanCompiler.CompileStep(Step(0, TemplateStepKind.Click, isWrite: true));

        Assert.True(compiled.IsWrite);
        Assert.NotNull(compiled.ExpectedAfter);
        Assert.NotEmpty(compiled.ExpectedAfter!);
    }
}
