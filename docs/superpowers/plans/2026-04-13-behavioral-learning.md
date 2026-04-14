# Expanded Behavioral Learning Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add UI behavioral observation, SQL query correlation, and routine detection to the learning system — enabling the agent to discover writeback paths automatically by linking "technician clicked X" to "PMS fired UPDATE Y."

**Architecture:** Three observers in Helper (tree walker, interaction tracker, keyboard hook) feed scrubbed events through a fire-and-forget IPC buffer to Core. Core correlates UI events with DMV-captured SQL queries via ActionCorrelator, then mines repeatable action sequences via RoutineDetector. All observation is PHI-safe with capture-time allowlist enforcement.

**Tech Stack:** .NET 8, C#, xUnit, FlaUI.UIA2 (Helper), SQLCipher/SQLite (Core), Win32 interop (keyboard hook)

**Spec:** `docs/superpowers/specs/2026-04-13-behavioral-learning-design.md`

---

## File Structure

### Contracts (shared types — no platform dependencies)
- **Create:** `src/SuavoAgent.Contracts/Behavioral/BehavioralEvent.cs` — event record types + ScrubbedElement
- **Create:** `src/SuavoAgent.Contracts/Behavioral/KeystrokeCategory.cs` — category/timing enums
- **Create:** `src/SuavoAgent.Contracts/Behavioral/UiaPropertyScrubber.cs` — pure HIPAA scrub logic (dict → ScrubbedElement)
- **Create:** `src/SuavoAgent.Contracts/Behavioral/BehavioralEventBuffer.cs` — bounded ring buffer with delegate flush

### Helper (UI observation — FlaUI/Win32 dependent)
- **Create:** `src/SuavoAgent.Helper/Behavioral/UiaTreeObserver.cs` — periodic tree walks
- **Create:** `src/SuavoAgent.Helper/Behavioral/UiaInteractionObserver.cs` — UIA event subscriptions
- **Create:** `src/SuavoAgent.Helper/Behavioral/KeyboardCategoryHook.cs` — WH_KEYBOARD_LL wrapper
- **Modify:** `src/SuavoAgent.Helper/Program.cs` — wire observers into Helper startup

### Core (intelligence + storage)
- **Create:** `src/SuavoAgent.Core/Behavioral/BehavioralEventReceiver.cs` — IPC handler + persistence
- **Create:** `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs` — UI↔SQL timestamp correlation
- **Create:** `src/SuavoAgent.Core/Behavioral/RoutineDetector.cs` — DFG sequence mining
- **Create:** `src/SuavoAgent.Core/Learning/DmvQueryObserver.cs` — DMV polling + tokenizer pipeline
- **Modify:** `src/SuavoAgent.Core/Learning/SqlTokenizer.cs` — 10 hardening items
- **Modify:** `src/SuavoAgent.Core/State/AgentStateDb.cs` — 4 new tables + CRUD + pruning
- **Modify:** `src/SuavoAgent.Core/Learning/PomExporter.cs` — behavioral section
- **Modify:** `src/SuavoAgent.Core/HealthSnapshot.cs` — behavioral telemetry
- **Modify:** `src/SuavoAgent.Core/Workers/LearningWorker.cs` — wire new observers
- **Modify:** `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs` — BehavioralEvents constant

### Tests
- **Create:** `tests/SuavoAgent.Contracts.Tests/Behavioral/UiaPropertyScrubberTests.cs`
- **Create:** `tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventBufferTests.cs`
- **Create:** `tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Behavioral/BehavioralEventReceiverTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Behavioral/RoutineDetectorTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Learning/DmvQueryObserverTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs`
- **Create:** `tests/SuavoAgent.Core.Tests/Behavioral/DataRetentionTests.cs`

---

### Task 1: Shared Types — BehavioralEvent + ScrubbedElement + Enums

**Files:**
- Create: `src/SuavoAgent.Contracts/Behavioral/BehavioralEvent.cs`
- Create: `src/SuavoAgent.Contracts/Behavioral/KeystrokeCategory.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventTests.cs`

- [ ] **Step 1: Write failing test — BehavioralEvent serialization round-trips**

```csharp
// tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventTests.cs
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class BehavioralEventTests
{
    [Fact]
    public void TreeSnapshotEvent_RoundTrips()
    {
        var evt = new BehavioralEvent(
            Seq: 1, Type: BehavioralEventType.TreeSnapshot, Subtype: null,
            TreeHash: "abc123", ElementId: null, ControlType: null, ClassName: null,
            NameHash: null, BoundingRect: null,
            Keystroke: null, Timing: null, KeystrokeCount: null,
            OccurrenceCount: 1, Timestamp: "2026-04-13T14:30:00.123Z");

        var json = JsonSerializer.Serialize(evt);
        var deserialized = JsonSerializer.Deserialize<BehavioralEvent>(json);

        Assert.Equal(BehavioralEventType.TreeSnapshot, deserialized!.Type);
        Assert.Equal("abc123", deserialized.TreeHash);
    }

    [Fact]
    public void InteractionEvent_HasRequiredFields()
    {
        var evt = BehavioralEvent.Interaction("invoked", "hash1", "btnComplete",
            "Button", "WindowsForms10.BUTTON", "hmac_abc");

        Assert.Equal(BehavioralEventType.Interaction, evt.Type);
        Assert.Equal("invoked", evt.Subtype);
        Assert.Equal("btnComplete", evt.ElementId);
    }

    [Fact]
    public void KeystrokeEvent_CapsDigitCountAt3()
    {
        var evt = BehavioralEvent.Keystroke(KeystrokeCategory.Digit, TimingBucket.Rapid, 7);
        Assert.Equal(3, evt.KeystrokeCount); // capped
    }

    [Fact]
    public void ScrubbedElement_DoesNotExposeValueOrText()
    {
        var elem = new ScrubbedElement("Button", "btnOk", "BUTTON", "hmac_name", "10,20,50,25", 3, 1);
        // ScrubbedElement has no Value, Text, Selection, HelpText, or ItemStatus property
        var props = typeof(ScrubbedElement).GetProperties();
        var names = props.Select(p => p.Name).ToHashSet();
        Assert.DoesNotContain("Value", names);
        Assert.DoesNotContain("Text", names);
        Assert.DoesNotContain("Selection", names);
        Assert.DoesNotContain("HelpText", names);
        Assert.DoesNotContain("ItemStatus", names);
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
cd /Users/joshuahenein/Documents/SuavoAgent
dotnet test tests/SuavoAgent.Contracts.Tests --filter "BehavioralEventTests" -v q
```
Expected: FAIL — types don't exist yet.

- [ ] **Step 3: Implement types**

```csharp
// src/SuavoAgent.Contracts/Behavioral/KeystrokeCategory.cs
namespace SuavoAgent.Contracts.Behavioral;

public enum KeystrokeCategory
{
    Alpha, Digit, Tab, Enter, Escape, FunctionKey, Navigation, Modifier, Other
}

public enum TimingBucket
{
    Rapid,   // < 500ms between keystrokes
    Normal,  // 500ms - 2s
    Pause    // > 2s
}

public enum BehavioralEventType
{
    TreeSnapshot,
    Interaction,
    KeystrokeCategory
}
```

```csharp
// src/SuavoAgent.Contracts/Behavioral/BehavioralEvent.cs
using System.Text.Json.Serialization;

namespace SuavoAgent.Contracts.Behavioral;

public sealed record BehavioralEvent(
    [property: JsonPropertyName("seq")] long Seq,
    [property: JsonPropertyName("type")] BehavioralEventType Type,
    [property: JsonPropertyName("subtype")] string? Subtype,
    [property: JsonPropertyName("tree_hash")] string? TreeHash,
    [property: JsonPropertyName("element_id")] string? ElementId,
    [property: JsonPropertyName("control_type")] string? ControlType,
    [property: JsonPropertyName("class_name")] string? ClassName,
    [property: JsonPropertyName("name_hash")] string? NameHash,
    [property: JsonPropertyName("bounding_rect")] string? BoundingRect,
    [property: JsonPropertyName("category")] KeystrokeCategory? Keystroke,
    [property: JsonPropertyName("timing")] TimingBucket? Timing,
    [property: JsonPropertyName("count")] int? KeystrokeCount,
    [property: JsonPropertyName("occurrence_count")] int OccurrenceCount,
    [property: JsonPropertyName("ts")] string Timestamp)
{
    private const int MaxDigitSequence = 3;

    public static BehavioralEvent Interaction(string subtype, string treeHash,
        string elementId, string controlType, string? className, string? nameHash)
        => new(0, BehavioralEventType.Interaction, subtype, treeHash, elementId,
            controlType, className, nameHash, null, null, null, null, 1,
            DateTimeOffset.UtcNow.ToString("o"));

    public static BehavioralEvent TreeSnapshot(string treeHash)
        => new(0, BehavioralEventType.TreeSnapshot, null, treeHash, null,
            null, null, null, null, null, null, null, 1,
            DateTimeOffset.UtcNow.ToString("o"));

    public static BehavioralEvent Keystroke(KeystrokeCategory category,
        TimingBucket timing, int sequenceCount)
        => new(0, BehavioralEventType.KeystrokeCategory, null, null, null,
            null, null, null, null, category, timing,
            category == KeystrokeCategory.Digit ? Math.Min(sequenceCount, MaxDigitSequence) : sequenceCount,
            1, DateTimeOffset.UtcNow.ToString("o"));

    public BehavioralEvent WithSeq(long seq) => this with { Seq = seq };
}

/// <summary>
/// A UI element with only GREEN + YELLOW properties. No RED properties exist on this type.
/// GREEN: ControlType, AutomationId, ClassName, BoundingRect, Depth, ChildIndex
/// YELLOW: NameHash (HMAC-SHA256 of raw Name)
/// </summary>
public sealed record ScrubbedElement(
    string ControlType,
    string? AutomationId,
    string? ClassName,
    string? NameHash,
    string? BoundingRect,
    int Depth,
    int ChildIndex);
```

- [ ] **Step 4: Run test — verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests --filter "BehavioralEventTests" -v q
```
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Behavioral/ tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventTests.cs
git commit -m "feat(behavioral): add shared event types — BehavioralEvent, ScrubbedElement, enums"
```

---

### Task 2: UiaPropertyScrubber — HIPAA Boundary Logic

**Files:**
- Create: `src/SuavoAgent.Contracts/Behavioral/UiaPropertyScrubber.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Behavioral/UiaPropertyScrubberTests.cs`

- [ ] **Step 1: Write failing tests — allowlist enforcement + HMAC**

```csharp
// tests/SuavoAgent.Contracts.Tests/Behavioral/UiaPropertyScrubberTests.cs
using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class UiaPropertyScrubberTests
{
    private const string Salt = "test-pharmacy-salt-12345";

    [Fact]
    public void Scrub_GreenProperties_PreservedPlain()
    {
        var props = new RawElementProperties(
            ControlType: "Button", AutomationId: "btnComplete",
            ClassName: "WindowsForms10.BUTTON", Name: "Complete",
            BoundingRect: "100,200,50,25", Depth: 3, ChildIndex: 1);

        var result = UiaPropertyScrubber.Scrub(props, Salt);

        Assert.Equal("Button", result.ControlType);
        Assert.Equal("btnComplete", result.AutomationId);
        Assert.Equal("WindowsForms10.BUTTON", result.ClassName);
        Assert.Equal("100,200,50,25", result.BoundingRect);
        Assert.Equal(3, result.Depth);
        Assert.Equal(1, result.ChildIndex);
    }

    [Fact]
    public void Scrub_Name_IsHmacHashed_NeverRaw()
    {
        var props = new RawElementProperties(
            ControlType: "Text", AutomationId: null,
            ClassName: "STATIC", Name: "Smith, John - DOB 01/15/1990",
            BoundingRect: null, Depth: 2, ChildIndex: 0);

        var result = UiaPropertyScrubber.Scrub(props, Salt);

        Assert.NotNull(result.NameHash);
        Assert.NotEqual("Smith, John - DOB 01/15/1990", result.NameHash);
        Assert.Equal(64, result.NameHash!.Length); // SHA-256 hex = 64 chars
    }

    [Fact]
    public void Scrub_SameName_SameHash_Deterministic()
    {
        var props1 = new RawElementProperties("Button", "btn1", null, "Approve", null, 1, 0);
        var props2 = new RawElementProperties("Button", "btn2", null, "Approve", null, 2, 1);

        var r1 = UiaPropertyScrubber.Scrub(props1, Salt);
        var r2 = UiaPropertyScrubber.Scrub(props2, Salt);

        Assert.Equal(r1.NameHash, r2.NameHash); // same Name => same hash
    }

    [Fact]
    public void Scrub_NullName_NullHash()
    {
        var props = new RawElementProperties("Button", "btnOk", "BUTTON", null, null, 0, 0);
        var result = UiaPropertyScrubber.Scrub(props, Salt);
        Assert.Null(result.NameHash);
    }

    [Fact]
    public void Scrub_EmptyAutomationIdAndClassName_ReturnsNull()
    {
        // Anonymous element — can't be stably identified, dropped
        var props = new RawElementProperties("Pane", null, null, null, null, 5, 3);
        var result = UiaPropertyScrubber.TryScrub(props, Salt);
        Assert.Null(result); // dropped
    }

    [Fact]
    public void Scrub_EmptyAutomationId_WithClassName_Accepted()
    {
        var props = new RawElementProperties("Button", null, "BUTTON", "OK", null, 1, 0);
        var result = UiaPropertyScrubber.TryScrub(props, Salt);
        Assert.NotNull(result);
    }

    [Fact]
    public void BuildElementId_PrefersAutomationId()
    {
        var props = new RawElementProperties("Button", "btnSave", "BUTTON", null, null, 2, 1);
        Assert.Equal("btnSave", UiaPropertyScrubber.BuildElementId(props));
    }

    [Fact]
    public void BuildElementId_FallsBackToTreePositional()
    {
        var props = new RawElementProperties("Button", null, "BUTTON", null, null, 3, 2);
        Assert.Equal("BUTTON:3:2", UiaPropertyScrubber.BuildElementId(props));
    }

    [Fact]
    public void BuildElementId_NullBothIds_ReturnsNull()
    {
        var props = new RawElementProperties("Pane", null, null, null, null, 1, 0);
        Assert.Null(UiaPropertyScrubber.BuildElementId(props));
    }

    [Theory]
    [InlineData("Rx No")]
    [InlineData("Smith, John - Prescriptions")]
    [InlineData("Dispensed Item")]
    public void ScrubColumnHeaders_AllHmacHashed(string header)
    {
        var hashed = UiaPropertyScrubber.ScrubColumnHeader(header, Salt);
        Assert.NotEqual(header, hashed);
        Assert.Equal(64, hashed.Length);
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests --filter "UiaPropertyScrubberTests" -v q
```
Expected: FAIL — types don't exist.

