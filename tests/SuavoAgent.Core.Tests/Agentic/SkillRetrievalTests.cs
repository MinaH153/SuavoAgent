using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using SuavoAgent.Core.Agentic;
using Xunit;

namespace SuavoAgent.Core.Tests.Agentic;

/// <summary>
/// Flywheel Increment 3 — retrieval-as-few-shot ranking + rendering (the pure, IO-free half).
/// Pins: lexical ranking is deterministic + label-aware; zero-overlap candidates are dropped;
/// the rendered block is bounded at whole-example boundaries; no-candidates ⇒ null (caller omits the
/// flag); malformed banked JSON is survived; and the block threads through BuildContext into the prompt.
/// </summary>
public class SkillRetrievalTests
{
    // Build a candidate row as GetVerifiedSkillsForApp returns it. Steps are click_by_label so the
    // signature parses to a label (the structural, PHI-safe shape Phase-3B banks).
    private static (string, string, string, int) Skill(string id, string taskKey, int success, params string[] labels)
    {
        var steps = labels.Select(l => new VerifiedStep(
            $"state-{l}",
            NextAction.Act("click_by_label", new Dictionary<string, object?> { ["label"] = l, ["process_name"] = "calc.exe" }).Signature(),
            "click_by_label",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["label"] = l, ["process_name"] = "calc.exe" }))).ToList();
        return (id, taskKey, JsonSerializer.Serialize(steps), success);
    }

    // ── ranking ──

    [Fact]
    public void Rank_OrdersByLexicalOverlap_ThenSuccessCount()
    {
        var cands = new List<(string, string, string, int)>
        {
            Skill("a", "task.refill", 5, "Refill", "Confirm"),    // overlaps "refill"
            Skill("b", "task.pricing", 9, "Price", "Update"),     // overlaps "pricing"/"price"? goal below
            Skill("c", "task.refill", 1, "Refill", "Submit"),     // overlaps "refill", lower success
        };

        var ranked = SkillRetrieval.Rank("refill the prescription", windowTitle: null, cands);

        // a and c both overlap "refill"; a wins on success_count (5 > 1). b has no overlap → dropped.
        Assert.Equal(new[] { "a", "c" }, ranked.Select(r => r.SkillId));
    }

    [Fact]
    public void Rank_UsesStepLabels_NotJustTaskKey()
    {
        var cands = new List<(string, string, string, int)>
        {
            Skill("withlabel", "task.x", 1, "Lipitor", "Confirm"),  // label matches the goal drug
            Skill("nolabel", "task.y", 9, "Foo", "Bar"),            // higher success but no overlap
        };

        var ranked = SkillRetrieval.Rank("find Lipitor and confirm", windowTitle: null, cands);

        Assert.Single(ranked);
        Assert.Equal("withlabel", ranked[0].SkillId); // label overlap beats a higher-success non-match
    }

    [Fact]
    public void Rank_Deterministic_AcrossCalls()
    {
        var cands = new List<(string, string, string, int)>
        {
            Skill("a", "task.refill", 3, "Refill"),
            Skill("b", "task.refill", 3, "Refill"), // identical score + success → stable tie-break on id
        };

        var r1 = SkillRetrieval.Rank("refill", null, cands).Select(r => r.SkillId).ToList();
        var r2 = SkillRetrieval.Rank("refill", null, cands).Select(r => r.SkillId).ToList();

        Assert.Equal(r1, r2);
        Assert.Equal(new[] { "a", "b" }, r1); // ascending id tie-break
    }

    [Fact]
    public void Rank_ZeroOverlap_ReturnsEmpty_NotNoise()
    {
        var cands = new List<(string, string, string, int)> { Skill("a", "task.alpha", 9, "Beta", "Gamma") };
        Assert.Empty(SkillRetrieval.Rank("completely unrelated objective", null, cands));
    }

    [Fact]
    public void Rank_StopwordsDoNotFakeOverlap()
    {
        // Only stopwords shared ("click"/"the"/"tab") ⇒ no real overlap ⇒ dropped.
        var cands = new List<(string, string, string, int)> { Skill("a", "task.foo", 9, "Bar") };
        Assert.Empty(SkillRetrieval.Rank("click the tab", null, cands));
    }

    [Fact]
    public void Rank_HonorsMaxExamples()
    {
        var cands = Enumerable.Range(0, 6).Select(i => Skill($"s{i}", "task.refill", i, "Refill")).ToList();
        var ranked = SkillRetrieval.Rank("refill", null, cands, maxExamples: 2);
        Assert.Equal(2, ranked.Count);
    }

    // ── rendering ──

    [Fact]
    public void BuildKnownSkillsFlag_RendersCompactTranscript()
    {
        var cands = new List<(string, string, string, int)> { Skill("a", "task.calc", 3, "Seven", "Plus", "Eight") };
        var block = SkillRetrieval.BuildKnownSkillsFlag("calc seven plus eight", null, cands);

        Assert.NotNull(block);
        Assert.Contains("click Seven", block);
        Assert.Contains("→", block);
        Assert.Contains("click Eight", block);
    }

    [Fact]
    public void BuildKnownSkillsFlag_NoCandidates_ReturnsNull()
    {
        Assert.Null(SkillRetrieval.BuildKnownSkillsFlag("x", null, new List<(string, string, string, int)>()));
    }

    [Fact]
    public void BuildKnownSkillsFlag_NoRelevant_ReturnsNull()
    {
        var cands = new List<(string, string, string, int)> { Skill("a", "task.alpha", 9, "Beta") };
        Assert.Null(SkillRetrieval.BuildKnownSkillsFlag("unrelated", null, cands));
    }

    [Fact]
    public void BuildKnownSkillsFlag_BoundedByMaxBlockChars_AtExampleBoundary()
    {
        // Many fat overlapping examples; the block must stop UNDER the budget and never clip mid-example
        // (no dangling text after the last "→").
        var cands = Enumerable.Range(0, 30)
            .Select(i => Skill($"s{i}", "task.refill", 30 - i, "Refill", "Prescription", "Confirm", "Submit"))
            .ToList();

        var block = SkillRetrieval.BuildKnownSkillsFlag("refill prescription confirm submit", null, cands);

        Assert.NotNull(block);
        Assert.True(block!.Length <= SkillRetrieval.MaxBlockChars, $"block {block.Length} > {SkillRetrieval.MaxBlockChars}");
        Assert.DoesNotContain("→ ;", block);       // no example clipped right after an arrow
        Assert.False(block.TrimEnd().EndsWith("→"), "block must not end on a dangling arrow");
    }

    [Fact]
    public void BuildKnownSkillsFlag_MalformedJson_Survives()
    {
        var cands = new List<(string, string, string, int)>
        {
            ("bad", "task.refill", "{not valid json", 5),                 // unparseable steps_json
            Skill("good", "task.refill", 3, "Refill"),
        };

        // The bad row contributes no labels/transcript but must not throw; the good row still renders.
        var block = SkillRetrieval.BuildKnownSkillsFlag("refill", null, cands);
        Assert.NotNull(block);
        Assert.Contains("click Refill", block);
    }

    [Fact]
    public void BuildKnownSkillsFlag_WindowTitleContributesToMatch()
    {
        var cands = new List<(string, string, string, int)> { Skill("a", "task.x", 1, "Calculator") };
        // Goal alone has no overlap; the window title "Calculator" does.
        var block = SkillRetrieval.BuildKnownSkillsFlag("do the thing", "Calculator", cands);
        Assert.NotNull(block);
    }
}
