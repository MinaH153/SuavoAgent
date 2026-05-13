using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Diagnostics;

/// In-memory model of ruleset-v1.json. Phase 1 ships an embedded resource;
/// Phase 2 swaps to cloud-distributed signed rulesets. Spec §8.4 RESOLVED
/// v1.0: signature uses a SEPARATE ECDsa P-256 keypair, not the
/// cmd-signing-key (crypto-domain separation).
public sealed class RulesetV1
{
    [JsonPropertyName("ruleset_version")]
    public string RulesetVersion { get; set; } = "v1.0";

    [JsonPropertyName("key_id")]
    public string KeyId { get; set; } = "ruleset-v1-key-2026-05-13";

    [JsonPropertyName("signed_at")]
    public string SignedAt { get; set; } = "";

    [JsonPropertyName("expires_at")]
    public string? ExpiresAt { get; set; }

    [JsonPropertyName("signature_alg")]
    public string SignatureAlg { get; set; } = "ECDSA_P256_SHA256";

    /// Calibration fingerprints. Bug 22 / 23 / 24 only. These are the ONLY
    /// entries that count_as_coverage at launch (spec §8.8 RESOLVED v1.0).
    [JsonPropertyName("calibration_fingerprints")]
    public Dictionary<string, string> CalibrationFingerprints { get; set; } = new();

    /// Candidate patterns: expected-but-unobserved classes. Not counted
    /// as coverage until first observed on Queen/Nadim (spec §8.8 split).
    [JsonPropertyName("candidate_patterns")]
    public List<CandidatePattern> CandidatePatterns { get; set; } = new();

    /// Invariant catalog. Phase 1: only complete-zero-actuation-log for
    /// Bug 23. Catalog grows as semantic_invariant_id-based fingerprints
    /// land.
    [JsonPropertyName("invariant_catalog")]
    public Dictionary<string, InvariantDefinition> InvariantCatalog { get; set; } = new();

    /// Patient name seed list. Spec §8.2 RESOLVED v1.0: MUST stay EMPTY in
    /// Phase 1. Census/global dictionaries are forbidden (operational
    /// false-positive cost: redacts words like May, Will, King, Price,
    /// Brown, White, Long).
    [JsonPropertyName("patient_names_seed")]
    public List<string> PatientNamesSeed { get; set; } = new();

    public sealed class CandidatePattern
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("source")] public string Source { get; set; } = "seed";
        [JsonPropertyName("confidence")] public string Confidence { get; set; } = "low";
        [JsonPropertyName("first_observed_at")] public string? FirstObservedAt { get; set; }
        [JsonPropertyName("observed_count")] public long ObservedCount { get; set; }
        [JsonPropertyName("counts_as_coverage")] public bool CountsAsCoverage { get; set; }
        [JsonPropertyName("signal_kind")] public string? SignalKind { get; set; }
        [JsonPropertyName("exception_type")] public string? ExceptionType { get; set; }
        [JsonPropertyName("stable_error_code")] public string? StableErrorCode { get; set; }
        [JsonPropertyName("notes")] public string? Notes { get; set; }
    }

    public sealed class InvariantDefinition
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("component")] public string Component { get; set; } = "";
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("severity")] public string Severity { get; set; } = "high";
    }

    /// <summary>
    /// Load the embedded ruleset-v1.json (Phase 1). Phase 2 will load
    /// from cloud-cached path with signature verification first; on
    /// verification failure, falls back to embedded last-known-good.
    /// </summary>
    public static RulesetV1 LoadEmbedded()
    {
        var asm = typeof(RulesetV1).Assembly;
        var name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("ruleset-v1.json", StringComparison.Ordinal));
        if (name is null)
        {
            return new RulesetV1(); // fail-closed minimal v0
        }
        using var stream = asm.GetManifestResourceStream(name);
        if (stream is null)
        {
            return new RulesetV1();
        }
        return JsonSerializer.Deserialize<RulesetV1>(stream, JsonOpts) ?? new RulesetV1();
    }

    /// <summary>
    /// Stub: verify the ruleset's ECDsa P-256 signature against the
    /// embedded <c>ruleset-signing-key.pub.pem</c> public key. Phase 2
    /// implements full signature verification; Phase 1 always returns
    /// true because the embedded ruleset is trusted by virtue of being
    /// shipped in the signed assembly itself.
    /// </summary>
    public bool VerifySignature(byte[]? signatureBytes = null)
    {
        // Phase 1: trust embedded ruleset (the assembly itself is
        // Authenticode-signed by publish.ps1; embedded resource is part
        // of the signed binary, so an additional ECDsa sig is belt+
        // suspenders that's only load-bearing when we move to cloud OTA
        // distribution in Phase 2.)
        return true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
