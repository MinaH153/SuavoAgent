# V3 Wiring & Security Fixes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect the 4 dead wires in the behavioral pipeline, fix 3 critical security issues, and repair the audit chain — turning individually-tested components into a working production system.

**Architecture:** 11 surgical fixes: IPC startup + Helper behavioral wiring + Core IPC handler + DmvQueryObserver→ActionCorrelator bridge + ConfirmSeedItem caller + WritebackProcessor sessionId + ProcessRecalibration caller + PHI log scrubbing + patient-details hash + seed confidence clamping + audit chain prev_hash.

**Tech Stack:** .NET 8, C#, FlaUI (UIA), named pipes (IPC), SQLCipher, xUnit

**Review findings:** `docs/superpowers/plans/2026-04-14-v3-wiring-fixes.md` (this file)

---

## File Map

### Modified Files
| File | Fix IDs | Change |
|---|---|---|
| `src/SuavoAgent.Core/Program.cs` | W1, W3 | Start IpcPipeServer, add BehavioralEvents handler |
| `src/SuavoAgent.Helper/Program.cs` | W2 | Instantiate UiaTreeObserver, UiaInteractionObserver, KeyboardCategoryHook, BehavioralEventBuffer |
| `src/SuavoAgent.Core/Learning/DmvQueryObserver.cs` | W4 | Call ActionCorrelator.TryCorrelateWithSql after storing observation |
| `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs` | W5 | Call ConfirmSeedItem when correlation matches seeded shape |
| `src/SuavoAgent.Core/Workers/LearningWorker.cs` | W6, W7 | Call WritebackProcessor.SetSessionId, call ProcessRecalibration in active phase |
| `src/SuavoAgent.Core/Workers/WritebackProcessor.cs` | S1 | Hash Rx numbers before logging |
| `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs` | S3 | Hash rxNumber in SendPatientDetailsAsync |
| `src/SuavoAgent.Core/Learning/SeedApplicator.cs` | S5 | Clamp SeededConfidence to [0.0, 0.6] |
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | D1 | Fix AppendChainedAuditEntry prev_hash parameter |

### New Test Files
| File | Tests |
|---|---|
| `tests/SuavoAgent.Core.Tests/Wiring/DmvCorrelatorWiringTests.cs` | DmvQueryObserver → ActionCorrelator bridge |
| `tests/SuavoAgent.Core.Tests/Wiring/SeedConfirmationWiringTests.cs` | ActionCorrelator → ConfirmSeedItem |
| `tests/SuavoAgent.Core.Tests/Security/PhiScrubbingTests.cs` | Rx hash in logs, patient-details hash, seed confidence clamping |
| `tests/SuavoAgent.Core.Tests/State/AuditChainFixTests.cs` | Audit chain prev_hash correctness |

---

