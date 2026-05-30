using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace SuavoAgent.Core.Workers;

/// <summary>
/// Process-wide registry of supervised-worker health. Each <see cref="ResilientHostedService"/>
/// records its restart count + escalation + last-fault time here on every supervised restart;
/// <see cref="HealthSnapshot"/> surfaces it to the heartbeat so the cloud can detect a
/// restart-looping or escalated worker (closed-loop remediation) instead of only a fully-silent
/// agent. Registered as a DI singleton; thread-safe.
/// </summary>
public sealed class WorkerHealthRegistry
{
    private readonly ConcurrentDictionary<string, WorkerHealth> _workers = new();

    /// <summary>Record (or overwrite) the latest fault state for <paramref name="worker"/>.</summary>
    public void RecordFault(string worker, int restartCount, bool escalated, DateTimeOffset faultedAtUtc)
        => _workers[worker] = new WorkerHealth(worker, restartCount, escalated, faultedAtUtc);

    /// <summary>Point-in-time copy of every worker that has faulted this process lifetime.</summary>
    public IReadOnlyList<WorkerHealth> Snapshot() => _workers.Values.ToArray();
}

/// <summary>Latest fault state for one supervised worker. Lifetime-cumulative, not per-fault.</summary>
public sealed record WorkerHealth(string Name, int RestartCount, bool Escalated, DateTimeOffset LastFaultUtc);
