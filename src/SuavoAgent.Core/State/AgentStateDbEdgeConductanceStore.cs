using System.Collections.Generic;
using System.Linq;
using SuavoAgent.Core.Agentic;

namespace SuavoAgent.Core.State;

/// <summary>
/// Durable <see cref="IEdgeConductanceStore"/> backed by <see cref="AgentStateDb"/> — the persistent
/// slime-mold trail memory that survives restart (the in-memory store is for tests / per-process cache).
/// Adapts the DB's primitive (pharmacy, task, state, action) columns to <see cref="EdgeKey"/>. The DB
/// layer stays free of Agentic types; this is the one seam that bridges them. Reinforcement/decay still
/// flow through <see cref="ConductanceLaw"/> via <see cref="EdgeReinforcement"/>.
/// </summary>
public sealed class AgentStateDbEdgeConductanceStore : IEdgeConductanceStore
{
    private readonly AgentStateDb _db;

    public AgentStateDbEdgeConductanceStore(AgentStateDb db) => _db = db;

    public double Get(EdgeKey edge, double absent) =>
        _db.GetEdgeConductance(edge.PharmacyId, edge.TaskKey, edge.StateHash, edge.ActionSig, absent);

    public void Set(EdgeKey edge, double conductance) =>
        _db.SetEdgeConductance(edge.PharmacyId, edge.TaskKey, edge.StateHash, edge.ActionSig, conductance);

    public IReadOnlyCollection<EdgeKey> Edges(string pharmacyId, string taskKey) =>
        _db.GetEdgeConductanceKeys(pharmacyId, taskKey)
           .Select(k => new EdgeKey(pharmacyId, taskKey, k.StateHash, k.ActionSig))
           .ToList();
}
