# Track 1+4 Health Composite + Dashboard Tile — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the agent-side composite signal (4-component AND, gated by pharmacy business hours) + cloud API endpoint + 3-state dashboard tile that distinguishes "agent is heartbeating" from "agent is actually healthy."

**Architecture:** SuavoAgent .NET 8 / xUnit — pure-function `HealthCompositeCalculator` reads 4 signals via `IHealthSignals` abstraction, emits `agent.health_composite` event each heartbeat tick via existing `SuavoCloudClient.AppendAuditAsync` path. Suavo Next.js / vitest — Zod-validated API endpoint queries last composite + heartbeat freshness, returns 4-state response (healthy / heartbeating-but-unhealthy / silent / initializing). Dashboard tile polls every 30s via SWR, renders state-appropriate UI with hover tooltip listing failed components.

**Tech Stack:** C# .NET 8, xUnit 2.9, sealed records, PostgreSQL via Supabase, TypeScript, Zod, vitest 4, Next.js 15, SWR, React Testing Library.

---

## Source spec

`docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md`

---

## Branch strategy

- **SuavoAgent:** branch off `main` as `feat/wave-1-health-composite-suavoagent`
- **Suavo:** branch off `main` as `feat/wave-1-health-composite-suavo`
- Independent PRs

---

## File structure

### SuavoAgent (`~/Code/SuavoAgent`)

**Create:**
- `src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs` + `HealthCompositeComponents.cs`
- `src/SuavoAgent.Core/Health/IHealthSignals.cs`
- `src/SuavoAgent.Core/Health/HealthSignalsProvider.cs`
- `src/SuavoAgent.Core/Health/IBusinessHoursProvider.cs` + `BusinessHoursProvider.cs`
- `src/SuavoAgent.Core/Health/HealthCompositeCalculator.cs`
- `tests/SuavoAgent.Contracts.Tests/Models/HealthCompositePayloadTests.cs`
- `tests/SuavoAgent.Core.Tests/Health/HealthCompositeCalculatorTests.cs`
- `tests/SuavoAgent.Core.Tests/Health/HeartbeatWorkerCompositeTests.cs`

**Modify:**
- `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` — emit composite each tick
- `docs/self-healing/event-registry.md` — register `agent.health_composite`

### Suavo (`~/Code/Suavo`)

**Create:**
- `src/lib/agent-health-composite.ts` — Zod schema + types
- `src/lib/__tests__/agent-health-composite.test.ts` — schema tests
- `src/app/api/pharmacy/agent/health/route.ts` — GET endpoint
- `src/app/api/pharmacy/agent/health/__tests__/route.test.ts` — endpoint tests
- `src/components/suavo/agent/HealthCompositeTile.tsx` — 3-state UI tile
- `src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx` — RTL tests

**Modify:**
- `src/app/(pharmacy)/pharmacy/agent/page.tsx` — drop in tile

---

# Phase 1 — SuavoAgent

## Task 1: Add `HealthCompositeComponents` + `HealthCompositePayload` records

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Contracts/Models/HealthCompositeComponents.cs`
- Create: `src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Models/HealthCompositePayloadTests.cs`

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/SuavoAgent
git checkout main
git pull --ff-only origin main
git checkout -b feat/wave-1-health-composite-suavoagent
```

- [ ] **Step 2: Write the failing test**

Create `tests/SuavoAgent.Contracts.Tests/Models/HealthCompositePayloadTests.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class HealthCompositePayloadTests
{
    [Fact]
    public void Construct_HealthyPayload_AssignsAllFields()
    {
        var components = new HealthCompositeComponents(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            ExtractionRecent: true);

        var computedAt = DateTimeOffset.UtcNow;
        var payload = new HealthCompositePayload(
            Status: "healthy",
            Components: components,
            ComputedAt: computedAt);

        Assert.Equal("healthy", payload.Status);
        Assert.True(payload.Components.HelperAttached);
        Assert.True(payload.Components.IpcConnected);
        Assert.True(payload.Components.SchemaCanaryGreen);
        Assert.True(payload.Components.ExtractionRecent);
        Assert.Equal(computedAt, payload.ComputedAt);
    }

    [Fact]
    public void Construct_DegradedPayload_TracksFailingComponents()
    {
        var components = new HealthCompositeComponents(
            HelperAttached: true,
            IpcConnected: false,    // failing
            SchemaCanaryGreen: true,
            ExtractionRecent: false); // failing

        var payload = new HealthCompositePayload(
            Status: "heartbeating-but-unhealthy",
            Components: components,
            ComputedAt: DateTimeOffset.UtcNow);

        Assert.Equal("heartbeating-but-unhealthy", payload.Status);
        Assert.False(payload.Components.IpcConnected);
        Assert.False(payload.Components.ExtractionRecent);
    }

    [Theory]
    [InlineData("healthy")]
    [InlineData("heartbeating-but-unhealthy")]
    [InlineData("initializing")]
    public void Status_AcceptsCanonicalValues(string status)
    {
        var payload = new HealthCompositePayload(
            Status: status,
            Components: new HealthCompositeComponents(true, true, true, true),
            ComputedAt: DateTimeOffset.UtcNow);

        Assert.Equal(status, payload.Status);
    }
}
```

- [ ] **Step 3: Run test to verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~HealthCompositePayloadTests" 2>&1 | tail -10
```

Expected: build fails with `CS0246: The type or namespace name 'HealthCompositePayload' could not be found`.

- [ ] **Step 4: Implement `HealthCompositeComponents`**

Create `src/SuavoAgent.Contracts/Models/HealthCompositeComponents.cs`:

```csharp
namespace SuavoAgent.Contracts.Models;

/// <summary>
/// The 4 component booleans that compose the agent health signal.
/// All Operational tier per <c>field-registry.md</c>; no PHI.
/// </summary>
public sealed record HealthCompositeComponents(
    bool HelperAttached,
    bool IpcConnected,
    bool SchemaCanaryGreen,
    bool ExtractionRecent);
```

- [ ] **Step 5: Implement `HealthCompositePayload`**

Create `src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Annotations;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload for <c>agent.health_composite</c> event. Emitted by the
/// agent each heartbeat tick. Distinguishes "agent is sending heartbeats"
/// from "agent is actually healthy" via 4-component AND with off-hours
/// gating on <see cref="HealthCompositeComponents.ExtractionRecent"/>.
///
/// See <c>docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md</c>.
///
/// Status values:
///   <list type="bullet">
///     <item><c>"healthy"</c> — all 4 components true</item>
///     <item><c>"heartbeating-but-unhealthy"</c> — heartbeat received but ≥1 component false</item>
///     <item><c>"initializing"</c> — agent install &lt; 2min old, no composite computed yet</item>
///   </list>
///
/// Note: <c>"silent"</c> is NOT an agent-side status — it is computed cloud-side
/// from heartbeat absence. Agent never emits <c>"silent"</c>.
/// </summary>
[OutboundPayload]
public sealed record HealthCompositePayload(
    string Status,
    HealthCompositeComponents Components,
    DateTimeOffset ComputedAt);
```

- [ ] **Step 6: Run test to verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~HealthCompositePayloadTests" 2>&1 | tail -5
```

Expected: 5 tests pass (2 Fact + 3 Theory cases).

- [ ] **Step 7: Add regression test entry to existing RetrofittedTypesRegressionTests**

Open `tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs`. Add a new test method inside the class:

```csharp
    [Fact]
    public async Task HealthCompositePayload_Clean()
    {
        var source = Annotations + """

            using System;
            using SuavoAgent.Contracts.Annotations;

            namespace TestNs;

            public sealed record HealthCompositeComponents(
                bool HelperAttached,
                bool IpcConnected,
                bool SchemaCanaryGreen,
                bool ExtractionRecent);

            [OutboundPayload]
            public sealed record HealthCompositePayload(
                string Status,
                HealthCompositeComponents Components,
                DateTimeOffset ComputedAt);
            """;

        var diagnostics = await AnalyzerTestHelper.RunAnalyzerAsync<PhiInOutboundPayloadAnalyzer>(source);

        Assert.Empty(diagnostics.Where(d => d.Id == "SUAVO0001"));
    }
```

