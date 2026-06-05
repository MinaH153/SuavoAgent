using System;
using System.Collections.Generic;
using System.Linq;

namespace SuavoAgent.Core.Agentic.Adapters;

/// <summary>
/// Grounds a Tier-2 (LLM) freeform action intent to a CONCRETE perceived element. The LLM reasons
/// "click Save"; this binds that to the actual on-screen element's signature
/// (<c>controlType</c> + <c>automationId</c>) so the strict actuation verbs (<c>click_by_signature</c>)
/// can execute it. This is THE bridge that lets the SAME reasoning brain act on ANY app — the model
/// names intent, grounding resolves it to what is literally on screen.
///
/// The LLM emits freeform parameters (it references the target by name/signature, e.g.
/// <c>{"element":"saveBtn"}</c> or <c>{"target":"Button|saveBtn"}</c>); the strict verbs need an exact
/// AutomationId + ControlType. Without this layer a small model can't actuate even when it reasons
/// correctly. Returns null when NO visible element matches — the loop then counts a no-progress strike
/// rather than acting on a hallucinated target (fail-closed: never invent an element that isn't there).
/// </summary>
public static class ActionGrounding
{
    /// <summary>
    /// Resolves an LLM click intent to grounded <c>click_by_signature</c> parameters
    /// (controlType + automationId + process_name), or null if no perceived element matches.
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? GroundClick(
        IReadOnlyDictionary<string, string> llmParams, PerceivedScreen? screen, string? processName)
    {
        if (llmParams is null) return null;
        if (string.IsNullOrWhiteSpace(processName)) return null;
        if (screen?.Signatures is not { Count: > 0 } signatures) return null;

        // The LLM's target hints = every non-empty parameter value it produced. It references the
        // element by name/signature, so the value(s) are what we match against the perceived screen.
        var hints = llmParams.Values
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .ToList();
        if (hints.Count == 0) return null;

        var match = BestMatch(signatures, hints);
        if (match is null) return null;

        // Signatures are "controlType|automationId" (PerceivedScreenMapper / SimulatedAppPerceiver).
        var parts = match.Split('|', 2);
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            return null;

        return new Dictionary<string, object?>
        {
            ["controlType"] = parts[0].Trim(),
            ["automationId"] = parts[1].Trim(),
            ["process_name"] = processName,
        };
    }

    // Match priority (stable — first perceived signature wins on ties):
    //   1. exact full-signature OR exact automationId,
    //   2. case-insensitive substring either way (loose reference, e.g. "save" → "saveBtn").
    private static string? BestMatch(IReadOnlyList<string> signatures, IReadOnlyList<string> hints)
    {
        foreach (var sig in signatures)
        {
            var aid = Aid(sig);
            foreach (var h in hints)
                if (string.Equals(h, sig, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(h, aid, StringComparison.OrdinalIgnoreCase))
                    return sig;
        }
        foreach (var sig in signatures)
        {
            var aid = Aid(sig);
            if (string.IsNullOrEmpty(aid)) continue;
            foreach (var h in hints)
                if (h.Length >= 2 &&
                    (aid.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                     h.Contains(aid, StringComparison.OrdinalIgnoreCase)))
                    return sig;
        }
        return null;
    }

    private static string Aid(string signature)
        => signature.Contains('|') ? signature.Split('|', 2)[1] : signature;
}
