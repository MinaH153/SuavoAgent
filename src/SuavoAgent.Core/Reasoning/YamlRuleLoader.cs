using SuavoAgent.Contracts.Reasoning;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SuavoAgent.Core.Reasoning;

/// <summary>
/// Loads rule YAML files from disk. Fail-closed on any validation error — a
/// single malformed rule file prevents the entire catalog from loading, so
/// operators notice immediately. Silent partial loads would mask regressions.
///
/// YAML layout per file:
///
/// <code>
/// rules:
///   - id: pricing-lookup.open-rx-item
///     skillId: pricing-lookup
///     priority: 100
///     when:
///       processName: "PioneerRx*"
///       visibleElements: ["Item", "Rx Item"]
///     then:
///       - type: Click
///         parameters: { name: "Item", controlType: "MenuItem" }
///         verifyAfter:
///           visibleElements: ["Rx Item"]
/// </code>
/// </summary>
public sealed class YamlRuleLoader
{
    private readonly ILogger<YamlRuleLoader> _logger;
    private readonly IDeserializer _yaml;

    public YamlRuleLoader(ILogger<YamlRuleLoader> logger)
    {
        _logger = logger;
        _yaml = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    /// <summary>
    /// Parses every *.yaml file under the given directory into a Rule catalog.
    /// Missing directory returns empty catalog. Any parse/validation error throws.
    /// </summary>
    public IReadOnlyList<Rule> LoadFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("RuleLoader: directory {Dir} does not exist — loading empty catalog", directory);
            return Array.Empty<Rule>();
        }

        var rules = new List<Rule>();
        var files = Directory.GetFiles(directory, "*.yaml", SearchOption.AllDirectories);

        foreach (var file in files.OrderBy(f => f))
        {
            try
            {
                var text = File.ReadAllText(file);
                var parsed = ParseYaml(text, file);
                rules.AddRange(parsed);
                _logger.LogDebug("RuleLoader: loaded {Count} rules from {File}", parsed.Count, Path.GetFileName(file));
            }
            catch (Exception ex)
            {
                // Fail-closed. One bad file = entire load fails.
                throw new InvalidOperationException(
                    $"RuleLoader: failed to parse {file} — {ex.Message}", ex);
            }
        }

        Validate(rules);
        _logger.LogInformation("RuleLoader: loaded {Total} rules across {Files} file(s)",
            rules.Count, files.Length);
        return rules;
    }

    /// <summary>
    /// Parses a single YAML document into rules. Used directly in tests.
    /// </summary>
    public IReadOnlyList<Rule> ParseYaml(string yamlText, string? source = null)
    {
        RuleFile? doc;
        try
        {
            doc = _yaml.Deserialize<RuleFile>(yamlText);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"RuleLoader: malformed YAML in {source ?? "input"} — {ex.Message}", ex);
        }

        if (doc?.Rules == null || doc.Rules.Count == 0)
            return Array.Empty<Rule>();

        var rules = new List<Rule>(doc.Rules.Count);
        foreach (var yml in doc.Rules)
        {
            rules.Add(ToRule(yml, source));
        }
        return rules;
    }

    private static Rule ToRule(YamlRule yml, string? source)
    {
        if (string.IsNullOrWhiteSpace(yml.Id))
            throw new InvalidOperationException($"Rule missing 'id' field in {source ?? "input"}");
        if (string.IsNullOrWhiteSpace(yml.SkillId))
            throw new InvalidOperationException($"Rule '{yml.Id}' missing 'skillId'");
        if (yml.When == null)
            throw new InvalidOperationException($"Rule '{yml.Id}' missing 'when' predicate");
        if (yml.Then == null || yml.Then.Count == 0)
            throw new InvalidOperationException($"Rule '{yml.Id}' has no 'then' actions");

        return new Rule
        {
            Id = yml.Id,
            SkillId = yml.SkillId,
            Priority = yml.Priority ?? 100,
            Version = yml.Version ?? "1.0.0",
            AutonomousOk = yml.AutonomousOk ?? true,
            Description = yml.Description,
            MinConfidence = yml.MinConfidence ?? 0.0,
            When = ToPredicate(yml.When, yml.Id),
            Then = yml.Then.Select(a => ToAction(a, yml.Id)).ToList(),
            Rollback = (yml.Rollback ?? new List<YamlAction>())
                .Select(a => ToAction(a, yml.Id)).ToList(),
        };
    }

    private static RulePredicate ToPredicate(YamlPredicate yml, string ruleId) =>
        new()
        {
            ProcessName = yml.ProcessName,
            WindowTitlePattern = yml.WindowTitlePattern,
            VisibleElements = yml.VisibleElements ?? new List<string>(),
            OperatorIdleMsAtLeast = yml.OperatorIdleMsAtLeast,
            StateFlags = yml.StateFlags ?? new Dictionary<string, string>(),
        };

    private static RuleActionSpec ToAction(YamlAction yml, string ruleId)
    {
        if (string.IsNullOrWhiteSpace(yml.Type))
            throw new InvalidOperationException($"Rule '{ruleId}' has action with no 'type'");
        if (!Enum.TryParse<RuleActionType>(yml.Type, ignoreCase: true, out var parsed))
            throw new InvalidOperationException($"Rule '{ruleId}' has unknown action type '{yml.Type}'");

        return new RuleActionSpec
        {
            Type = parsed,
            Parameters = yml.Parameters ?? new Dictionary<string, string>(),
            Description = yml.Description,
            VerifyAfter = yml.VerifyAfter == null ? null : ToPredicate(yml.VerifyAfter, ruleId),
        };
    }

    /// <summary>Post-load validation — catches duplicate ids, etc.</summary>
    private static void Validate(IReadOnlyList<Rule> rules)
    {
        var dupes = rules.GroupBy(r => r.Id)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        if (dupes.Count > 0)
            throw new InvalidOperationException(
                $"RuleLoader: duplicate rule ids: {string.Join(", ", dupes)}");
    }

    // --- YAML DTOs (internal, never leak to callers) ---------------------------

    private sealed class RuleFile
    {
        public List<YamlRule>? Rules { get; set; }
    }

    private sealed class YamlRule
    {
        public string? Id { get; set; }
        public string? SkillId { get; set; }
        public int? Priority { get; set; }
        public string? Version { get; set; }
        public bool? AutonomousOk { get; set; }
        public string? Description { get; set; }
        public double? MinConfidence { get; set; }
        public YamlPredicate? When { get; set; }
        public List<YamlAction>? Then { get; set; }
        public List<YamlAction>? Rollback { get; set; }
    }

    private sealed class YamlPredicate
    {
        public string? ProcessName { get; set; }
        public string? WindowTitlePattern { get; set; }
        public List<string>? VisibleElements { get; set; }
        public int? OperatorIdleMsAtLeast { get; set; }
        public Dictionary<string, string>? StateFlags { get; set; }
    }

    private sealed class YamlAction
    {
        public string? Type { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
        public string? Description { get; set; }
        public YamlPredicate? VerifyAfter { get; set; }
    }
}
