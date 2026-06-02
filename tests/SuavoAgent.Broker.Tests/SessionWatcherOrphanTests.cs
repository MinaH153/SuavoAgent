using System;
using System.Collections.Generic;
using SuavoAgent.Broker;
using Xunit;

namespace SuavoAgent.Broker.Tests;

// Orphan-Helper reconciliation guard.
//
// The bug (pilot box, 2026-06-01): after an OTA self-update the agent swapped +
// regenerated the manifest correctly and restarted — but the OLD Helper survived
// as an orphan (a CreateProcessAsUser child isn't killed when its launching
// Broker exits). It kept the Core<->Helper IPC named pipe bound, so the FRESH
// Helper the new Broker launched couldn't take the pipe, and Core saw
// `ipc_unreachable` — the agent looked updated but couldn't render/act. Recovery
// needed a full stop+restart (upgrade.ps1).
//
// Fix: at Broker startup the _helpers map is empty (this Broker has launched
// none yet), so EVERY running SuavoAgent.Helper is an orphan from a dead prior
// instance → kill it before launching ours. These guard the pure decision.
public class SessionWatcherOrphanTests
{
    [Fact]
    public void FreshStart_AllRunningHelpersAreOrphans()
    {
        // _helpers empty at startup => every running Helper is a prior-instance orphan.
        var orphans = SessionWatcher.OrphanHelperPids(
            runningHelperPids: new[] { 7828, 21552 },
            trackedHelperPids: Array.Empty<int>());

        Assert.Equal(new HashSet<int> { 7828, 21552 }, orphans);
    }

    [Fact]
    public void TrackedHelperIsNotAnOrphan()
    {
        // Once this Broker has launched + is tracking a Helper, it is NOT an orphan;
        // only untracked strays get killed.
        var orphans = SessionWatcher.OrphanHelperPids(
            runningHelperPids: new[] { 100, 200 },
            trackedHelperPids: new[] { 100 });

        Assert.Equal(new HashSet<int> { 200 }, orphans);
    }

    [Fact]
    public void NoRunningHelpers_NoOrphans()
    {
        var orphans = SessionWatcher.OrphanHelperPids(
            runningHelperPids: Array.Empty<int>(),
            trackedHelperPids: Array.Empty<int>());

        Assert.Empty(orphans);
    }
}