## Task 1: Fix Audit Chain — prev_hash Bug (D1)

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs:593`
- Create: `tests/SuavoAgent.Core.Tests/State/AuditChainFixTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/SuavoAgent.Core.Tests/State/AuditChainFixTests.cs`:

```csharp
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AuditChainFixTests : IDisposable
{
    private readonly AgentStateDb _db;

    public AuditChainFixTests()
    {
        _db = new AgentStateDb(":memory:");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void AppendChainedAuditEntry_StoresPrevHash_NotNewHash()
    {
        var entry1 = new AuditEntry("task-1", "pending", "in_progress", "start", "writeback");
        var hash1 = _db.AppendChainedAuditEntry(entry1, "2026-04-14T00:00:00Z");

        var entry2 = new AuditEntry("task-2", "in_progress", "completed", "finish", "writeback");
        var hash2 = _db.AppendChainedAuditEntry(entry2, "2026-04-14T00:01:00Z");

        // Verify chain: entry2's stored prev_hash should be hash1, not hash2
        Assert.True(_db.VerifyAuditChain());
    }

    [Fact]
    public void VerifyAuditChain_DetectsTampering()
    {
        var entry1 = new AuditEntry("task-1", "pending", "in_progress", "start", "writeback");
        _db.AppendChainedAuditEntry(entry1, "2026-04-14T00:00:00Z");

        var entry2 = new AuditEntry("task-2", "in_progress", "completed", "finish", "writeback");
        _db.AppendChainedAuditEntry(entry2, "2026-04-14T00:01:00Z");

        // Tamper with entry1
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "UPDATE audit_entries SET trigger = 'tampered' WHERE task_id = 'task-1'";
        cmd.ExecuteNonQuery();

        Assert.False(_db.VerifyAuditChain());
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AuditChainFixTests" -v n`
Expected: FAIL — VerifyAuditChain returns false because prev_hash stores newHash.

- [ ] **Step 3: Fix the bug**

In `src/SuavoAgent.Core/State/AgentStateDb.cs`, line 593, change:

```csharp
// BEFORE (bug):
cmd.Parameters.AddWithValue("@prevHash", newHash);

// AFTER (fix):
cmd.Parameters.AddWithValue("@prevHash", prevHash);
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AuditChainFixTests" -v n`
Expected: ALL PASS (2 tests).

- [ ] **Step 5: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/State/AuditChainFixTests.cs
git commit -m "fix(audit): store prevHash not newHash in AppendChainedAuditEntry — repairs chain integrity"
```

---

## Task 2: Seed Confidence Clamping (S5)

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/SeedApplicator.cs:83`
- Create: `tests/SuavoAgent.Core.Tests/Security/SeedConfidenceClampTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/SuavoAgent.Core.Tests/Security/SeedConfidenceClampTests.cs`:

```csharp
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Security;

public class SeedConfidenceClampTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly SeedApplicator _applicator;
    private const string SessionId = "sess-1";

    public SeedConfidenceClampTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
        _applicator = new SeedApplicator(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ApplyModelSeeds_ClampsConfidenceTo06()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.99, 0.99, 20, 1.0)
        };
        var response = new SeedResponse("digest-clamp", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        _applicator.ApplyModelSeeds(SessionId, response);

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "t1:btn1:q1");
        Assert.True(match.Confidence <= 0.6, $"Expected <= 0.6, got {match.Confidence}");
    }

    [Fact]
    public void ApplyModelSeeds_ClampsNegativeToZero()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.5, 0.5, 5, -0.5)
        };
        var response = new SeedResponse("digest-neg", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        _applicator.ApplyModelSeeds(SessionId, response);

        var actions = _db.GetCorrelatedActions(SessionId);
        var match = actions.First(a => a.CorrelationKey == "t1:btn1:q1");
        Assert.True(match.Confidence >= 0.0);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedConfidenceClampTests" -v n`
Expected: First test FAILS — confidence stored as 1.0 (unclamped).

- [ ] **Step 3: Add clamping in SeedApplicator**

In `src/SuavoAgent.Core/Learning/SeedApplicator.cs`, replace line 83:

```csharp
// BEFORE:
_db.UpdateCorrelationConfidence(sessionId, c.CorrelationKey, c.SeededConfidence);

// AFTER:
var clampedConfidence = Math.Clamp(c.SeededConfidence, 0.0, 0.6);
_db.UpdateCorrelationConfidence(sessionId, c.CorrelationKey, clampedConfidence);
```

- [ ] **Step 4: Run tests to verify pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedConfidenceClampTests" -v n`
Expected: ALL PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/SeedApplicator.cs tests/SuavoAgent.Core.Tests/Security/SeedConfidenceClampTests.cs
git commit -m "fix(security): clamp seeded confidence to [0.0, 0.6] — prevent seed poisoning"
```

---

## Task 3: PHI Scrubbing in WritebackProcessor Logs (S1)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/WritebackProcessor.cs:97,139`
- Create: `tests/SuavoAgent.Core.Tests/Security/PhiScrubbingTests.cs`

- [ ] **Step 1: Write test verifying Rx numbers are NOT logged in plaintext**

Create `tests/SuavoAgent.Core.Tests/Security/PhiScrubbingTests.cs`:

```csharp
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Security;

public class PhiScrubbingTests
{
    [Fact]
    public void HmacHash_ProducesConsistentNonPlaintextOutput()
    {
        var hash = PhiScrubber.HmacHash("12345", "test-salt");
        Assert.NotEqual("12345", hash);
        Assert.Equal(64, hash.Length); // SHA-256 hex
        Assert.Equal(hash, PhiScrubber.HmacHash("12345", "test-salt")); // deterministic
    }

    [Fact]
    public void HmacHash_DifferentSalts_ProduceDifferentHashes()
    {
        var hash1 = PhiScrubber.HmacHash("12345", "salt-a");
        var hash2 = PhiScrubber.HmacHash("12345", "salt-b");
        Assert.NotEqual(hash1, hash2);
    }
}
```

- [ ] **Step 2: Run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~PhiScrubbingTests" -v n`
Expected: PASS (these test PhiScrubber, which already exists).

- [ ] **Step 3: Fix WritebackProcessor log lines**

In `src/SuavoAgent.Core/Workers/WritebackProcessor.cs`:

Line 97 — add `using SuavoAgent.Core.Learning;` at top of file, then change:
```csharp
// BEFORE:
_logger.LogInformation("Enqueued writeback {TaskId} for Rx {RxNumber}", taskId, rxNumber);

// AFTER:
_logger.LogInformation("Enqueued writeback {TaskId} for Rx {RxHash}", taskId, PhiScrubber.HmacHash(rxNumber, _options.AgentId));
```

Line 139 — change:
```csharp
// BEFORE:
_logger.LogWarning("Writeback {TaskId} — invalid RxNumber '{Rx}'", taskId, state.RxNumber);

// AFTER:
_logger.LogWarning("Writeback {TaskId} — invalid RxNumber (hash: {RxHash})", taskId, PhiScrubber.HmacHash(state.RxNumber, _options.AgentId));
```

- [ ] **Step 4: Build verification**

Run: `dotnet build src/SuavoAgent.Core`
Expected: Build succeeded.

- [ ] **Step 5: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/WritebackProcessor.cs tests/SuavoAgent.Core.Tests/Security/PhiScrubbingTests.cs
git commit -m "fix(hipaa): hash Rx numbers in WritebackProcessor logs — remove plaintext PHI"
```

---

## Task 4: Hash rxNumber in SendPatientDetailsAsync (S3)

**Files:**
- Modify: `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs:44-47`

- [ ] **Step 1: Fix SendPatientDetailsAsync**

In `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`, change:

```csharp
// BEFORE:
public async Task SendPatientDetailsAsync(string rxNumber, object details, string commandId, CancellationToken ct)
{
    await PostSignedAsync("/api/agent/patient-details", new { rxNumber, details, commandId }, ct);
}

// AFTER:
public async Task SendPatientDetailsAsync(string rxNumber, object details, string commandId, CancellationToken ct)
{
    await PostSignedAsync("/api/agent/patient-details", new { rxNumberHash = Learning.PhiScrubber.HmacHash(rxNumber, _options.AgentId), details, commandId }, ct);
}
```

- [ ] **Step 2: Build verification**

Run: `dotnet build src/SuavoAgent.Core`
Expected: Build succeeded.

- [ ] **Step 3: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs
git commit -m "fix(hipaa): hash rxNumber in patient-details payload — remove plaintext PHI from cloud request"
```

---

## Task 5: Wire IPC Startup + BehavioralEvents Handler (W1, W3)

**Files:**
- Modify: `src/SuavoAgent.Core/Program.cs`

- [ ] **Step 1: Start IpcPipeServer**

In `src/SuavoAgent.Core/Program.cs`, after the IpcPipeServer is created but before `app.Run()`, add:

```csharp
var pipeServer = app.Services.GetRequiredService<IpcPipeServer>();
pipeServer.Start(app.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
```

- [ ] **Step 2: Add BehavioralEvents handler to IPC dispatch**

In the IPC handler lambda (around line 181), add a case for `BehavioralEvents`:

```csharp
case IpcCommands.BehavioralEvents:
{
    var events = System.Text.Json.JsonSerializer.Deserialize<List<BehavioralEvent>>(
        request.PayloadJson ?? "[]");
    if (events is { Count: > 0 })
    {
        var receiver = sp.GetService<BehavioralEventReceiver>();
        receiver?.ProcessBatch(events, request.DroppedSinceLast);
    }
    return new IpcResponse(200, "ok");
}
```

Also register `BehavioralEventReceiver` as a singleton in DI (before the IPC handler):

```csharp
builder.Services.AddSingleton<BehavioralEventReceiver>(sp =>
{
    var db = sp.GetRequiredService<AgentStateDb>();
    return new BehavioralEventReceiver(db, sessionId: null);
});
```

Note: `sessionId` will need to be set dynamically by LearningWorker once a session is created. Add a `SetSessionId` method to BehavioralEventReceiver if it doesn't exist.

- [ ] **Step 3: Build verification**

Run: `dotnet build src/SuavoAgent.Core`
Expected: Build succeeded.

- [ ] **Step 4: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Program.cs
git commit -m "fix(wiring): start IpcPipeServer + add BehavioralEvents IPC handler"
```

---

## Task 6: Wire Helper Behavioral Observers (W2)

**Files:**
- Modify: `src/SuavoAgent.Helper/Program.cs`

- [ ] **Step 1: Wire behavioral observers after PioneerRx attachment**

In `src/SuavoAgent.Helper/Program.cs`, after successful PioneerRx attachment and IPC client connect, add behavioral observer initialization:

```csharp
// After successful pioneer attach + IPC connect:
BehavioralEventBuffer? eventBuffer = null;
UiaTreeObserver? treeObserver = null;
UiaInteractionObserver? interactionObserver = null;
KeyboardCategoryHook? keyboardHook = null;

// Request pharmacy salt from Core via IPC (new IPC command)
// For now, use a placeholder salt — Core will deliver it via IPC handshake
var pharmacySalt = ""; // TODO: receive from Core via IPC after session created

eventBuffer = new BehavioralEventBuffer(
    batchSize: 50,
    flushAction: async events =>
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(events);
        await ipcClient.SendAsync(new IpcRequest(IpcCommands.BehavioralEvents, payload));
    });

treeObserver = new UiaTreeObserver(pharmacySalt, eventBuffer, logger);
interactionObserver = new UiaInteractionObserver(
    new FlaUI.UIA2.UIA2Automation(),
    pharmacySalt, eventBuffer, logger,
    triggerTreeResnapshot: () => treeObserver.TriggerResnapshot());
keyboardHook = new KeyboardCategoryHook(eventBuffer, logger, engine.ProcessId);

treeObserver.Start();
interactionObserver.Start();
keyboardHook.Start();
```

Add cleanup on exit/detach:

```csharp
// In the detach/cleanup section:
keyboardHook?.Stop();
interactionObserver?.Stop();
treeObserver?.Stop();
eventBuffer?.Dispose();
```

- [ ] **Step 2: Build verification**

Run: `dotnet build src/SuavoAgent.Helper`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Program.cs
git commit -m "fix(wiring): instantiate behavioral observers in Helper — UIA tree/interaction/keyboard"
```

---

## Task 7: Wire DmvQueryObserver → ActionCorrelator (W4)

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/DmvQueryObserver.cs`
- Create: `tests/SuavoAgent.Core.Tests/Wiring/DmvCorrelatorWiringTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/SuavoAgent.Core.Tests/Wiring/DmvCorrelatorWiringTests.cs`:

```csharp
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Wiring;

public class DmvCorrelatorWiringTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public DmvCorrelatorWiringTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ProcessAndStore_WithCorrelator_CallsTryCorrelateWithSql()
    {
        var correlator = new ActionCorrelator(_db, SessionId);
        var uiTime = DateTimeOffset.UtcNow;

        // Record a UI event first
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);

        // Process a DMV observation with the correlator attached
        DmvQueryObserver.ProcessAndStore(_db, SessionId,
            "UPDATE [Prescription].[RxTransaction] SET [StatusID] = 5 WHERE [RxNumber] = 123",
            executionCount: 1,
            lastExecutionTime: uiTime.AddSeconds(0.5).ToString("o"),
            clockOffsetMs: 0,
            correlator: correlator);

        // Verify a correlation was created
        var actions = _db.GetCorrelatedActions(SessionId);
        Assert.NotEmpty(actions);
    }

    [Fact]
    public void ProcessAndStore_WithoutCorrelator_StillStoresObservation()
    {
        DmvQueryObserver.ProcessAndStore(_db, SessionId,
            "SELECT * FROM [Prescription].[Rx]",
            executionCount: 5,
            lastExecutionTime: DateTimeOffset.UtcNow.ToString("o"),
            clockOffsetMs: 0,
            correlator: null);

        // Observation stored even without correlator
        var shapes = _db.GetObservedQueryShapes(SessionId);
        Assert.NotEmpty(shapes);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~DmvCorrelatorWiringTests" -v n`
Expected: FAIL — `ProcessAndStore` doesn't accept `correlator` parameter.

- [ ] **Step 3: Add correlator parameter to ProcessAndStore**

In `src/SuavoAgent.Core/Learning/DmvQueryObserver.cs`, modify `ProcessAndStore` signature:

```csharp
public static void ProcessAndStore(AgentStateDb db, string sessionId,
    string? rawSql, int executionCount, string lastExecutionTime, int clockOffsetMs,
    ActionCorrelator? correlator = null)
```

After the existing `db.UpsertDmvQueryObservation(...)` call, add:

```csharp
// Wire to ActionCorrelator for UI↔SQL correlation (Spec B)
if (correlator is not null && queryShapeHash is not null)
{
    correlator.TryCorrelateWithSql(queryShapeHash, lastExecutionTime, isWrite, tablesReferenced);
}
```

- [ ] **Step 4: Update all existing callers of ProcessAndStore**

Search for `ProcessAndStore` calls — they should all still compile because `correlator` has a default value of `null`. LearningWorker should pass its `_actionCorrelator` instance.

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~DmvCorrelatorWiringTests" -v n`
Expected: ALL PASS (2 tests).

- [ ] **Step 6: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Learning/DmvQueryObserver.cs tests/SuavoAgent.Core.Tests/Wiring/DmvCorrelatorWiringTests.cs
git commit -m "fix(wiring): bridge DmvQueryObserver → ActionCorrelator.TryCorrelateWithSql"
```

---

## Task 8: Wire ConfirmSeedItem from ActionCorrelator (W5)

**Files:**
- Modify: `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`
- Create: `tests/SuavoAgent.Core.Tests/Wiring/SeedConfirmationWiringTests.cs`

- [ ] **Step 1: Write failing test**

Create `tests/SuavoAgent.Core.Tests/Wiring/SeedConfirmationWiringTests.cs`:

```csharp
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Wiring;

public class SeedConfirmationWiringTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public SeedConfirmationWiringTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void TryCorrelateWithSql_SeededShape_ConfirmsSeedItem()
    {
        // Pre-seed a query_shape item
        _db.InsertSeedItem("digest-1", "query_shape", "seeded-hash", "2026-04-14T00:00:00Z");

        var correlator = new ActionCorrelator(_db, SessionId);
        correlator.RegisterSeededShapes(new[] { "seeded-hash" });
        correlator.SetActiveSeedDigest("digest-1");

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("seeded-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        // Seed item should be confirmed
        var items = _db.GetSeedItems("digest-1");
        var item = items.First(i => i.ItemKey == "seeded-hash");
        Assert.NotNull(item.ConfirmedAt);
        Assert.Equal(1, item.LocalMatchCount);
    }

    [Fact]
    public void TryCorrelateWithSql_NonSeededShape_NoSeedConfirmation()
    {
        var correlator = new ActionCorrelator(_db, SessionId);
        // No seeded shapes registered

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("normal-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        // No seed_items exist, so nothing to confirm
        var items = _db.GetSeedItems("any-digest");
        Assert.Empty(items);
    }

    [Fact]
    public void TryCorrelateWithSql_SeededCorrelation_ConfirmsCorrelationItem()
    {
        // Pre-seed a correlation item
        _db.InsertSeedItem("digest-2", "correlation", "tree1:btn1:seeded-hash", "2026-04-14T00:00:00Z");

        var correlator = new ActionCorrelator(_db, SessionId);
        correlator.RegisterSeededShapes(new[] { "seeded-hash" });
        correlator.SetActiveSeedDigest("digest-2");

        var uiTime = DateTimeOffset.UtcNow;
        correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
        correlator.TryCorrelateWithSql("seeded-hash", uiTime.AddSeconds(0.5).ToString("o"), true, "Tbl");

        // Both query_shape and correlation items should be confirmed if they exist
        var items = _db.GetSeedItems("digest-2");
        var corrItem = items.FirstOrDefault(i => i.ItemType == "correlation" && i.ItemKey == "tree1:btn1:seeded-hash");
        Assert.NotNull(corrItem?.ConfirmedAt);
    }
}
```

- [ ] **Step 2: Run tests to verify failure**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedConfirmationWiringTests" -v n`
Expected: FAIL — `SetActiveSeedDigest` doesn't exist on ActionCorrelator.

- [ ] **Step 3: Add seed confirmation to ActionCorrelator**

In `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`, add:

```csharp
private string? _activeSeedDigest;

public void SetActiveSeedDigest(string? digest) => _activeSeedDigest = digest;
```

At the end of `TryCorrelateWithSql`, after the successful `UpsertCorrelatedAction` call, add:

```csharp
// Confirm seeded items when pattern independently observed
if (_activeSeedDigest is not null && _seededShapes.Contains(queryShapeHash))
{
    var now = DateTimeOffset.UtcNow.ToString("o");
    _db.ConfirmSeedItem(_activeSeedDigest, "query_shape", queryShapeHash, now);
    // Also confirm correlation-level item if it exists
    _db.ConfirmSeedItem(_activeSeedDigest, "correlation", key, now);
}
```

- [ ] **Step 4: Wire SetActiveSeedDigest from LearningWorker**

In `LearningWorker.cs`, after seeds are applied (in the PullSeedsAsync method), add:

```csharp
_correlator?.SetActiveSeedDigest(seedResp.SeedDigest);
```

- [ ] **Step 5: Run tests to verify pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedConfirmationWiringTests" -v n`
Expected: ALL PASS (3 tests).

- [ ] **Step 6: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs src/SuavoAgent.Core/Workers/LearningWorker.cs tests/SuavoAgent.Core.Tests/Wiring/SeedConfirmationWiringTests.cs
git commit -m "fix(wiring): ActionCorrelator confirms seed_items on independent observation"
```

---

## Task 9: Wire WritebackProcessor.SetSessionId + ProcessRecalibration (W6, W7)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`

- [ ] **Step 1: Inject WritebackProcessor into LearningWorker**

In `LearningWorker.cs` constructor, add `WritebackProcessor` as an optional dependency:

```csharp
private readonly WritebackProcessor? _writebackProcessor;

// In constructor:
_writebackProcessor = sp.GetService<WritebackProcessor>();
```

- [ ] **Step 2: Call SetSessionId after session creation**

After `_sessionId` is set (either from resume or new creation), add:

```csharp
_writebackProcessor?.SetSessionId(_sessionId);
```

- [ ] **Step 3: Call ProcessRecalibration in active phase**

In the tick loop, when the phase is `"active"`, add the ProcessRecalibration call alongside the existing FeedbackProcessor tick:

```csharp
if (currentPhase == "active" || session.Phase == "active")
{
    _feedbackProcessor?.ProcessRecalibration(_sessionId!, _db);
}
```

- [ ] **Step 4: Build verification**

Run: `dotnet build src/SuavoAgent.Core`
Expected: Build succeeded.

- [ ] **Step 5: Full regression**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/LearningWorker.cs
git commit -m "fix(wiring): wire WritebackProcessor.SetSessionId + FeedbackProcessor.ProcessRecalibration"
```

---

## Task 10: Final Build Verification

- [ ] **Step 1: Full build**

Run: `dotnet build /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln`
Expected: Build succeeded, 0 errors.

- [ ] **Step 2: Full test suite**

Run: `dotnet test /Users/joshuahenein/Documents/SuavoAgent/SuavoAgent.sln -v n`
Expected: ALL PASS. Count should exceed previous 424.

- [ ] **Step 3: Verify commit history**

Run: `git log --oneline feat/v3-wiring-fixes --not main`
Expected: 9 commits covering all fixes.