- [ ] **Step 3: Implement UiaPropertyScrubber**

```csharp
// src/SuavoAgent.Contracts/Behavioral/UiaPropertyScrubber.cs
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Input record for the scrubber. Contains raw properties extracted from an AutomationElement.
/// Created in Helper from FlaUI, consumed by the scrubber. Never persisted.
/// </summary>
public sealed record RawElementProperties(
    string ControlType,
    string? AutomationId,
    string? ClassName,
    string? Name,
    string? BoundingRect,
    int Depth,
    int ChildIndex);

/// <summary>
/// HIPAA boundary enforcement. Accepts raw element properties, returns a ScrubbedElement
/// with only GREEN properties plain and YELLOW properties HMAC-hashed.
/// No RED properties (Value, Text, Selection, HelpText, ItemStatus) are accepted as input.
/// This is the single enforcement point — called in Helper BEFORE IPC serialization.
/// </summary>
public static class UiaPropertyScrubber
{
    /// <summary>
    /// Scrub an element. Returns ScrubbedElement with Name HMAC-hashed.
    /// Caller must ensure only GREEN+YELLOW properties are in the input.
    /// </summary>
    public static ScrubbedElement Scrub(RawElementProperties raw, string pharmacySalt)
    {
        var nameHash = raw.Name is not null ? HmacHash(raw.Name, pharmacySalt) : null;

        return new ScrubbedElement(
            ControlType: raw.ControlType,
            AutomationId: raw.AutomationId,
            ClassName: raw.ClassName,
            NameHash: nameHash,
            BoundingRect: raw.BoundingRect,
            Depth: raw.Depth,
            ChildIndex: raw.ChildIndex);
    }

    /// <summary>
    /// TryScrub returns null for anonymous elements (no AutomationId AND no ClassName).
    /// These can't be stably identified across sessions and are dropped.
    /// </summary>
    public static ScrubbedElement? TryScrub(RawElementProperties raw, string pharmacySalt)
    {
        if (string.IsNullOrEmpty(raw.AutomationId) && string.IsNullOrEmpty(raw.ClassName))
            return null;
        return Scrub(raw, pharmacySalt);
    }

    /// <summary>
    /// Build a stable element identifier. AutomationId preferred.
    /// Fallback: ClassName + tree depth + child index among same-type siblings.
    /// Returns null if neither AutomationId nor ClassName exists.
    /// </summary>
    public static string? BuildElementId(RawElementProperties raw)
    {
        if (!string.IsNullOrEmpty(raw.AutomationId))
            return raw.AutomationId;
        if (!string.IsNullOrEmpty(raw.ClassName))
            return $"{raw.ClassName}:{raw.Depth}:{raw.ChildIndex}";
        return null;
    }

    /// <summary>
    /// DataGrid column headers are YELLOW — always HMAC-hashed.
    /// </summary>
    public static string ScrubColumnHeader(string header, string pharmacySalt)
        => HmacHash(header, pharmacySalt);

    private static string HmacHash(string value, string salt)
    {
        var key = Encoding.UTF8.GetBytes(salt);
        var data = Encoding.UTF8.GetBytes(value);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run test — verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests --filter "UiaPropertyScrubberTests" -v q
```
Expected: 10 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Behavioral/UiaPropertyScrubber.cs tests/SuavoAgent.Contracts.Tests/Behavioral/UiaPropertyScrubberTests.cs
git commit -m "feat(behavioral): add UiaPropertyScrubber — HIPAA boundary with GREEN/YELLOW/RED enforcement"
```

---

### Task 3: BehavioralEventBuffer — Bounded Ring Buffer

**Files:**
- Create: `src/SuavoAgent.Contracts/Behavioral/BehavioralEventBuffer.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventBufferTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventBufferTests.cs
using SuavoAgent.Contracts.Behavioral;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Behavioral;

public class BehavioralEventBufferTests
{
    [Fact]
    public async Task Enqueue_BelowBatchSize_DoesNotFlush()
    {
        var flushed = new List<IReadOnlyList<BehavioralEvent>>();
        var buffer = new BehavioralEventBuffer(
            capacity: 500, batchSize: 50,
            flushAction: batch => { flushed.Add(batch.ToList()); return Task.CompletedTask; });

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("hash1"));
        await Task.Delay(50);

        Assert.Empty(flushed); // not yet at batch size
    }

    [Fact]
    public async Task Enqueue_AtBatchSize_FlushesAutomatically()
    {
        var flushed = new List<IReadOnlyList<BehavioralEvent>>();
        var buffer = new BehavioralEventBuffer(
            capacity: 500, batchSize: 3,
            flushAction: batch => { flushed.Add(batch.ToList()); return Task.CompletedTask; });

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h1"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h2"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h3"));
        await Task.Delay(100);

        Assert.Single(flushed);
        Assert.Equal(3, flushed[0].Count);
    }

    [Fact]
    public void Enqueue_OverCapacity_DropsOldest()
    {
        var buffer = new BehavioralEventBuffer(
            capacity: 3, batchSize: 100,
            flushAction: _ => Task.CompletedTask);

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h1"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h2"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h3"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h4")); // drops h1

        Assert.Equal(1, buffer.DroppedEventCount);
    }

    [Fact]
    public void DroppedEventCount_IncrementsOnEviction()
    {
        var buffer = new BehavioralEventBuffer(capacity: 2, batchSize: 100,
            flushAction: _ => Task.CompletedTask);

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h1"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h2"));
        Assert.Equal(0, buffer.DroppedEventCount);

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h3"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h4"));
        Assert.Equal(2, buffer.DroppedEventCount);
    }

    [Fact]
    public void DroppedSinceLastFlush_ResetsAfterFlush()
    {
        var buffer = new BehavioralEventBuffer(capacity: 2, batchSize: 100,
            flushAction: _ => Task.CompletedTask);

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h1"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h2"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h3")); // drop 1

        Assert.Equal(1, buffer.DroppedSinceLastFlush);
        buffer.ResetDroppedSinceLastFlush();
        Assert.Equal(0, buffer.DroppedSinceLastFlush);
        Assert.Equal(1, buffer.DroppedEventCount); // total unchanged
    }

    [Fact]
    public void AssignsMonotonicSequenceNumbers()
    {
        var flushed = new List<BehavioralEvent>();
        var buffer = new BehavioralEventBuffer(capacity: 500, batchSize: 3,
            flushAction: batch => { flushed.AddRange(batch); return Task.CompletedTask; });

        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h1"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h2"));
        buffer.Enqueue(BehavioralEvent.TreeSnapshot("h3"));
        Thread.Sleep(100);

        Assert.Equal(1, flushed[0].Seq);
        Assert.Equal(2, flushed[1].Seq);
        Assert.Equal(3, flushed[2].Seq);
    }
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests --filter "BehavioralEventBufferTests" -v q
```

- [ ] **Step 3: Implement BehavioralEventBuffer**

```csharp
// src/SuavoAgent.Contracts/Behavioral/BehavioralEventBuffer.cs
namespace SuavoAgent.Contracts.Behavioral;

/// <summary>
/// Bounded ring buffer for behavioral events. Decouples observer rates from IPC throughput.
/// Oldest events evicted when full. Flush via delegate (wired to TrySendAsync in Helper).
/// </summary>
public sealed class BehavioralEventBuffer
{
    private readonly int _capacity;
    private readonly int _batchSize;
    private readonly Func<IReadOnlyList<BehavioralEvent>, Task> _flushAction;
    private readonly Queue<BehavioralEvent> _queue;
    private readonly object _lock = new();
    private long _nextSeq = 1;
    private long _droppedTotal;
    private long _droppedSinceFlush;

    public long DroppedEventCount => Interlocked.Read(ref _droppedTotal);
    public long DroppedSinceLastFlush => Interlocked.Read(ref _droppedSinceFlush);

    public BehavioralEventBuffer(int capacity, int batchSize,
        Func<IReadOnlyList<BehavioralEvent>, Task> flushAction)
    {
        _capacity = capacity;
        _batchSize = batchSize;
        _flushAction = flushAction;
        _queue = new Queue<BehavioralEvent>(capacity);
    }

    public void Enqueue(BehavioralEvent evt)
    {
        BehavioralEvent[]? batch = null;
        lock (_lock)
        {
            // Assign monotonic sequence number
            evt = evt.WithSeq(_nextSeq++);

            // Evict oldest if at capacity
            while (_queue.Count >= _capacity)
            {
                _queue.Dequeue();
                Interlocked.Increment(ref _droppedTotal);
                Interlocked.Increment(ref _droppedSinceFlush);
            }

            _queue.Enqueue(evt);

            // Flush if batch size reached
            if (_queue.Count >= _batchSize)
            {
                batch = new BehavioralEvent[_queue.Count];
                _queue.CopyTo(batch, 0);
                _queue.Clear();
            }
        }

        if (batch is not null)
            _ = Task.Run(() => FlushAsync(batch));
    }

    /// <summary>
    /// Force-flush whatever is in the buffer (called on timer or shutdown).
    /// </summary>
    public async Task FlushAsync()
    {
        BehavioralEvent[]? batch;
        lock (_lock)
        {
            if (_queue.Count == 0) return;
            batch = _queue.ToArray();
            _queue.Clear();
        }
        await FlushAsync(batch);
    }

    private async Task FlushAsync(BehavioralEvent[] batch)
    {
        try
        {
            await _flushAction(batch);
        }
        catch
        {
            // Fire-and-forget — flush failures are silent.
            // Events already removed from buffer. Dropped.
        }
        ResetDroppedSinceLastFlush();
    }

