using System;
using System.Collections.Generic;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// One executable frontier candidate the <see cref="PhysarumActionPolicy"/> may select. <see cref="ActionSig"/>
/// equals the <see cref="NextAction.Signature()"/> of the action it would emit, so a recorded
/// <see cref="EdgeKey.ActionSig"/> lines up with an enumerated candidate across runs (the conductance lookup key).
/// </summary>
public readonly record struct CandidateAction(string Verb, IReadOnlyDictionary<string, object?> Parameters, string ActionSig);

/// <summary>
/// Pure frontier candidate generator: from a scrubbed <see cref="PerceivedScreen"/> it produces the
/// EXECUTABLE click actions the slime-mold explorer ranks over — bound to a sandbox process. No store
/// reads, no inference. v1 is CLICK-ONLY by design: typing exploration is deferred until the Helper binds
/// type/press to a resolved target window (Codex Q1 — blind type/press can leak to the foreground window).
///
/// <para>Two sources: (1) every non-empty <see cref="PerceivedScreen.Signatures"/> entry ("controlType|automationId")
/// → a <c>click_by_signature</c> candidate; (2) every "role:name" line in <see cref="PerceivedScreen.ElementSummary"/>
/// whose role is not "text" → a <c>click_by_label</c> candidate. Labels are trimmed (consistent EdgeKey across
/// runs) but never altered otherwise — the label IS the actuation target. Deduped by <see cref="CandidateAction.ActionSig"/>
/// and capped to <paramref name="maxCandidates"/> to bound the O(elements) cost on a low-spec box.</para>
/// </summary>
public static class CandidateEnumerator
{
    public const int DefaultMaxCandidates = 50;

    public static IReadOnlyList<CandidateAction> Enumerate(
        PerceivedScreen? screen,
        IReadOnlySet<string> allowedVerbs,
        string processName,
        int maxCandidates = DefaultMaxCandidates)
    {
        var result = new List<CandidateAction>();
        if (screen is null || string.IsNullOrWhiteSpace(processName) || maxCandidates <= 0) return result;
        // v1 explore actuation is click-only; "Click" is the loop's whitelist token for the click verbs.
        if (allowedVerbs is null || !allowedVerbs.Contains("Click")) return result;

        var seen = new HashSet<string>(StringComparer.Ordinal);

        // (1) click_by_signature from structural signatures ("controlType|automationId").
        if (screen.Signatures is { Count: > 0 } sigs)
        {
            foreach (var sig in sigs)
            {
                if (string.IsNullOrWhiteSpace(sig)) continue;
                var parts = sig.Split('|', 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
                    continue;
                if (!TryAdd(result, seen, "click_by_signature", new Dictionary<string, object?>
                    {
                        ["controlType"] = parts[0].Trim(),
                        ["automationId"] = parts[1].Trim(),
                        ["process_name"] = processName,
                    }, maxCandidates))
                    return result;
            }
        }

        // (2) click_by_label from "role:name" element lines (skip "text:" content lines).
        if (screen.ElementSummary is { } elements)
        {
            foreach (var line in elements)
            {
                var label = ExtractClickableLabel(line);
                if (label is null) continue;
                if (!TryAdd(result, seen, "click_by_label", new Dictionary<string, object?>
                    {
                        ["label"] = label,
                        ["process_name"] = processName,
                    }, maxCandidates))
                    return result;
            }
        }

        return result;
    }

    /// <summary>"role:name" → trimmed name; "text:…" or unparseable → null (text isn't a click target).</summary>
    private static string? ExtractClickableLabel(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var idx = line.IndexOf(':');
        if (idx <= 0 || idx == line.Length - 1) return null;
        var role = line[..idx].Trim();
        if (role.Equals("text", StringComparison.OrdinalIgnoreCase)) return null;
        var name = line[(idx + 1)..].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>Adds a deduped candidate; returns false once the cap is reached (caller stops).</summary>
    private static bool TryAdd(
        List<CandidateAction> result, HashSet<string> seen, string verb,
        IReadOnlyDictionary<string, object?> p, int max)
    {
        var sig = NextAction.Act(verb, p).Signature();
        if (seen.Add(sig)) result.Add(new CandidateAction(verb, p, sig));
        return result.Count < max;
    }
}
