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

    /// <summary>
    /// Monotonic integer version. Phase 2 OTA freshness gate compares
    /// against this (NOT the string <c>ruleset_version</c>, which is
    /// display-only and lexicographically unsafe — e.g. <c>"v1.10" &lt;
    /// "v1.9"</c> under string compare). Cloud-side bundle builder allocates
    /// strictly greater ints via a monotonic sequence per key_id. Codex
    /// Comp 2 round-6 HIGH + round-7 HIGH RESOLVED.
    /// </summary>
    [JsonPropertyName("ruleset_version_int")]
    public int RulesetVersionInt { get; set; } = 10;

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
        try
        {
            var asm = typeof(RulesetV1).Assembly;
            var name = asm.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("ruleset-v1.json", StringComparison.Ordinal));
            if (name is null) return FailClosedFallback("manifest-resource-missing");

            using var stream = asm.GetManifestResourceStream(name);
            if (stream is null) return FailClosedFallback("resource-stream-null");

            var loaded = JsonSerializer.Deserialize<RulesetV1>(stream, JsonOpts);
            if (loaded is null) return FailClosedFallback("deserialize-returned-null");
            return loaded;
        }
        catch (Exception ex)
        {
            return FailClosedFallback($"exception:{ex.GetType().Name}");
        }
    }

    /// <summary>
    /// Spec §4 failure-mode contract: on embedded-resource load failure,
    /// fingerprint compute uses ruleset-v0 (hardcoded minimal fallback).
    /// The version label MUST NOT silently claim <c>v1.0</c> — telemetry
    /// needs to distinguish a real v1.0 from a silent fallback so dashboards
    /// can alert when the agent is operating without the calibration set
    /// (Codex chunk 3 HIGH).
    /// </summary>
    private static RulesetV1 FailClosedFallback(string reason)
    {
        System.Diagnostics.Trace.TraceWarning(
            "SuavoAgent.Diagnostics: embedded ruleset-v1.json failed to load ({0}); falling back to ruleset-v0 (no calibrations, no candidates).",
            reason);
        return new RulesetV1
        {
            RulesetVersion = "ruleset-v0",
            // Fallback int is intentionally 0 so any real cloud bundle (always >= 1
            // by the monotonic sequence) wins the OTA freshness gate. The version
            // string is "ruleset-v0" so dashboards distinguish fallback from real.
            RulesetVersionInt = 0,
            KeyId = "ruleset-v0-fallback",
            SignedAt = string.Empty,
            SignatureAlg = "NONE",
        };
    }

    // NOTE: signature verification for OTA-distributed rulesets lives in
    // RulesetSignatureVerifier.Verify(RulesetBundle) — real ECDsa P-256 against
    // the embedded multi-key trust store (ruleset-signing-key-<key_id>.pub.pem),
    // RFC 8785 canonicalization, fail-closed. ConfigSyncWorker invokes it on BOTH
    // the cached-load and network-fetch paths. A prior `VerifySignature() => true`
    // stub once lived here; it had no callers and its stale "Phase 2 will verify"
    // remark misled readers into thinking verification was unimplemented. Removed
    // to keep the trust boundary legible (the real verifier is the only authority).

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };
}
