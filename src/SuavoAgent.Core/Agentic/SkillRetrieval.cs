using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace SuavoAgent.Core.Agentic;

/// <summary>One ranked retrieval candidate: a banked skill scored against the live objective, plus the
/// compact worked-example transcript that would be injected ("task.calc: click 7 → click x → click 8").</summary>
public readonly record struct RetrievedSkill(string SkillId, int Score, int SuccessCount, string Transcript);

/// <summary>
/// Flywheel Increment 3 — retrieval-as-few-shot (the proposal-prompt side of the moat). When the Tier-2
/// LLM IS called, inject the 1–3 banked trajectories NEAREST to the live objective as worked examples, so
/// the model imitates its OWN verified history instead of re-deriving. Pure, IO-free, deterministic
/// (testable; the DB read is the caller's job) — the "retrieve" half of replay-first's "replay" half.
///
/// <para><b>Lexical/structural ranking, NO embedder</b> (the deliberate v1 choice — tens of skills per
/// app, an embedder is unjustified weight on a 2-thread box): score = overlap between the live query
/// tokens (objective Goal + current WindowTitle) and each skill's tokens (its task_key + the labels
/// parsed out of its banked step signatures via <see cref="ActionSignatureParser"/>). Ties break on
/// success_count (the thicker tube wins). Zero-overlap candidates are dropped — an unrelated skill is
/// noise in the prompt, not a free example.</para>
///
/// <para><b>HIPAA.</b> Every banked field is Phase-3B PHI-certified before it ever lands in
/// <c>verified_skills</c>, so surfacing it into the prompt introduces no new exposure; the outbound guard
/// still scrubs the assembled prompt. The few-shot block is ADVISORY — the reasoner's output still flows
/// through grounding + the composite safety gate + postconditions, so a hallucinated imitation can change
/// what is PROPOSED but never bypass what is ALLOWED. Output is hard-capped so it cannot blow the Tier-2
/// small-token budget.</para>
/// </summary>
public static class SkillRetrieval
{
    /// <summary>Flag key read by <c>InferencePromptBuilder</c> to render the worked examples.</summary>
    public const string KnownSkillsFlag = "known_skills";

    /// <summary>Max worked examples injected (the spec's 1–3).</summary>
    public const int DefaultMaxExamples = 3;

    /// <summary>Total char budget for the rendered block — keeps the Tier-2 prompt bounded.</summary>
    public const int MaxBlockChars = 600;

    private const int MaxStepsPerExample = 8;   // a worked example is a sketch, not a full replay log
    private const int MinTokenLen = 3;          // ignore 1–2 char noise tokens when scoring

