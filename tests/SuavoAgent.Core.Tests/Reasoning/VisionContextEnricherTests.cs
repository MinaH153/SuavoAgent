using System;
using System.Collections.Generic;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// VisionContextEnricher is the single seam where a captured ScreenFrame meets reasoning.
/// It merges OCR text + visual elements into a RuleContext and unions element names into
/// VisibleElements so existing name-based predicates also benefit.
/// </summary>
public class VisionContextEnricherTests
{
    private static RuleContext Seed() => new()
    {
        SkillId = "vision-observe",
        VisibleElements = new HashSet<string> { "ExistingElement" },
    };

    private static ScreenFrame Frame(
        IReadOnlyList<TextRegion>? text = null,
        IReadOnlyList<VisualElement>? elements = null) => new()
    {
        Id = "frame-1",
        CapturedAt = DateTimeOffset.UnixEpoch,
        Width = 1280,
        Height = 800,
        ExtractorId = "test",
        TextRegions = text ?? Array.Empty<TextRegion>(),
        Elements = elements ?? Array.Empty<VisualElement>(),
    };

    [Fact]
    public void Enrich_PopulatesScreenTextAndElements()
    {
        var frame = Frame(
            text: new[] { new TextRegion { Text = "Pricing", Bounds = new Rect(0, 0, 50, 12), Confidence = 0.9 } },
            elements: new[] { new VisualElement { Role = "button", Name = "Save", Bounds = new Rect(10, 20, 40, 16), Confidence = 0.8 } });

        var ctx = VisionContextEnricher.Enrich(Seed(), frame);

        Assert.Single(ctx.ScreenText);
        Assert.Equal("Pricing", ctx.ScreenText[0].Text);
        Assert.Single(ctx.ScreenElements);
        Assert.Equal("Save", ctx.ScreenElements[0].Name);
    }

    [Fact]
    public void Enrich_UnionsElementNamesIntoVisibleElements()
    {
        var frame = Frame(elements: new[]
        {
            new VisualElement { Role = "button", Name = "Save", Bounds = new Rect(0, 0, 1, 1), Confidence = 0.8 },
            new VisualElement { Role = "input", Name = null, Bounds = new Rect(0, 0, 1, 1), Confidence = 0.5 },
        });

        var ctx = VisionContextEnricher.Enrich(Seed(), frame);

        Assert.Contains("ExistingElement", ctx.VisibleElements); // preserved
        Assert.Contains("Save", ctx.VisibleElements);            // added from frame
        Assert.Equal(2, ctx.VisibleElements.Count);              // null name not added
    }

    [Fact]
    public void Enrich_EmptyFrame_LeavesScreenDataEmptyAndElementsUnchanged()
    {
        var ctx = VisionContextEnricher.Enrich(Seed(), Frame());

        Assert.Empty(ctx.ScreenText);
        Assert.Empty(ctx.ScreenElements);
        Assert.Equal(new HashSet<string> { "ExistingElement" }, ctx.VisibleElements);
    }
}
