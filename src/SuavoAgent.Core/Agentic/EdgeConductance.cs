using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SuavoAgent.Core.Agentic;

/// <summary>
/// Identity of one (state → action) exploration edge: which pharmacy + task, the perceived-screen
/// content hash (the state), and the action signature. Value-equality (record struct) makes it a
/// clean dictionary / table key. PHI-safe: both hash + signature are scrubbed structural strings.
/// </summary>
public readonly record struct EdgeKey(string PharmacyId, string TaskKey, string StateHash, string ActionSig);

/// <summary>
/// Persistent conductance ("tube thickness" / externalized trail memory) for exploration edges — the
/// slime-mold substrate. A missing edge reads as the caller-supplied <c>absent</c> default (the Floor),
/// so a never-seen edge is revivable, not dead. Reinforcement/decay go through <see cref="ConductanceLaw"/>
/// (verified-only). Production replay of a KNOWN verified template does NOT touch this — conductance
/// governs frontier EXPLORATION only.
/// </summary>
public interface IEdgeConductanceStore
{
    /// <summary>Current conductance for the edge, or <paramref name="absent"/> if never seen.</summary>
    double Get(EdgeKey edge, double absent);

    /// <summary>Persist the edge's conductance (callers pass an already-clamped value from the law).</summary>
    void Set(EdgeKey edge, double conductance);

    /// <summary>All known edges for a (pharmacy, task) — drives the periodic evaporation sweep.</summary>
    IReadOnlyCollection<EdgeKey> Edges(string pharmacyId, string taskKey);
}

/// <summary>
/// In-memory conductance store — used by tests and as a per-process cache. Durable persistence
/// (survives restart) is the SQLite-backed store in AgentStateDb; this is the same contract.
/// </summary>
public sealed class InMemoryEdgeConductanceStore : IEdgeConductanceStore
{
    private readonly ConcurrentDictionary<EdgeKey, double> _edges = new();

    public double Get(EdgeKey edge, double absent) => _edges.TryGetValue(edge, out var v) ? v : absent;

    public void Set(EdgeKey edge, double conductance) => _edges[edge] = conductance;

    public IReadOnlyCollection<EdgeKey> Edges(string pharmacyId, string taskKey) =>
        _edges.Keys.Where(k => k.PharmacyId == pharmacyId && k.TaskKey == taskKey).ToList();
}
