using Microsoft.Extensions.Logging.Abstractions;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using Xunit;

namespace SuavoAgent.Core.Tests.Reasoning;

public class YamlRuleLoaderTests
{
    private static YamlRuleLoader NewLoader() =>
        new(NullLogger<YamlRuleLoader>.Instance);

    // --- Happy path ----------------------------------------------------------

    [Fact]
    public void ParseYaml_MinimalRule_Succeeds()
    {
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            when:
              processName: "foo*"
            then:
              - type: Click
        """;

        var rules = NewLoader().ParseYaml(yaml);

        Assert.Single(rules);
        var r = rules[0];
        Assert.Equal("r1", r.Id);
        Assert.Equal("test", r.SkillId);
        Assert.Equal("foo*", r.When.ProcessName);
        Assert.Single(r.Then);
        Assert.Equal(RuleActionType.Click, r.Then[0].Type);
    }

    [Fact]
    public void ParseYaml_RichRule_AllFieldsRoundTrip()
    {
        const string yaml = """
        rules:
          - id: rich
            skillId: pricing
            priority: 250
            version: 2.1.0
            autonomousOk: false
            description: "Rich rule"
            minConfidence: 0.95
            when:
              processName: "PioneerPharmacy*"
              windowTitlePattern: "Edit Rx Item"
              visibleElements: ["Supplier", "Cost"]
              operatorIdleMsAtLeast: 2000
              stateFlags:
                focused: "true"
            then:
              - type: Click
                description: "Click Pricing tab"
                parameters:
                  name: "Pricing"
                  controlType: "TabItem"
                verifyAfter:
                  visibleElements: ["Supplier"]
            rollback:
              - type: PressKey
                parameters:
                  key: "Escape"
        """;

        var rules = NewLoader().ParseYaml(yaml);

        Assert.Single(rules);
        var r = rules[0];
        Assert.Equal(250, r.Priority);
        Assert.Equal("2.1.0", r.Version);
        Assert.False(r.AutonomousOk);
        Assert.Equal(0.95, r.MinConfidence);
        Assert.Equal("PioneerPharmacy*", r.When.ProcessName);
        Assert.Equal("Edit Rx Item", r.When.WindowTitlePattern);
        Assert.Equal(2, r.When.VisibleElements.Count);
        Assert.Equal(2000, r.When.OperatorIdleMsAtLeast);
        Assert.Equal("true", r.When.StateFlags["focused"]);

        Assert.Single(r.Then);
        Assert.Equal("Pricing", r.Then[0].Parameters["name"]);
        Assert.NotNull(r.Then[0].VerifyAfter);
        Assert.Contains("Supplier", r.Then[0].VerifyAfter!.VisibleElements);

        Assert.Single(r.Rollback);
        Assert.Equal(RuleActionType.PressKey, r.Rollback[0].Type);
    }

    [Fact]
    public void ParseYaml_EmptyDocument_ReturnsEmpty()
    {
        Assert.Empty(NewLoader().ParseYaml("rules: []"));
        Assert.Empty(NewLoader().ParseYaml(""));
    }

    // --- Validation failures -------------------------------------------------

    [Fact]
    public void ParseYaml_MissingId_Throws()
    {
        const string yaml = """
        rules:
          - skillId: foo
            when: {}
            then:
              - type: Click
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
        Assert.Contains("'id'", ex.Message);
    }

    [Fact]
    public void ParseYaml_MissingSkillId_Throws()
    {
        const string yaml = """
        rules:
          - id: r1
            when: {}
            then:
              - type: Click
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
        Assert.Contains("skillId", ex.Message);
    }

    [Fact]
    public void ParseYaml_NoActions_Throws()
    {
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            when: {}
            then: []
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
        Assert.Contains("'then'", ex.Message);
    }

    [Fact]
    public void ParseYaml_UnknownActionType_Throws()
    {
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            when: {}
            then:
              - type: FlyToMars
        """;

        var ex = Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
        Assert.Contains("FlyToMars", ex.Message);
    }

    [Fact]
    public void ParseYaml_MalformedYaml_Throws()
    {
        const string yaml = "rules: [- not: valid: yaml:::";
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
    }

    // --- Case insensitivity --------------------------------------------------