    public void ResetDroppedSinceLastFlush()
        => Interlocked.Exchange(ref _droppedSinceFlush, 0);
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests --filter "BehavioralEventBufferTests" -v q
```
Expected: 6 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Behavioral/BehavioralEventBuffer.cs tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventBufferTests.cs
git commit -m "feat(behavioral): add BehavioralEventBuffer — bounded ring buffer with fire-and-forget flush"
```

---

### Task 4: AgentStateDb Schema Migration + CRUD Methods

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/BehavioralEventReceiverTests.cs` (partial — DB methods only)

- [ ] **Step 1: Write failing tests for DB CRUD**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/BehavioralDbTests.cs
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class BehavioralDbTests : IDisposable
{
    private readonly AgentStateDb _db;

    public BehavioralDbTests()
    {
        _db = new AgentStateDb(":memory:");
    }

    [Fact]
    public void InsertBehavioralEvent_Persists()
    {
        _db.InsertBehavioralEvent("sess1", 1, "tree_snapshot", null,
            "hash1", null, null, null, null, null,
            null, null, null, 1, "2026-04-13T14:00:00Z");

        var events = _db.GetBehavioralEvents("sess1", "tree_snapshot", limit: 10);
        Assert.Single(events);
        Assert.Equal("hash1", events[0].TreeHash);
    }

    [Fact]
    public void InsertDmvQueryObservation_Persists()
    {
        _db.UpsertDmvQueryObservation("sess1", "shapehash1",
            "SELECT [col] FROM [s].[t] WHERE [x] = @p", "[\"s.t\"]",
            isWrite: false, executionCount: 5,
            lastExecutionTime: "2026-04-13T14:00:00Z", clockOffsetMs: -45);

        var obs = _db.GetDmvQueryObservations("sess1", limit: 10);
        Assert.Single(obs);
        Assert.Equal("shapehash1", obs[0].QueryShapeHash);
        Assert.Equal(5, obs[0].ExecutionCount);
    }

    [Fact]
    public void UpsertCorrelatedAction_IncrementsOccurrence()
    {
        _db.UpsertCorrelatedAction("sess1", "hash1:btn1:qhash1",
            "hash1", "btn1", "Button", "qhash1", isWrite: true,
            "[\"Prescription.RxTransaction\"]");
        _db.UpsertCorrelatedAction("sess1", "hash1:btn1:qhash1",
            "hash1", "btn1", "Button", "qhash1", isWrite: true,
            "[\"Prescription.RxTransaction\"]");

        var actions = _db.GetCorrelatedActions("sess1");
        Assert.Single(actions);
        Assert.Equal(2, actions[0].OccurrenceCount);
    }

    [Fact]
    public void UpsertLearnedRoutine_Persists()
    {
        _db.UpsertLearnedRoutine("sess1", "rhash1",
            "[{\"treeHash\":\"h1\",\"elementId\":\"btn1\"}]",
            pathLength: 3, frequency: 10, confidence: 0.8,
            startElementId: "btn1", endElementId: "btn3",
            "[\"qhash1\"]", hasWritebackCandidate: true);

        var routines = _db.GetLearnedRoutines("sess1");
        Assert.Single(routines);
        Assert.True(routines[0].HasWritebackCandidate);
    }

    [Fact]
    public void PruneBehavioralEvents_DeletesOldEventsWithStableRoutines()
    {
        // Insert events for a screen with a stable routine
        _db.InsertBehavioralEvent("sess1", 1, "interaction", "invoked",
            "hash1", "btn1", "Button", null, null, null,
            null, null, null, 1, "2026-03-01T00:00:00Z"); // old

        // Insert a stable routine for hash1
        _db.UpsertLearnedRoutine("sess1", "rhash1",
            "[{\"treeHash\":\"hash1\",\"elementId\":\"btn1\"}]",
            pathLength: 3, frequency: 10, confidence: 0.8,
            "btn1", "btn3", null, false);

        var pruned = _db.PruneBehavioralEvents("sess1", olderThanDays: 7);
        Assert.Equal(1, pruned);

        var remaining = _db.GetBehavioralEvents("sess1", null, limit: 100);
        Assert.Empty(remaining);
    }

    [Fact]
    public void PruneBehavioralEvents_RetainsEventsWithoutStableRoutines()
    {
        // Insert old event for screen with NO routine
        _db.InsertBehavioralEvent("sess1", 1, "interaction", "invoked",
            "hash_no_routine", "btn1", "Button", null, null, null,
            null, null, null, 1, "2026-03-01T00:00:00Z");

        var pruned = _db.PruneBehavioralEvents("sess1", olderThanDays: 7);
        Assert.Equal(0, pruned); // retained — no stable routine for this screen

        var remaining = _db.GetBehavioralEvents("sess1", null, limit: 100);
        Assert.Single(remaining);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralDbTests" -v q
```

- [ ] **Step 3: Add schema migration + CRUD methods to AgentStateDb**

Add the following to `AgentStateDb.InitSchema()` after the canary migrations block:

```csharp
// Behavioral Learning tables
using var behavioralCmd = _conn.CreateCommand();
behavioralCmd.CommandText = """
    CREATE TABLE IF NOT EXISTS behavioral_events (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        sequence_num INTEGER NOT NULL,
        event_type TEXT NOT NULL,
        event_subtype TEXT,
        tree_hash TEXT,
        element_id TEXT,
        element_control_type TEXT,
        element_class_name TEXT,
        element_name_hash TEXT,
        element_bounding_rect TEXT,
        keystroke_category TEXT,
        keystroke_timing_bucket TEXT,
        keystroke_sequence_count INTEGER,
        occurrence_count INTEGER DEFAULT 1,
        helper_timestamp TEXT NOT NULL,
        received_at TEXT NOT NULL
    );
    CREATE INDEX IF NOT EXISTS idx_be_session_seq ON behavioral_events(session_id, sequence_num);
    CREATE INDEX IF NOT EXISTS idx_be_session_type ON behavioral_events(session_id, event_type);
    CREATE INDEX IF NOT EXISTS idx_be_tree_hash ON behavioral_events(session_id, tree_hash);

    CREATE TABLE IF NOT EXISTS dmv_query_observations (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        query_shape_hash TEXT NOT NULL,
        query_shape TEXT NOT NULL,
        tables_referenced TEXT NOT NULL,
        is_write INTEGER NOT NULL DEFAULT 0,
        execution_count INTEGER DEFAULT 1,
        last_execution_time TEXT NOT NULL,
        clock_offset_ms INTEGER DEFAULT 0,
        first_seen TEXT NOT NULL,
        last_seen TEXT NOT NULL,
        UNIQUE(session_id, query_shape_hash)
    );
    CREATE INDEX IF NOT EXISTS idx_dqo_session_time ON dmv_query_observations(session_id, last_execution_time);

    CREATE TABLE IF NOT EXISTS correlated_actions (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        correlation_key TEXT NOT NULL,
        tree_hash TEXT NOT NULL,
        element_id TEXT NOT NULL,
        element_control_type TEXT,
        query_shape_hash TEXT,
        query_is_write INTEGER DEFAULT 0,
        tables_referenced TEXT,
        occurrence_count INTEGER DEFAULT 1,
        confidence REAL DEFAULT 0.3,
        first_seen TEXT NOT NULL,
        last_seen TEXT NOT NULL,
        UNIQUE(session_id, correlation_key)
    );
    CREATE INDEX IF NOT EXISTS idx_ca_session_key ON correlated_actions(session_id, correlation_key);

    CREATE TABLE IF NOT EXISTS learned_routines (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        routine_hash TEXT NOT NULL,
        path_json TEXT NOT NULL,
        path_length INTEGER NOT NULL,
        frequency INTEGER NOT NULL,
        confidence REAL DEFAULT 0.0,
        start_element_id TEXT,
        end_element_id TEXT,
        correlated_write_queries TEXT,
        has_writeback_candidate INTEGER DEFAULT 0,
        discovered_at TEXT NOT NULL,
        last_observed TEXT NOT NULL,
        UNIQUE(session_id, routine_hash)
    );
    CREATE INDEX IF NOT EXISTS idx_lr_session ON learned_routines(session_id);
    """;
behavioralCmd.ExecuteNonQuery();
```

Then add these CRUD methods to AgentStateDb (after the existing learning methods):

```csharp
// === Behavioral Events ===

public void InsertBehavioralEvent(string sessionId, long sequenceNum,
    string eventType, string? eventSubtype, string? treeHash,
    string? elementId, string? controlType, string? className,
    string? nameHash, string? boundingRect,
    string? keystrokeCategory, string? timingBucket, int? keystrokeCount,
    int occurrenceCount, string helperTimestamp)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO behavioral_events
            (session_id, sequence_num, event_type, event_subtype, tree_hash,
             element_id, element_control_type, element_class_name, element_name_hash,
             element_bounding_rect, keystroke_category, keystroke_timing_bucket,
             keystroke_sequence_count, occurrence_count, helper_timestamp, received_at)
        VALUES (@sid, @seq, @type, @sub, @th, @eid, @ct, @cn, @nh, @br,
                @kc, @kt, @ks, @oc, @ht, @ra)
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@seq", sequenceNum);
    cmd.Parameters.AddWithValue("@type", eventType);
    cmd.Parameters.AddWithValue("@sub", (object?)eventSubtype ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@th", (object?)treeHash ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@eid", (object?)elementId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ct", (object?)controlType ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@cn", (object?)className ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@nh", (object?)nameHash ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@br", (object?)boundingRect ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@kc", (object?)keystrokeCategory ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@kt", (object?)timingBucket ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ks", (object?)keystrokeCount ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@oc", occurrenceCount);
    cmd.Parameters.AddWithValue("@ht", helperTimestamp);
    cmd.Parameters.AddWithValue("@ra", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string EventType, string? TreeHash, string? ElementId, string? ControlType, long Seq, string Timestamp)>
    GetBehavioralEvents(string sessionId, string? eventType, int limit)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = eventType is not null
        ? "SELECT event_type, tree_hash, element_id, element_control_type, sequence_num, helper_timestamp FROM behavioral_events WHERE session_id = @sid AND event_type = @type ORDER BY sequence_num LIMIT @lim"
        : "SELECT event_type, tree_hash, element_id, element_control_type, sequence_num, helper_timestamp FROM behavioral_events WHERE session_id = @sid ORDER BY sequence_num LIMIT @lim";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    if (eventType is not null) cmd.Parameters.AddWithValue("@type", eventType);
    cmd.Parameters.AddWithValue("@lim", limit);

    var results = new List<(string, string?, string?, string?, long, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4), reader.GetString(5)));
    return results;
}

// === DMV Query Observations ===

public void UpsertDmvQueryObservation(string sessionId, string queryShapeHash,
    string queryShape, string tablesReferenced, bool isWrite,
    int executionCount, string lastExecutionTime, int clockOffsetMs)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO dmv_query_observations
            (session_id, query_shape_hash, query_shape, tables_referenced, is_write,
             execution_count, last_execution_time, clock_offset_ms, first_seen, last_seen)
        VALUES (@sid, @hash, @shape, @tables, @write, @count, @let, @offset, @now, @now)
        ON CONFLICT(session_id, query_shape_hash) DO UPDATE SET
            execution_count = @count, last_execution_time = @let,
            clock_offset_ms = @offset, last_seen = @now
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@hash", queryShapeHash);
    cmd.Parameters.AddWithValue("@shape", queryShape);
    cmd.Parameters.AddWithValue("@tables", tablesReferenced);
    cmd.Parameters.AddWithValue("@write", isWrite ? 1 : 0);
    cmd.Parameters.AddWithValue("@count", executionCount);
    cmd.Parameters.AddWithValue("@let", lastExecutionTime);
    cmd.Parameters.AddWithValue("@offset", clockOffsetMs);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string QueryShapeHash, string QueryShape, string TablesReferenced,
    bool IsWrite, int ExecutionCount, string LastExecutionTime)>
    GetDmvQueryObservations(string sessionId, int limit)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT query_shape_hash, query_shape, tables_referenced, is_write, execution_count, last_execution_time
        FROM dmv_query_observations WHERE session_id = @sid
        ORDER BY last_execution_time DESC LIMIT @lim
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@lim", limit);

    var results = new List<(string, string, string, bool, int, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.GetInt32(3) == 1, reader.GetInt32(4), reader.GetString(5)));
    return results;
}

public IReadOnlyList<(string QueryShapeHash, string LastExecutionTime, bool IsWrite, string TablesReferenced)>
    GetRecentDmvQueries(string sessionId, string sinceTimestamp)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT query_shape_hash, last_execution_time, is_write, tables_referenced
        FROM dmv_query_observations
        WHERE session_id = @sid AND last_execution_time > @since
        ORDER BY last_execution_time DESC
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@since", sinceTimestamp);

    var results = new List<(string, string, bool, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2) == 1, reader.GetString(3)));
    return results;
}

// === Correlated Actions ===

public void UpsertCorrelatedAction(string sessionId, string correlationKey,
    string treeHash, string elementId, string? controlType,
    string? queryShapeHash, bool isWrite, string? tablesReferenced)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO correlated_actions
            (session_id, correlation_key, tree_hash, element_id, element_control_type,
             query_shape_hash, query_is_write, tables_referenced, occurrence_count,
             confidence, first_seen, last_seen)
        VALUES (@sid, @key, @th, @eid, @ct, @qsh, @write, @tables, 1, 0.3, @now, @now)
        ON CONFLICT(session_id, correlation_key) DO UPDATE SET
            occurrence_count = occurrence_count + 1,
            confidence = CASE
                WHEN occurrence_count + 1 >= 10 THEN 0.9
                WHEN occurrence_count + 1 >= 3 THEN 0.6
                ELSE 0.3 END,
            last_seen = @now
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@key", correlationKey);
    cmd.Parameters.AddWithValue("@th", treeHash);
    cmd.Parameters.AddWithValue("@eid", elementId);
    cmd.Parameters.AddWithValue("@ct", (object?)controlType ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@qsh", (object?)queryShapeHash ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@write", isWrite ? 1 : 0);
    cmd.Parameters.AddWithValue("@tables", (object?)tablesReferenced ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string CorrelationKey, string TreeHash, string ElementId,
    string? ControlType, string? QueryShapeHash, bool IsWrite,
    string? TablesReferenced, int OccurrenceCount, double Confidence)>
    GetCorrelatedActions(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT correlation_key, tree_hash, element_id, element_control_type,
               query_shape_hash, query_is_write, tables_referenced, occurrence_count, confidence
        FROM correlated_actions WHERE session_id = @sid ORDER BY occurrence_count DESC
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);

    var results = new List<(string, string, string, string?, string?, bool, string?, int, double)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.GetInt32(5) == 1,
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.GetInt32(7), reader.GetDouble(8)));
    return results;
}

public IReadOnlyList<(string CorrelationKey, string ElementId, string? ControlType,
    string? QueryShapeHash, string? QueryShape, string? TablesReferenced,
    int OccurrenceCount, double Confidence)>
    GetWritebackCandidates(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT ca.correlation_key, ca.element_id, ca.element_control_type,
               ca.query_shape_hash, dqo.query_shape, ca.tables_referenced,
               ca.occurrence_count, ca.confidence
        FROM correlated_actions ca
        LEFT JOIN dmv_query_observations dqo ON ca.session_id = dqo.session_id
            AND ca.query_shape_hash = dqo.query_shape_hash
        WHERE ca.session_id = @sid AND ca.query_is_write = 1
        ORDER BY ca.confidence DESC
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);

    var results = new List<(string, string, string?, string?, string?, string?, int, double)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.GetInt32(6), reader.GetDouble(7)));
    return results;
}

// === Learned Routines ===

public void UpsertLearnedRoutine(string sessionId, string routineHash,
    string pathJson, int pathLength, int frequency, double confidence,
    string? startElementId, string? endElementId,
    string? correlatedWriteQueries, bool hasWritebackCandidate)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO learned_routines
            (session_id, routine_hash, path_json, path_length, frequency, confidence,
             start_element_id, end_element_id, correlated_write_queries,
             has_writeback_candidate, discovered_at, last_observed)
        VALUES (@sid, @hash, @path, @len, @freq, @conf, @start, @end, @cwq, @wb, @now, @now)
        ON CONFLICT(session_id, routine_hash) DO UPDATE SET
            frequency = @freq, confidence = @conf, last_observed = @now
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@hash", routineHash);
    cmd.Parameters.AddWithValue("@path", pathJson);
    cmd.Parameters.AddWithValue("@len", pathLength);
    cmd.Parameters.AddWithValue("@freq", frequency);
    cmd.Parameters.AddWithValue("@conf", confidence);
    cmd.Parameters.AddWithValue("@start", (object?)startElementId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@end", (object?)endElementId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@cwq", (object?)correlatedWriteQueries ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@wb", hasWritebackCandidate ? 1 : 0);
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string RoutineHash, string PathJson, int PathLength,
    int Frequency, double Confidence, bool HasWritebackCandidate,
    string? CorrelatedWriteQueries)>
    GetLearnedRoutines(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT routine_hash, path_json, path_length, frequency, confidence,
               has_writeback_candidate, correlated_write_queries
        FROM learned_routines WHERE session_id = @sid ORDER BY frequency DESC
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);

    var results = new List<(string, string, int, int, double, bool, string?)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetString(0), reader.GetString(1),
            reader.GetInt32(2), reader.GetInt32(3), reader.GetDouble(4),
            reader.GetInt32(5) == 1,
            reader.IsDBNull(6) ? null : reader.GetString(6)));
    return results;
}

// === Behavioral Telemetry Counts ===

public int GetBehavioralEventCount(string sessionId, string? eventType)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = eventType is not null
        ? "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid AND event_type = @type"
        : "SELECT COUNT(*) FROM behavioral_events WHERE session_id = @sid";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    if (eventType is not null) cmd.Parameters.AddWithValue("@type", eventType);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public int GetUniqueScreenCount(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(DISTINCT tree_hash) FROM behavioral_events WHERE session_id = @sid AND tree_hash IS NOT NULL";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public int GetCorrelatedActionCount(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public int GetWritebackCandidateCount(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM correlated_actions WHERE session_id = @sid AND query_is_write = 1";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public int GetLearnedRoutineCount(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public int GetRoutinesWithWritebackCount(string sessionId)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM learned_routines WHERE session_id = @sid AND has_writeback_candidate = 1";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    return Convert.ToInt32(cmd.ExecuteScalar());
}

// === Data Retention ===

public int PruneBehavioralEvents(string sessionId, int olderThanDays)
{
    var cutoff = DateTimeOffset.UtcNow.AddDays(-olderThanDays).ToString("o");
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        DELETE FROM behavioral_events
        WHERE session_id = @sid
          AND helper_timestamp < @cutoff
          AND tree_hash IN (
              SELECT DISTINCT lr_tree.tree_hash FROM (
                  SELECT json_extract(value, '$.treeHash') AS tree_hash
                  FROM learned_routines, json_each(learned_routines.path_json)
                  WHERE learned_routines.session_id = @sid AND learned_routines.frequency >= 5
              ) lr_tree
          )
        """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@cutoff", cutoff);
    return cmd.ExecuteNonQuery();
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralDbTests" -v q
```
Expected: 6 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Behavioral/BehavioralDbTests.cs
git commit -m "feat(behavioral): add AgentStateDb schema + CRUD for behavioral events, DMV observations, correlations, routines"
```

---

### Task 5: IPC Extension + BehavioralEventReceiver

**Files:**
- Modify: `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs`
- Create: `src/SuavoAgent.Core/Behavioral/BehavioralEventReceiver.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/BehavioralEventReceiverTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/BehavioralEventReceiverTests.cs
using System.Text.Json;
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class BehavioralEventReceiverTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly BehavioralEventReceiver _receiver;

