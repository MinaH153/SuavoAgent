using System;
using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// Tier-2 grounding: when a RuleContext carries screen text/elements, the prompt must surface
/// them so the local LLM reasons over what's on screen. Critically, when vision is empty the
/// prompt must be byte-identical to today (Codex Q3: omit screen keys, don't emit empty arrays).
/// </summary>
public class InferencePromptBuilderVisionTests
{
    private static InferenceRequest Req(RuleContext ctx) =>
        new() { Context = ctx, EscalationReason = "no rule matched" };

    [Fact]
    public void BuildUserMessage_WithScreenData_SurfacesTextAndElements()
    {
        var ctx = new RuleContext
        {
            SkillId = "vision-observe",
            ScreenText = new[] { new TextRegion { Text = "Pricing tab", Bounds = new Rect(0, 0, 80, 14), Confidence = 0.95 } },
            ScreenElements = new[] { new VisualElement { Role = "button", Name = "Save", Bounds = new Rect(0, 0, 1, 1), Confidence = 0.9 } },
        };

        var msg = InferencePromptBuilder.BuildUserMessage(Req(ctx));

        Assert.Contains("screen_text", msg);
        Assert.Contains("Pricing tab", msg);
        Assert.Contains("screen_elements", msg);
        Assert.Contains("Save", msg);
    }

    [Fact]
    public void BuildUserMessage_NoVision_OmitsScreenKeys_BackCompat()
    {
        var ctx = new RuleContext { SkillId = "pricing-lookup", ProcessName = "PioneerPharmacy" };

        var msg = InferencePromptBuilder.BuildUserMessage(Req(ctx));

        Assert.DoesNotContain("screen_text", msg);
        Assert.DoesNotContain("screen_elements", msg);
    }

    [Fact]
    public void BuildUserMessage_CapsScreenText()
    {
        var many = Enumerable.Range(0, 50)
            .Select(i => new TextRegion { Text = $"line{i}", Bounds = new Rect(0, i, 10, 1), Confidence = 0.5 })
            .ToArray();
        var ctx = new RuleContext { SkillId = "s", ScreenText = many };

        var msg = InferencePromptBuilder.BuildUserMessage(Req(ctx));

        // 16-region cap: line40..line49 (highest by tie-break) included; line0 dropped.
        var lineCount = Enumerable.Range(0, 50).Count(i => msg.Contains($"\"line{i}\""));
        Assert.True(lineCount <= 16, $"expected <=16 text lines, got {lineCount}");
    }
}