    /// <summary>Tokens that carry no task signal — excluded from both query and skill token sets so a
    /// shared "the"/"click" doesn't fake an overlap. Small on purpose (an extra entry could mask a real
    /// match; a missing one only adds harmless noise to the score).</summary>
    private static readonly HashSet<string> Stopwords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "with", "then", "this", "that", "from", "into", "when",
        "click", "type", "press", "open", "select", "set", "use", "tab", "field",
        "process", "name", "exe", "task", "navigate", "screen", "window", "button",
    };

    /// <summary>
    /// Rank the candidate skills against the live query and render the top-K worked examples as the
    /// <see cref="KnownSkillsFlag"/> value, or null when nothing is relevant (caller omits the flag).
    /// <paramref name="candidates"/> is what <c>AgentStateDb.GetVerifiedSkillsForApp</c> returns.
    /// </summary>
    public static string? BuildKnownSkillsFlag(
        string goal,
        string? windowTitle,
        IReadOnlyList<(string SkillId, string TaskKey, string StepsJson, int SuccessCount)> candidates,
        int maxExamples = DefaultMaxExamples)
    {
        var ranked = Rank(goal, windowTitle, candidates, maxExamples);
        if (ranked.Count == 0) return null;

        var sb = new StringBuilder();
        foreach (var r in ranked)
        {
            if (r.Transcript.Length == 0) continue;
            // Bound the WHOLE block: stop before overshooting the budget rather than truncating an
            // example mid-arrow (a clipped "click 7 → cli" is misleading to the model).
            var addition = (sb.Length == 0 ? 0 : 2) + r.Transcript.Length; // "; " joiner
            if (sb.Length + addition > MaxBlockChars) break;
            if (sb.Length > 0) sb.Append("; ");
            sb.Append(r.Transcript);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    /// <summary>
    /// Deterministically rank candidates: lexical overlap score DESC, success_count DESC, skill_id ASC
    /// (stable tie-break). Zero-score candidates are dropped. Exposed for unit tests + observability.
    /// </summary>
    public static IReadOnlyList<RetrievedSkill> Rank(
        string goal,
        string? windowTitle,
        IReadOnlyList<(string SkillId, string TaskKey, string StepsJson, int SuccessCount)> candidates,
        int maxExamples = DefaultMaxExamples)
    {
        if (candidates is null || candidates.Count == 0) return Array.Empty<RetrievedSkill>();

        var queryTokens = Tokenize($"{goal} {windowTitle}");
        if (queryTokens.Count == 0) return Array.Empty<RetrievedSkill>();

        var scored = new List<RetrievedSkill>(candidates.Count);
        foreach (var (skillId, taskKey, stepsJson, successCount) in candidates)
        {
            var labels = ParseLabels(stepsJson, out var transcript);
            var skillTokens = Tokenize(taskKey);
            skillTokens.UnionWith(labels);
            var score = skillTokens.Count == 0 ? 0 : queryTokens.Count(t => skillTokens.Contains(t));
            if (score <= 0) continue; // an unrelated skill is prompt noise, not a free example
            scored.Add(new RetrievedSkill(skillId, score, successCount, transcript));
        }

        return scored
            .OrderByDescending(r => r.Score)
            .ThenByDescending(r => r.SuccessCount)
            .ThenBy(r => r.SkillId, StringComparer.Ordinal)
            .Take(maxExamples <= 0 ? DefaultMaxExamples : maxExamples)
            .ToList();
    }

    /// <summary>Lower-cased significant tokens (≥3 chars, non-stopword) split on non-alphanumerics.
    /// "task.calc" → {calc}; "Save As — Calculator" → {save, calculator}.</summary>
    private static HashSet<string> Tokenize(string? text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return set;
        var cur = new StringBuilder();
        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch)) cur.Append(char.ToLowerInvariant(ch));
            else FlushToken(cur, set);
        }
        FlushToken(cur, set);
        return set;
    }

    private static void FlushToken(StringBuilder cur, HashSet<string> set)
    {
        if (cur.Length >= MinTokenLen)
        {
            var tok = cur.ToString();
            if (!Stopwords.Contains(tok)) set.Add(tok);
        }
        cur.Clear();
    }

    /// <summary>
    /// Parse a banked steps_json into (a) the label token set for scoring and (b) a compact worked-example
    /// transcript ("task: click Seven → click Plus → click Eight"). Robust to malformed JSON / unparseable
    /// signatures — those contribute nothing rather than throwing (retrieval is best-effort advisory).
    /// </summary>
    private static HashSet<string> ParseLabels(string stepsJson, out string transcript)
    {
        transcript = string.Empty;
        var labels = new HashSet<string>(StringComparer.Ordinal);
        IReadOnlyList<VerifiedStep> steps;
        try { steps = VerifiedSkill.DeserializeSteps(stepsJson); }
        catch { return labels; }
        if (steps.Count == 0) return labels;

        var parts = new List<string>(Math.Min(steps.Count, MaxStepsPerExample));
        foreach (var step in steps.Take(MaxStepsPerExample))
        {
            var action = ActionSignatureParser.TryParse(step.ActionSignature);
            var verb = action?.Verb ?? VerbOf(step.ActionSignature);
            var label = action?.Parameters is { } p && p.TryGetValue("label", out var l) ? l as string : null;

            if (!string.IsNullOrEmpty(label))
            {
                foreach (var tok in Tokenize(label)) labels.Add(tok);
                parts.Add($"{ShortVerb(verb)} {label}");
            }
            else if (!string.IsNullOrEmpty(verb))
            {
                parts.Add(ShortVerb(verb));
            }
        }

        transcript = parts.Count > 0 ? string.Join(" → ", parts) : string.Empty;
        return labels;
    }

    /// <summary>"click_by_label" → "click"; "type_into_field" → "type"; "press_keys" → "press".</summary>
    private static string ShortVerb(string? verb) => verb switch
    {
        "click_by_label" or "click_by_signature" => "click",
        "type_into_field" => "type",
        "press_keys" => "press",
        "launch_sandbox_app" => "launch",
        null or "" => "act",
        _ => verb,
    };

    private static string? VerbOf(string signature)
    {
        if (string.IsNullOrEmpty(signature)) return null;
        var open = signature.IndexOf('(');
        return open > 0 ? signature[..open] : null;
    }
}
