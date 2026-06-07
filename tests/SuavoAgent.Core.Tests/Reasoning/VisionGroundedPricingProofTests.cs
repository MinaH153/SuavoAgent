using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// END-TO-END proof that the agent genuinely REASONS ABOUT WHAT IT SEES.
///
/// Until now the W4b vision predicates (textPresent / textNearElement) were evaluated by RuleEngine
/// and unit-tested in isolation, but the YAML loader couldn't express them — so NO production rule
/// reasoned over the captured screen; every rule matched the static UIA visibleElements snapshot
/// (which drifts across PioneerRx versions/pharmacies).
///
/// This exercises the FULL loop on a realistic, PHI-scrubbed PioneerRx pricing screen:
///   YAML rule (loader) -> VisionContextEnricher (merge ScreenFrame into RuleContext) -> RuleEngine
/// and asserts the agent reaches a GROUNDED decision ("I'm on the Pricing tab with a supplier cost
/// grid"), with negative cases proving the spatial reasoning actually constrains. Zero actuation —
/// the rule emits a read-only Log. Deterministic (no GGUF), so it runs in CI.
/// </summary>
public class VisionGroundedPricingProofTests
{
    // The exact production rule shipped in Reasoning/Rules/pricing-lookup.yaml.
    private const string VisionRuleYaml = """
        rules:
          - id: pricing-lookup.confirm-pricing-screen
            skillId: pricing-lookup
            priority: 165
            autonomousOk: true
            when:
              processName: "PioneerPharmacy*"
              textPresent: ["Pricing", "Supplier"]
              textNearElement:
                - text: "Cost"
                  elementRole: "DataItem"
                  maxDistancePx: 300
            then:
              - type: Log
                description: "Vision-confirmed: on the Pricing tab with a supplier cost grid"
        """;

    private static RuleEngine BuildEngine()
    {
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .ParseYaml(VisionRuleYaml, "test:vision-pricing");
        return new RuleEngine(rules, NullLogger<RuleEngine>.Instance);
    }

    private static TextRegion T(string text, int x, int y, int w = 70, int h = 14) =>
        new() { Text = text, Bounds = new Rect(x, y, w, h), Confidence = 0.95 };

    private static VisualElement E(string role, string? name, int x, int y, int w = 70, int h = 18) =>
        new() { Role = role, Name = name, Bounds = new Rect(x, y, w, h), Confidence = 1.0 };

    /// <summary>A realistic PioneerRx Pricing tab — already PHI-scrubbed (the patient cell is
    /// the sentinel [PATIENT], proving scrubbed content flows but carries no PHI). The supplier
    /// grid renders "Cost Per Unit" right next to a DataItem cell.</summary>
    private static ScreenFrame PricingScreen() => new()
    {
        Id = "frame-pricing",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[]
        {
            T("Edit Rx Item", 40, 20),
            T("Pricing", 120, 80),
            T("Supplier", 400, 160),
            T("Cost Per Unit", 560, 160, 90),
            T("McKesson", 400, 190),
            T("0.0316", 580, 190),
            T("[PATIENT]", 40, 60), // already scrubbed — present but harmless
        },
        Elements = new[]
        {
            E("TabItem", "Pricing", 110, 78, 80, 22),
            E("DataItem", "McKesson", 400, 188, 150, 18),
            E("DataItem", "0.0316", 565, 188, 70, 18),   // the cost cell, next to "Cost Per Unit"
            E("Edit", "[PATIENT]", 40, 58, 200, 18),
        },
    };

    /// <summary>The main PioneerRx menu — no Pricing/Supplier text, no supplier grid.</summary>
    private static ScreenFrame MainMenuScreen() => new()
    {
        Id = "frame-menu",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[] { T("Item", 20, 10), T("Patient", 80, 10), T("Reports", 160, 10) },
        Elements = new[] { E("MenuItem", "Item", 18, 8), E("MenuItem", "Patient", 78, 8) },
    };

    /// <summary>Pricing text is on screen, but there's NO supplier cost grid (the cost cell is far
    /// away / not a DataItem). Proves the SPATIAL grounding constrains — text presence alone is not
    /// enough, so a half-rendered or wrong screen won't falsely confirm.</summary>
    private static ScreenFrame PricingTextNoGridScreen() => new()
    {
        Id = "frame-pricing-no-grid",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[] { T("Pricing", 120, 80), T("Supplier", 400, 160), T("Cost Per Unit", 560, 160, 90) },
        Elements = new[] { E("Button", "Close", 1700, 40) }, // no DataItem anywhere near "Cost"
    };

    private static RuleContext Observe(ScreenFrame frame) =>
        VisionContextEnricher.Enrich(
            new RuleContext { SkillId = "pricing-lookup", ProcessName = "PioneerPharmacy", OperatorIdleMs = 5000 },
            frame);

    [Fact]
    public void Loader_ParsesVisionPredicates_FromYaml()
    {
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .ParseYaml(VisionRuleYaml, "test");
        var when = Assert.Single(rules).When;

        Assert.Equal(new[] { "Pricing", "Supplier" }, when.TextPresent);
        var near = Assert.Single(when.TextNearElement);
        Assert.Equal("Cost", near.Text);
        Assert.Equal("DataItem", near.ElementRole);
        Assert.Equal(300, near.MaxDistancePx);
    }

    [Fact]
    public void Agent_ReasonsAboutPricingScreen_ReachesGroundedDecision()
    {
        var result = BuildEngine().Evaluate(Observe(PricingScreen()));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("pricing-lookup.confirm-pricing-screen", result.MatchedRule!.Id);
    }

    [Fact]
    public void Agent_NonPricingScreen_DoesNotFalselyConfirm()
    {
        var result = BuildEngine().Evaluate(Observe(MainMenuScreen()));
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Agent_PricingTextWithoutSupplierGrid_DoesNotConfirm_SpatialGroundingHolds()
    {
        // Text says "Pricing"/"Supplier"/"Cost" but no DataItem grid cell is near "Cost" — a
        // half-rendered or wrong screen. The spatial predicate must reject it.
        var result = BuildEngine().Evaluate(Observe(PricingTextNoGridScreen()));
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void BundledPricingRules_LoadCleanly_AndIncludeTheVisionRule()
    {
        // Loads the ACTUAL shipped Reasoning/Rules/pricing-lookup.yaml (all rules together), proving
        // the whole file still parses with the new vision rule in it — not just the inline copy.
        var dir = Path.Combine(AppContext.BaseDirectory, "Reasoning", "Rules");
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .LoadFromDirectory(dir, required: true);

        var vision = rules.Single(r => r.Id == "pricing-lookup.confirm-pricing-screen");
        Assert.Equal(new[] { "Pricing", "Supplier" }, vision.When.TextPresent);
        Assert.Equal("DataItem", Assert.Single(vision.When.TextNearElement).ElementRole);
    }

    [Fact]
    public void Enricher_PassesScrubbedScreenIntoContext_WithoutLeakingPhi()
    {
        // What the agent reasons over is the scrubbed structure: the patient cell is the [PATIENT]
        // sentinel, never real PHI.
        var ctx = Observe(PricingScreen());
        Assert.Contains(ctx.ScreenText, t => t.Text == "Supplier");
        Assert.Contains(ctx.ScreenElements, e => e.Role == "DataItem");
        Assert.All(ctx.ScreenText, t => Assert.DoesNotContain("John", t.Text)); // no raw patient name
        Assert.Contains(ctx.ScreenText, t => t.Text == "[PATIENT]");
    }
}
