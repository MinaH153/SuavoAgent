using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Reasoning;

public sealed record PreparedLearnedRule(
    AgentStateDb.ActiveAutoRuleBinding Binding,
    Rule Rule);

public interface IActiveLearnedRuleRegistry
{
    int Count { get; }
    PreparedLearnedRule Prepare(
        string approvalId,
        string ruleId,
        string templateId,
        string yamlSha256);
    void Admit(PreparedLearnedRule prepared);
    void Remove(string ruleId);
    bool TryGetExact(
        string approvalId,
        string ruleId,
        string templateId,
        string yamlSha256,
        out Rule? rule);
    IReadOnlyList<Rule> GetRulesForSkill(string skillId);
}

/// <summary>
/// Thread-safe live registry for learned rules admitted by an exact, durable local approval command.
/// Built-in rules remain in <see cref="RuleEngine"/>; this registry is evaluated only after them.
/// Every read revalidates SQLite and the YAML digest, so a demotion, retirement, or file change
/// invalidates the rule without a restart.
/// </summary>
public sealed class ActiveLearnedRuleRegistry : IActiveLearnedRuleRegistry
{
    private sealed record Entry(AgentStateDb.ActiveAutoRuleBinding Binding, Rule Rule);

    private readonly AgentStateDb _db;
    private readonly string _rulesRoot;
    private readonly YamlRuleLoader _loader;
    private readonly ILogger<ActiveLearnedRuleRegistry> _logger;
    private readonly object _sync = new();
    private ImmutableDictionary<string, Entry> _entries =
        ImmutableDictionary.Create<string, Entry>(StringComparer.Ordinal);

