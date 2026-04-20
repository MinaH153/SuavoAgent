using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Contracts.Learning;
using SuavoAgent.Contracts.Reasoning;
using SuavoAgent.Core.Reasoning;
using SuavoAgent.Core.State;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// The v3.12 "agent writes its own rules" emitter. Takes a verified
/// <see cref="WorkflowTemplate"/> and lands a YAML rule file under
/// <c>&lt;rulesRoot&gt;/&lt;skillId&gt;/&lt;templateId&gt;.yaml</c>.
///
/// Safety invariants (see spec §4.2):
///   • <c>autonomousOk</c> is hardcoded <c>false</c>. Not a parameter.
///   • Writeback step without ExpectedAfter → throws (TemplateStep would have
///     thrown already, but we double-check here as defense in depth).
///   • Writeback step whose ExpectedVisible has fewer than 3 elements → throws,
///     because the derived predicate cannot be structurally discriminating.
///   • Each writeback step's action gets a <c>VerifyAfter</c> carrying the
///     template's <c>ExpectedAfter</c> as structural fingerprints.
///   • Rule priority: 200 non-writeback, 300 writeback.
///   • Rule id: <c>auto.&lt;skillId&gt;.&lt;templateId[:12]&gt;</c>.
///   • Every emit upserts an <c>auto_rule_approvals</c> row with
///     status=Pending — shadow/approved transitions are flipped by a separate
///     workflow.
///   • Output round-trips through <see cref="YamlRuleLoader.ParseYaml"/> before
///     it hits disk; a round-trip mismatch fails closed.
/// </summary>
public sealed class TemplateRuleGenerator
{
    private const int MinFingerprintsForWriteback = 3;

    private readonly AgentStateDb _db;
    private readonly string _rulesRoot;
    private readonly ILogger<TemplateRuleGenerator> _logger;
    private readonly ISerializer _yaml;

    public TemplateRuleGenerator(AgentStateDb db, string rulesRoot,
        ILogger<TemplateRuleGenerator> logger)
    {
        _db = db;
        _rulesRoot = rulesRoot;
        _logger = logger;
        _yaml = new SerializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build();
    }