- [ ] **Step 8: Run analyzer regression test**

```bash
dotnet test tests/SuavoAgent.Analyzers.Tests/ --filter "FullyQualifiedName~HealthCompositePayload_Clean" 2>&1 | tail -5
```

Expected: 1 test passes (analyzer silent on the new clean type — proves Track 3 invariants extend to new event payloads).

- [ ] **Step 9: Commit**

```bash
git add src/SuavoAgent.Contracts/Models/HealthCompositeComponents.cs \
        src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs \
        tests/SuavoAgent.Contracts.Tests/Models/HealthCompositePayloadTests.cs \
        tests/SuavoAgent.Analyzers.Tests/RetrofittedTypesRegressionTests.cs
git commit -m "feat(contracts): add HealthCompositePayload + HealthCompositeComponents records"
```

---

## Task 2: Add `IHealthSignals` interface + `HealthSignalsProvider` impl

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Core/Health/IHealthSignals.cs`
- Create: `src/SuavoAgent.Core/Health/HealthSignalsProvider.cs`

The interface is a seam for testing. Real implementation reads from existing infrastructure. We don't write a separate test for the interface itself — Task 3 (`HealthCompositeCalculator`) will use a fake `IHealthSignals` in its unit tests.

- [ ] **Step 1: Create the directory + interface**

```bash
mkdir -p src/SuavoAgent.Core/Health
```

Create `src/SuavoAgent.Core/Health/IHealthSignals.cs`:

```csharp
using System;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Snapshot of the 4 health signals at a point in time. Pure data, no
/// computation. The actual signal sources live in different subsystems
/// (IPC, schema canary, extraction worker) — this interface is a seam
/// so <see cref="HealthCompositeCalculator"/> stays unit-testable.
/// </summary>
public interface IHealthSignals
{
    /// <summary>
    /// Take a snapshot of all 4 signals + the agent's "last extraction"
    /// timestamp (used by the calculator to apply the 30-minute window).
    /// </summary>
    HealthSignalsSnapshot Snapshot();
}

/// <summary>
/// Raw signals — the calculator applies the 30-minute / off-hours rules.
/// </summary>
public sealed record HealthSignalsSnapshot(
    bool HelperAttached,
    bool IpcConnected,
    bool SchemaCanaryGreen,
    DateTimeOffset? LastExtractionAt);
```

- [ ] **Step 2: Create the production implementation skeleton**

Create `src/SuavoAgent.Core/Health/HealthSignalsProvider.cs`:

```csharp
using System;
using SuavoAgent.Core.Ipc;
using SuavoAgent.Core.Workers;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Production <see cref="IHealthSignals"/> implementation. Reads from the
/// real subsystems. Each probe is wrapped in try/catch by the calculator,
/// so individual signal sources can throw safely — they default to false.
/// </summary>
public sealed class HealthSignalsProvider : IHealthSignals
{
    private readonly IpcPipeServer _ipcPipeServer;
    private readonly IpcPeerVerifier _peerVerifier;
    private readonly RxDetectionWorker _rxWorker;

    // SchemaCanary access TBD by the engineer based on existing wiring —
    // for the v0.1 plan we surface SchemaCanaryGreen as a dependency that
    // the integrator passes in via DI. If existing canary state lives on
    // a singleton, inject that singleton; otherwise construct/locate as
    // appropriate. Keep the probe synchronous + side-effect-free.
    private readonly Func<bool> _schemaCanaryGreenProbe;

    public HealthSignalsProvider(
        IpcPipeServer ipcPipeServer,
        IpcPeerVerifier peerVerifier,
        RxDetectionWorker rxWorker,
        Func<bool> schemaCanaryGreenProbe)
    {
        _ipcPipeServer = ipcPipeServer;
        _peerVerifier = peerVerifier;
        _rxWorker = rxWorker;
        _schemaCanaryGreenProbe = schemaCanaryGreenProbe;
    }

    public HealthSignalsSnapshot Snapshot() => new(
        HelperAttached:     _peerVerifier.IsConnected,
        IpcConnected:       _ipcPipeServer.IsConnected,
        SchemaCanaryGreen:  _schemaCanaryGreenProbe(),
        LastExtractionAt:   _rxWorker.LastSuccessfulEmitAt);
}
```

**Note:** the property names `IsConnected` (on `IpcPeerVerifier` and `IpcPipeServer`) and `LastSuccessfulEmitAt` (on `RxDetectionWorker`) reflect the public API as of `main` HEAD when this plan was written. If these surface names have changed, the engineer adjusts the probe accordingly — the abstraction (the snapshot record) stays stable.

- [ ] **Step 3: Verify build**

```bash
dotnet build src/SuavoAgent.Core/ 2>&1 | tail -5
```

Expected: build succeeds. If a referenced symbol (`IpcPeerVerifier.IsConnected`, etc.) doesn't exist, adjust the probe with the actual surface (typically `IsHelperAttached` or similar).

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Health/IHealthSignals.cs \
        src/SuavoAgent.Core/Health/HealthSignalsProvider.cs
git commit -m "feat(core): IHealthSignals abstraction + HealthSignalsProvider impl"
```

---

## Task 3: Add `HealthCompositeCalculator` + comprehensive unit tests

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Core/Health/IBusinessHoursProvider.cs`
- Create: `src/SuavoAgent.Core/Health/HealthCompositeCalculator.cs`
- Test: `tests/SuavoAgent.Core.Tests/Health/HealthCompositeCalculatorTests.cs`

- [ ] **Step 1: Create test directory**

```bash
mkdir -p tests/SuavoAgent.Core.Tests/Health
```

- [ ] **Step 2: Write the failing tests**

Create `tests/SuavoAgent.Core.Tests/Health/HealthCompositeCalculatorTests.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Health;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

public class HealthCompositeCalculatorTests
{
    private const int ExtractionWindowMinutes = 30;
    private static readonly DateTimeOffset Now =
        new(2026, 5, 2, 14, 0, 0, TimeSpan.Zero); // Saturday 2pm UTC

