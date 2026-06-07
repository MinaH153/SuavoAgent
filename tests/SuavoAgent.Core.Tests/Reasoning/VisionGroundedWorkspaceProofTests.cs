using System;
using System.IO;
using System.Linq;
using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Contracts.Vision;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

/// <summary>
/// END-TO-END proof that the agent can NAME THE SCREEN IT IS ON — its situational grounding —
/// from the captured PioneerRx screen, before it tries to do anything.
///
/// Unlike the pricing rule (whose labels were originally speculative), this rule is grounded in the
/// REAL PioneerRx main Rx workspace (Nadim's Better Life Pharmacy capture, 2026-06): the live,
/// logged-in workspace renders the signature labels "Rx Profile", "Quick Search", and "Workflow"
/// together. We encode only STRUCTURE (control labels), never patient data.
///
/// The key adversarial case is the Edit Rx Item dialog, which ALSO shows "Quick Search" — so a
/// single-label match would false-positive. Requiring "Rx Profile" AND "Quick Search" together is
/// what makes "I'm on the live workspace" reliable. Read-only Log, deterministic (no GGUF), CI-safe.
/// </summary>
public class VisionGroundedWorkspaceProofTests
{
    // The exact production rule shipped in Reasoning/Rules/pms-screens.yaml.
    private const string WorkspaceRuleYaml = """
        rules:
          - id: pms-screen.rx-workspace-ready
            skillId: pms-screen
            priority: 100
            autonomousOk: true
            when:
              processName: "PioneerPharmacy*"
              textPresent: ["Rx Profile", "Quick Search"]
            then:
              - type: Log
                description: "Recognized the PioneerRx Rx workspace — logged in and ready to work"
        """;

    private static RuleEngine BuildEngine()
    {
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .ParseYaml(WorkspaceRuleYaml, "test:pms-workspace");
        return new RuleEngine(rules, NullLogger<RuleEngine>.Instance);
    }

    private static TextRegion T(string text, int x, int y, int w = 80, int h = 14) =>
        new() { Text = text, Bounds = new Rect(x, y, w, h), Confidence = 0.95 };

    private static VisualElement E(string role, string? name, int x, int y, int w = 80, int h = 18) =>
        new() { Role = role, Name = name, Bounds = new Rect(x, y, w, h), Confidence = 1.0 };

    /// <summary>The REAL PioneerRx main Rx workspace, logged in and ready — already PHI-scrubbed
    /// (the patient cell is the [PATIENT] sentinel). Renders the signature labels together.</summary>
    private static ScreenFrame WorkspaceScreen() => new()
    {
        Id = "frame-workspace",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[]
        {
            T("Rx Profile", 40, 60, 90),
            T("Quick Search", 300, 60, 100),
            T("Workflow", 520, 60),
            T("[PATIENT]", 40, 120, 200), // already scrubbed — present but harmless
        },
        Elements = new[]
        {
            E("TabItem", "Rx Profile", 38, 58, 90, 22),
            E("Edit", "Quick Search", 298, 58, 140, 22),
            E("TabItem", "Workflow", 518, 58, 80, 22),
        },
    };

    /// <summary>The Edit Rx Item dialog — the trap. It ALSO shows "Quick Search" (per the pricing
    /// workflow) but NOT "Rx Profile". A single-label rule would mistake it for the workspace; the
    /// conjunction must reject it.</summary>
    private static ScreenFrame EditItemDialogScreen() => new()
    {
        Id = "frame-edit-item",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[]
        {
            T("Edit Rx Item", 40, 20, 90),
            T("Quick Search", 300, 60, 100),
            T("Pricing", 120, 100),
        },
        Elements = new[] { E("Edit", "Quick Search", 298, 58, 140, 22) },
    };

    /// <summary>The Windows desktop / PioneerRx not in focus — no PMS workspace labels at all.</summary>
    private static ScreenFrame UnknownScreen() => new()
    {
        Id = "frame-unknown",
        CapturedAt = DateTimeOffset.UtcNow,
        Width = 1920,
        Height = 1080,
        ExtractorId = "composite-tesseract-eng+uia",
        TextRegions = new[] { T("Recycle Bin", 20, 10), T("Start", 10, 1050) },
        Elements = new[] { E("Button", "Start", 8, 1048) },
    };

    private static RuleContext Observe(ScreenFrame frame) =>
        VisionContextEnricher.Enrich(
            new RuleContext { SkillId = "pms-screen", ProcessName = "PioneerPharmacy", OperatorIdleMs = 5000 },
            frame);

    [Fact]
    public void Loader_ParsesWorkspaceRule_FromYaml()
    {
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .ParseYaml(WorkspaceRuleYaml, "test");
        var rule = Assert.Single(rules);

        Assert.Equal("pms-screen.rx-workspace-ready", rule.Id);
        Assert.Equal("pms-screen", rule.SkillId);
        Assert.Equal(new[] { "Rx Profile", "Quick Search" }, rule.When.TextPresent);
    }

    [Fact]
    public void Agent_RecognizesRxWorkspace_WhenLoggedInAndReady()
    {
        var result = BuildEngine().Evaluate(Observe(WorkspaceScreen()));

        Assert.Equal(MatchOutcome.Matched, result.Outcome);
        Assert.Equal("pms-screen.rx-workspace-ready", result.MatchedRule!.Id);
    }

    [Fact]
    public void Agent_OnEditItemDialog_DoesNotMistakeForWorkspace()
    {
        // "Quick Search" is present but "Rx Profile" is not — the conjunction must reject it so the
        // agent doesn't think a modal dialog is the live workspace.
        var result = BuildEngine().Evaluate(Observe(EditItemDialogScreen()));
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void Agent_OnUnknownScreen_DoesNotConfirmWorkspace()
    {
        var result = BuildEngine().Evaluate(Observe(UnknownScreen()));
        Assert.Equal(MatchOutcome.NoMatch, result.Outcome);
    }

    [Fact]
    public void BundledPmsScreenRules_LoadCleanly_AndIncludeWorkspaceRule()
    {
        // Loads the ACTUAL shipped Reasoning/Rules/pms-screens.yaml alongside every other rule file,
        // proving the whole on-disk catalog still parses with this rule in it.
        var dir = Path.Combine(AppContext.BaseDirectory, "Reasoning", "Rules");
        var rules = new YamlRuleLoader(NullLogger<YamlRuleLoader>.Instance)
            .LoadFromDirectory(dir, required: true);

        var workspace = rules.Single(r => r.Id == "pms-screen.rx-workspace-ready");
        Assert.Equal("pms-screen", workspace.SkillId);
        Assert.Equal(new[] { "Rx Profile", "Quick Search" }, workspace.When.TextPresent);
    }

    [Fact]
    public void Enricher_PassesScrubbedWorkspace_WithoutLeakingPhi()
    {
        // The agent reasons over scrubbed structure: the patient cell is the [PATIENT] sentinel,
        // never a raw name.
        var ctx = Observe(WorkspaceScreen());
        Assert.Contains(ctx.ScreenText, t => t.Text == "Rx Profile");
        Assert.Contains(ctx.ScreenText, t => t.Text == "[PATIENT]");
        Assert.All(ctx.ScreenText, t => Assert.DoesNotContain("John", t.Text));
    }
}
