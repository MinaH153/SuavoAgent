using System.Reflection;
using System.Text.Json;
using Json.Schema;
using SuavoAgent.Diagnostics;
using Xunit;

namespace SuavoAgent.Diagnostics.Tests;

/// Ruleset-v1 loader + schema validation tests. Spec §8.8 RESOLVED v1.0:
/// calibration_fingerprints (3 entries only) + candidate_patterns (with
/// counts_as_coverage:false + first_observed_at:null + observed_count:0).
public class RulesetV1Tests
{
    [Fact]
    public void Embedded_ruleset_loads_without_exception()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        Assert.NotNull(ruleset);
        Assert.Equal("v1.0", ruleset.RulesetVersion);
    }

    [Fact]
    public void Embedded_ruleset_has_exactly_3_calibration_fingerprints()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        // Spec §8.8: calibration_fingerprints carry the only entries that
        // count as coverage. Phase 1 = Bug 22 / Bug 23 / Bug 24.
        Assert.Equal(3, ruleset.CalibrationFingerprints.Count);
        Assert.Contains("bug-22", ruleset.CalibrationFingerprints.Keys);
        Assert.Contains("bug-23", ruleset.CalibrationFingerprints.Keys);
        Assert.Contains("bug-24", ruleset.CalibrationFingerprints.Keys);
    }

    [Fact]
    public void Embedded_ruleset_calibration_fingerprints_are_non_empty()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        foreach (var kv in ruleset.CalibrationFingerprints)
        {
            Assert.False(string.IsNullOrWhiteSpace(kv.Value),
                $"calibration_fingerprints[{kv.Key}] is empty");
        }
    }

    [Fact]
    public void All_candidate_patterns_have_counts_as_coverage_false()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        foreach (var candidate in ruleset.CandidatePatterns)
        {
            Assert.False(candidate.CountsAsCoverage,
                $"candidate '{candidate.Name}' has counts_as_coverage=true; spec §8.8 forbids this at seed time");
            Assert.Null(candidate.FirstObservedAt);
            Assert.Equal(0, candidate.ObservedCount);
            Assert.Equal("seed", candidate.Source);
        }
    }

    [Fact]
    public void All_candidate_patterns_have_confidence_low()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        foreach (var candidate in ruleset.CandidatePatterns)
        {
            Assert.Equal("low", candidate.Confidence);
        }
    }

    [Fact]
    public void Patient_names_seed_is_empty_in_Phase1()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        // Codex §8.2 RESOLVED v1.0: MUST stay EMPTY. Census/global
        // dictionaries forbidden.
        Assert.Empty(ruleset.PatientNamesSeed);
    }

    [Fact]
    public void Invariant_catalog_contains_complete_zero_actuation_log()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        Assert.Contains("complete-zero-actuation-log", ruleset.InvariantCatalog.Keys);
        var invariant = ruleset.InvariantCatalog["complete-zero-actuation-log"];
        Assert.Equal("Core", invariant.Component);
        Assert.Equal("critical", invariant.Severity);
    }

    [Fact]
    public void Signature_alg_is_ECDSA_P256_SHA256()
    {
        var ruleset = RulesetV1.LoadEmbedded();
        // Codex §8.4 RESOLVED v1.0: Ed25519 rejected, P-256 confirmed.
        Assert.Equal("ECDSA_P256_SHA256", ruleset.SignatureAlg);
    }

    // ── JSON Schema gate ──

    [Fact]
    public void Embedded_ruleset_validates_against_embedded_schema()
    {
        var asm = typeof(RulesetV1).Assembly;
        var rulesetJson = LoadEmbedded(asm, "ruleset-v1.json");
        var schemaJson = LoadEmbedded(asm, "ruleset-v1.schema.json");

        var schema = JsonSchema.FromText(schemaJson);
        using var doc = JsonDocument.Parse(rulesetJson);
        var result = schema.Evaluate(doc.RootElement, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });
        Assert.True(result.IsValid, FormatErrors(result));
    }

    [Fact]
    public void Schema_rejects_ruleset_with_nonzero_patient_names_seed()
    {
        var asm = typeof(RulesetV1).Assembly;
        var schemaJson = LoadEmbedded(asm, "ruleset-v1.schema.json");
        var schema = JsonSchema.FromText(schemaJson);

        // Hand-craft an invalid ruleset with patient_names_seed = ["Smith"]
        var bad = """
        {
          "ruleset_version": "v1.0",
          "key_id": "test",
          "signature_alg": "ECDSA_P256_SHA256",
          "calibration_fingerprints": {},
          "invariant_catalog": {},
          "candidate_patterns": [],
          "patient_names_seed": ["Smith"]
        }
        """;
        using var doc = JsonDocument.Parse(bad);
        var result = schema.Evaluate(doc.RootElement);
        Assert.False(result.IsValid,
            "Schema must REJECT ruleset with non-empty patient_names_seed per §8.2 RESOLVED v1.0");
    }

    private static string LoadEmbedded(Assembly asm, string suffix)
    {
        var name = asm.GetManifestResourceNames()
            .First(n => n.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = asm.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Resource {suffix} not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string FormatErrors(EvaluationResults result)
    {
        return string.Join("\n", result.Details.Where(d => !d.IsValid)
            .Select(d => $"{d.InstanceLocation}: {string.Join(",", d.Errors?.Values ?? Array.Empty<string>())}"));
    }
}