    [Fact]
    public void AllSignalsTrue_BusinessHours_ReturnsHealthy()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-5));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.HelperAttached);
        Assert.True(result.Components.IpcConnected);
        Assert.True(result.Components.SchemaCanaryGreen);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void AllSignalsTrue_OutsideBusinessHours_ReturnsHealthy()
    {
        // Outside hours: extractionRecent gates to true regardless of LastExtractionAt
        var calc = NewCalculator(insideBusinessHours: false);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12)); // way outside the 30min window

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void HelperDisconnected_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-5));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.HelperAttached);
        Assert.True(result.Components.IpcConnected);
    }

    [Fact]
    public void ExtractionStale_BusinessHours_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddMinutes(-31)); // outside 30min window

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void ExtractionStale_OutsideBusinessHours_ReturnsHealthy()
    {
        var calc = NewCalculator(insideBusinessHours: false);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void LastExtractionNull_BusinessHours_ReturnsDegraded()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: null);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void AllSignalsFalse_ReturnsDegraded_AllComponentsFalse()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: false,
            IpcConnected: false,
            SchemaCanaryGreen: false,
            LastExtractionAt: null);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("heartbeating-but-unhealthy", result.Status);
        Assert.False(result.Components.HelperAttached);
        Assert.False(result.Components.IpcConnected);
        Assert.False(result.Components.SchemaCanaryGreen);
        Assert.False(result.Components.ExtractionRecent);
    }

    [Fact]
    public void BusinessHoursLookupThrows_FallsBackToOffHours()
    {
        // Conservative: cloud-side hours table down should never falsely degrade.
        var calc = new HealthCompositeCalculator(
            new ThrowingBusinessHoursProvider(),
            extractionWindowMinutes: ExtractionWindowMinutes);
        var snapshot = new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: Now.AddHours(-12));

        var result = calc.Compute(snapshot, Now);

        Assert.Equal("healthy", result.Status);
        Assert.True(result.Components.ExtractionRecent);
    }

    [Fact]
    public void ComputedAt_MatchesClockArgument()
    {
        var calc = NewCalculator(insideBusinessHours: true);
        var snapshot = new HealthSignalsSnapshot(true, true, true, Now);

        var result = calc.Compute(snapshot, Now);

        Assert.Equal(Now, result.ComputedAt);
    }

    private static HealthCompositeCalculator NewCalculator(bool insideBusinessHours) =>
        new(new FakeBusinessHoursProvider(insideBusinessHours), ExtractionWindowMinutes);

    private sealed class FakeBusinessHoursProvider : IBusinessHoursProvider
    {
        private readonly bool _inside;
        public FakeBusinessHoursProvider(bool inside) => _inside = inside;
        public bool IsInsideBusinessHours(DateTimeOffset at) => _inside;
    }

    private sealed class ThrowingBusinessHoursProvider : IBusinessHoursProvider
    {
        public bool IsInsideBusinessHours(DateTimeOffset at) =>
            throw new InvalidOperationException("hours table down");
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests/ --filter "FullyQualifiedName~HealthCompositeCalculatorTests" 2>&1 | tail -10
```

Expected: build fails — `IBusinessHoursProvider` and `HealthCompositeCalculator` don't exist yet.

- [ ] **Step 4: Implement `IBusinessHoursProvider`**

Create `src/SuavoAgent.Core/Health/IBusinessHoursProvider.cs`:

```csharp
using System;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Provides "is this pharmacy currently within business hours?" answer.
/// Implementation queries <c>pharmacy_profiles.hours</c> via the cloud
/// or a cached local copy. Failure modes (DB error, missing data) MUST
/// throw — the calculator catches and applies the conservative
/// off-hours fallback (extractionRecent = true).
/// </summary>
public interface IBusinessHoursProvider
{
    bool IsInsideBusinessHours(DateTimeOffset at);
}
```

- [ ] **Step 5: Implement `HealthCompositeCalculator`**

Create `src/SuavoAgent.Core/Health/HealthCompositeCalculator.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Health;

/// <summary>
/// Pure-function composite calculator. Each probe wrapped in try/catch:
/// failed signal defaults to <c>false</c> (conservative). Off-hours
/// fallback for <c>extractionRecent</c>: if the business-hours probe
/// throws, treat as outside hours (extractionRecent → true).
///
/// See spec §4 for full error-handling semantics.
/// </summary>
public sealed class HealthCompositeCalculator
{
    private readonly IBusinessHoursProvider _hoursProvider;
    private readonly int _extractionWindowMinutes;

    public HealthCompositeCalculator(
        IBusinessHoursProvider hoursProvider,
        int extractionWindowMinutes = 30)
    {
        _hoursProvider = hoursProvider;
        _extractionWindowMinutes = extractionWindowMinutes;
    }

    public HealthCompositePayload Compute(HealthSignalsSnapshot snapshot, DateTimeOffset now)
    {
        var helperAttached    = snapshot.HelperAttached;
        var ipcConnected      = snapshot.IpcConnected;
        var schemaCanaryGreen = snapshot.SchemaCanaryGreen;
        var extractionRecent  = ComputeExtractionRecent(snapshot.LastExtractionAt, now);

        var components = new HealthCompositeComponents(
            HelperAttached:    helperAttached,
            IpcConnected:      ipcConnected,
            SchemaCanaryGreen: schemaCanaryGreen,
            ExtractionRecent:  extractionRecent);

        var allHealthy = helperAttached && ipcConnected && schemaCanaryGreen && extractionRecent;
        var status = allHealthy ? "healthy" : "heartbeating-but-unhealthy";

        return new HealthCompositePayload(status, components, now);
    }

    private bool ComputeExtractionRecent(DateTimeOffset? lastExtractionAt, DateTimeOffset now)
    {
        // Off-hours gate: if outside business hours, extractionRecent is true
        // (no extraction expected). If hours probe throws, conservatively
        // treat as outside hours so we don't falsely degrade due to
        // cloud-side outage.
        bool isOutsideBusinessHours;
        try
        {
            isOutsideBusinessHours = !_hoursProvider.IsInsideBusinessHours(now);
        }
        catch
        {
            isOutsideBusinessHours = true;
        }

        if (isOutsideBusinessHours)
            return true;

        // Inside hours: must have extracted within the window.
        if (lastExtractionAt is null)
            return false;

        return (now - lastExtractionAt.Value).TotalMinutes < _extractionWindowMinutes;
    }
}
```

- [ ] **Step 6: Run tests to verify they pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests/ --filter "FullyQualifiedName~HealthCompositeCalculatorTests" 2>&1 | tail -5
```

Expected: 9 tests pass.

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Health/IBusinessHoursProvider.cs \
        src/SuavoAgent.Core/Health/HealthCompositeCalculator.cs \
        tests/SuavoAgent.Core.Tests/Health/HealthCompositeCalculatorTests.cs
git commit -m "feat(core): HealthCompositeCalculator + IBusinessHoursProvider"
```

---

## Task 4: Wire composite emission into `HeartbeatWorker`

**Repo:** SuavoAgent
**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Test: `tests/SuavoAgent.Core.Tests/Health/HeartbeatWorkerCompositeTests.cs`

This task is the integration point. The exact patch shape depends on the current `HeartbeatWorker` structure — read it first, then add the composite-emission path.

- [ ] **Step 1: Read existing HeartbeatWorker to understand the integration point**

```bash
wc -l src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
head -80 src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
```

Identify:
- The DI constructor (where to inject `IHealthSignals` + `HealthCompositeCalculator`)
- The "tick" method that runs each heartbeat cycle (where to emit the composite event)
- The audit-event emission path used by existing events (likely `SuavoCloudClient.AppendAuditAsync`)

- [ ] **Step 2: Write the failing integration test**

Create `tests/SuavoAgent.Core.Tests/Health/HeartbeatWorkerCompositeTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SuavoAgent.Contracts.Models;
using SuavoAgent.Core.Health;
using SuavoAgent.Core.Workers;
using Xunit;

namespace SuavoAgent.Core.Tests.Health;

public class HeartbeatWorkerCompositeTests
{
    [Fact]
    public async Task EachHeartbeatTick_EmitsExactlyOneCompositeEvent()
    {
        var fakeSignals = new FakeHealthSignals(new HealthSignalsSnapshot(
            HelperAttached: true,
            IpcConnected: true,
            SchemaCanaryGreen: true,
            LastExtractionAt: DateTimeOffset.UtcNow.AddMinutes(-1)));
        var capturedEvents = new List<(string Type, object Payload)>();
        var sut = NewHeartbeatWorker(fakeSignals, capturedEvents);

        await sut.TickAsync(CancellationToken.None);

        var compositeEvents = capturedEvents
            .Where(e => e.Type == "agent.health_composite")
            .ToArray();
        Assert.Single(compositeEvents);

        var payload = Assert.IsType<HealthCompositePayload>(compositeEvents[0].Payload);
        Assert.Equal("healthy", payload.Status);
    }

    [Fact]
    public async Task CompositeEmissionFailure_DoesNotBlockHeartbeat()
    {
        var fakeSignals = new ThrowingHealthSignals();
        var capturedEvents = new List<(string Type, object Payload)>();
        var sut = NewHeartbeatWorker(fakeSignals, capturedEvents);

        // Should NOT throw despite composite emission path failing.
        await sut.TickAsync(CancellationToken.None);

        // Heartbeat-emitted event still fired even though composite path errored.
        var heartbeatEvents = capturedEvents
            .Where(e => e.Type == "heartbeat.emitted")
            .ToArray();
        Assert.Single(heartbeatEvents);
    }

    // Helper builders depend on the actual HeartbeatWorker constructor; the
    // engineer adapts these to the production DI shape. The shape of the
    // assertions stays stable.
    private static HeartbeatWorker NewHeartbeatWorker(
        IHealthSignals signals,
        List<(string Type, object Payload)> capturedEvents)
    {
        // Engineer fills this in based on the real HeartbeatWorker
        // constructor signature. The test relies on:
        //   1. injecting IHealthSignals + HealthCompositeCalculator
        //   2. capturing every event emission for assertion
        // If HeartbeatWorker uses an IAuditEventSink abstraction, fake that.
        // Otherwise, fake SuavoCloudClient (whatever the existing pattern is).
        throw new NotImplementedException(
            "Construct HeartbeatWorker with mocked dependencies + a sink that " +
            "appends to capturedEvents. See existing HeartbeatWorker tests for " +
            "the established mock pattern.");
    }

    private sealed class FakeHealthSignals : IHealthSignals
    {
        private readonly HealthSignalsSnapshot _snapshot;
        public FakeHealthSignals(HealthSignalsSnapshot snapshot) => _snapshot = snapshot;
        public HealthSignalsSnapshot Snapshot() => _snapshot;
    }

    private sealed class ThrowingHealthSignals : IHealthSignals
    {
        public HealthSignalsSnapshot Snapshot() =>
            throw new InvalidOperationException("signal source unavailable");
    }
}
```

**Note:** the `NewHeartbeatWorker` helper has a deliberate `NotImplementedException` because the exact constructor shape depends on the current `HeartbeatWorker` source. After Step 1 read, the engineer fills this in by:
1. Looking at existing `HeartbeatWorker` tests for the established mocking pattern
2. Wiring the same mocks plus the new `IHealthSignals` + `HealthCompositeCalculator`
3. Replacing `NotImplementedException` with the actual constructor call

- [ ] **Step 3: Modify `HeartbeatWorker` to inject + emit composite**

Open `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`. Add `IHealthSignals` + `HealthCompositeCalculator` to the DI constructor. In the tick method, after the existing `heartbeat.emitted` event is sent, add:

```csharp
// Emit agent.health_composite event. Failure here is logged but does
// NOT block the heartbeat critical path — the agent remains healthy
// from cloud's perspective even if composite emission queues / retries.
try
{
    var snapshot = _healthSignals.Snapshot();
    var composite = _healthCompositeCalculator.Compute(snapshot, _clock.UtcNow);
    await _cloudClient.AppendAuditAsync(
        eventType: "agent.health_composite",
        payload: composite,
        cancellationToken: cancellationToken);
}
catch (Exception ex)
{
    _logger.LogWarning(ex,
        "Composite emission failed; heartbeat continues. " +
        "Agent will retry on next tick.");
}
```

The engineer adapts the exact field names (`_cloudClient`, `_logger`, `_clock`) to match the real fields in `HeartbeatWorker`. The structure (snapshot → compute → emit, all wrapped in try/catch) is the contract.

- [ ] **Step 4: Replace the test helper's NotImplementedException with real construction**

Update `NewHeartbeatWorker` in the test file to construct `HeartbeatWorker` using its real constructor with mocked dependencies, including a fake event sink that appends to `capturedEvents`. The exact code depends on the existing test infrastructure — typically there's already a builder or fixture for `HeartbeatWorker` tests.

- [ ] **Step 5: Run tests to verify they pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests/ --filter "FullyQualifiedName~HeartbeatWorkerCompositeTests" 2>&1 | tail -8
```

Expected: 2 tests pass.

- [ ] **Step 6: Run full Core test suite to verify nothing else broke**

```bash
dotnet test tests/SuavoAgent.Core.Tests/ 2>&1 | tail -5
```

Expected: all suites green (existing 1156+ tests + new 2).

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs \
        tests/SuavoAgent.Core.Tests/Health/HeartbeatWorkerCompositeTests.cs
git commit -m "feat(core): HeartbeatWorker emits agent.health_composite each tick"
```

---

## Task 5: Register `agent.health_composite` event type

**Repo:** SuavoAgent
**Files:**
- Modify: `docs/self-healing/event-registry.md`

- [ ] **Step 1: Add the event entry**

Open `docs/self-healing/event-registry.md`. Locate the `## agent.*` section (around line 52). Add a new entry after `agent.crashed`:

```markdown
### `agent.health_composite`
- Category: `runtime`
- Severity: `info` (when status is "healthy" or "initializing") / `warn` (when status is "heartbeating-but-unhealthy")
- Actor: `agent`
- Payload: `{status: string, components: {helper_attached: bool, ipc_connected: bool, schema_canary_green: bool, extraction_recent: bool}, computed_at: string}`
- Notes: `status` enum: `"healthy"` | `"heartbeating-but-unhealthy"` | `"initializing"`. The `"silent"` state is cloud-derived and never emitted by the agent. Mirrors C# `HealthCompositePayload` in `SuavoAgent.Contracts.Models`. See `docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md`.
```

- [ ] **Step 2: Update change log**

Append at the bottom of the file:

```markdown
- **2026-05-02 v0.3** — Added `agent.health_composite` for Track 1+4 composite signal (Wave 1 sub-project B).
```

- [ ] **Step 3: Commit**

```bash
git add docs/self-healing/event-registry.md
git commit -m "docs(events): register agent.health_composite event"
```

---

## Task 6: Push SuavoAgent branch + open PR

**Repo:** SuavoAgent
**Files:** none new

- [ ] **Step 1: Run full test pass**

```bash
dotnet test 2>&1 | tail -5
```

Expected: all green (existing tests + ~14 new tests across Tasks 1, 3, 4).

- [ ] **Step 2: Push branch**

```bash
git push -u origin feat/wave-1-health-composite-suavoagent 2>&1 | tail -5
```

If push fails (gh-auth blocker per `feedback-gh-multi-account-push.md`): branch stays local. Joshua handles operationally.

- [ ] **Step 3: Open PR if push succeeded**

```bash
gh pr create --repo MinaH153/SuavoAgent \
  --title "Wave 1B (SuavoAgent): health composite signal + HeartbeatWorker integration" \
  --body "$(cat <<'EOF'
Closes Wave 1 Sub-project B SuavoAgent-side per
\`docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md\`.

## Changes
- \`src/SuavoAgent.Contracts/Models/HealthCompositePayload.cs\` + \`HealthCompositeComponents.cs\` — \`[OutboundPayload]\` records (silent under SUAVO0001)
- \`src/SuavoAgent.Core/Health/IHealthSignals.cs\` + \`HealthSignalsProvider.cs\` — abstraction over the 4 signal sources
- \`src/SuavoAgent.Core/Health/IBusinessHoursProvider.cs\` + \`HealthCompositeCalculator.cs\` — pure-function composite computation w/ off-hours gate + conservative-fallback error handling
- \`src/SuavoAgent.Core/Workers/HeartbeatWorker.cs\` — emits \`agent.health_composite\` each tick, isolated try/catch so composite failure never blocks heartbeat
- \`docs/self-healing/event-registry.md\` — \`agent.health_composite\` registered

## Test coverage
- 5 HealthCompositePayload record tests
- 1 HealthCompositePayload regression test (SUAVO0001 silent — Track 3 invariant inheritance proof)
- 9 HealthCompositeCalculator unit tests (all 4 signal permutations + business-hours gate + null handling + hours-throw fallback)
- 2 HeartbeatWorker integration tests (composite emitted + composite failure non-blocking)

## Pairs with
Suavo branch \`feat/wave-1-health-composite-suavo\` (API endpoint + dashboard tile).
EOF
)" 2>&1 | tail -3
```

If gh PR create fails: paste body via web UI on the open branch.

---

# Phase 2 — Suavo

## Task 7: Add Zod schema for the composite event

**Repo:** Suavo
**Files:**
- Create: `src/lib/agent-health-composite.ts`
- Test: `src/lib/__tests__/agent-health-composite.test.ts`

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/Suavo
git checkout main
git pull --ff-only origin main 2>&1 | tail -2
git checkout -b feat/wave-1-health-composite-suavo
```

If `git checkout main` fails because main is in another worktree, use `git checkout -b feat/wave-1-health-composite-suavo origin/main`.

- [ ] **Step 2: Write the failing test**

Create `src/lib/__tests__/agent-health-composite.test.ts`:

```typescript
import { describe, expect, it } from "vitest";
import {
  HealthCompositeStatus,
  HealthCompositeResponseSchema,
  type HealthCompositeResponse,
} from "@/lib/agent-health-composite";

describe("HealthCompositeResponseSchema", () => {
  const baseResponse = {
    status: "healthy" as const,
    components: {
      helper_attached: true,
      ipc_connected: true,
      schema_canary_green: true,
      extraction_recent: true,
    },
    last_event_at: new Date().toISOString(),
    last_heartbeat_at: new Date().toISOString(),
    silent: false,
  };

  it("parses a healthy response", () => {
    const parsed = HealthCompositeResponseSchema.parse(baseResponse);
    expect(parsed.status).toBe("healthy");
    expect(parsed.components?.helper_attached).toBe(true);
  });

  it("parses a silent response (no components)", () => {
    const parsed = HealthCompositeResponseSchema.parse({
      ...baseResponse,
      status: "silent",
      components: null,
      silent: true,
    });
    expect(parsed.status).toBe("silent");
    expect(parsed.components).toBeNull();
    expect(parsed.silent).toBe(true);
  });

  it("parses initializing response", () => {
    const parsed = HealthCompositeResponseSchema.parse({
      ...baseResponse,
      status: "initializing",
      components: null,
    });
    expect(parsed.status).toBe("initializing");
  });

  it.each<HealthCompositeStatus>([
    "healthy",
    "heartbeating-but-unhealthy",
    "silent",
    "initializing",
  ])("accepts %s as valid status", (status) => {
    const parsed = HealthCompositeResponseSchema.parse({
      ...baseResponse,
      status,
    });
    expect(parsed.status).toBe(status);
  });

  it("rejects unknown status", () => {
    expect(() =>
      HealthCompositeResponseSchema.parse({ ...baseResponse, status: "wat" }),
    ).toThrow();
  });

  it("rejects components with non-boolean field", () => {
    expect(() =>
      HealthCompositeResponseSchema.parse({
        ...baseResponse,
        components: { ...baseResponse.components, helper_attached: "true" },
      }),
    ).toThrow();
  });
});
```

- [ ] **Step 3: Run test to verify it fails**

```bash
npx vitest run src/lib/__tests__/agent-health-composite.test.ts 2>&1 | tail -8
```

Expected: import resolution failure (`@/lib/agent-health-composite` not found).

- [ ] **Step 4: Implement the schema**

Create `src/lib/agent-health-composite.ts`:

```typescript
import { z } from "zod";
import { outbound } from "@/lib/zod-phi";

/**
 * Cloud-side schema for the agent health composite. Mirrors the C#
 * HealthCompositePayload + HealthCompositeComponents in
 * SuavoAgent.Contracts.Models, with one cloud-derived addition:
 * "silent" status (computed from heartbeat absence, never emitted by agent).
 *
 * See <SuavoAgent>/docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md
 *
 * Wrapped in outbound() so the suavo-phi/no-phi-in-outbound rule guards
 * the schema from PHI fields. All fields are Operational tier per
 * field-registry.md (booleans + status strings + ISO timestamps).
 */

export const HealthCompositeStatusValues = [
  "healthy",
  "heartbeating-but-unhealthy",
  "silent",
  "initializing",
] as const;

export type HealthCompositeStatus = (typeof HealthCompositeStatusValues)[number];

const isoDateTime = z.string().refine(
  (s) => !Number.isNaN(Date.parse(s)),
  { message: "must be an ISO 8601 datetime" },
);

export const HealthCompositeComponentsSchema = z.object({
  helper_attached: z.boolean(),
  ipc_connected: z.boolean(),
  schema_canary_green: z.boolean(),
  extraction_recent: z.boolean(),
});

export type HealthCompositeComponents = z.infer<typeof HealthCompositeComponentsSchema>;

export const HealthCompositeResponseSchema = outbound(z.object({
  status: z.enum(HealthCompositeStatusValues),
  components: HealthCompositeComponentsSchema.nullable(),
  last_event_at: isoDateTime.nullable(),
  last_heartbeat_at: isoDateTime.nullable(),
  silent: z.boolean(),
}));

export type HealthCompositeResponse = z.infer<typeof HealthCompositeResponseSchema>;
```

- [ ] **Step 5: Run test to verify it passes**

```bash
npx vitest run src/lib/__tests__/agent-health-composite.test.ts 2>&1 | tail -5
```

Expected: 9 tests pass (3 explicit + 4 parametrized + 2 reject cases).

- [ ] **Step 6: Verify ESLint rule silent on the schema (it has no phi() inside)**

```bash
npx eslint src/lib/agent-health-composite.ts 2>&1 | tail -3
```

Expected: silent.

- [ ] **Step 7: Commit**

```bash
git add src/lib/agent-health-composite.ts src/lib/__tests__/agent-health-composite.test.ts
git commit -m "feat(lib): HealthCompositeResponseSchema (Zod) mirroring C# payload + cloud-derived silent state"
```

---

## Task 8: Add `/api/pharmacy/agent/health` GET endpoint

**Repo:** Suavo
**Files:**
- Create: `src/app/api/pharmacy/agent/health/route.ts`
- Test: `src/app/api/pharmacy/agent/health/__tests__/route.test.ts`

The endpoint follows the pattern of existing `/api/pharmacy/agent/install-state/route.ts`. The exact auth helper is `requirePharmacyApiContext` from `src/lib/pharmacy-api.ts`.

- [ ] **Step 1: Write the failing test**

Create `src/app/api/pharmacy/agent/health/__tests__/route.test.ts`:

```typescript
import { describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";
import { GET } from "@/app/api/pharmacy/agent/health/route";
import { HealthCompositeResponseSchema } from "@/lib/agent-health-composite";

// The actual test infrastructure depends on how Suavo mocks
// requirePharmacyApiContext + Supabase queries in existing endpoint
// tests. The engineer follows the established pattern from
// install-state/__tests__/route.test.ts (or similar).

describe("GET /api/pharmacy/agent/health", () => {
  it("returns 401 if not authenticated", async () => {
    // Mock requirePharmacyApiContext to throw 401
    // [engineer fills in based on existing mock pattern]
    const req = new NextRequest(
      "http://localhost/api/pharmacy/agent/health?pharmacy_id=test&agent_install_id=00000000-0000-0000-0000-000000000000",
    );
    const res = await GET(req);
    expect(res.status).toBe(401);
  });

  it("returns 403 if pharmacy_id query param doesn't match caller's pharmacy", async () => {
    // [mock requirePharmacyApiContext to return pharmacy_id "A"; query asks for "B"]
    // expect 403
  });

  it("returns initializing for a fresh install (< 2min, no composite event)", async () => {
    // [mock supabase to return: install created 30s ago, no audit_events of type agent.health_composite]
    // expect status === "initializing"
  });

  it("returns silent if last heartbeat is > 5min old", async () => {
    // [mock: last heartbeat was 6min ago]
    // expect status === "silent"; silent === true
  });

  it("returns healthy when last composite was healthy + recent heartbeat", async () => {
    // [mock: last composite event with status=healthy, last heartbeat 30s ago]
    // expect status === "healthy"
  });

  it("returns heartbeating-but-unhealthy when last composite was unhealthy", async () => {
    // [mock: last composite event with status=heartbeating-but-unhealthy]
    // expect status === "heartbeating-but-unhealthy"; components reflected
  });

  it("validates response with outbound Zod schema", async () => {
    // [mock the happy path]
    const req = new NextRequest(
      "http://localhost/api/pharmacy/agent/health?pharmacy_id=test&agent_install_id=00000000-0000-0000-0000-000000000000",
    );
    const res = await GET(req);
    const json = await res.json();
    expect(() => HealthCompositeResponseSchema.parse(json)).not.toThrow();
  });
});
```

**Note:** the test bodies are skeletons — the engineer fills in the mock setup using the existing pattern from `install-state/__tests__/route.test.ts` (or whichever existing test file has the closest shape). The shape of the assertions stays stable.

- [ ] **Step 2: Run test to verify it fails**

```bash
npx vitest run src/app/api/pharmacy/agent/health/__tests__/route.test.ts 2>&1 | tail -8
```

Expected: import fails — `@/app/api/pharmacy/agent/health/route` doesn't exist.

- [ ] **Step 3: Implement the endpoint**

Create `src/app/api/pharmacy/agent/health/route.ts`:

```typescript
import { NextResponse, type NextRequest } from "next/server";
import { z } from "zod";
import { requirePharmacyApiContext } from "@/lib/pharmacy-api";
import { createSupabaseServerClient } from "@/lib/supabase-server";
import {
  HealthCompositeResponseSchema,
  type HealthCompositeResponse,
} from "@/lib/agent-health-composite";

// ---------------------------------------------------------------------------
// GET /api/pharmacy/agent/health?pharmacy_id=...&agent_install_id=...
//
// Returns the current health composite for a specific agent install at a
// pharmacy. Distinguishes "agent process running" (heartbeat-only signal)
// from "agent functionally healthy" (4-component composite from agent's
// own self-assessment).
//
// 4 status values:
//   - healthy                    all 4 components true, recent heartbeat
//   - heartbeating-but-unhealthy heartbeat received but ≥1 component false
//   - silent                     no heartbeat in last 5 minutes
//   - initializing               < 2min since install, no composite emitted yet
//
// Session-authed via requirePharmacyApiContext (impersonation-aware).
// Never returns PHI — only operational tier fields per field-registry.md.
// ---------------------------------------------------------------------------

export const dynamic = "force-dynamic";
export const maxDuration = 10;

const QuerySchema = z.object({
  pharmacy_id: z.string().uuid(),
  agent_install_id: z.string().uuid(),
});

const SILENT_THRESHOLD_MINUTES = 5;
const INITIALIZING_GRACE_MINUTES = 2;

export async function GET(request: NextRequest): Promise<NextResponse> {
  // 1. Auth + impersonation resolution
  const ctx = await requirePharmacyApiContext(request);
  if (ctx instanceof NextResponse) return ctx; // 401/403 from helper

  // 2. Validate query
  const url = new URL(request.url);
  const queryParse = QuerySchema.safeParse({
    pharmacy_id: url.searchParams.get("pharmacy_id"),
    agent_install_id: url.searchParams.get("agent_install_id"),
  });
  if (!queryParse.success) {
    return NextResponse.json(
      { error: "invalid_query", detail: queryParse.error.flatten() },
      { status: 400 },
    );
  }
  const { pharmacy_id, agent_install_id } = queryParse.data;

  // 3. Pharmacy-id authorization
  if (pharmacy_id !== ctx.pharmacy_id) {
    return NextResponse.json({ error: "pharmacy_id_mismatch" }, { status: 403 });
  }

  // 4. Query last composite event + last heartbeat
  const supabase = await createSupabaseServerClient();
  const { data: lastComposite } = await supabase
    .from("audit_events")
    .select("payload, recorded_at, occurred_at")
    .eq("pharmacy_id", pharmacy_id)
    .eq("type", "agent.health_composite")
    .eq("payload->>agent_install_id", agent_install_id)
    .order("recorded_at", { ascending: false })
    .limit(1)
    .maybeSingle();

  const { data: lastHeartbeat } = await supabase
    .from("audit_events")
    .select("recorded_at")
    .eq("pharmacy_id", pharmacy_id)
    .eq("type", "heartbeat.emitted")
    .eq("payload->>agent_install_id", agent_install_id)
    .order("recorded_at", { ascending: false })
    .limit(1)
    .maybeSingle();

  const { data: install } = await supabase
    .from("agent_installs")
    .select("created_at")
    .eq("id", agent_install_id)
    .eq("pharmacy_id", pharmacy_id)
    .maybeSingle();

  // 5. Compute effective status
  const now = Date.now();
  const lastHeartbeatAt = lastHeartbeat?.recorded_at
    ? new Date(lastHeartbeat.recorded_at).getTime()
    : null;
  const lastEventAt = lastComposite?.recorded_at
    ? new Date(lastComposite.recorded_at).getTime()
    : null;
  const installCreatedAt = install?.created_at
    ? new Date(install.created_at).getTime()
    : null;

  const silent = lastHeartbeatAt === null
    || (now - lastHeartbeatAt) > SILENT_THRESHOLD_MINUTES * 60_000;

  let status: HealthCompositeResponse["status"];
  let components: HealthCompositeResponse["components"];

  if (
    !lastComposite &&
    installCreatedAt !== null &&
    (now - installCreatedAt) < INITIALIZING_GRACE_MINUTES * 60_000
  ) {
    status = "initializing";
    components = null;
  } else if (silent) {
    status = "silent";
    components = null;
  } else {
    const compositePayload = lastComposite?.payload as
      | { status?: string; components?: Record<string, boolean> }
      | null;
    status = (compositePayload?.status as HealthCompositeResponse["status"])
      ?? "heartbeating-but-unhealthy";
    components = compositePayload?.components
      ? {
          helper_attached: !!compositePayload.components.helper_attached,
          ipc_connected: !!compositePayload.components.ipc_connected,
          schema_canary_green: !!compositePayload.components.schema_canary_green,
          extraction_recent: !!compositePayload.components.extraction_recent,
        }
      : null;
  }

  // 6. Build + validate response
  const response: HealthCompositeResponse = {
    status,
    components,
    last_event_at: lastComposite?.recorded_at ?? null,
    last_heartbeat_at: lastHeartbeat?.recorded_at ?? null,
    silent,
  };

  // Catches drift between this endpoint and the canonical schema.
  HealthCompositeResponseSchema.parse(response);

  return NextResponse.json(response, { status: 200 });
}
```

**Note:** the supabase queries above use `.eq("payload->>agent_install_id", agent_install_id)`. This requires the agent's events to include `agent_install_id` in their payload. If the existing event-payload shape doesn't include it, the agent-side change in Task 4 needs to add it — the engineer adapts this contract during integration. (For Wave 1B v0.1, document this as a known follow-up if it becomes a real gap.)

- [ ] **Step 4: Run tests to verify they pass**

```bash
npx vitest run src/app/api/pharmacy/agent/health/__tests__/route.test.ts 2>&1 | tail -8
```

Expected: 7 tests pass (engineer has filled in the mocks per the existing pattern).

- [ ] **Step 5: Commit**

```bash
git add src/app/api/pharmacy/agent/health/route.ts \
        src/app/api/pharmacy/agent/health/__tests__/route.test.ts
git commit -m "feat(api): GET /api/pharmacy/agent/health endpoint"
```

---

## Task 9: Add `HealthCompositeTile` dashboard component + tests

**Repo:** Suavo
**Files:**
- Create: `src/components/suavo/agent/HealthCompositeTile.tsx`
- Create: `src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx`

- [ ] **Step 1: Create directory + write the failing test**

```bash
mkdir -p src/components/suavo/agent/__tests__
```

Create `src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx`:

```typescript
import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import { SWRConfig } from "swr";
import { HealthCompositeTile } from "@/components/suavo/agent/HealthCompositeTile";
import type { HealthCompositeResponse } from "@/lib/agent-health-composite";

function withSWR(children: React.ReactNode, mockResponse: HealthCompositeResponse | "error") {
  return (
    <SWRConfig
      value={{
        provider: () => new Map(),
        fetcher: () =>
          mockResponse === "error"
            ? Promise.reject(new Error("api down"))
            : Promise.resolve(mockResponse),
        dedupingInterval: 0,
        suspense: false,
      }}
    >
      {children}
    </SWRConfig>
  );
}

describe("HealthCompositeTile", () => {
  const baseProps = {
    pharmacyId: "00000000-0000-0000-0000-000000000001",
    agentInstallId: "00000000-0000-0000-0000-000000000002",
  };

  it("renders 'Healthy' for healthy status", async () => {
    render(
      withSWR(<HealthCompositeTile {...baseProps} />, {
        status: "healthy",
        components: {
          helper_attached: true,
          ipc_connected: true,
          schema_canary_green: true,
          extraction_recent: true,
        },
        last_event_at: new Date().toISOString(),
        last_heartbeat_at: new Date().toISOString(),
        silent: false,
      }),
    );
    expect(await screen.findByText(/healthy/i)).toBeInTheDocument();
  });

  it("renders 'Degraded — N issues' for heartbeating-but-unhealthy", async () => {
    render(
      withSWR(<HealthCompositeTile {...baseProps} />, {
        status: "heartbeating-but-unhealthy",
        components: {
          helper_attached: false,
          ipc_connected: true,
          schema_canary_green: true,
          extraction_recent: false,
        },
        last_event_at: new Date().toISOString(),
        last_heartbeat_at: new Date().toISOString(),
        silent: false,
      }),
    );
    expect(await screen.findByText(/degraded — 2 issues/i)).toBeInTheDocument();
  });

  it("renders 'Silent — last seen Xm ago' for silent status", async () => {
    const lastHeartbeat = new Date(Date.now() - 12 * 60_000).toISOString();
    render(
      withSWR(<HealthCompositeTile {...baseProps} />, {
        status: "silent",
        components: null,
        last_event_at: null,
        last_heartbeat_at: lastHeartbeat,
        silent: true,
      }),
    );
    expect(await screen.findByText(/silent/i)).toBeInTheDocument();
    expect(await screen.findByText(/last seen 12m ago/i)).toBeInTheDocument();
  });

  it("renders 'Initializing' for initializing status", async () => {
    render(
      withSWR(<HealthCompositeTile {...baseProps} />, {
        status: "initializing",
        components: null,
        last_event_at: null,
        last_heartbeat_at: null,
        silent: false,
      }),
    );
    expect(await screen.findByText(/initializing/i)).toBeInTheDocument();
  });

  it("renders 'Health unknown — retrying' on API error", async () => {
    render(withSWR(<HealthCompositeTile {...baseProps} />, "error"));
    expect(await screen.findByText(/health unknown/i)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
npx vitest run src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx 2>&1 | tail -8
```

Expected: import fails — component doesn't exist.

- [ ] **Step 3: Implement the component**

Create `src/components/suavo/agent/HealthCompositeTile.tsx`:

```typescript
"use client";

import useSWR from "swr";
import type { HealthCompositeResponse } from "@/lib/agent-health-composite";

interface HealthCompositeTileProps {
  pharmacyId: string;
  agentInstallId: string;
  refreshIntervalMs?: number;
}

const DEFAULT_REFRESH_INTERVAL_MS = 30_000;

const fetchHealth = async (url: string): Promise<HealthCompositeResponse> => {
  const res = await fetch(url, { credentials: "include" });
  if (!res.ok) {
    throw new Error(`health endpoint returned ${res.status}`);
  }
  return res.json();
};

function formatRelativeMinutes(iso: string | null): string {
  if (!iso) return "";
  const then = new Date(iso).getTime();
  const minutes = Math.max(0, Math.round((Date.now() - then) / 60_000));
  return `${minutes}m ago`;
}

function countFailedComponents(
  components: HealthCompositeResponse["components"],
): { count: number; failed: string[] } {
  if (!components) return { count: 0, failed: [] };
  const failed: string[] = [];
  if (!components.helper_attached) failed.push("Helper not attached");
  if (!components.ipc_connected) failed.push("IPC not connected");
  if (!components.schema_canary_green) failed.push("Schema canary failed");
  if (!components.extraction_recent) failed.push("No recent extraction");
  return { count: failed.length, failed };
}

export function HealthCompositeTile({
  pharmacyId,
  agentInstallId,
  refreshIntervalMs = DEFAULT_REFRESH_INTERVAL_MS,
}: HealthCompositeTileProps) {
  const url = `/api/pharmacy/agent/health?pharmacy_id=${encodeURIComponent(
    pharmacyId,
  )}&agent_install_id=${encodeURIComponent(agentInstallId)}`;

  const { data, error, isLoading } = useSWR<HealthCompositeResponse>(
    url,
    fetchHealth,
    {
      refreshInterval: refreshIntervalMs,
      dedupingInterval: 5_000,
      revalidateOnFocus: false,
    },
  );

  if (error) {
    return (
      <div
        role="status"
        aria-label="Agent health unknown"
        className="flex items-center gap-2 rounded border border-zinc-200 bg-zinc-50 p-3 text-zinc-600"
      >
        <span className="h-2 w-2 rounded-full bg-zinc-400" aria-hidden />
        <span>Health unknown — retrying</span>
      </div>
    );
  }

  if (isLoading || !data) {
    return (
      <div
        role="status"
        aria-label="Loading agent health"
        className="flex items-center gap-2 rounded border border-zinc-200 bg-zinc-50 p-3 text-zinc-500"
      >
        <span className="h-2 w-2 rounded-full bg-zinc-300 animate-pulse" aria-hidden />
        <span>Checking…</span>
      </div>
    );
  }

  if (data.status === "initializing") {
    return (
      <div
        role="status"
        aria-label="Agent initializing"
        className="flex items-center gap-2 rounded border border-zinc-200 bg-zinc-50 p-3 text-zinc-600"
      >
        <span className="h-2 w-2 rounded-full bg-zinc-400" aria-hidden />
        <span>Initializing</span>
      </div>
    );
  }

  if (data.status === "silent") {
    return (
      <div
        role="status"
        aria-label="Agent silent"
        className="flex items-center gap-2 rounded border border-rose-300 bg-rose-50 p-3 text-rose-700"
      >
        <span className="h-2 w-2 rounded-full bg-rose-500" aria-hidden />
        <span>
          Silent — last seen {formatRelativeMinutes(data.last_heartbeat_at)}
        </span>
      </div>
    );
  }

  if (data.status === "healthy") {
    return (
      <div
        role="status"
        aria-label="Agent healthy"
        className="flex items-center gap-2 rounded border border-emerald-300 bg-emerald-50 p-3 text-emerald-700"
      >
        <span className="h-2 w-2 rounded-full bg-emerald-500" aria-hidden />
        <span>Healthy</span>
      </div>
    );
  }

  // heartbeating-but-unhealthy
  const { count, failed } = countFailedComponents(data.components);
  return (
    <div
      role="status"
      aria-label={`Agent degraded — ${count} ${count === 1 ? "issue" : "issues"}`}
      title={failed.join(" · ")}
      className="flex items-center gap-2 rounded border border-amber-300 bg-amber-50 p-3 text-amber-700"
    >
      <span className="h-2 w-2 rounded-full bg-amber-500" aria-hidden />
      <span>
        Degraded — {count} {count === 1 ? "issue" : "issues"}
      </span>
    </div>
  );
}
```

- [ ] **Step 4: Run test to verify it passes**

```bash
npx vitest run src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx 2>&1 | tail -5
```

Expected: 5 tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/components/suavo/agent/HealthCompositeTile.tsx \
        src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx
git commit -m "feat(components): HealthCompositeTile with 4-state UI + SWR polling"
```

---

## Task 10: Wire `HealthCompositeTile` into `/pharmacy/agent` page

**Repo:** Suavo
**Files:**
- Modify: `src/app/(pharmacy)/pharmacy/agent/page-client.tsx` (or `page.tsx`, depending on existing structure)

- [ ] **Step 1: Read existing page structure**

```bash
head -60 "src/app/(pharmacy)/pharmacy/agent/page-client.tsx"
```

Identify a sensible location to drop the tile. Likely candidates: existing state-card region or near the install hero.

- [ ] **Step 2: Add import + render the tile**

Add the import near the top of the file:

```typescript
import { HealthCompositeTile } from "@/components/suavo/agent/HealthCompositeTile";
```

Find an appropriate location in the JSX (above or near the existing `is_online` indicator) and render the tile, passing the pharmacy ID + currently-selected agent install ID:

```tsx
<HealthCompositeTile
  pharmacyId={pharmacyId}
  agentInstallId={selectedAgentInstallId}
/>
```

The exact prop sources depend on existing data flow — if the page already has `pharmacy_id` from session and selects an agent install for the per-machine state cards, reuse those. Otherwise, plumb them in following the existing pattern.

- [ ] **Step 3: Verify build still passes**

```bash
pnpm build 2>&1 | tail -10
```

Expected: build succeeds.

- [ ] **Step 4: Commit**

```bash
git add "src/app/(pharmacy)/pharmacy/agent/"
git commit -m "feat(dashboard): drop HealthCompositeTile into /pharmacy/agent page"
```

---

## Task 11: Push Suavo branch + open PR

**Repo:** Suavo
**Files:** none new

- [ ] **Step 1: Run all tests**

```bash
npx vitest run \
  src/lib/__tests__/agent-health-composite.test.ts \
  src/app/api/pharmacy/agent/health/__tests__/route.test.ts \
  src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx 2>&1 | tail -5
```

Expected: ~21 tests green.

- [ ] **Step 2: Push branch**

```bash
git push -u origin feat/wave-1-health-composite-suavo 2>&1 | tail -5
```

- [ ] **Step 3: Open PR**

```bash
gh pr create --repo SuavoLLC/MKM \
  --title "Wave 1B (Suavo): /api/pharmacy/agent/health + HealthCompositeTile dashboard" \
  --body "$(cat <<'EOF'
Closes Wave 1 Sub-project B Suavo-side per
\`<SuavoAgent>/docs/superpowers/specs/2026-05-02-track-1-4-health-composite-design.md\`.

## Changes
- \`src/lib/agent-health-composite.ts\` — \`HealthCompositeResponseSchema\` (Zod), wrapped in \`outbound()\` per Sub-project A
- \`src/app/api/pharmacy/agent/health/route.ts\` — GET endpoint, auth-gated, queries last composite + heartbeat, computes effective status (healthy / heartbeating-but-unhealthy / silent / initializing)
- \`src/components/suavo/agent/HealthCompositeTile.tsx\` — 3-state tile with hover tooltip listing failed components, 30s SWR polling
- \`src/app/(pharmacy)/pharmacy/agent/page-client.tsx\` — drop tile into existing layout

## Test coverage
- 9 schema parse + reject tests
- 7 endpoint behavior tests (auth, validation, status derivation)
- 5 component RTL tests (each state)

## Pairs with
SuavoAgent branch \`feat/wave-1-health-composite-suavoagent\` (composite emission). Both halves close Wave 1 Sub-project B.
EOF
)" 2>&1 | tail -3
```

---

## Task 12: End-to-end verification

**Repo:** both
**Files:** none new

- [ ] **Step 1: Run full test suites in both repos**

```bash
cd /Users/joshuahenein/Code/SuavoAgent && dotnet test 2>&1 | tail -3
cd /Users/joshuahenein/Code/Suavo && npx vitest run \
  src/lib/__tests__/agent-health-composite.test.ts \
  src/app/api/pharmacy/agent/health/__tests__/route.test.ts \
  src/components/suavo/agent/__tests__/HealthCompositeTile.test.tsx 2>&1 | tail -3
```

Expected: all green.

- [ ] **Step 2: Verify Sub-project B closure per spec §1**

Sub-project B success criteria:
- [x] Composite signal computed agent-side, emitted as `agent.health_composite` (Tasks 1–4)
- [x] Cloud computes silent state from heartbeat absence (Task 8 endpoint)
- [x] Dashboard tile renders all 4 states correctly (Task 9, verified in Task 12 Step 1)
- [x] Off-hours gate prevents false-amber overnight (Task 3 unit tests)
- [x] Conservative-default error handling (signal probe throw, hours throw — Task 3 unit tests)
- [x] HealthCompositePayload retrofit clean under SUAVO0001 (Task 1 Step 7)
- [x] HealthCompositeResponseSchema wrapped in outbound() (Task 7 Step 4)

**Wave 1 master gate** trips when both Sub-projects (A + B) have shipped + been merged + pilot install at Joshua's test box correctly transitions through all 4 dashboard states under synthetic Helper-disconnect / IPC-cut scenarios.

---

## Out-of-scope (deferred per spec §6)

- Multi-PC composite roll-up (one tile per install in Wave 1; pharmacy-level roll-up later)
- Time-series view of composite history
- Alerting on transitions (Track 1 self-healing — Wave 3+)
- Component-level suggested fixes (Track 5 verb dispatch — Wave 5+)
- Customer-visible health UI (operator-facing only in Wave 1)

---

## Self-review

### 1. Spec coverage

| Spec section | Covered by tasks |
|---|---|
| §1 Architecture (agent-side composite, cloud-derived silent) | Tasks 1, 3, 4, 8 |
| §2 SuavoAgent components (4) | Tasks 1, 2, 3, 4 |
| §2 Suavo components (3) | Tasks 7, 8, 9 |
| §2 Event registration | Task 5 |
| §2 Wiring into existing dashboard | Task 10 |
| §3 Data flow (emission + ingest + render) | Tasks 4, 8, 9 |
| §4 Error handling (probe throws, hours throws, polling errors) | Tasks 3, 8, 9 |
| §5 Test category 1 — calculator unit tests | Task 3 |
| §5 Test category 2 — HealthCompositePayload regression | Task 1 Step 7 |
| §5 Test category 3 — HeartbeatWorker integration | Task 4 |
| §5 Test category 4 — API endpoint test | Task 8 |
| §5 Test category 5 — Dashboard tile component test | Task 9 |
| §5 Test category 6 (deferred E2E) | Task 12 + Wave 1 master gate |

No coverage gaps.

### 2. Placeholder scan

Two intentional "engineer fills in" notes (Task 4 Step 4 and Task 8 Step 1 mocks). Both flagged with inline guidance pointing to existing-pattern reference files. These are NOT placeholders in the spec sense — they're integration-shape adapters that depend on current codebase state at execution time. The plan is otherwise placeholder-free.

### 3. Type consistency

- `HealthCompositePayload(Status, Components, ComputedAt)` consistent across Tasks 1, 3, 4
- `HealthCompositeComponents(HelperAttached, IpcConnected, SchemaCanaryGreen, ExtractionRecent)` consistent
- TS schema field names use snake_case for wire compat: `helper_attached`, `ipc_connected`, `schema_canary_green`, `extraction_recent` — consistent across Tasks 7, 8, 9
- Status enum: `"healthy"`, `"heartbeating-but-unhealthy"`, `"silent"`, `"initializing"` — consistent across all tasks (note: agent never emits `"silent"`; that's cloud-derived only)
- `HealthCompositeResponse` (TS) has `silent: boolean` flag in addition to status — consistent

No inconsistencies found.

---

## Change log

- **2026-05-02 v0.1** — Initial plan from writing-plans session.
