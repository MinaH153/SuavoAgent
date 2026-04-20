using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Contracts.Learning;

/// <summary>
/// The v3.12 transfer currency — a PHI-free, fingerprint-verified action
/// sequence extracted from Spec-B observations. Templates cross pharmacies
/// via Spec-D seeds (§6 of the spec) or stay local.
///
/// Invariants:
///   - Steps are ordered by Ordinal starting at 0 with no gaps.
///   - If any step is a writeback, HasWriteback must be true.
///   - ScreenSignatureV1 is derived from Step[0].ExpectedVisible by
///     <see cref="ComputeScreenSignature"/>; StepsHash is derived from the
///     full Steps list by <see cref="ComputeStepsHash"/>. TemplateId is
///     <c>SHA-256(ScreenSignatureV1 + "|" + StepsHash)</c> — same Steps
///     over the same screen always yield the same id.
///
/// Extractor responsibility for idempotency: when a recomputed template
/// yields a matching ScreenSignatureV1 + StepsHash, update ObservationCount
/// without bumping TemplateVersion. When StepsHash differs, bump
/// TemplateVersion and retire the prior version with
/// retirement_reason="superseded".
/// </summary>
public sealed record WorkflowTemplate(
    string TemplateId,
    string TemplateVersion,
    string SkillId,
    string ProcessNameGlob,
    IReadOnlyList<PmsVersionFingerprint> PmsVersionRange,
    string ScreenSignatureV1,
    string StepsHash,
    string? RoutineHashOrigin,
    IReadOnlyList<TemplateStep> Steps,
    double AggregateConfidence,
    int ObservationCount,
    bool HasWriteback,
    string ExtractedAt,
    string ExtractedBy,
    string? RetiredAt,
    string? RetirementReason)
{
    /// <summary>
    /// Canonical step serialization — `Ordinal \t Kind \t Target \t IsWrite \t QueryShape \t Confidence \t HintCanonical`
    /// per step, lines joined with `\n` (no trailing newline), UTF-8 SHA-256.
    /// Lock-stepped with the spec's §2.4 definition.
    /// </summary>
    public static string ComputeStepsHash(IReadOnlyList<TemplateStep> steps)
    {
        if (steps is null) throw new ArgumentNullException(nameof(steps));
        if (steps.Count == 0) throw new ArgumentException("Steps must not be empty", nameof(steps));

        // Iterate in Ordinal order to guarantee canonical form even if caller
        // passed steps out of order.
        var ordered = steps.OrderBy(s => s.Ordinal);
        var text = string.Join('\n', ordered.Select(s => s.CanonicalLine()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Canonical screen-family signature built from a sorted representation
    /// of ExpectedVisible signatures on the entry step. Used as cache key
    /// and cross-pharmacy de-duplicator; must NOT include plaintext names.
    /// </summary>
    public static string ComputeScreenSignature(IReadOnlyList<ElementSignature> entryExpectedVisible)
    {
        if (entryExpectedVisible is null) throw new ArgumentNullException(nameof(entryExpectedVisible));
        if (entryExpectedVisible.Count == 0)
            throw new ArgumentException("Entry ExpectedVisible must not be empty", nameof(entryExpectedVisible));

        var sorted = entryExpectedVisible
            .OrderBy(s => s.ControlType, StringComparer.Ordinal)
            .ThenBy(s => s.AutomationId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.ClassName ?? string.Empty, StringComparer.Ordinal);

        var text = string.Join('\n', sorted.Select(s => s.CanonicalRepr));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    /// <summary>
    /// Deterministic TemplateId = SHA-256(ScreenSignatureV1 + "|" + StepsHash).
    /// Extractor writes this into both the DB and the auto-rule YAML id suffix.
    /// </summary>
    public static string ComputeTemplateId(string screenSignatureV1, string stepsHash)
    {
        if (string.IsNullOrWhiteSpace(screenSignatureV1))
            throw new ArgumentException("screenSignatureV1 required", nameof(screenSignatureV1));
        if (string.IsNullOrWhiteSpace(stepsHash))
            throw new ArgumentException("stepsHash required", nameof(stepsHash));
        var joined = screenSignatureV1 + "|" + stepsHash;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined))).ToLowerInvariant();
    }
}