    public ActiveLearnedRuleRegistry(
        AgentStateDb db,
        string rulesRoot,
        YamlRuleLoader loader,
        ILogger<ActiveLearnedRuleRegistry> logger)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _rulesRoot = Path.GetFullPath(rulesRoot ?? throw new ArgumentNullException(nameof(rulesRoot)));
        _loader = loader ?? throw new ArgumentNullException(nameof(loader));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ReloadFromDurableRegistry();
    }

    public int Count => Volatile.Read(ref _entries).Count;

    public PreparedLearnedRule Prepare(
        string approvalId,
        string ruleId,
        string templateId,
        string yamlSha256)
    {
        if (templateId.Length != 64 || yamlSha256.Length != 64 ||
            !templateId.All(IsLowerHex) || !yamlSha256.All(IsLowerHex) ||
            string.IsNullOrWhiteSpace(approvalId) || string.IsNullOrWhiteSpace(ruleId))
            throw new InvalidOperationException("rule_binding_invalid");

        var template = _db.GetWorkflowTemplate(templateId)
            ?? throw new InvalidOperationException("template_not_found");
        if (template.RetiredAt is not null || template.CaptureOnly)
            throw new InvalidOperationException("template_retired");

        var expectedRuleId = $"auto.{template.SkillId}.{templateId[..12]}";
        if (!string.Equals(expectedRuleId, ruleId, StringComparison.Ordinal))
            throw new InvalidOperationException("rule_template_binding_mismatch");

        var path = GetSafeRulePath(template.SkillId, templateId);
        byte[] yamlBytes;
        try
        {
            var length = new FileInfo(path).Length;
            if (length is <= 0 or > 1_048_576)
                throw new InvalidOperationException("rule_file_size_invalid");
            yamlBytes = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("rule_file_unavailable", ex);
        }

        var actualHash = SHA256.HashData(yamlBytes);
        byte[] expectedHash;
        try { expectedHash = Convert.FromHexString(yamlSha256); }
        catch (FormatException ex) { throw new InvalidOperationException("rule_digest_invalid", ex); }
        if (!CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new InvalidOperationException("rule_digest_mismatch");

        string yamlText;
        try { yamlText = new UTF8Encoding(false, true).GetString(yamlBytes); }
        catch (DecoderFallbackException ex)
        {
            throw new InvalidOperationException("rule_file_encoding_invalid", ex);
        }
        var parsed = _loader.ParseYaml(yamlText, path);
        if (parsed.Count != 1 ||
            !string.Equals(parsed[0].Id, ruleId, StringComparison.Ordinal) ||
            !string.Equals(parsed[0].SkillId, template.SkillId, StringComparison.Ordinal) ||
            parsed[0].AutonomousOk)
            throw new InvalidOperationException("rule_yaml_binding_invalid");

        var durable = _db.GetActiveAutoRuleBindings().FirstOrDefault(binding =>
            string.Equals(binding.ApprovalId, approvalId, StringComparison.Ordinal) &&
            string.Equals(binding.RuleId, ruleId, StringComparison.Ordinal) &&
            string.Equals(binding.TemplateId, templateId, StringComparison.Ordinal) &&
            string.Equals(binding.YamlSha256, yamlSha256, StringComparison.Ordinal));
        var binding = durable ?? new AgentStateDb.ActiveAutoRuleBinding(
            approvalId, ruleId, templateId, yamlSha256, "pending", DateTimeOffset.UtcNow.ToString("o"));
        return new PreparedLearnedRule(binding, parsed[0]);
    }

    public void Admit(PreparedLearnedRule prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        var durable = _db.GetActiveAutoRuleBindings().SingleOrDefault(binding =>
            SameBinding(binding, prepared.Binding));
        if (durable is null || !_db.IsActiveAutoRuleBinding(durable))
            throw new InvalidOperationException("active_registry_not_committed");

        var exact = prepared with { Binding = durable };
        lock (_sync)
            _entries = _entries.SetItem(exact.Binding.RuleId, new Entry(exact.Binding, exact.Rule));
    }

    public void Remove(string ruleId)
    {
        lock (_sync)
            _entries = _entries.Remove(ruleId);
    }

    public bool TryGetExact(
        string approvalId,
        string ruleId,
        string templateId,
        string yamlSha256,
        out Rule? rule)
    {
        rule = null;
        if (!TryGetValidEntry(ruleId, out var entry) ||
            !string.Equals(entry!.Binding.ApprovalId, approvalId, StringComparison.Ordinal) ||
            !string.Equals(entry.Binding.TemplateId, templateId, StringComparison.Ordinal) ||
            !string.Equals(entry.Binding.YamlSha256, yamlSha256, StringComparison.Ordinal))
            return false;
        rule = entry.Rule;
        return true;
    }

    public IReadOnlyList<Rule> GetRulesForSkill(string skillId)
    {
        var snapshot = Volatile.Read(ref _entries);
        var rules = new List<Rule>();
        foreach (var entry in snapshot.Values)
        {
            if (!string.Equals(entry.Rule.SkillId, skillId, StringComparison.Ordinal)) continue;
            if (TryGetValidEntry(entry.Binding.RuleId, out var valid)) rules.Add(valid!.Rule);
        }
        return rules.OrderByDescending(rule => rule.Priority).ThenBy(rule => rule.Id, StringComparer.Ordinal).ToArray();
    }

    private bool TryGetValidEntry(string ruleId, out Entry? entry)
    {
        entry = null;
        var snapshot = Volatile.Read(ref _entries);
        if (!snapshot.TryGetValue(ruleId, out var candidate)) return false;

        try
        {
            if (!_db.IsActiveAutoRuleBinding(candidate.Binding)) throw new InvalidOperationException("binding_inactive");
            var prepared = Prepare(
                candidate.Binding.ApprovalId,
                candidate.Binding.RuleId,
                candidate.Binding.TemplateId,
                candidate.Binding.YamlSha256);
            if (!SameBinding(candidate.Binding, prepared.Binding)) throw new InvalidOperationException("binding_changed");
            entry = new Entry(candidate.Binding, prepared.Rule);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Remove(ruleId);
            _logger.LogSafeWarning(ex);
            return false;
        }
    }

    private void ReloadFromDurableRegistry()
    {
        foreach (var binding in _db.GetActiveAutoRuleBindings())
        {
            try
            {
                if (!_db.IsActiveAutoRuleBinding(binding)) continue;
                var prepared = Prepare(
                    binding.ApprovalId, binding.RuleId, binding.TemplateId, binding.YamlSha256);
                lock (_sync)
                    _entries = _entries.SetItem(binding.RuleId, new Entry(binding, prepared.Rule));
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                _logger.LogSafeWarning(ex);
            }
        }
    }

    private string GetSafeRulePath(string skillId, string templateId)
    {
        var path = Path.GetFullPath(Path.Combine(_rulesRoot, skillId, templateId + ".yaml"));
        var prefix = _rulesRoot.EndsWith(Path.DirectorySeparatorChar)
            ? _rulesRoot
            : _rulesRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rule_path_invalid");
        return path;
    }

    private static bool SameBinding(
        AgentStateDb.ActiveAutoRuleBinding left,
        AgentStateDb.ActiveAutoRuleBinding right) =>
        string.Equals(left.ApprovalId, right.ApprovalId, StringComparison.Ordinal) &&
        string.Equals(left.RuleId, right.RuleId, StringComparison.Ordinal) &&
        string.Equals(left.TemplateId, right.TemplateId, StringComparison.Ordinal) &&
        string.Equals(left.YamlSha256, right.YamlSha256, StringComparison.Ordinal);

    private static bool IsLowerHex(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f';
}