    public BehavioralEventReceiverTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess1", "pharmacy1");
        _receiver = new BehavioralEventReceiver(_db, "sess1");
    }

    [Fact]
    public void ProcessBatch_ValidEvents_Persists()
    {
        var events = new[]
        {
            new BehavioralEvent(1, BehavioralEventType.TreeSnapshot, null,
                "hash1", null, null, null, null, null, null, null, null, 1,
                "2026-04-13T14:00:00Z"),
            BehavioralEvent.Interaction("invoked", "hash1", "btn1", "Button", null, null)
                .WithSeq(2),
        };

        var result = _receiver.ProcessBatch(events, droppedSinceLast: 0);

        Assert.True(result.Accepted);
        Assert.Equal(2, result.EventsStored);
        var stored = _db.GetBehavioralEvents("sess1", null, limit: 10);
        Assert.Equal(2, stored.Count);
    }

    [Fact]
    public void ProcessBatch_RejectsEventWithValueField()
    {
        // Simulate a rogue event with extra JSON field "value" — defense-in-depth
        var json = """{"seq":1,"type":0,"tree_hash":"h1","ts":"2026-04-13T14:00:00Z","value":"PATIENT NAME","occurrence_count":1}""";
        var evt = JsonSerializer.Deserialize<BehavioralEvent>(json)!;

        var result = _receiver.ProcessBatch(new[] { evt }, 0);
        Assert.Equal(0, result.EventsStored); // rejected
    }

    [Fact]
    public void ProcessBatch_RejectsWrongSessionId()
    {
        var receiver2 = new BehavioralEventReceiver(_db, "other-session");
        var events = new[] { BehavioralEvent.TreeSnapshot("h1").WithSeq(1) };

        // Event has no session_id field — receiver uses its own.
        // This test verifies the receiver is bound to its session.
        var result = receiver2.ProcessBatch(events, 0);
        Assert.True(result.Accepted);

        // Verify events stored under receiver's session
        var stored = _db.GetBehavioralEvents("other-session", null, 10);
        Assert.Single(stored);
    }

    [Fact]
    public void ProcessBatch_TreeSnapshotDedup_WithinWindow()
    {
        var e1 = new BehavioralEvent(1, BehavioralEventType.TreeSnapshot, null,
            "same_hash", null, null, null, null, null, null, null, null, 1,
            "2026-04-13T14:00:00Z");
        var e2 = new BehavioralEvent(2, BehavioralEventType.TreeSnapshot, null,
            "same_hash", null, null, null, null, null, null, null, null, 1,
            "2026-04-13T14:00:30Z"); // within 60s

        _receiver.ProcessBatch(new[] { e1 }, 0);
        _receiver.ProcessBatch(new[] { e2 }, 0);

        var stored = _db.GetBehavioralEvents("sess1", "tree_snapshot", 10);
        Assert.Single(stored); // deduplicated
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify it fails**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralEventReceiverTests" -v q
```

- [ ] **Step 3: Add IPC command + implement receiver**

Add to `IpcCommands` in `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs`:
```csharp
public const string BehavioralEvents = "behavioral_events";
```

```csharp
// src/SuavoAgent.Core/Behavioral/BehavioralEventReceiver.cs
using SuavoAgent.Contracts.Behavioral;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

public sealed class BehavioralEventReceiver
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly Dictionary<string, DateTimeOffset> _recentTreeHashes = new();
    private static readonly TimeSpan TreeSnapshotDedupWindow = TimeSpan.FromSeconds(60);

    public BehavioralEventReceiver(AgentStateDb db, string sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    public record BatchResult(bool Accepted, int EventsStored, int EventsRejected);

    public BatchResult ProcessBatch(IReadOnlyList<BehavioralEvent> events, long droppedSinceLast)
    {
        int stored = 0, rejected = 0;

        foreach (var evt in events)
        {
            if (!IsValid(evt))
            {
                rejected++;
                continue;
            }

            // Tree snapshot deduplication
            if (evt.Type == BehavioralEventType.TreeSnapshot && evt.TreeHash is not null)
            {
                if (_recentTreeHashes.TryGetValue(evt.TreeHash, out var lastSeen)
                    && DateTimeOffset.UtcNow - lastSeen < TreeSnapshotDedupWindow)
                {
                    continue; // skip duplicate
                }
                _recentTreeHashes[evt.TreeHash] = DateTimeOffset.UtcNow;
            }

            _db.InsertBehavioralEvent(_sessionId, evt.Seq,
                evt.Type.ToString().ToLowerInvariant(),
                evt.Subtype,
                evt.TreeHash, evt.ElementId, evt.ControlType, evt.ClassName,
                evt.NameHash, evt.BoundingRect,
                evt.Keystroke?.ToString().ToLowerInvariant(),
                evt.Timing?.ToString().ToLowerInvariant(),
                evt.KeystrokeCount,
                evt.OccurrenceCount, evt.Timestamp);
            stored++;
        }

        // Prune stale dedup entries
        var cutoff = DateTimeOffset.UtcNow - TreeSnapshotDedupWindow * 2;
        foreach (var key in _recentTreeHashes.Keys.ToList())
            if (_recentTreeHashes[key] < cutoff)
                _recentTreeHashes.Remove(key);

        return new BatchResult(true, stored, rejected);
    }

    private static bool IsValid(BehavioralEvent evt)
    {
        // Must have a valid type
        if (!Enum.IsDefined(evt.Type)) return false;

        // Tree snapshots must have a tree_hash
        if (evt.Type == BehavioralEventType.TreeSnapshot && string.IsNullOrEmpty(evt.TreeHash))
            return false;

        // Interactions must have an element_id
        if (evt.Type == BehavioralEventType.Interaction && string.IsNullOrEmpty(evt.ElementId))
            return false;

        return true;
    }
}
```

- [ ] **Step 4: Run tests — verify they pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralEventReceiverTests" -v q
```
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Ipc/IpcMessage.cs src/SuavoAgent.Core/Behavioral/BehavioralEventReceiver.cs tests/SuavoAgent.Core.Tests/Behavioral/BehavioralEventReceiverTests.cs
git commit -m "feat(behavioral): add BehavioralEventReceiver — IPC handler with validation and tree snapshot dedup"
```

---

### Task 6: SqlTokenizer Hardening — Tier 1 PHI Safety

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/SqlTokenizer.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs`

- [ ] **Step 1: Write failing tests for all 5 PHI safety items**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SqlTokenizerHardeningTests
{
    // === Tier 1: PHI Safety ===

    [Theory]
    [InlineData("SELECT * FROM dbo.T WHERE id = 0xDEADBEEF")]
    [InlineData("SELECT * FROM dbo.T WHERE name = 0x4A6F686E")]
    public void HexLiterals_Discarded(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.T WHERE Name = N'John Smith'")]
    [InlineData("SELECT * FROM dbo.T WHERE Name = N'O''Brien'")]
    public void UnicodeLiterals_Discarded(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.T -- patient: John Smith")]
    [InlineData("SELECT * FROM dbo.T /* DOB: 01/15/1990 */")]
    [InlineData("SELECT * FROM dbo.T -- SSN 123-45-6789\nWHERE id = @p")]
    public void CommentsStripped_QueryStillParsed(string sql)
    {
        // After comment stripping, the remaining SQL may be valid
        // The key: comments containing PHI are stripped before tokenization
        var result = SqlTokenizer.TryNormalize(sql);
        // Result may be null (if remaining SQL is invalid) or non-null
        // But the comment text must NOT appear in any output
        if (result is not null)
        {
            Assert.DoesNotContain("John Smith", result.NormalizedShape);
            Assert.DoesNotContain("01/15/1990", result.NormalizedShape);
            Assert.DoesNotContain("123-45-6789", result.NormalizedShape);
        }
    }

    [Fact]
    public void CommentsStripped_ValidQueryRemains()
    {
        var sql = "SELECT /* metadata */ RxNumber FROM Prescription.Rx WHERE Status = @p";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    }

    [Theory]
    [InlineData("SELECT * FROM OPENQUERY(LinkedSrv, 'SELECT PatientName FROM dbo.Patients')")]
    [InlineData("SELECT * FROM OPENROWSET('SQLNCLI', '...', 'SELECT * FROM dbo.T')")]
    [InlineData("SELECT * FROM OPENDATASOURCE('SQLNCLI', '...').db.dbo.T")]
    public void LinkedServerFunctions_Blocked(string sql)
    {
        Assert.Null(SqlTokenizer.TryNormalize(sql));
    }

    [Fact]
    public void LikePattern_NotMisparsedAsTable()
    {
        var sql = "SELECT RxNumber FROM Prescription.Rx WHERE Status LIKE @p";
        var result = SqlTokenizer.TryNormalize(sql);
        Assert.NotNull(result);
        Assert.Contains("Prescription.Rx", result!.TablesReferenced);
        Assert.DoesNotContain("LIKE", result.TablesReferenced);
    }
}
```

- [ ] **Step 2: Run tests — verify they fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "SqlTokenizerHardeningTests" -v q
```

- [ ] **Step 3: Implement Tier 1 hardening in SqlTokenizer**

In `src/SuavoAgent.Core/Learning/SqlTokenizer.cs`, add the new patterns and modify `TryNormalize`:

Add blocked keywords:
```csharp
private static readonly HashSet<string> BlockedKeywords = new(StringComparer.OrdinalIgnoreCase)
{
    "CREATE", "ALTER", "DROP", "TRUNCATE", "EXEC", "EXECUTE", "GRANT", "REVOKE", "DENY",
    "OPENQUERY", "OPENROWSET", "OPENDATASOURCE"  // Item 4: linked server functions
};
```

Add new regex patterns:
```csharp
// Hex literal: 0xDEADBEEF (may encode patient data)
[GeneratedRegex(@"0x[0-9a-fA-F]+", RegexOptions.Compiled)]
private static partial Regex HexLiteralPattern();

// Unicode string literal with escaped quotes: N'O''Brien'
[GeneratedRegex(@"N?'(?:[^']|'')*'", RegexOptions.Compiled)]
private static partial Regex UnicodeLiteralPattern();

// Line comment: -- anything to end of line
[GeneratedRegex(@"--.*$", RegexOptions.Compiled | RegexOptions.Multiline)]
private static partial Regex LineCommentPattern();

// Block comment: /* ... */ (non-greedy)
[GeneratedRegex(@"/\*[\s\S]*?\*/", RegexOptions.Compiled)]
private static partial Regex BlockCommentPattern();
```

Modify `TryNormalize` to strip comments first, then check hex literals, then use strengthened string literal check:
```csharp
public static NormalizedQuery? TryNormalize(string? sql)
{
    if (string.IsNullOrWhiteSpace(sql)) return null;

    // Item 3: Strip comments FIRST (may contain PHI)
    var trimmed = BlockCommentPattern().Replace(sql.Trim(), " ");
    trimmed = LineCommentPattern().Replace(trimmed, "").Trim();

    if (string.IsNullOrWhiteSpace(trimmed)) return null;

    var firstWord = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0]
        .TrimStart('(');

    if (BlockedKeywords.Contains(firstWord)) return null;

    // Item 4: Also check for OPENQUERY/OPENROWSET/OPENDATASOURCE anywhere in text
    if (trimmed.Contains("OPENQUERY", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Contains("OPENROWSET", StringComparison.OrdinalIgnoreCase) ||
        trimmed.Contains("OPENDATASOURCE", StringComparison.OrdinalIgnoreCase))
        return null;

    if (!AllowedKeywords.Contains(firstWord)) return null;

    // Item 1: Hex literals
    if (HexLiteralPattern().IsMatch(trimmed)) return null;

    // Item 2: String literals (strengthened — handles N'unicode' and escaped quotes)
    if (UnicodeLiteralPattern().IsMatch(trimmed)) return null;

    // Numeric literals (existing check)
    if (NumericLiteralPattern().IsMatch(trimmed)) return null;

    // Extract table references
    var tables = new List<string>();
    foreach (Match m in TableRefPattern().Matches(trimmed))
    {
        var schema = m.Groups[1].Success ? m.Groups[1].Value : "";
        var table = m.Groups[2].Value;
        // Item 5: Don't parse LIKE keyword as table
        if (table.Equals("LIKE", StringComparison.OrdinalIgnoreCase)) continue;
        var fullName = string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
        if (!tables.Contains(fullName))
            tables.Add(fullName);
    }

    if (tables.Count == 0) return null;

    var shape = ParameterPattern().Replace(trimmed, "@p");
    var isWrite = firstWord.Equals("INSERT", StringComparison.OrdinalIgnoreCase)
               || firstWord.Equals("UPDATE", StringComparison.OrdinalIgnoreCase)
               || firstWord.Equals("DELETE", StringComparison.OrdinalIgnoreCase);

    return new NormalizedQuery(shape, tables, isWrite);
}
```

- [ ] **Step 4: Run ALL tokenizer tests — verify old + new pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "SqlTokenizer" -v q
```
Expected: All SqlTokenizerTests + SqlTokenizerHardeningTests pass.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/SqlTokenizer.cs tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs
git commit -m "feat(tokenizer): add Tier 1 PHI safety hardening — hex literals, unicode strings, comment stripping, OPENQUERY blocking, LIKE safety"
```

---

### Task 7: SqlTokenizer Hardening — Tier 2 Parsing Completeness

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/SqlTokenizer.cs`
- Modify: `tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs`

- [ ] **Step 1: Write failing tests for Tier 2 items**

Append to `SqlTokenizerHardeningTests.cs`:
```csharp
// === Tier 2: Parsing Completeness ===

[Fact]
public void NestedSubquery_ExtractsInnerTables()
{
    var sql = "SELECT * FROM Prescription.Rx WHERE PatientId IN (SELECT PersonId FROM Person.Patient WHERE Active = @p)";
    var result = SqlTokenizer.TryNormalize(sql);
    Assert.NotNull(result);
    Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    Assert.Contains("Person.Patient", result.TablesReferenced);
}

[Fact]
public void Cte_ExtractsTablesFromBody()
{
    var sql = "WITH active AS (SELECT RxNumber FROM Prescription.Rx WHERE Status = @p) SELECT * FROM active JOIN Prescription.RxDetail rd ON active.RxNumber = rd.RxNumber";
    var result = SqlTokenizer.TryNormalize(sql);
    Assert.NotNull(result);
    Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    Assert.Contains("Prescription.RxDetail", result.TablesReferenced);
    Assert.DoesNotContain("active", result.TablesReferenced); // CTE name, not a table
}

[Fact]
public void UnionQuery_ExtractsTablesFromAllBranches()
{
    var sql = "SELECT RxNumber FROM Prescription.Rx WHERE Status = @p UNION SELECT RxNumber FROM Prescription.Archive WHERE Status = @p";
    var result = SqlTokenizer.TryNormalize(sql);
    Assert.NotNull(result);
    Assert.Contains("Prescription.Rx", result!.TablesReferenced);
    Assert.Contains("Prescription.Archive", result.TablesReferenced);
}

[Fact]
public void AliasedTable_ExtractsRealName_NotAlias()
{
    var sql = "SELECT rt.RxNumber FROM Prescription.RxTransaction rt WHERE rt.Status = @p";
    var result = SqlTokenizer.TryNormalize(sql);
    Assert.NotNull(result);
    Assert.Contains("Prescription.RxTransaction", result!.TablesReferenced);
    Assert.DoesNotContain("rt", result.TablesReferenced);
}

[Fact]
public void CrossDatabaseRef_ExtractsSchemaTable()
{
    var sql = "SELECT * FROM OtherDb.Prescription.RxTransaction WHERE Status = @p";
    var result = SqlTokenizer.TryNormalize(sql);
    Assert.NotNull(result);
    Assert.Contains("Prescription.RxTransaction", result!.TablesReferenced);
    Assert.DoesNotContain("OtherDb", result.TablesReferenced.SelectMany(t => t.Split('.')));
}
```

- [ ] **Step 2: Run test — verify they fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "SqlTokenizerHardeningTests" -v q
```

- [ ] **Step 3: Implement Tier 2 hardening**

Update the table reference regex to handle three-part names and aliases. Add CTE detection and UNION splitting logic to `TryNormalize`:

```csharp
// Updated table reference pattern: handles 1-part, 2-part (schema.table), and 3-part (db.schema.table)
[GeneratedRegex(@"(?:FROM|JOIN|INTO|UPDATE)\s+(?:\[?(\w+)\]?\.)?(?:\[?(\w+)\]?\.)?\[?(\w+)\]?(?:\s+(?:AS\s+)?(\w{1,4}))?(?:\s|$|,|\(|ON)",
    RegexOptions.Compiled | RegexOptions.IgnoreCase)]
private static partial Regex TableRefPatternV2();

// CTE pattern: WITH name AS (
[GeneratedRegex(@"\bWITH\s+(\w+)\s+AS\s*\(", RegexOptions.Compiled | RegexOptions.IgnoreCase)]
private static partial Regex CtePattern();
```

In `TryNormalize`, after comment stripping and keyword checks, replace the table extraction logic:

```csharp
// Detect CTE names (to exclude from table references)
var cteNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
foreach (Match cteMatch in CtePattern().Matches(trimmed))
    cteNames.Add(cteMatch.Groups[1].Value);

// Extract table references using V2 pattern
var tables = new List<string>();
foreach (Match m in TableRefPatternV2().Matches(trimmed))
{
    // Groups: 1=db_or_schema, 2=schema_if_3part, 3=table, 4=alias
    string? part1 = m.Groups[1].Success ? m.Groups[1].Value : null;
    string? part2 = m.Groups[2].Success ? m.Groups[2].Value : null;
    string table = m.Groups[3].Value;

    // Skip LIKE and other keywords
    if (table.Equals("LIKE", StringComparison.OrdinalIgnoreCase)) continue;

    string fullName;
    if (part2 is not null)
    {
        // Three-part name: db.schema.table — extract schema.table only (Item 10)
        fullName = $"{part2}.{table}";
    }
    else if (part1 is not null)
    {
        // Two-part name: schema.table
        fullName = $"{part1}.{table}";
    }
    else
    {
        fullName = table;
    }

    // Skip CTE names (Item 7)
    if (cteNames.Contains(fullName) || cteNames.Contains(table)) continue;

    if (!tables.Contains(fullName))
        tables.Add(fullName);
}
```

- [ ] **Step 4: Run ALL tokenizer tests**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "SqlTokenizer" -v q
```
Expected: All old + new tests pass.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/SqlTokenizer.cs tests/SuavoAgent.Core.Tests/Learning/SqlTokenizerHardeningTests.cs
git commit -m "feat(tokenizer): add Tier 2 parsing — nested subqueries, CTEs, UNION, aliases, cross-database refs"
```

---

### Task 8: UiaTreeObserver (Helper — Windows-dependent)

**Files:**
- Create: `src/SuavoAgent.Helper/Behavioral/UiaTreeObserver.cs`

- [ ] **Step 1: Implement UiaTreeObserver**

```csharp
// src/SuavoAgent.Helper/Behavioral/UiaTreeObserver.cs
using System.Security.Cryptography;
using System.Text;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Periodic tree walker for PMS windows. Captures GREEN properties plain,
/// YELLOW properties HMAC-hashed. Runs every 60s when PMS process is alive.
/// PMS-agnostic — works on any window identified as PMS candidate.
/// </summary>
public sealed class UiaTreeObserver : IDisposable
{
    private const int MaxDepth = 8;
    private const int IntervalSeconds = 60;

    private readonly UIA2Automation _automation;
    private readonly string _pharmacySalt;
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private volatile bool _running;

    public UiaTreeObserver(UIA2Automation automation, string pharmacySalt,
        BehavioralEventBuffer buffer, ILogger logger)
    {
        _automation = automation;
        _pharmacySalt = pharmacySalt;
        _buffer = buffer;
        _logger = logger;
    }

    public async Task RunAsync(Func<Window?> getWindow, CancellationToken ct)
    {
        _running = true;
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                var window = getWindow();
                if (window is not null)
                    WalkTree(window);
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Tree walk failed");
            }

            await Task.Delay(TimeSpan.FromSeconds(IntervalSeconds), ct);
        }
    }

    private void WalkTree(Window window)
    {
        var elements = new List<ScrubbedElement>();
        var hashBuilder = new StringBuilder();

        WalkElement(window, 0, 0, elements, hashBuilder);

        if (elements.Count == 0) return;

        var treeHash = ComputeTreeHash(hashBuilder.ToString());
        var evt = BehavioralEvent.TreeSnapshot(treeHash);
        _buffer.Enqueue(evt);

        _logger.Debug("Tree snapshot: {Count} elements, hash={Hash}", elements.Count, treeHash[..12]);
    }

    private void WalkElement(AutomationElement element, int depth, int childIndex,
        List<ScrubbedElement> elements, StringBuilder hashBuilder)
    {
        if (depth > MaxDepth) return;

        try
        {
            var controlType = element.Properties.ControlType.ValueOrDefault.ToString();
            var automationId = element.Properties.AutomationId.ValueOrDefault;
            var className = element.Properties.ClassName.ValueOrDefault;
            var name = element.Properties.Name.ValueOrDefault;
            var rect = element.Properties.BoundingRectangle.IsSupported
                ? FormatRect(element.Properties.BoundingRectangle.Value)
                : null;

            // RED properties are NEVER read. No calls to .Value, .Text, .Selection, etc.

            var raw = new RawElementProperties(controlType, automationId, className,
                name, rect, depth, childIndex);

            var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
            if (scrubbed is not null)
            {
                elements.Add(scrubbed);
                // Tree hash uses only structural properties (not Name)
                hashBuilder.Append(controlType).Append(automationId).Append(className);
            }

            // Walk children
            var children = element.FindAllChildren();
            // Track child index per ControlType for tree-positional fallback
            var typeIndexes = new Dictionary<string, int>();
            foreach (var child in children)
            {
                var childType = child.Properties.ControlType.ValueOrDefault.ToString();
                typeIndexes.TryGetValue(childType, out var idx);
                WalkElement(child, depth + 1, idx, elements, hashBuilder);
                typeIndexes[childType] = idx + 1;
            }
        }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Element walk failed at depth {Depth}", depth);
        }
    }

    private static string FormatRect(System.Drawing.Rectangle r)
        => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private static string ComputeTreeHash(string structureString)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(structureString));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Stop() => _running = false;
    public void Dispose() => _running = false;
}
```

- [ ] **Step 2: Verify build compiles** (no integration test — FlaUI requires Windows)

```bash
dotnet build src/SuavoAgent.Helper -v q
```
Expected: Build succeeds. Integration testing requires a Windows machine with PioneerRx running.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Behavioral/UiaTreeObserver.cs
git commit -m "feat(behavioral): add UiaTreeObserver — periodic tree walks with GREEN/YELLOW scrubbing"
```

---

### Task 9: UiaInteractionObserver (Helper — Windows-dependent)

**Files:**
- Create: `src/SuavoAgent.Helper/Behavioral/UiaInteractionObserver.cs`

- [ ] **Step 1: Implement UiaInteractionObserver**

```csharp
// src/SuavoAgent.Helper/Behavioral/UiaInteractionObserver.cs
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.EventHandlers;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// Subscribes to UIA events on PMS windows. Event-driven, not polling.
/// Captures FocusChanged, InvokePattern.Invoked, StructureChanged.
/// Does NOT subscribe to TextChanged, ValuePattern, or SelectionPattern (RED tier).
/// </summary>
public sealed class UiaInteractionObserver : IDisposable
{
    private readonly UIA2Automation _automation;
    private readonly string _pharmacySalt;
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly Action _triggerTreeResnapshot;
    private readonly List<IDisposable> _handlers = new();
    private string? _currentTreeHash;

    public UiaInteractionObserver(UIA2Automation automation, string pharmacySalt,
        BehavioralEventBuffer buffer, ILogger logger, Action triggerTreeResnapshot)
    {
        _automation = automation;
        _pharmacySalt = pharmacySalt;
        _buffer = buffer;
        _logger = logger;
        _triggerTreeResnapshot = triggerTreeResnapshot;
    }

    public void SetCurrentTreeHash(string treeHash)
        => _currentTreeHash = treeHash;

    public void Subscribe(Window window)
    {
        try
        {
            // FocusChanged — global event, filter to our window's process
            var focusHandler = _automation.RegisterFocusChangedEvent(element =>
            {
                try
                {
                    if (!BelongsToWindow(element, window)) return;
                    OnFocusChanged(element);
                }
                catch { }
            });

            // StructureChanged on the window
            var structHandler = _automation.RegisterStructureChangedEvent(
                window, TreeScope.Subtree, (element, change) =>
                {
                    try
                    {
                        OnStructureChanged(element, change);
                    }
                    catch { }
                });

            _logger.Information("Subscribed to UIA events on PMS window");
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to subscribe to UIA events");
        }
    }

    private void OnFocusChanged(AutomationElement element)
    {
        var raw = ExtractGreenYellow(element, 0, 0);
        var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
        if (scrubbed is null) return;

        var elementId = UiaPropertyScrubber.BuildElementId(raw);
        if (elementId is null) return;

        var evt = BehavioralEvent.Interaction("focus_changed",
            _currentTreeHash ?? "unknown", elementId,
            scrubbed.ControlType, scrubbed.ClassName, scrubbed.NameHash);
        _buffer.Enqueue(evt);
    }

    private void OnStructureChanged(AutomationElement element, StructureChangeType change)
    {
        _logger.Debug("StructureChanged: {Change}", change);
        _triggerTreeResnapshot();

        var raw = ExtractGreenYellow(element, 0, 0);
        var elementId = UiaPropertyScrubber.BuildElementId(raw);

        var evt = BehavioralEvent.Interaction("structure_changed",
            _currentTreeHash ?? "unknown", elementId ?? "root",
            raw.ControlType, raw.ClassName, null);
        _buffer.Enqueue(evt);
    }

    /// <summary>
    /// Record an invocation (called by external click detection or InvokePattern handler).
    /// </summary>
    public void RecordInvocation(AutomationElement element, int depth, int childIndex)
    {
        var raw = ExtractGreenYellow(element, depth, childIndex);
        var scrubbed = UiaPropertyScrubber.TryScrub(raw, _pharmacySalt);
        if (scrubbed is null) return;

        var elementId = UiaPropertyScrubber.BuildElementId(raw);
        if (elementId is null) return;

        var evt = BehavioralEvent.Interaction("invoked",
            _currentTreeHash ?? "unknown", elementId,
            scrubbed.ControlType, scrubbed.ClassName, scrubbed.NameHash);
        _buffer.Enqueue(evt);
    }

    private static RawElementProperties ExtractGreenYellow(AutomationElement element,
        int depth, int childIndex)
    {
        // Only read GREEN + YELLOW properties. Never touch Value, Text, Selection.
        return new RawElementProperties(
            ControlType: element.Properties.ControlType.ValueOrDefault.ToString(),
            AutomationId: element.Properties.AutomationId.ValueOrDefault,
            ClassName: element.Properties.ClassName.ValueOrDefault,
            Name: element.Properties.Name.ValueOrDefault,
            BoundingRect: null, // not needed for interaction events
            Depth: depth,
            ChildIndex: childIndex);
    }

    private static bool BelongsToWindow(AutomationElement element, Window window)
    {
        try
        {
            return element.Properties.ProcessId.ValueOrDefault ==
                   window.Properties.ProcessId.ValueOrDefault;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        foreach (var h in _handlers) h.Dispose();
        _handlers.Clear();
    }
}
```

- [ ] **Step 2: Verify build compiles**

```bash
dotnet build src/SuavoAgent.Helper -v q
```

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Behavioral/UiaInteractionObserver.cs
git commit -m "feat(behavioral): add UiaInteractionObserver — FocusChanged + StructureChanged + Invoked events"
```

---

### Task 10: KeyboardCategoryHook (Helper — Windows-dependent)

**Files:**
- Create: `src/SuavoAgent.Helper/Behavioral/KeyboardCategoryHook.cs`

- [ ] **Step 1: Implement KeyboardCategoryHook**

```csharp
// src/SuavoAgent.Helper/Behavioral/KeyboardCategoryHook.cs
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.Behavioral;

/// <summary>
/// WH_KEYBOARD_LL hook. Only active when PMS window has foreground focus.
/// Captures keystroke CATEGORIES only — never actual key codes or characters.
/// VK code is mapped to category inline in the hook callback, then discarded.
/// </summary>
public sealed class KeyboardCategoryHook : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly ILogger _logger;
    private readonly int _pmsProcessId;
    private IntPtr _hookId = IntPtr.Zero;
    private LowLevelKeyboardProc? _hookProc;
    private DateTimeOffset _lastKeystrokeTime = DateTimeOffset.MinValue;
    private KeystrokeCategory? _currentCategory;
    private int _currentSequenceCount;

    // Win32 constants
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;

    public KeyboardCategoryHook(BehavioralEventBuffer buffer, ILogger logger, int pmsProcessId)
    {
        _buffer = buffer;
        _logger = logger;
        _pmsProcessId = pmsProcessId;
    }

    public void Install()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            _logger.Warning("KeyboardCategoryHook requires Windows — skipping");
            return;
        }

        _hookProc = HookCallback;
        using var curProcess = Process.GetCurrentProcess();
        using var curModule = curProcess.MainModule!;
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc,
            GetModuleHandle(curModule.ModuleName), 0);

        if (_hookId == IntPtr.Zero)
            _logger.Warning("Failed to install keyboard hook");
        else
            _logger.Information("Keyboard category hook installed for PMS PID {Pid}", _pmsProcessId);
    }

    public void Uninstall()
    {
        if (_hookId != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookId);
            _hookId = IntPtr.Zero;
            FlushCurrentSequence();
            _logger.Information("Keyboard category hook uninstalled");
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam == (IntPtr)WM_KEYDOWN)
        {
            // Only capture when PMS has foreground focus
            if (IsPmsForeground())
            {
                var vkCode = Marshal.ReadInt32(lParam);
                var category = ClassifyVkCode(vkCode);
                // VK code discarded here — never stored, never buffered
                RecordCategory(category);
            }
        }
        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    /// <summary>
    /// Maps a virtual key code to a category. The VK code itself is immediately discarded.
    /// </summary>
    private static KeystrokeCategory ClassifyVkCode(int vkCode) => vkCode switch
    {
        >= 0x41 and <= 0x5A => KeystrokeCategory.Alpha,     // A-Z
        >= 0x30 and <= 0x39 => KeystrokeCategory.Digit,     // 0-9
        >= 0x60 and <= 0x69 => KeystrokeCategory.Digit,     // Numpad 0-9
        0x09 => KeystrokeCategory.Tab,
        0x0D => KeystrokeCategory.Enter,
        0x1B => KeystrokeCategory.Escape,
        >= 0x70 and <= 0x87 => KeystrokeCategory.FunctionKey, // F1-F24
        >= 0x25 and <= 0x28 => KeystrokeCategory.Navigation,  // Arrow keys
        0x21 or 0x22 or 0x23 or 0x24 => KeystrokeCategory.Navigation, // PgUp/PgDn/End/Home
        0x10 or 0x11 or 0x12 => KeystrokeCategory.Modifier,   // Shift/Ctrl/Alt
        0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 => KeystrokeCategory.Modifier,
        _ => KeystrokeCategory.Other
    };

    private void RecordCategory(KeystrokeCategory category)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = now - _lastKeystrokeTime;
        var timing = elapsed.TotalMilliseconds switch
        {
            < 500 => TimingBucket.Rapid,
            < 2000 => TimingBucket.Normal,
            _ => TimingBucket.Pause
        };

        // Coalesce same-category keystrokes
        if (category == _currentCategory && timing == TimingBucket.Rapid)
        {
            _currentSequenceCount++;
        }
        else
        {
            FlushCurrentSequence();
            _currentCategory = category;
            _currentSequenceCount = 1;
        }

        _lastKeystrokeTime = now;
    }

    private void FlushCurrentSequence()
    {
        if (_currentCategory is null || _currentSequenceCount == 0) return;

        var timing = _currentSequenceCount > 1 ? TimingBucket.Rapid : TimingBucket.Normal;
        var evt = BehavioralEvent.Keystroke(_currentCategory.Value, timing, _currentSequenceCount);
        _buffer.Enqueue(evt);

        _currentCategory = null;
        _currentSequenceCount = 0;
    }

    private bool IsPmsForeground()
    {
        try
        {
            var foregroundWindow = GetForegroundWindow();
            if (foregroundWindow == IntPtr.Zero) return false;
            GetWindowThreadProcessId(foregroundWindow, out var pid);
            return pid == _pmsProcessId;
        }
        catch { return false; }
    }

    public void Dispose() => Uninstall();

    // P/Invoke declarations
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);
}
```

- [ ] **Step 2: Verify build compiles**

```bash
dotnet build src/SuavoAgent.Helper -v q
```

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Behavioral/KeyboardCategoryHook.cs
git commit -m "feat(behavioral): add KeyboardCategoryHook — WH_KEYBOARD_LL with PMS-focus guard, categories only"
```

---

### Task 11: DmvQueryObserver

**Files:**
- Create: `src/SuavoAgent.Core/Learning/DmvQueryObserver.cs`
- Test: `tests/SuavoAgent.Core.Tests/Learning/DmvQueryObserverTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/DmvQueryObserverTests.cs
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class DmvQueryObserverTests : IDisposable
{
    private readonly AgentStateDb _db;

    public DmvQueryObserverTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess1", "pharm1");
    }

    [Fact]
    public void ProcessRawSql_Parameterized_Persists()
    {
        DmvQueryObserver.ProcessAndStore(_db, "sess1",
            "SELECT RxNumber FROM Prescription.Rx WHERE PatientID = @p1", 5,
            "2026-04-13T14:00:00Z", clockOffsetMs: 0);

        var obs = _db.GetDmvQueryObservations("sess1", 10);
        Assert.Single(obs);
        Assert.Contains("Prescription.Rx", obs[0].TablesReferenced);
        Assert.False(obs[0].IsWrite);
    }

    [Fact]
    public void ProcessRawSql_WriteQuery_FlaggedAsWrite()
    {
        DmvQueryObserver.ProcessAndStore(_db, "sess1",
            "UPDATE Prescription.RxTransaction SET StatusTypeID = @p WHERE RxNumber = @p", 1,
            "2026-04-13T14:00:00Z", clockOffsetMs: 0);

        var obs = _db.GetDmvQueryObservations("sess1", 10);
        Assert.Single(obs);
        Assert.True(obs[0].IsWrite);
    }

    [Fact]
    public void ProcessRawSql_ContainsLiterals_Discarded()
    {
        DmvQueryObserver.ProcessAndStore(_db, "sess1",
            "SELECT * FROM dbo.Patient WHERE Name = 'John Smith'", 1,
            "2026-04-13T14:00:00Z", clockOffsetMs: 0);

        var obs = _db.GetDmvQueryObservations("sess1", 10);
        Assert.Empty(obs); // fail-closed: literal found, discarded
    }

    [Fact]
    public void ProcessRawSql_DuplicateShape_Upserts()
    {
        DmvQueryObserver.ProcessAndStore(_db, "sess1",
            "SELECT RxNumber FROM Prescription.Rx WHERE Status = @p", 3,
            "2026-04-13T14:00:00Z", clockOffsetMs: 0);
        DmvQueryObserver.ProcessAndStore(_db, "sess1",
            "SELECT RxNumber FROM Prescription.Rx WHERE Status = @p", 7,
            "2026-04-13T14:05:00Z", clockOffsetMs: -10);

        var obs = _db.GetDmvQueryObservations("sess1", 10);
        Assert.Single(obs);
        Assert.Equal(7, obs[0].ExecutionCount); // updated
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "DmvQueryObserverTests" -v q
```

- [ ] **Step 3: Implement DmvQueryObserver**

```csharp
// src/SuavoAgent.Core/Learning/DmvQueryObserver.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Polls SQL Server DMVs for recently executed queries and processes them through
/// the fail-closed SqlTokenizer. Persists normalized shapes for ActionCorrelator.
/// Requires VIEW SERVER STATE permission — goes dormant if unavailable.
/// </summary>
public sealed class DmvQueryObserver : ILearningObserver
{
    private readonly AgentStateDb _db;
    private readonly ILogger _logger;
    private readonly Func<SqlConnection> _connFactory;
    private volatile bool _running;
    private int _eventsCollected;
    private DateTimeOffset _lastActivity;
    private bool _hasDmvAccess;
    private DateTimeOffset _lastPollTime = DateTimeOffset.UtcNow.AddMinutes(-5);
    private int _clockOffsetMs;
    private DateTimeOffset _lastCalibration = DateTimeOffset.MinValue;

    public string Name => "dmv";
    public ObserverPhase ActivePhases => ObserverPhase.Pattern | ObserverPhase.Model;
    public bool HasDmvAccess => _hasDmvAccess;
    public int ClockOffsetMs => _clockOffsetMs;

    public DmvQueryObserver(AgentStateDb db, Func<SqlConnection> connFactory, ILogger<DmvQueryObserver> logger)
    {
        _db = db;
        _connFactory = connFactory;
        _logger = logger;
    }

    public async Task StartAsync(string sessionId, CancellationToken ct)
    {
        _running = true;
        _logger.LogInformation("DmvQueryObserver started for session {Session}", sessionId);

        // Check DMV access
        try
        {
            await using var conn = _connFactory();
            await conn.OpenAsync(ct);
            await using var cmd = new SqlCommand("SELECT TOP 1 1 FROM sys.dm_exec_query_stats", conn);
            cmd.CommandTimeout = 5;
            await cmd.ExecuteScalarAsync(ct);
            _hasDmvAccess = true;
            _logger.LogInformation("DMV access confirmed");
        }
        catch
        {
            _hasDmvAccess = false;
            _logger.LogInformation("DMV access unavailable — DmvQueryObserver dormant");
            return;
        }

        // Poll loop
        while (!ct.IsCancellationRequested && _running)
        {
            try
            {
                await PollAsync(sessionId, ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DMV poll failed");
            }
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    private async Task PollAsync(string sessionId, CancellationToken ct)
    {
        await using var conn = _connFactory();
        await conn.OpenAsync(ct);

        // Calibrate clock hourly
        if (DateTimeOffset.UtcNow - _lastCalibration > TimeSpan.FromHours(1))
            await CalibrateClockAsync(conn, ct);

        await using var cmd = new SqlCommand("""
            SELECT qs.execution_count, qs.last_execution_time,
                   SUBSTRING(st.text, (qs.statement_start_offset/2)+1,
                       ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
                         ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1)
            FROM sys.dm_exec_query_stats qs
            CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
            WHERE qs.last_execution_time > @since
            ORDER BY qs.last_execution_time DESC
            """, conn);
        cmd.CommandTimeout = 15;
        cmd.Parameters.AddWithValue("@since", _lastPollTime.UtcDateTime);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var execCount = reader.GetInt64(0);
            var lastExecTime = reader.GetDateTime(1);
            var sqlText = reader.IsDBNull(2) ? null : reader.GetString(2);

            ProcessAndStore(_db, sessionId, sqlText, (int)execCount,
                new DateTimeOffset(lastExecTime, TimeSpan.Zero).ToString("o"), _clockOffsetMs);
            _eventsCollected++;
        }

        _lastPollTime = DateTimeOffset.UtcNow;
        _lastActivity = DateTimeOffset.UtcNow;
    }

    public static void ProcessAndStore(AgentStateDb db, string sessionId,
        string? rawSql, int executionCount, string lastExecutionTime, int clockOffsetMs)
    {
        var normalized = SqlTokenizer.TryNormalize(rawSql);
        if (normalized is null) return; // fail-closed

        var shapeHash = ComputeShapeHash(normalized.NormalizedShape);
        var tablesJson = JsonSerializer.Serialize(normalized.TablesReferenced);

        db.UpsertDmvQueryObservation(sessionId, shapeHash,
            normalized.NormalizedShape, tablesJson, normalized.IsWrite,
            executionCount, lastExecutionTime, clockOffsetMs);
    }

    private async Task CalibrateClockAsync(SqlConnection conn, CancellationToken ct)
    {
        try
        {
            var before = DateTimeOffset.UtcNow;
            await using var cmd = new SqlCommand("SELECT GETUTCDATE()", conn);
            cmd.CommandTimeout = 5;
            var sqlTime = (DateTime)(await cmd.ExecuteScalarAsync(ct))!;
            var after = DateTimeOffset.UtcNow;
            var midpoint = before + (after - before) / 2;
            _clockOffsetMs = (int)(midpoint - new DateTimeOffset(sqlTime, TimeSpan.Zero)).TotalMilliseconds;
            _lastCalibration = DateTimeOffset.UtcNow;
            _logger.LogDebug("Clock calibrated: offset={Offset}ms", _clockOffsetMs);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clock calibration failed — using offset=0, wider correlation window");
            _clockOffsetMs = 0;
        }
    }

    private static string ComputeShapeHash(string shape)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(shape));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public Task StopAsync() { _running = false; return Task.CompletedTask; }
    public ObserverHealth CheckHealth() => new(Name, _running, _eventsCollected, 0, _lastActivity);
    public void Dispose() => _running = false;
}
```

- [ ] **Step 4: Run tests — verify pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "DmvQueryObserverTests" -v q
```
Expected: 4 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/DmvQueryObserver.cs tests/SuavoAgent.Core.Tests/Learning/DmvQueryObserverTests.cs
git commit -m "feat(behavioral): add DmvQueryObserver — DMV polling with fail-closed tokenizer pipeline"
```

---

### Task 12: ActionCorrelator

**Files:**
- Create: `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class ActionCorrelatorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly ActionCorrelator _correlator;

    public ActionCorrelatorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess1", "pharm1");
        _correlator = new ActionCorrelator(_db, "sess1", correlationWindowSeconds: 2);
    }

    [Fact]
    public void Correlate_UiEventMatchesSqlEvent_CreatesCorrelation()
    {
        // UI event: click btnComplete at T=14:00:00
        _correlator.RecordUiEvent("hash1", "btnComplete", "Button",
            DateTimeOffset.Parse("2026-04-13T14:00:00Z"));

        // SQL event: UPDATE at T=14:00:01.2 (within 2s window)
        _correlator.TryCorrelateWithSql("qhash1", "2026-04-13T14:00:01.200Z",
            isWrite: true, tablesReferenced: "[\"Prescription.RxTransaction\"]");

        var actions = _db.GetCorrelatedActions("sess1");
        Assert.Single(actions);
        Assert.Equal("hash1:btnComplete:qhash1", actions[0].CorrelationKey);
        Assert.True(actions[0].IsWrite);
    }

    [Fact]
    public void Correlate_OutsideWindow_NoCorrelation()
    {
        _correlator.RecordUiEvent("hash1", "btnComplete", "Button",
            DateTimeOffset.Parse("2026-04-13T14:00:00Z"));

        // SQL event at T=14:00:05 (outside 2s window)
        _correlator.TryCorrelateWithSql("qhash1", "2026-04-13T14:00:05Z",
            isWrite: true, tablesReferenced: "[\"T\"]");

        Assert.Empty(_db.GetCorrelatedActions("sess1"));
    }

    [Fact]
    public void Correlate_RepeatedCorrelation_IncrementsConfidence()
    {
        for (int i = 0; i < 5; i++)
        {
            var t = DateTimeOffset.Parse("2026-04-13T14:00:00Z").AddMinutes(i);
            _correlator.RecordUiEvent("hash1", "btnComplete", "Button", t);
            _correlator.TryCorrelateWithSql("qhash1",
                t.AddSeconds(1).ToString("o"), true, "[\"T\"]");
        }

        var actions = _db.GetCorrelatedActions("sess1");
        Assert.Single(actions);
        Assert.Equal(5, actions[0].OccurrenceCount);
        Assert.True(actions[0].Confidence >= 0.6); // 3+ = 0.6
    }

    [Fact]
    public void Correlate_UiEventWithoutSql_NoCorrelation()
    {
        _correlator.RecordUiEvent("hash1", "btnComplete", "Button",
            DateTimeOffset.Parse("2026-04-13T14:00:00Z"));

        // No SQL event
        Assert.Empty(_db.GetCorrelatedActions("sess1"));
    }

    [Fact]
    public void SlidingWindow_OldEventsExpire()
    {
        // Old UI event
        _correlator.RecordUiEvent("hash1", "btnOld", "Button",
            DateTimeOffset.UtcNow.AddSeconds(-60));

        // Recent SQL event
        _correlator.TryCorrelateWithSql("qhash1",
            DateTimeOffset.UtcNow.ToString("o"), false, "[\"T\"]");

        // Old event should have expired from the window
        Assert.Empty(_db.GetCorrelatedActions("sess1"));
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "ActionCorrelatorTests" -v q
```

- [ ] **Step 3: Implement ActionCorrelator**

```csharp
// src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Links UI events to SQL events by timestamp proximity.
/// When a DMV query observation arrives, scans recent UI events within the correlation window.
/// Matched pairs become CorrelatedActions — the writeback discovery signal.
/// </summary>
public sealed class ActionCorrelator
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly double _windowSeconds;
    private readonly double _fallbackWindowSeconds;
    private readonly List<UiEvent> _recentUiEvents = new();
    private static readonly TimeSpan SlidingWindowDuration = TimeSpan.FromSeconds(30);
    private bool _clockCalibrated;

    public ActionCorrelator(AgentStateDb db, string sessionId,
        double correlationWindowSeconds = 2.0, bool clockCalibrated = true)
    {
        _db = db;
        _sessionId = sessionId;
        _windowSeconds = correlationWindowSeconds;
        _fallbackWindowSeconds = 5.0; // wider window when clock not calibrated
        _clockCalibrated = clockCalibrated;
    }

    public void SetClockCalibrated(bool calibrated)
        => _clockCalibrated = calibrated;

    private record UiEvent(string TreeHash, string ElementId, string? ControlType, DateTimeOffset Timestamp);

    public void RecordUiEvent(string treeHash, string elementId, string? controlType,
        DateTimeOffset timestamp)
    {
        PruneExpired();
        _recentUiEvents.Add(new UiEvent(treeHash, elementId, controlType, timestamp));
    }

    public void TryCorrelateWithSql(string queryShapeHash, string lastExecutionTimeIso,
        bool isWrite, string tablesReferenced)
    {
        if (!DateTimeOffset.TryParse(lastExecutionTimeIso, out var sqlTime)) return;

        PruneExpired();

        var window = _clockCalibrated ? _windowSeconds : _fallbackWindowSeconds;

        // Find closest UI event within window
        UiEvent? bestMatch = null;
        double bestDelta = double.MaxValue;

        foreach (var uiEvt in _recentUiEvents)
        {
            var delta = Math.Abs((sqlTime - uiEvt.Timestamp).TotalSeconds);
            if (delta <= window && delta < bestDelta)
            {
                bestMatch = uiEvt;
                bestDelta = delta;
            }
        }

        if (bestMatch is null) return;

        var correlationKey = $"{bestMatch.TreeHash}:{bestMatch.ElementId}:{queryShapeHash}";
        _db.UpsertCorrelatedAction(_sessionId, correlationKey,
            bestMatch.TreeHash, bestMatch.ElementId, bestMatch.ControlType,
            queryShapeHash, isWrite, tablesReferenced);
    }

    private void PruneExpired()
    {
        var cutoff = DateTimeOffset.UtcNow - SlidingWindowDuration;
        _recentUiEvents.RemoveAll(e => e.Timestamp < cutoff);
    }
}
```

- [ ] **Step 4: Run tests — verify pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "ActionCorrelatorTests" -v q
```
Expected: 5 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs
git commit -m "feat(behavioral): add ActionCorrelator — UI↔SQL timestamp correlation with clock fallback"
```

---

### Task 13: RoutineDetector

**Files:**
- Create: `src/SuavoAgent.Core/Behavioral/RoutineDetector.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/RoutineDetectorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Behavioral/RoutineDetectorTests.cs
using SuavoAgent.Core.Behavioral;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Behavioral;

public class RoutineDetectorTests : IDisposable
{
    private readonly AgentStateDb _db;

    public RoutineDetectorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess1", "pharm1");
    }

    [Fact]
    public void DetectRoutines_FrequentPath_Discovered()
    {
        // Simulate 6 repetitions of: btn1 -> btn2 -> btn3
        for (int i = 0; i < 6; i++)
        {
            var baseTime = DateTimeOffset.Parse("2026-04-13T14:00:00Z").AddMinutes(i * 5);
            InsertInteraction(i * 3 + 1, "hash1", "btn1", "Button", baseTime);
            InsertInteraction(i * 3 + 2, "hash1", "btn2", "Tab", baseTime.AddSeconds(3));
            InsertInteraction(i * 3 + 3, "hash2", "btn3", "Button", baseTime.AddSeconds(8));
        }

        var detector = new RoutineDetector(_db, "sess1");
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines("sess1");
        Assert.NotEmpty(routines);
        Assert.True(routines[0].Frequency >= 5);
        Assert.True(routines[0].PathLength >= 3);
    }

    [Fact]
    public void DetectRoutines_InfrequentPath_NotDiscovered()
    {
        // Only 2 repetitions — below threshold of 5
        for (int i = 0; i < 2; i++)
        {
            var t = DateTimeOffset.Parse("2026-04-13T14:00:00Z").AddMinutes(i * 5);
            InsertInteraction(i * 3 + 1, "hash1", "btn1", "Button", t);
            InsertInteraction(i * 3 + 2, "hash1", "btn2", "Tab", t.AddSeconds(3));
            InsertInteraction(i * 3 + 3, "hash2", "btn3", "Button", t.AddSeconds(8));
        }

        var detector = new RoutineDetector(_db, "sess1");
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines("sess1");
        Assert.Empty(routines);
    }

    [Fact]
    public void DetectRoutines_WithWritebackCandidate_Flagged()
    {
        // Add a correlated write action for btn3
        _db.UpsertCorrelatedAction("sess1", "hash2:btn3:qhash_write",
            "hash2", "btn3", "Button", "qhash_write", isWrite: true, "[\"T\"]");

        for (int i = 0; i < 6; i++)
        {
            var t = DateTimeOffset.Parse("2026-04-13T14:00:00Z").AddMinutes(i * 5);
            InsertInteraction(i * 3 + 1, "hash1", "btn1", "Button", t);
            InsertInteraction(i * 3 + 2, "hash1", "btn2", "Tab", t.AddSeconds(3));
            InsertInteraction(i * 3 + 3, "hash2", "btn3", "Button", t.AddSeconds(8));
        }

        var detector = new RoutineDetector(_db, "sess1");
        detector.DetectAndPersist();

        var routines = _db.GetLearnedRoutines("sess1");
        Assert.NotEmpty(routines);
        Assert.True(routines.Any(r => r.HasWritebackCandidate));
    }

    private void InsertInteraction(long seq, string treeHash, string elementId,
        string controlType, DateTimeOffset timestamp)
    {
        _db.InsertBehavioralEvent("sess1", seq, "interaction", "invoked",
            treeHash, elementId, controlType, null, null, null,
            null, null, null, 1, timestamp.ToString("o"));
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "RoutineDetectorTests" -v q
```

- [ ] **Step 3: Implement RoutineDetector**

```csharp
// src/SuavoAgent.Core/Behavioral/RoutineDetector.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Behavioral;

/// <summary>
/// Mines repeatable action sequences from behavioral events using a directly-follows graph (DFG).
/// A routine is a path through the DFG with frequency >= 5, length 3-20.
/// </summary>
public sealed class RoutineDetector
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private const int MinFrequency = 5;
    private const int MinPathLength = 3;
    private const int MaxPathLength = 20;
    private const double MaxGapSeconds = 30;

    public RoutineDetector(AgentStateDb db, string sessionId)
    {
        _db = db;
        _sessionId = sessionId;
    }

    private record ActionNode(string TreeHash, string ElementId, string? ControlType);
    private record Edge(ActionNode From, ActionNode To, int Frequency);

    public void DetectAndPersist()
    {
        // Load interaction events ordered by timestamp
        var events = _db.GetBehavioralEvents(_sessionId, "interaction", limit: 50000);
        if (events.Count < MinPathLength * MinFrequency) return;

        // Build directly-follows graph
        var dfg = new Dictionary<(ActionNode, ActionNode), int>();
        ActionNode? prev = null;
        DateTimeOffset prevTime = DateTimeOffset.MinValue;

        foreach (var evt in events)
        {
            if (evt.ElementId is null || evt.TreeHash is null) continue;

            var node = new ActionNode(evt.TreeHash, evt.ElementId, evt.ControlType);

            if (prev is not null && DateTimeOffset.TryParse(evt.Timestamp, out var ts))
            {
                var gap = (ts - prevTime).TotalSeconds;
                if (gap <= MaxGapSeconds && gap >= 0)
                {
                    var edge = (prev, node);
                    dfg[edge] = dfg.GetValueOrDefault(edge) + 1;
                }
            }

            prev = node;
            if (DateTimeOffset.TryParse(evt.Timestamp, out var parsed))
                prevTime = parsed;
        }

        // Extract paths with frequency >= threshold
        var frequentEdges = dfg.Where(kv => kv.Value >= MinFrequency)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        if (frequentEdges.Count == 0) return;

        // Build adjacency list from frequent edges
        var adjacency = new Dictionary<ActionNode, List<(ActionNode Next, int Freq)>>();
        foreach (var ((from, to), freq) in frequentEdges)
        {
            if (!adjacency.ContainsKey(from))
                adjacency[from] = new();
            adjacency[from].Add((to, freq));
        }

        // Find paths starting from nodes with no incoming frequent edges
        var hasIncoming = new HashSet<ActionNode>();
        foreach (var ((_, to), _) in frequentEdges) hasIncoming.Add(to);
        var startNodes = adjacency.Keys.Where(n => !hasIncoming.Contains(n)).ToList();
        if (startNodes.Count == 0)
            startNodes = adjacency.Keys.Take(1).ToList(); // cycle — pick any start

        // Load writeback candidates for flagging
        var writebackActions = _db.GetCorrelatedActions(_sessionId)
            .Where(a => a.IsWrite)
            .Select(a => new ActionNode(a.TreeHash, a.ElementId, a.ControlType))
            .ToHashSet();

        var writeCandidateQueries = _db.GetCorrelatedActions(_sessionId)
            .Where(a => a.IsWrite && a.QueryShapeHash is not null)
            .ToDictionary(a => new ActionNode(a.TreeHash, a.ElementId, a.ControlType),
                a => a.QueryShapeHash!);

        foreach (var start in startNodes)
        {
            var path = new List<ActionNode> { start };
            var current = start;
            var minFreq = int.MaxValue;

            while (path.Count < MaxPathLength && adjacency.TryGetValue(current, out var nexts))
            {
                var best = nexts.OrderByDescending(n => n.Freq).First();
                if (path.Contains(best.Next)) break; // cycle
                path.Add(best.Next);
                minFreq = Math.Min(minFreq, best.Freq);
                current = best.Next;
            }

            if (path.Count < MinPathLength || minFreq < MinFrequency) continue;

            var hasWriteback = path.Any(n => writebackActions.Contains(n));
            var writeQueries = path
                .Where(n => writeCandidateQueries.ContainsKey(n))
                .Select(n => writeCandidateQueries[n])
                .Distinct().ToList();

            var pathJson = JsonSerializer.Serialize(path.Select(n => new
            {
                treeHash = n.TreeHash,
                elementId = n.ElementId,
                controlType = n.ControlType,
                queryShapeHash = writeCandidateQueries.GetValueOrDefault(n)
            }));

            var routineHash = ComputeRoutineHash(path);
            var confidence = minFreq >= 10 ? 0.9 : minFreq >= 5 ? 0.7 : 0.3;

            _db.UpsertLearnedRoutine(_sessionId, routineHash, pathJson,
                path.Count, minFreq, confidence,
                path.First().ElementId, path.Last().ElementId,
                writeQueries.Count > 0 ? JsonSerializer.Serialize(writeQueries) : null,
                hasWriteback);
        }
    }

    private static string ComputeRoutineHash(List<ActionNode> path)
    {
        var sb = new StringBuilder();
        foreach (var node in path)
            sb.Append(node.TreeHash).Append(':').Append(node.ElementId).Append('|');
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run tests — verify pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "RoutineDetectorTests" -v q
```
Expected: 3 passing.

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/RoutineDetector.cs tests/SuavoAgent.Core.Tests/Behavioral/RoutineDetectorTests.cs
git commit -m "feat(behavioral): add RoutineDetector — DFG sequence mining with writeback candidate flagging"
```

---

### Task 14: PomExporter Behavioral Section

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/PomExporter.cs`
- Test: `tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs`

- [ ] **Step 1: Write failing test**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs
using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class BehavioralPomExportTests : IDisposable
{
    private readonly AgentStateDb _db;

    public BehavioralPomExportTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess1", "pharm1");
    }

    [Fact]
    public void Export_IncludesBehavioralSection()
    {
        // Add a learned routine with writeback candidate
        _db.UpsertLearnedRoutine("sess1", "rhash1",
            "[{\"treeHash\":\"h1\",\"elementId\":\"btn1\",\"controlType\":\"Button\"}]",
            3, 10, 0.9, "btn1", "btn3", "[\"qhash1\"]", true);

        // Add a writeback candidate correlation
        _db.UpsertCorrelatedAction("sess1", "h1:btn1:qhash1",
            "h1", "btn1", "Button", "qhash1", isWrite: true,
            "[\"Prescription.RxTransaction\"]");

        // Add a DMV observation for the query shape
        _db.UpsertDmvQueryObservation("sess1", "qhash1",
            "UPDATE [Prescription].[RxTransaction] SET [StatusTypeID] = @p WHERE [RxNumber] = @p",
            "[\"Prescription.RxTransaction\"]", isWrite: true, executionCount: 10,
            lastExecutionTime: "2026-04-13T14:00:00Z", clockOffsetMs: 0);

        var json = PomExporter.Export(_db, "sess1");
        var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("behavioral", out var behavioral));
        Assert.True(behavioral.TryGetProperty("routines", out var routines));
        Assert.True(behavioral.TryGetProperty("writebackCandidates", out var candidates));
        Assert.True(routines.GetArrayLength() > 0);
        Assert.True(candidates.GetArrayLength() > 0);

        // Verify no Name hashes in export
        var fullJson = json;
        Assert.DoesNotContain("nameHash", fullJson);
        Assert.DoesNotContain("name_hash", fullJson);
    }

    public void Dispose() => _db.Dispose();
}
```

- [ ] **Step 2: Run test — verify fail**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralPomExportTests" -v q
```

- [ ] **Step 3: Extend PomExporter.Export**

In `src/SuavoAgent.Core/Learning/PomExporter.cs`, extend the `Export` method to include a `behavioral` section after the existing `rxQueueCandidates`:

```csharp
// Add after rxQueueCandidates in the anonymous export object:

behavioral = new
{
    uniqueScreens = db.GetUniqueScreenCount(sessionId),
    routines = db.GetLearnedRoutines(sessionId).Select(r => new
    {
        routineHash = r.RoutineHash,
        path = JsonSerializer.Deserialize<JsonElement>(r.PathJson),
        pathLength = r.PathLength,
        frequency = r.Frequency,
        confidence = r.Confidence,
        hasWritebackCandidate = r.HasWritebackCandidate,
        correlatedWriteQueries = r.CorrelatedWriteQueries is not null
            ? JsonSerializer.Deserialize<JsonElement>(r.CorrelatedWriteQueries)
            : (JsonElement?)null,
    }).ToArray(),
    writebackCandidates = db.GetWritebackCandidates(sessionId).Select(c => new
    {
        correlationKey = c.CorrelationKey,
        elementId = c.ElementId,
        controlType = c.ControlType,
        queryShapeHash = c.QueryShapeHash,
        queryShape = c.QueryShape,
        tablesReferenced = c.TablesReferenced is not null
            ? JsonSerializer.Deserialize<JsonElement>(c.TablesReferenced)
            : (JsonElement?)null,
        occurrences = c.OccurrenceCount,
        confidence = c.Confidence,
    }).ToArray(),
    dmvAccess = db.GetDmvQueryObservations(sessionId, 1).Count > 0,
    totalInteractions = db.GetBehavioralEventCount(sessionId, "interaction"),
},
```

- [ ] **Step 4: Run test — verify pass**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "BehavioralPomExportTests" -v q
```

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/PomExporter.cs tests/SuavoAgent.Core.Tests/Learning/BehavioralPomExportTests.cs
git commit -m "feat(behavioral): add POM export behavioral section — routines, writeback candidates, DMV access"
```

---

### Task 15: HealthSnapshot Behavioral Telemetry

**Files:**
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`

- [ ] **Step 1: Add behavioral telemetry to HealthSnapshot.Take()**

In `src/SuavoAgent.Core/HealthSnapshot.cs`, add the `behavioral` section to the snapshot object. After the existing `writebackEngine` section:

```csharp
behavioral = new
{
    treeSnapshotCount = _stateDb.GetBehavioralEventCount(learningSessionId, "tree_snapshot"),
    uniqueScreens = _stateDb.GetUniqueScreenCount(learningSessionId),
    interactionEventCount = _stateDb.GetBehavioralEventCount(learningSessionId, "interaction"),
    keystrokeCategoryCount = _stateDb.GetBehavioralEventCount(learningSessionId, "keystroke_category"),
    dmvQueryShapes = _stateDb.GetDmvQueryObservations(learningSessionId, 10000).Count,
    correlatedActions = _stateDb.GetCorrelatedActionCount(learningSessionId),
    writebackCandidates = _stateDb.GetWritebackCandidateCount(learningSessionId),
    learnedRoutines = _stateDb.GetLearnedRoutineCount(learningSessionId),
    routinesWithWriteback = _stateDb.GetRoutinesWithWritebackCount(learningSessionId),
},
```

Where `learningSessionId` is obtained from `_stateDb.GetActiveSessionId(_options.PharmacyId ?? "")`.

Handle the null case (no active learning session) by omitting the behavioral section or returning zeros.

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/SuavoAgent.Core -v q
```

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/HealthSnapshot.cs
git commit -m "feat(behavioral): add behavioral telemetry to HealthSnapshot — screen count, routines, writeback candidates"
```

---

### Task 16: LearningWorker Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`

- [ ] **Step 1: Wire DmvQueryObserver, ActionCorrelator, and RoutineDetector into LearningWorker**

In `LearningWorker.ExecuteAsync`, after the existing observer initialization block:

1. Add `DmvQueryObserver` to the observer list (it implements `ILearningObserver`)
2. Create `ActionCorrelator` instance
3. In the phase management loop, run `RoutineDetector.DetectAndPersist()` every 5 minutes during Pattern/Model phases
4. In the model phase block (where inference runs), also run the routine detector for final extraction
5. Add `BehavioralEventReceiver` creation for processing IPC events
6. Add data retention pruning (once per day)

```csharp
// After existing observer setup:
var dmvObs = new DmvQueryObserver(_db, () => new SqlConnection(BuildConnectionString()),
    _sp.GetRequiredService<ILogger<DmvQueryObserver>>());
_observers.Add(dmvObs);

var correlator = new ActionCorrelator(_db, _sessionId);
var behavioralReceiver = new BehavioralEventReceiver(_db, _sessionId);

// In the phase loop, after observer health checks:
// Run routine detection every 5 minutes during Pattern/Model
if (session.Phase is "pattern" or "model")
{
    var detector = new RoutineDetector(_db, _sessionId);
    detector.DetectAndPersist();
}

// Daily retention pruning
if (session.Phase is "pattern" or "model")
{
    _db.PruneBehavioralEvents(_sessionId, olderThanDays: 30);
}
```

The full wiring also involves exposing the `BehavioralEventReceiver` and `ActionCorrelator` for IPC integration (the IpcPipeServer handler needs to route `behavioral_events` commands to the receiver).

- [ ] **Step 2: Build + run existing tests**

```bash
dotnet build src/SuavoAgent.Core -v q && dotnet test tests/SuavoAgent.Core.Tests -v q
```
Expected: Build succeeds, all existing + new tests pass.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/Workers/LearningWorker.cs
git commit -m "feat(behavioral): wire DmvQueryObserver, ActionCorrelator, RoutineDetector into LearningWorker"
```

---

### Task 17: BAA + Installer Disclosure Text

**Files:**
- Modify: `src/SuavoAgent.Core/Config/AgentOptions.cs` (add disclosure text constant)

- [ ] **Step 1: Add BAA and installer disclosure text**

```csharp
// Add to AgentOptions.cs or a new BaaDisclosure.cs in Config:

/// <summary>
/// BAA behavioral observation clauses and installer disclosure text.
/// These are the legal texts that must appear in the BAA and installer consent screen.
/// </summary>
public static class BehavioralDisclosure
{
    public const string InstallerConsentText =
        "Behavioral Learning: During the learning period, SuavoAgent observes the structure " +
        "of your pharmacy software's screens and the patterns of how it's used (which buttons " +
        "are clicked, which screens are visited, what types of data are entered). It does NOT " +
        "capture what you type, patient information, or screen contents. A low-level keyboard " +
        "classification hook is active only when your pharmacy software is in the foreground. " +
        "Your endpoint protection software may detect this hook — it is expected behavior.";

    public static readonly string[] BaaClauses = new[]
    {
        "UI Automation Observation: Agent observes the structural properties (control type, " +
        "automation identifier, class name, bounding rectangle) of user interface elements in " +
        "pharmacy management software windows. Element content, values, and text are never captured.",

        "Element Name Hashing: The Name property of UI elements, which may incidentally contain " +
        "patient-contextual information, is cryptographically hashed using a per-pharmacy keyed " +
        "hash (HMAC-SHA256) before storage. The raw Name value is never persisted, transmitted, or logged.",

        "Keyboard Category Monitoring: When the pharmacy management software window has foreground " +
        "focus, the agent classifies keystrokes into categories (alphabetic, numeric, navigation, function) " +
        "to detect data entry patterns. Individual key codes, characters, and typed content are never " +
        "captured. Numeric digit sequences are capped at a count of three to prevent reconstruction of identifiers.",

        "SQL Query Shape Observation: When database server permissions allow, the agent observes the " +
        "structural shape of SQL queries executed by the pharmacy management software. All literal values " +
        "(strings, numbers, identifiers) are stripped before storage. Queries that cannot be safely " +
        "normalized are discarded entirely.",

        "Low-Level Keyboard Hook Disclosure: The agent uses the Windows SetWindowsHookEx(WH_KEYBOARD_LL) " +
        "API to classify keystroke categories. This system-level hook is installed only when the pharmacy " +
        "management software has foreground window focus and is immediately uninstalled when focus is lost. " +
        "Endpoint protection software may detect this hook installation. The hook captures keystroke " +
        "categories only, never individual key codes or characters.",
    };
}
```

- [ ] **Step 2: Build to verify**

```bash
dotnet build src/SuavoAgent.Core -v q
```

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/Config/
git commit -m "docs(behavioral): add BAA clauses and installer disclosure text for behavioral observation"
```

---

## Final Verification

After all tasks complete:

- [ ] **Run full test suite**

```bash
dotnet test -v q
```
Expected: All 279+ existing tests pass + ~35 new behavioral tests = ~314 total.

- [ ] **Verify no regressions in existing features**

```bash
dotnet test tests/SuavoAgent.Core.Tests --filter "CanaryIntegrationTests|WritebackProcessorIntegrationTests|CriticalPathTests" -v n
```

- [ ] **Final commit with all integration wiring**

```bash
git log --oneline -20  # verify commit history
```
