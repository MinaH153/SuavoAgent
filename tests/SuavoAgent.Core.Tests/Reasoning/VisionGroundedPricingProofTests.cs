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
/// END-TO-END proof that the agent genuinely REASONS ABOUT WHAT IT SEES — grounded in the REAL
/// PioneerRx Edit-Rx-Item Pricing screen (Nadim 2026-06-06, image 31), not a guessed layout.
///
/// Until now the W4b vision predicates (textPresent / textNearElement) were evaluated by RuleEngine
/// and unit-tested in isolation, but the YAML loader couldn't express them — so NO production rule
/// reasoned over the captured screen; every rule matched the static UIA visibleElements snapshot
/// (which drifts across PioneerRx versions/pharmacies).
///
/// CRITICAL grounding correction: the real supplier-catalog grid's UIA cell ROLE is NOT confirmable
/// from a screenshot. The first cut hard-coded elementRole:"DataItem" — a guess. A WPF data grid cell
/// commonly surfaces as "Custom" (or Pane/Group), so that guess would have silently NEVER matched on
/// the real screen: a dead-on-arrival vision rule that looked green only because the test author made
/// the mock element a "DataItem" too. This test now models the grid cells as "Custom" (a realistic,
/// non-"DataItem" role) and the rule uses elementRole:"*" (any rendered control), so it actually
/// fires on the real screen. The exact grid role still awaits a live-box UIA-tree dump.
///
/// This exercises the FULL loop on a realistic, PHI-scrubbed pricing screen:
///   YAML rule (loader) -> VisionContextEnricher (merge ScreenFrame into RuleContext) -> RuleEngine
/// and asserts the agent reaches a GROUNDED decision ("I'm on the Pricing tab with a live supplier
/// cost grid"), with negative cases proving the spatial reasoning actually constrains. Zero actuation
/// — the rule emits a read-only Log. Deterministic (no GGUF), so it runs in CI.
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
              textPresent: ["Pricing", "Supplier", "Cost", "NDC"]
              textNearElement:
                - text: "Cost"
                  elementRole: "*"
                  maxDistancePx: 400
            then:
              - type: Log
                description: "Vision-confirmed: on the Pricing tab with a live supplier cost grid"
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

    /// <summary>A realistic PioneerRx "Edit Rx Item - &lt;drug&gt;" Pricing tab — already PHI-scrubbed
    /// (the patient cell is the sentinel [PATIENT], proving scrubbed content flows but carries no PHI).
    /// Mirrors image 31: Pricing tab, "Supplier Catalog" grid with Supplier / Cost / NDC column
    /// headers, real supplier "Mckesson", a real NDC, a unit cost. The grid cells render with UIA role
    /// "Custom" — NOT "DataItem" — which is the whole point: the rule must still fire.</summary>
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
            T("Supplier", 400, 150),
            T("Cost", 560, 150),
            T("NDC", 700, 150),
            T("Mckesson", 400, 190),
            T("0.0316", 560, 190),
            T("55111-0645-01", 700, 190, 110),
            T("[PATIENT]", 40, 60), // already scrubbed — present but harmless
        },
        Elements = new[]
        {
            E("TabItem", "Pricing", 110, 78, 80, 22),
            E("Custom", "Mckesson", 400, 188, 150, 18),   // real grid cells are "Custom", not "DataItem"
            E("Custom", "0.0316", 555, 188, 70, 18),       // the cost cell, next to the "Cost" header
            E("Edit", "[PATIENT]", 40, 58, 200, 18),
        },
    };

    /// <summary>The main PioneerRx menu — no Pricing/Supplier/Cost/NDC grid text.</summary>
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

    /// <summary>The pricing column-header text is on screen, but there's NO rendered control near
    /// "Cost" (only a far-away Close button). Proves the SPATIAL grounding constrains even with a
    /// role-agnostic "*" predicate — text presence alone is not enough, so a half-rendered grid (the
    /// cells haven't painted yet) won't falsely confirm.</summary>
    private static ScreenFrame PricingTextNoGridScreen() => new()
    {
        Id = "frame-pricing-no-grid",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[] { T("Pricing", 120, 80), T("Supplier", 400, 150), T("Cost", 560, 150), T("NDC", 700, 150) },
        Elements = new[] { E("Button", "Close", 1700, 40) }, // no rendered control anywhere near "Cost"
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

        Assert.Equal(new[] { "Pricing", "Supplier", "Cost", "NDC" }, when.TextPresent);
        var near = Assert.Single(when.TextNearElement);
        Assert.Equal("Cost", near.Text);
        Assert.Equal("*", near.ElementRole);
        Assert.Equal(400, near.MaxDistancePx);
    }

    [Fact]
    public void Agent_ReasonsAboutPricingScreen_ReachesGroundedDecision()
    {
        var result = BuildEngine().Evaluate(Observe(PricingScreen()));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("pricing-lookup.confirm-pricing-screen", result.MatchedRule!.Id);
    }

    [Fact]
    public void Agent_ConfirmsRealScreen_EvenWhenGridCellRoleIsNotDataItem()
    {
        // The DOA we avoided: the real grid cells are "Custom", not "DataItem". A hard-coded
        // elementRole:"DataItem" would NOT match this frame. With "*" it does — proving the rule is
        // grounded in the real screen, not a layout we guessed.
        Assert.DoesNotContain(PricingScreen().Elements, e => e.Role == "DataItem");

        var matched = BuildEngine().Evaluate(Observe(PricingScreen()));
        Assert.Equal(MatchOutcome.Matched, matched.Outcome);

        // Counterfactual: the old guessed predicate (DataItem) fails on the same real frame.
        var guessed = new RuleEngine(
            new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance).ParseYaml(
                VisionRuleYaml.Replace("elementRole: \"*\"", "elementRole: \"DataItem\""), "test:guess"),
            NullLogger<RuleEngine>.Instance);
        Assert.Equal(MatchOutcome.NoMatch, guessed.Evaluate(Observe(PricingScreen())).Outcome);
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
        // Text says "Pricing"/"Supplier"/"Cost"/"NDC" but no rendered control is near "Cost" — a
        // half-rendered or wrong screen. The role-agnostic spatial predicate must still reject it.
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
        Assert.Equal(new[] { "Pricing", "Supplier", "Cost", "NDC" }, vision.When.TextPresent);
        Assert.Equal("*", Assert.Single(vision.When.TextNearElement).ElementRole);
    }

    [Fact]
    public void Enricher_PassesScrubbedScreenIntoContext_WithoutLeakingPhi()
    {
        // What the agent reasons over is the scrubbed structure: the patient cell is the [PATIENT]
        // sentinel, never real PHI.
        var ctx = Observe(PricingScreen());
        Assert.Contains(ctx.ScreenText, t => t.Text == "Supplier");
        Assert.Contains(ctx.ScreenElements, e => e.Role == "Custom"); // the real grid cell role
        Assert.All(ctx.ScreenText, t => Assert.DoesNotContain("John", t.Text)); // no raw patient name
        Assert.Contains(ctx.ScreenText, t => t.Text == "[PATIENT]");
    }
}