    /// <summary>
    /// Emits a YAML rule file for <paramref name="template"/>. Returns the path.
    /// </summary>
    public string EmitTemplateRule(WorkflowTemplate template)
    {
        if (template is null) throw new ArgumentNullException(nameof(template));
        ValidateOrThrow(template);

        // Template IDs from real extractor output are 64-char SHA-256 hexes;
        // cross-pharmacy seed IDs may be shorter. Clamp defensively.
        var idSuffix = template.TemplateId.Length >= 12
            ? template.TemplateId[..12]
            : template.TemplateId;
        var ruleId = $"auto.{template.SkillId}.{idSuffix}";
        var yamlText = BuildYaml(template, ruleId);

        // Round-trip through YamlRuleLoader so a mis-serialisation never
        // escapes to disk.
        var parsed = new YamlRuleLoader(Microsoft.Extensions.Logging.Abstractions.NullLogger<YamlRuleLoader>.Instance)
            .ParseYaml(yamlText, source: $"memory://{ruleId}");
        if (parsed.Count != 1 || parsed[0].Id != ruleId || parsed[0].AutonomousOk)
        {
            throw new InvalidOperationException(
                $"TemplateRuleGenerator produced invalid YAML for {ruleId} (round-trip failed)");
        }

        var dir = Path.Combine(_rulesRoot, template.SkillId);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, template.TemplateId + ".yaml");
        File.WriteAllText(path, yamlText);

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(yamlText)))
            .ToLowerInvariant();
        _db.UpsertAutoRuleApproval(ruleId, template.TemplateId, hash);

        _logger.LogInformation(
            "TemplateRuleGenerator: emitted {RuleId} for template {TemplateId} at {Path}",
            ruleId, template.TemplateId, path);
        return path;
    }

    /// <summary>
    /// Emits a rule for every active (non-retired) template. Returns the count.
    /// </summary>
    public int EmitPendingRules()
    {
        var templates = _db.GetActiveWorkflowTemplates();
        int count = 0;
        foreach (var row in templates)
        {
            try
            {
                var template = Rehydrate(row);
                EmitTemplateRule(template);
                count++;
            }
            catch (InvalidOperationException ex)
            {
                _logger.LogWarning(ex,
                    "TemplateRuleGenerator: validation failed for template {TemplateId} — skipping. Reason: {Reason}",
                    row.TemplateId, ex.Message);
            }
            catch (System.Text.Json.JsonException ex)
            {
                _logger.LogWarning(ex,
                    "TemplateRuleGenerator: could not rehydrate template {TemplateId} — skipping. Reason: {Reason}",
                    row.TemplateId, ex.Message);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "TemplateRuleGenerator: unexpected failure for template {TemplateId}: {Type}:{Reason}",
                    row.TemplateId, ex.GetType().Name, ex.Message);
            }
        }
        return count;
    }

    private static WorkflowTemplate Rehydrate(AgentStateDb.WorkflowTemplateRow row)
    {
        // Tolerate camelCase (extractor output) and PascalCase (seed clients)
        // so both local extraction and cross-pharmacy seed ingress rehydrate
        // through the same path.
        var opts = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };
        var steps = JsonSerializer.Deserialize<List<TemplateStep>>(row.StepsJson, opts)
            ?? throw new InvalidOperationException($"Steps JSON could not parse for {row.TemplateId}");
        var range = JsonSerializer.Deserialize<List<PmsVersionFingerprint>>(row.PmsVersionRangeJson, opts)
            ?? new List<PmsVersionFingerprint>();
        return new WorkflowTemplate(
            TemplateId: row.TemplateId, TemplateVersion: row.TemplateVersion,
            SkillId: row.SkillId, ProcessNameGlob: row.ProcessNameGlob,
            PmsVersionRange: range,
            ScreenSignatureV1: row.ScreenSignature, StepsHash: row.StepsHash,
            RoutineHashOrigin: row.RoutineHashOrigin,
            Steps: steps,
            AggregateConfidence: row.AggregateConfidence, ObservationCount: row.ObservationCount,
            HasWriteback: row.HasWriteback, ExtractedAt: row.ExtractedAt,
            ExtractedBy: row.ExtractedBy,
            RetiredAt: row.RetiredAt, RetirementReason: row.RetirementReason);
    }

    private static void ValidateOrThrow(WorkflowTemplate t)
    {
        foreach (var step in t.Steps)
        {
            if (step.IsWrite)
            {
                if (step.ExpectedAfter is null || step.ExpectedAfter.Count == 0)
                    throw new InvalidOperationException(
                        $"Template {t.TemplateId} step {step.Ordinal}: writeback requires ExpectedAfter");
                if (step.ExpectedVisible.Count < MinFingerprintsForWriteback)
                    throw new InvalidOperationException(
                        $"Template {t.TemplateId} step {step.Ordinal}: writeback predicate " +
                        $"requires at least {MinFingerprintsForWriteback} ElementFingerprints");
            }
        }
    }

    private string BuildYaml(WorkflowTemplate template, string ruleId)
    {
        var doc = new YamlRuleFile
        {
            Rules = new List<YamlOutputRule>
            {
                BuildRule(template, ruleId),
            },
        };
        return _yaml.Serialize(doc);
    }

    private YamlOutputRule BuildRule(WorkflowTemplate template, string ruleId)
    {
        var whenPredicate = BuildWhen(template);
        var thenActions = template.Steps.Select(s => BuildAction(s)).ToList();
        return new YamlOutputRule
        {
            Id = ruleId,
            SkillId = template.SkillId,
            Priority = template.HasWriteback ? 300 : 200,
            AutonomousOk = false,
            Description = $"Auto-generated from template {template.TemplateId} v{template.TemplateVersion}",
            Version = template.TemplateVersion,
            When = whenPredicate,
            Then = thenActions,
        };
    }

    private YamlOutputPredicate BuildWhen(WorkflowTemplate template)
    {
        var entry = template.Steps[0].ExpectedVisible;
        return new YamlOutputPredicate
        {
            ProcessName = template.ProcessNameGlob,
            ElementFingerprints = entry.Select(ToYamlFp).ToList(),
        };
    }

    private YamlOutputAction BuildAction(TemplateStep step)
    {
        var actionType = step.Kind switch
        {
            TemplateStepKind.Click => RuleActionType.Click,
            TemplateStepKind.Type => RuleActionType.Type,
            TemplateStepKind.PressKey => RuleActionType.PressKey,
            TemplateStepKind.WaitForElement => RuleActionType.WaitForElement,
            TemplateStepKind.VerifyElement => RuleActionType.VerifyElement,
            _ => throw new InvalidOperationException($"Unknown TemplateStepKind {step.Kind}"),
        };

        var parameters = new Dictionary<string, string>
        {
            ["automationId"] = step.Target.AutomationId,
            ["controlType"] = step.Target.ControlType,
        };
        if (!string.IsNullOrEmpty(step.Target.ClassName))
            parameters["className"] = step.Target.ClassName!;
        if (step.Hint?.KeyName is { } key)
            parameters["key"] = key;
        if (step.Hint?.Placeholder is { } ph)
            parameters["placeholder"] = ph.ToString();

        YamlOutputPredicate? verifyAfter = null;
        if (step.ExpectedAfter is { Count: > 0 } after)
        {
            verifyAfter = new YamlOutputPredicate
            {
                ElementFingerprints = after.Select(ToYamlFp).ToList(),
            };
        }

        return new YamlOutputAction
        {
            Type = actionType.ToString(),
            Parameters = parameters,
            VerifyAfter = verifyAfter,
            Description = step.IsWrite
                ? $"Writeback step {step.Ordinal} (query shape {step.CorrelatedQueryShapeHash ?? "unknown"})"
                : $"Step {step.Ordinal}",
        };
    }

    private static YamlElementFingerprint ToYamlFp(ElementSignature s) => new()
    {
        ControlType = s.ControlType,
        AutomationId = s.AutomationId,
        ClassName = s.ClassName,
    };

    // YAML output DTOs — camelCase fields match the loader's private
    // YamlRule/YamlPredicate/YamlAction DTOs exactly.

    private sealed class YamlRuleFile
    {
        public List<YamlOutputRule> Rules { get; set; } = new();
    }

    private sealed class YamlOutputRule
    {
        public string? Id { get; set; }
        public string? SkillId { get; set; }
        public int Priority { get; set; }
        public bool AutonomousOk { get; set; }
        public string? Description { get; set; }
        public string? Version { get; set; }
        public YamlOutputPredicate? When { get; set; }
        public List<YamlOutputAction>? Then { get; set; }
    }

    private sealed class YamlOutputPredicate
    {
        public string? ProcessName { get; set; }
        public string? WindowTitlePattern { get; set; }
        public List<string>? VisibleElements { get; set; }
        public int? OperatorIdleMsAtLeast { get; set; }
        public Dictionary<string, string>? StateFlags { get; set; }
        public List<YamlElementFingerprint>? ElementFingerprints { get; set; }
    }

    private sealed class YamlOutputAction
    {
        public string? Type { get; set; }
        public Dictionary<string, string>? Parameters { get; set; }
        public string? Description { get; set; }
        public YamlOutputPredicate? VerifyAfter { get; set; }
    }

    private sealed class YamlElementFingerprint
    {
        public string? ControlType { get; set; }
        public string? AutomationId { get; set; }
        public string? ClassName { get; set; }
    }
}
