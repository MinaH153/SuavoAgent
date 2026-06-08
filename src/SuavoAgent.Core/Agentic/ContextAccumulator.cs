using System;
using System.Collections.Generic;
using System.Linq;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Pure, immutable working-memory builder. Every method returns a NEW
/// <see cref="WorkingMemory"/> (never mutates the prior). History is bounded to the
/// memory window so the cloud-reasoning prompt stays small and cheap. The
/// consecutive-no-progress run-length is tracked as a scalar so it survives history
/// trimming and is the stuck signal that replaces blind retry.
/// </summary>
public static class ContextAccumulator
{
    public static WorkingMemory Start(AgentObjective objective)
        => new(objective, LatestScreen: null, History: Array.Empty<StepRecord>(),
               ConsecutiveNoProgress: 0, PerceiveNullStrikes: 0);

    /// <summary>Fold a fresh scrubbed observation in; resets the null-perception strike run.</summary>
    public static WorkingMemory RecordObservation(WorkingMemory prior, PerceivedScreen observed)
        => prior with { LatestScreen = observed, PerceiveNullStrikes = 0 };

    /// <summary>A null perception is a fail-closed strike, not "empty screen".</summary>
    public static WorkingMemory RecordPerceiveNull(WorkingMemory prior)
        => prior with { PerceiveNullStrikes = prior.PerceiveNullStrikes + 1 };

    /// <summary>
    /// Append a completed step. <paramref name="decisionScreenHash"/> is the hash of the
    /// screen the action was chosen on; together with the action signature it defines
    /// "the same move". K identical consecutive moves ⇒ no progress.
    /// </summary>
    public static WorkingMemory RecordStep(
        WorkingMemory prior,
        string decisionScreenHash,
        NextAction action,
        ActStatus? outcome,
        PostconditionVerdict? verdict,
        int memoryWindow)
    {
        // Capture the action structurally (verb + stringified params) alongside its signature, so a banked
        // skill can reconstruct the EXACT action on replay without parsing the unescaped signature string.
        var actionParams = action.Parameters is { Count: > 0 }
            ? action.Parameters.ToDictionary(kv => kv.Key, kv => kv.Value?.ToString() ?? string.Empty)
            : null;
        var record = new StepRecord(
            decisionScreenHash, action.Signature(), outcome, verdict,
            ActionVerb: action.Kind == NextActionKind.Act ? action.Verb : null,
            ActionParams: actionParams);

        var history = prior.History.Append(record).ToList();
        if (history.Count > memoryWindow)
            history = history.Skip(history.Count - memoryWindow).ToList();

        var last = prior.History.Count > 0 ? prior.History[^1] : null;
        var sameMove = last is not null
            && last.DecisionScreenHash == record.DecisionScreenHash
            && last.ActionSignature == record.ActionSignature;
        var consecutive = sameMove ? prior.ConsecutiveNoProgress + 1 : 1;

        return prior with { History = history, ConsecutiveNoProgress = consecutive };
    }

    /// <summary>True once K consecutive identical (screenHash, action) moves have stacked up.</summary>
    public static bool IsNoProgress(WorkingMemory memory, int window)
        => memory.ConsecutiveNoProgress >= window;
}