    [Theory]
    [InlineData("Click", RuleActionType.Click)]
    [InlineData("click", RuleActionType.Click)]
    [InlineData("CLICK", RuleActionType.Click)]
    [InlineData("PressKey", RuleActionType.PressKey)]
    [InlineData("presskey", RuleActionType.PressKey)]
    public void ParseYaml_ActionType_IsCaseInsensitive(string input, RuleActionType expected)
    {
        var yaml = $$"""
        rules:
          - id: r1
            skillId: test
            when: {}
            then:
              - type: {{input}}
        """;

        var rules = NewLoader().ParseYaml(yaml);
        Assert.Equal(expected, rules[0].Then[0].Type);
    }

    // --- Directory loading ---------------------------------------------------

    [Fact]
    public void LoadFromDirectory_MissingOptionalDirectory_ReturnsEmpty()
    {
        var rules = NewLoader().LoadFromDirectory("/definitely/does/not/exist");
        Assert.Empty(rules);
    }

    [Fact]
    public void LoadFromDirectory_MissingRequiredDirectory_Throws()
    {
        Assert.Throws<InvalidOperationException>(() =>
            NewLoader().LoadFromDirectory("/definitely/does/not/exist", required: true));
    }

    // --- Codex M-2 — stricter YAML rejection --------------------------------

    [Fact]
    public void ParseYaml_RootIsScalar_Throws()
    {
        // A raw string as the document root is NOT a mapping — reject.
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml("just a string"));
    }

    [Fact]
    public void ParseYaml_MissingRulesField_Throws()
    {
        // Document with other keys but no `rules:` = operator typo, fail-closed.
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml("other: value"));
    }

    [Fact]
    public void ParseYaml_TypoedKey_Throws()
    {
        // Unknown property — e.g. "rulz:" typo — must fail because we removed
        // IgnoreUnmatchedProperties so typos can't silently drop rules.
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            priorit: 100    # typo
            when: {}
            then:
              - type: Click
        """;
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
    }

    [Fact]
    public void ParseYaml_InvalidRegexAtLoad_Throws()
    {
        // Pattern validation (M-7) happens at load, not at first evaluation.
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            when:
              windowTitlePattern: "(unclosed"
            then:
              - type: Click
        """;
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
    }

    [Fact]
    public void ParseYaml_EmptyProcessName_Throws()
    {
        const string yaml = """
        rules:
          - id: r1
            skillId: test
            when:
              processName: "   "
            then:
              - type: Click
        """;
        Assert.Throws<InvalidOperationException>(() => NewLoader().ParseYaml(yaml));
    }

    [Fact]
    public void LoadFromDirectory_LoadsAllYamlFiles()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-rule-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yaml"), """
            rules:
              - id: r1
                skillId: s1
                when: {}
                then:
                  - type: Click
            """);
            File.WriteAllText(Path.Combine(dir, "b.yaml"), """
            rules:
              - id: r2
                skillId: s2
                when: {}
                then:
                  - type: Log
            """);

            var rules = NewLoader().LoadFromDirectory(dir);
            Assert.Equal(2, rules.Count);
            Assert.Contains(rules, r => r.Id == "r1");
            Assert.Contains(rules, r => r.Id == "r2");
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadFromDirectory_DuplicateRuleIds_Throws()
    {
        var dir = Path.Combine(Path.GetTempPath(), "suavo-rule-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "a.yaml"), """
            rules:
              - id: dupe
                skillId: s1
                when: {}
                then:
                  - type: Click
            """);
            File.WriteAllText(Path.Combine(dir, "b.yaml"), """
            rules:
              - id: dupe
                skillId: s2
                when: {}
                then:
                  - type: Log
            """);

            Assert.Throws<InvalidOperationException>(() => NewLoader().LoadFromDirectory(dir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void LoadFromDirectory_ShippedCatalog_Valid()
    {
        // Verify the bundled YAML files ship valid. If this test fails, the
        // agent would fail-closed on startup — catch it in CI.
        var shippedDir = Path.Combine(AppContext.BaseDirectory, "Reasoning", "Rules");
        if (!Directory.Exists(shippedDir))
        {
            // Build hasn't copied them yet — skip rather than fail the suite.
            return;
        }

        var rules = NewLoader().LoadFromDirectory(shippedDir);
        Assert.NotEmpty(rules);
        // Every shipped rule has an id, skill, and at least one action.
        Assert.All(rules, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.Id));
            Assert.False(string.IsNullOrEmpty(r.SkillId));
            Assert.NotEmpty(r.Then);
        });
    }
}
