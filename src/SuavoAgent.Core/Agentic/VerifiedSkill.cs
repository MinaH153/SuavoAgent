using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// One step of a banked verified skill: the perceived-screen content hash (the state) and the action
/// signature that VERIFIABLY advanced it (the action). PHI-safe — both are scrubbed structural strings.
/// The signature is self-contained (verb + ordered params, e.g. <c>click_by_label(label=Seven,process_name=calc.exe)</c>)
/// so a future replayer can execute it directly without re-deriving anything.
/// </summary>
/// <param name="Verb">The action verb for airtight replay reconstruction (e.g. "click_by_label"); null on
/// legacy sig-only banked rows, which replay via the <see cref="ActionSignatureParser"/> fallback.</param>
/// <param name="ParamsJson">The action's params as a JSON object string (delimiter-safe, unlike the
/// signature) — replay deserializes it to rebuild the EXACT action even when a value contains "," or "=".</param>
public readonly record struct VerifiedStep(string StateHash, string ActionSignature, string? Verb = null, string? ParamsJson = null);

/// <summary>
/// The amortize ratchet's artifact (lever 3 of the moat: "derive a task's decomposition ONCE → bank it
/// as a skill → replay forever"). A VerifiedSkill is the ordered (state → action) chain of a run that
/// reached its objective with EVERY banked step execution-verified (Phase-1 <c>Met</c>). It is the
/// explicit, named, promotable form of what the conductance store learns implicitly — in the explorer's
/// own vocabulary (label/signature clicks), NOT the legacy DFG <c>WorkflowTemplate</c> shape.
///
/// <para><see cref="SkillId"/> is deterministic over (pharmacy, task, app, steps) so re-verifying the
/// SAME path upserts the SAME row and just thickens its success count — the tube getting reinforced.</para>
/// </summary>
public sealed record VerifiedSkill(
    string SkillId,
    string PharmacyId,
    string TaskKey,
    string App,
    IReadOnlyList<VerifiedStep> Steps,
    string StepsHash)
{
    public static VerifiedSkill Create(string pharmacyId, string taskKey, string app, IReadOnlyList<VerifiedStep> steps)
    {
        if (steps is null || steps.Count == 0)
            throw new ArgumentException("A verified skill must have at least one step", nameof(steps));
        var stepsHash = ComputeStepsHash(steps);
        var skillId = ComputeSkillId(pharmacyId, taskKey, app, stepsHash);
        return new VerifiedSkill(skillId, pharmacyId, taskKey, app ?? string.Empty, steps, stepsHash);
    }

    /// <summary>Canonical hash of the ordered (state, action) chain — same path ⇒ same hash.</summary>
    public static string ComputeStepsHash(IReadOnlyList<VerifiedStep> steps)
    {
        var text = string.Join('\n', steps.Select(s => $"{s.StateHash}\t{s.ActionSignature}"));
        return Hex(text);
    }

    /// <summary>Deterministic id = SHA-256(pharmacy|task|app|stepsHash). Stable across runs for dedup/upsert.</summary>
    public static string ComputeSkillId(string pharmacyId, string taskKey, string app, string stepsHash)
        => Hex($"{pharmacyId}|{taskKey}|{app}|{stepsHash}");

    public string SerializeSteps() => JsonSerializer.Serialize(Steps);

    public static IReadOnlyList<VerifiedStep> DeserializeSteps(string json)
        => JsonSerializer.Deserialize<List<VerifiedStep>>(json) ?? new List<VerifiedStep>();

    private static string Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}
