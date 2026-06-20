# Presence Cursor (Phase 1) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Evolve the SuavoAgent IntentCursor flash-overlay into a *persistent* "presence cursor" that glides target→target, lands a pre-click reticle, pulses on click, and **never disappears** — plus a preferences system with instant local hide.

**Architecture:** New `SuavoAgent.Helper/Presence/` subsystem mirroring the IntentCursor + ActuationConfig patterns. Pure logic (preferences load/clamp, motion planning, visibility gating) is unit-tested via xUnit + hand-written fakes; the Win32 persistent overlay is a thin `IPresenceRenderer` shell (build + on-box verified). The agent's actuation path (`SendInputDriver`) drives the cursor through `PresenceController` before/after each click. **Cosmetic never gates actuation** — a hidden cursor means the agent still acts.

**Tech Stack:** C# / .NET 8, `net8.0-windows` (Helper), Win32 GDI layered window (`UpdateLayeredWindow`), Serilog, xUnit 2.9.2 (built-in `Assert.*`, no FluentAssertions, no mocking lib — hand-written fakes).

## Global Constraints

- **Invariant:** visuals NEVER block, slow, or fail actuation. Every presence call is fire-and-forget / try-caught; a render failure is logged and swallowed. (verbatim from spec §1, §5.6)
- **Performance (2-core box):** idle = ZERO repaint. The persistent overlay animates only during a glide/pulse, then goes static and blocks. Spring/lerp interpolation only — no splines. (spec §5.5)
- **Renderer path:** GDI-persistent-with-idle-no-repaint for Phase 1; DirectComposition migration is Phase 1.5 (out of scope here). (spec §7)
- **HIPAA / PHI:** the cursor/reticle/pulse render NO page or screen content — only the agent's own chrome. No element text/labels in Phase 1. (spec §2.4)
- **Defaults:** `Enabled=true`, `CursorVisible=true`, `SuppressWhenSessionDisconnected=true`. (spec §5.6, §7)
- **Config location:** `%PROGRAMDATA%\SuavoAgent\presence.json` (mirror `actuation.json`).
- **Brand DNA:** acting tone = gold `#C8A96A`; spring/ease-in-out-cubic motion. (spec §1)
- **Namespaces:** `SuavoAgent.Helper.Presence` for all new Helper code; tests in `tests/SuavoAgent.Helper.Tests/Presence/`.

---

## File Structure

**Create:**
- `src/SuavoAgent.Helper/Presence/PresencePreferences.cs` — options record + `PresenceTones` constants + `SafeDefault()` + `FromJson()` (pure parse/clamp).
- `src/SuavoAgent.Helper/Presence/PresenceBootstrap.cs` — load `presence.json` from ProgramData → `PresencePreferences`.
- `src/SuavoAgent.Helper/Presence/PresencePreferenceStore.cs` — thread-safe current prefs + `SetVisible` local override + `Changed` event.
- `src/SuavoAgent.Helper/Presence/PresenceMotion.cs` — pure glide planner (`PlanGlide`).
- `src/SuavoAgent.Helper/Presence/IPresenceRenderer.cs` — persistent-renderer interface.
- `src/SuavoAgent.Helper/Presence/PresenceController.cs` — the brain (MoveTo/Reticle/Click/Park, gating).
- `src/SuavoAgent.Helper/Presence/WindowsPresenceRenderer.cs` — Win32 persistent overlay (GDI, idle-no-repaint).
- `src/SuavoAgent.Helper/Presence/PresenceHotkeyListener.cs` — Win32 `RegisterHotKey` thread → `store.SetVisible(toggle)`.
- `tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs`
- `tests/SuavoAgent.Helper.Tests/Presence/PresencePreferenceStoreTests.cs`
- `tests/SuavoAgent.Helper.Tests/Presence/PresenceMotionTests.cs`
- `tests/SuavoAgent.Helper.Tests/Presence/PresenceControllerTests.cs`

**Modify:**
- `src/SuavoAgent.Helper/Actuation/SendInputDriver.cs` — inject `PresenceController`; call `MoveTo`+`Reticle` before, `Click` after, at the existing `TryGlow` site (~lines 87-108).
- `src/SuavoAgent.Helper/Program.cs` — build prefs/store/renderer/controller/hotkey; pass controller to `SendInputDriver`.
- `src/SuavoAgent.Helper/IpcCommandServer.cs` — add a `presence.set_visible` verb (optional remote hide), mirroring existing actuation verbs.

---

## Task 1: PresencePreferences record + tones + SafeDefault

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/PresencePreferences.cs`
- Test: `tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs`

**Interfaces:**
- Produces: `record PresencePreferences { bool Enabled; bool CursorVisible; bool BubbleVisible; bool GlowVisible; bool ObserveVisualsVisible; string Tone; int CursorSizePx; int GlideSpeedPxPerSec; string Easing; double GlowIntensity; string BubbleVerbosity; bool AutoObserveOnTakeover; int TargetMonitor; bool MirrorToDashboard; bool SuppressWhenSessionDisconnected; static PresencePreferences SafeDefault(); bool IsCursorActive => Enabled && CursorVisible; }`; `static class PresenceTones { const string Acting; const string Observing; const string Confirm; }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresencePreferencesTests
{
    [Fact]
    public void SafeDefault_IsVisibleAndSessionGated()
    {
        var p = PresencePreferences.SafeDefault();

        Assert.True(p.Enabled);
        Assert.True(p.CursorVisible);
        Assert.True(p.SuppressWhenSessionDisconnected);
        Assert.True(p.IsCursorActive);
        Assert.Equal(PresenceTones.Acting, p.Tone);
        Assert.Equal("labels", p.BubbleVerbosity);
        Assert.True(p.GlideSpeedPxPerSec > 0);
    }

    [Fact]
    public void IsCursorActive_FalseWhenDisabledOrHidden()
    {
        var p = PresencePreferences.SafeDefault();
        Assert.False((p with { Enabled = false }).IsCursorActive);
        Assert.False((p with { CursorVisible = false }).IsCursorActive);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferencesTests"`
Expected: FAIL — `PresencePreferences` / `PresenceTones` do not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Helper/Presence/PresencePreferences.cs
namespace SuavoAgent.Helper.Presence;

/// <summary>Tone keys for the presence cursor. Brand DNA: gold = acting,
/// sage = observing/learning, wine = awaiting confirmation.</summary>
public static class PresenceTones
{
    public const string Acting = "acting";       // gold #C8A96A
    public const string Observing = "observing";  // sage
    public const string Confirm = "confirm";      // wine-red danger
}

/// <summary>Presence-layer preferences. Source of truth is the dashboard
/// (synced over heartbeat, Phase 5); a local presence.json + SafeDefault keep
/// it working offline. Visuals are cosmetic and never gate actuation.</summary>
public sealed record PresencePreferences
{
    public bool Enabled { get; init; } = true;
    public bool CursorVisible { get; init; } = true;
    public bool BubbleVisible { get; init; } = true;
    public bool GlowVisible { get; init; } = true;
    public bool ObserveVisualsVisible { get; init; } = true;
    public string Tone { get; init; } = PresenceTones.Acting;
    public int CursorSizePx { get; init; } = 34;
    public int GlideSpeedPxPerSec { get; init; } = 1600;
    public string Easing { get; init; } = "ease-in-out-cubic";
    public double GlowIntensity { get; init; } = 0.6;
    public string BubbleVerbosity { get; init; } = "labels"; // off | labels | labels+llm
    public bool AutoObserveOnTakeover { get; init; } = true;
    public int TargetMonitor { get; init; } = 0; // 0 = primary
    public bool MirrorToDashboard { get; init; } = true;
    public bool SuppressWhenSessionDisconnected { get; init; } = true;

    /// <summary>Cursor renders only when enabled AND visible. Either false = silent agent.</summary>
    public bool IsCursorActive => Enabled && CursorVisible;

    public static PresencePreferences SafeDefault() => new();
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferencesTests"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/PresencePreferences.cs tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs
git commit -m "feat(presence): PresencePreferences record + tones + safe defaults"
```

---

## Task 2: FromJson parse/clamp + PresenceBootstrap loader

**Files:**
- Modify: `src/SuavoAgent.Helper/Presence/PresencePreferences.cs` (add `FromJson`)
- Create: `src/SuavoAgent.Helper/Presence/PresenceBootstrap.cs`
- Test: `tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs` (add cases)

**Interfaces:**
- Consumes: `PresencePreferences.SafeDefault()` (Task 1).
- Produces: `static PresencePreferences PresencePreferences.FromJson(string? raw, ILogger logger)`; `static class PresenceBootstrap { const string ConfigFileName = "presence.json"; static PresencePreferences LoadConfig(ILogger logger); }`

- [ ] **Step 1: Write the failing test**

```csharp
// add to PresencePreferencesTests.cs
using Serilog;

// ... inside the class:
private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

[Fact]
public void FromJson_NullOrEmpty_ReturnsSafeDefault()
{
    Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson(null, Log));
    Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson("", Log));
}

[Fact]
public void FromJson_BadJson_ReturnsSafeDefault()
{
    Assert.Equal(PresencePreferences.SafeDefault(), PresencePreferences.FromJson("{not json", Log));
}

[Fact]
public void FromJson_OverridesAndClamps()
{
    var p = PresencePreferences.FromJson(
        "{\"cursorVisible\":false,\"glideSpeedPxPerSec\":999999,\"cursorSizePx\":2,\"glowIntensity\":5.0}", Log);

    Assert.False(p.CursorVisible);
    Assert.Equal(8000, p.GlideSpeedPxPerSec); // clamped to max
    Assert.Equal(8, p.CursorSizePx);          // clamped to min
    Assert.Equal(1.0, p.GlowIntensity);       // clamped to [0,1]
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferencesTests"`
Expected: FAIL — `FromJson` not defined.

- [ ] **Step 3: Write minimal implementation**

Add to `PresencePreferences.cs` (top: `using System; using System.Text.Json; using System.Text.Json.Serialization; using Serilog;`):

```csharp
    private sealed record Json(
        [property: JsonPropertyName("enabled")] bool? Enabled,
        [property: JsonPropertyName("cursorVisible")] bool? CursorVisible,
        [property: JsonPropertyName("bubbleVisible")] bool? BubbleVisible,
        [property: JsonPropertyName("glowVisible")] bool? GlowVisible,
        [property: JsonPropertyName("observeVisualsVisible")] bool? ObserveVisualsVisible,
        [property: JsonPropertyName("tone")] string? Tone,
        [property: JsonPropertyName("cursorSizePx")] int? CursorSizePx,
        [property: JsonPropertyName("glideSpeedPxPerSec")] int? GlideSpeedPxPerSec,
        [property: JsonPropertyName("easing")] string? Easing,
        [property: JsonPropertyName("glowIntensity")] double? GlowIntensity,
        [property: JsonPropertyName("bubbleVerbosity")] string? BubbleVerbosity,
        [property: JsonPropertyName("autoObserveOnTakeover")] bool? AutoObserveOnTakeover,
        [property: JsonPropertyName("targetMonitor")] int? TargetMonitor,
        [property: JsonPropertyName("mirrorToDashboard")] bool? MirrorToDashboard,
        [property: JsonPropertyName("suppressWhenSessionDisconnected")] bool? SuppressWhenSessionDisconnected);

    /// <summary>Parse + clamp operator JSON. Any failure → SafeDefault (never throws).</summary>
    public static PresencePreferences FromJson(string? raw, ILogger logger)
    {
        if (string.IsNullOrWhiteSpace(raw)) return SafeDefault();
        var safe = SafeDefault();
        try
        {
            var j = JsonSerializer.Deserialize<Json>(raw,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (j is null) return safe;
            return safe with
            {
                Enabled = j.Enabled ?? safe.Enabled,
                CursorVisible = j.CursorVisible ?? safe.CursorVisible,
                BubbleVisible = j.BubbleVisible ?? safe.BubbleVisible,
                GlowVisible = j.GlowVisible ?? safe.GlowVisible,
                ObserveVisualsVisible = j.ObserveVisualsVisible ?? safe.ObserveVisualsVisible,
                Tone = j.Tone ?? safe.Tone,
                CursorSizePx = j.CursorSizePx is { } cs ? Math.Clamp(cs, 8, 200) : safe.CursorSizePx,
                GlideSpeedPxPerSec = j.GlideSpeedPxPerSec is { } gs ? Math.Clamp(gs, 200, 8000) : safe.GlideSpeedPxPerSec,
                Easing = j.Easing ?? safe.Easing,
                GlowIntensity = j.GlowIntensity is { } gi ? Math.Clamp(gi, 0.0, 1.0) : safe.GlowIntensity,
                BubbleVerbosity = j.BubbleVerbosity ?? safe.BubbleVerbosity,
                AutoObserveOnTakeover = j.AutoObserveOnTakeover ?? safe.AutoObserveOnTakeover,
                TargetMonitor = j.TargetMonitor is { } tm ? Math.Clamp(tm, 0, 16) : safe.TargetMonitor,
                MirrorToDashboard = j.MirrorToDashboard ?? safe.MirrorToDashboard,
                SuppressWhenSessionDisconnected = j.SuppressWhenSessionDisconnected ?? safe.SuppressWhenSessionDisconnected,
            };
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Presence: failed to parse preferences JSON, using safe default");
            return safe;
        }
    }
```

Create `src/SuavoAgent.Helper/Presence/PresenceBootstrap.cs`:

```csharp
using System;
using System.IO;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Loads presence.json from %PROGRAMDATA%\SuavoAgent. Mirrors ActuationBootstrap.</summary>
public static class PresenceBootstrap
{
    public const string ConfigFileName = "presence.json";

    public static PresencePreferences LoadConfig(ILogger logger)
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent",
            ConfigFileName);

        if (!File.Exists(path))
        {
            logger.Information("Presence: no config at {Path}, using safe default (visible)", path);
            return PresencePreferences.SafeDefault();
        }

        try
        {
            return PresencePreferences.FromJson(File.ReadAllText(path), logger);
        }
        catch (Exception ex)
        {
            logger.Warning(ex, "Presence: failed to read {Path}, using safe default", path);
            return PresencePreferences.SafeDefault();
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferencesTests"`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/PresencePreferences.cs src/SuavoAgent.Helper/Presence/PresenceBootstrap.cs tests/SuavoAgent.Helper.Tests/Presence/PresencePreferencesTests.cs
git commit -m "feat(presence): presence.json parse/clamp + bootstrap loader"
```

---

## Task 3: PresencePreferenceStore (thread-safe current + hide override + Changed event)

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/PresencePreferenceStore.cs`
- Test: `tests/SuavoAgent.Helper.Tests/Presence/PresencePreferenceStoreTests.cs`

**Interfaces:**
- Consumes: `PresencePreferences` (Task 1).
- Produces: `class PresencePreferenceStore { PresencePreferenceStore(PresencePreferences initial); PresencePreferences Current { get; }; void SetVisible(bool visible); void Replace(PresencePreferences prefs); event Action<PresencePreferences>? Changed; }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Helper.Tests/Presence/PresencePreferenceStoreTests.cs
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresencePreferenceStoreTests
{
    [Fact]
    public void SetVisible_TogglesCursorVisible_AndRaisesChanged()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        PresencePreferences? last = null;
        store.Changed += p => last = p;

        store.SetVisible(false);

        Assert.False(store.Current.CursorVisible);
        Assert.NotNull(last);
        Assert.False(last!.CursorVisible);
    }

    [Fact]
    public void Replace_SwapsAllAndRaisesChanged()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var raised = 0;
        store.Changed += _ => raised++;

        store.Replace(PresencePreferences.SafeDefault() with { GlowIntensity = 0.2 });

        Assert.Equal(0.2, store.Current.GlowIntensity);
        Assert.Equal(1, raised);
    }

    [Fact]
    public void SetVisible_NoChange_DoesNotRaise()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault()); // visible by default
        var raised = 0;
        store.Changed += _ => raised++;

        store.SetVisible(true); // already visible

        Assert.Equal(0, raised);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferenceStoreTests"`
Expected: FAIL — `PresencePreferenceStore` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Helper/Presence/PresencePreferenceStore.cs
using System;

namespace SuavoAgent.Helper.Presence;

/// <summary>Holds the live presence preferences. Thread-safe. SetVisible is the
/// instant local-hide override (hotkey/tray/IPC); Replace applies a full pref set
/// (e.g. cloud sync, Phase 5). Raises Changed only on an actual change.</summary>
public sealed class PresencePreferenceStore
{
    private readonly object _lock = new();
    private PresencePreferences _current;

    public PresencePreferenceStore(PresencePreferences initial)
        => _current = initial ?? PresencePreferences.SafeDefault();

    public PresencePreferences Current
    {
        get { lock (_lock) return _current; }
    }

    public event Action<PresencePreferences>? Changed;

    public void SetVisible(bool visible)
    {
        PresencePreferences next;
        lock (_lock)
        {
            if (_current.CursorVisible == visible) return;
            _current = _current with { CursorVisible = visible };
            next = _current;
        }
        Changed?.Invoke(next);
    }

    public void Replace(PresencePreferences prefs)
    {
        if (prefs is null) return;
        lock (_lock) { _current = prefs; }
        Changed?.Invoke(prefs);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresencePreferenceStoreTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/PresencePreferenceStore.cs tests/SuavoAgent.Helper.Tests/Presence/PresencePreferenceStoreTests.cs
git commit -m "feat(presence): thread-safe preference store with hide override + Changed event"
```

---

## Task 4: PresenceMotion.PlanGlide (pure planner)

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/PresenceMotion.cs`
- Test: `tests/SuavoAgent.Helper.Tests/Presence/PresenceMotionTests.cs`

**Interfaces:**
- Consumes: `PresencePreferences` (Task 1).
- Produces: `static class PresenceMotion { static (int durationMs, string easing) PlanGlide(int fromX, int fromY, int toX, int toY, PresencePreferences prefs); const int MinGlideMs = 120; const int MaxGlideMs = 900; }`

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Helper.Tests/Presence/PresenceMotionTests.cs
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceMotionTests
{
    [Fact]
    public void PlanGlide_ZeroDistance_ClampsToMin()
    {
        var (dur, _) = PresenceMotion.PlanGlide(100, 100, 100, 100, PresencePreferences.SafeDefault());
        Assert.Equal(PresenceMotion.MinGlideMs, dur);
    }

    [Fact]
    public void PlanGlide_HugeDistance_ClampsToMax()
    {
        var (dur, _) = PresenceMotion.PlanGlide(0, 0, 100000, 0, PresencePreferences.SafeDefault());
        Assert.Equal(PresenceMotion.MaxGlideMs, dur);
    }

    [Fact]
    public void PlanGlide_ScalesWithDistanceAndSpeed()
    {
        // 1600 px at 1600 px/s ≈ 1000ms → clamped to MaxGlideMs; use a mid distance.
        var prefs = PresencePreferences.SafeDefault(); // 1600 px/s
        var (dur, easing) = PresenceMotion.PlanGlide(0, 0, 800, 0, prefs); // 800px/1600 = 500ms
        Assert.Equal(500, dur);
        Assert.Equal(prefs.Easing, easing);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresenceMotionTests"`
Expected: FAIL — `PresenceMotion` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Helper/Presence/PresenceMotion.cs
using System;

namespace SuavoAgent.Helper.Presence;

/// <summary>Pure glide planning: duration scales with travel distance and the
/// configured glide speed, clamped so a tiny hop still reads and a long sweep
/// never drags. Spring/lerp only — no spline pathing (research-verified).</summary>
public static class PresenceMotion
{
    public const int MinGlideMs = 120;
    public const int MaxGlideMs = 900;

    public static (int durationMs, string easing) PlanGlide(
        int fromX, int fromY, int toX, int toY, PresencePreferences prefs)
    {
        var dx = toX - fromX;
        var dy = toY - fromY;
        var distance = Math.Sqrt((double)dx * dx + (double)dy * dy);
        var speed = Math.Max(1, prefs.GlideSpeedPxPerSec);
        var ms = (int)Math.Round(distance / speed * 1000.0);
        return (Math.Clamp(ms, MinGlideMs, MaxGlideMs), prefs.Easing);
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresenceMotionTests"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/PresenceMotion.cs tests/SuavoAgent.Helper.Tests/Presence/PresenceMotionTests.cs
git commit -m "feat(presence): pure glide-duration planner (distance × speed, clamped)"
```

---

## Task 5: IPresenceRenderer + PresenceController (gating brain)

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/IPresenceRenderer.cs`
- Create: `src/SuavoAgent.Helper/Presence/PresenceController.cs`
- Test: `tests/SuavoAgent.Helper.Tests/Presence/PresenceControllerTests.cs`

**Interfaces:**
- Consumes: `PresencePreferenceStore` (Task 3), `PresenceMotion.PlanGlide` (Task 4), `PresencePreferences.IsCursorActive` (Task 1).
- Produces:
  - `interface IPresenceRenderer { void Glide(int fromX, int fromY, int toX, int toY, int durationMs, string easing, string tone, int diameterPx); void Reticle(int x, int y, int diameterPx, string tone); void ClickPulse(int x, int y, string tone); void Hide(); void Show(); }`
  - `class PresenceController { PresenceController(IPresenceRenderer renderer, PresencePreferenceStore store, ILogger logger, Func<bool>? isSessionInteractive = null); void MoveTo(int x, int y); void Reticle(int x, int y); void Click(int x, int y); void Park(); }`

**Behavior:** `MoveTo` plans a glide from the last rest point to (x,y) and calls `renderer.Glide`, updating the last point. `Reticle`/`Click` no-op (return normally) when `!store.Current.IsCursorActive` or session non-interactive — proving cosmetic-never-gates. Subscribes to `store.Changed` → `Hide()`/`Show()`. All renderer calls are try-caught.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/SuavoAgent.Helper.Tests/Presence/PresenceControllerTests.cs
using System.Collections.Generic;
using Serilog;
using SuavoAgent.Helper.Presence;
using Xunit;

namespace SuavoAgent.Helper.Tests.Presence;

public class PresenceControllerTests
{
    private static readonly ILogger Log = new LoggerConfiguration().CreateLogger();

    private sealed class FakeRenderer : IPresenceRenderer
    {
        public List<string> Calls { get; } = new();
        public void Glide(int fx, int fy, int tx, int ty, int dur, string e, string tone, int dia)
            => Calls.Add($"glide:{fx},{fy}->{tx},{ty}");
        public void Reticle(int x, int y, int dia, string tone) => Calls.Add($"reticle:{x},{y}");
        public void ClickPulse(int x, int y, string tone) => Calls.Add($"click:{x},{y}");
        public void Hide() => Calls.Add("hide");
        public void Show() => Calls.Add("show");
    }

    [Fact]
    public void MoveTo_WhenActive_GlidesFromLastRestPoint()
    {
        var r = new FakeRenderer();
        var c = new PresenceController(r, new PresencePreferenceStore(PresencePreferences.SafeDefault()), Log);

        c.MoveTo(100, 100); // first move: place, no glide
        c.MoveTo(300, 100); // glide 100,100 -> 300,100

        Assert.Contains("glide:100,100->300,100", r.Calls);
    }

    [Fact]
    public void Reticle_And_Click_NoOp_WhenCursorHidden_ButDoNotThrow()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var r = new FakeRenderer();
        var c = new PresenceController(r, store, Log);
        store.SetVisible(false); // hide

        c.MoveTo(10, 10);
        c.Reticle(10, 10);
        c.Click(10, 10);

        Assert.DoesNotContain(r.Calls, s => s.StartsWith("reticle"));
        Assert.DoesNotContain(r.Calls, s => s.StartsWith("click"));
        Assert.DoesNotContain(r.Calls, s => s.StartsWith("glide"));
    }

    [Fact]
    public void StoreHide_TriggersRendererHide()
    {
        var store = new PresencePreferenceStore(PresencePreferences.SafeDefault());
        var r = new FakeRenderer();
        _ = new PresenceController(r, store, Log);

        store.SetVisible(false);

        Assert.Contains("hide", r.Calls);
    }

    [Fact]
    public void Reticle_NoOp_WhenSessionNotInteractive()
    {
        var r = new FakeRenderer();
        var c = new PresenceController(r, new PresencePreferenceStore(PresencePreferences.SafeDefault()),
            Log, isSessionInteractive: () => false);

        c.Reticle(5, 5);

        Assert.Empty(r.Calls);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresenceControllerTests"`
Expected: FAIL — `IPresenceRenderer` / `PresenceController` not defined.

- [ ] **Step 3: Write minimal implementation**

```csharp
// src/SuavoAgent.Helper/Presence/IPresenceRenderer.cs
namespace SuavoAgent.Helper.Presence;

/// <summary>Persistent presence overlay. The Windows impl owns ONE long-lived
/// layered window and animates only on demand (idle = no repaint). Tests use a fake.</summary>
public interface IPresenceRenderer
{
    void Glide(int fromX, int fromY, int toX, int toY, int durationMs, string easing, string tone, int diameterPx);
    void Reticle(int x, int y, int diameterPx, string tone);
    void ClickPulse(int x, int y, string tone);
    void Hide();
    void Show();
}
```

```csharp
// src/SuavoAgent.Helper/Presence/PresenceController.cs
using System;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>The presence brain. Decides what to render based on preferences and
/// drives the renderer. NEVER throws into the caller — every render call is
/// try-caught — so actuation is never blocked or failed by a visual.</summary>
public sealed class PresenceController
{
    private readonly IPresenceRenderer _renderer;
    private readonly PresencePreferenceStore _store;
    private readonly ILogger _logger;
    private readonly Func<bool> _isSessionInteractive;
    private readonly object _lock = new();
    private bool _placed;
    private int _lastX, _lastY;

    public PresenceController(
        IPresenceRenderer renderer,
        PresencePreferenceStore store,
        ILogger logger,
        Func<bool>? isSessionInteractive = null)
    {
        _renderer = renderer;
        _store = store;
        _logger = logger.ForContext<PresenceController>();
        _isSessionInteractive = isSessionInteractive ?? (() => true);
        _store.Changed += OnPrefsChanged;
    }

    private bool Active
    {
        get
        {
            var p = _store.Current;
            if (!p.IsCursorActive) return false;
            if (p.SuppressWhenSessionDisconnected && !_isSessionInteractive()) return false;
            return true;
        }
    }

    public void MoveTo(int x, int y)
    {
        if (!Active) return;
        try
        {
            var prefs = _store.Current;
            lock (_lock)
            {
                if (!_placed)
                {
                    _placed = true;
                    _lastX = x; _lastY = y;
                    _renderer.Reticle(x, y, prefs.CursorSizePx, prefs.Tone); // first appearance, no glide
                    return;
                }
                var (dur, easing) = PresenceMotion.PlanGlide(_lastX, _lastY, x, y, prefs);
                _renderer.Glide(_lastX, _lastY, x, y, dur, easing, prefs.Tone, prefs.CursorSizePx);
                _lastX = x; _lastY = y;
            }
        }
        catch (Exception ex) { _logger.Debug(ex, "presence MoveTo failed (non-fatal)"); }
    }

    public void Reticle(int x, int y)
    {
        if (!Active) return;
        try
        {
            var prefs = _store.Current;
            _renderer.Reticle(x, y, prefs.CursorSizePx, prefs.Tone);
        }
        catch (Exception ex) { _logger.Debug(ex, "presence Reticle failed (non-fatal)"); }
    }

    public void Click(int x, int y)
    {
        if (!Active) return;
        try { _renderer.ClickPulse(x, y, _store.Current.Tone); }
        catch (Exception ex) { _logger.Debug(ex, "presence Click failed (non-fatal)"); }
    }

    /// <summary>Cursor stays where it is (persistent). Reset glide origin only.</summary>
    public void Park() { /* persistent overlay: nothing to tear down */ }

    private void OnPrefsChanged(PresencePreferences prefs)
    {
        try
        {
            if (prefs.IsCursorActive) _renderer.Show();
            else _renderer.Hide();
        }
        catch (Exception ex) { _logger.Debug(ex, "presence visibility toggle failed (non-fatal)"); }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj --filter "FullyQualifiedName~PresenceControllerTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/IPresenceRenderer.cs src/SuavoAgent.Helper/Presence/PresenceController.cs tests/SuavoAgent.Helper.Tests/Presence/PresenceControllerTests.cs
git commit -m "feat(presence): controller — gated glide/reticle/click, cosmetic never gates actuation"
```

---

## Task 6: WindowsPresenceRenderer (Win32 persistent overlay, idle-no-repaint)

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/WindowsPresenceRenderer.cs`

**Interfaces:**
- Consumes: `IPresenceRenderer` (Task 5), `PresenceTones` (Task 1).
- Produces: `class WindowsPresenceRenderer : IPresenceRenderer, IDisposable` with `void Start()`.

**Design:** ONE STA background thread owns a layered, topmost, click-through, no-activate window for the session. Commands arrive via a `BlockingCollection<Action>`; the thread `Take()`s (blocks → zero CPU at rest), runs the command (a glide animation loop repaints ~60fps via `UpdateLayeredWindow`, then stops). Reticle/click-pulse are short animations. Adapt the proven painting/P-Invoke from `WindowsIntentCursorRenderer.cs`. Not unit-tested (Win32) — verified by build + the Task 8 box demo.

- [ ] **Step 1: Implement the renderer**

```csharp
// src/SuavoAgent.Helper/Presence/WindowsPresenceRenderer.cs
using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Persistent click-through GDI overlay for the presence cursor. One
/// session-long layered window; commands run on a dedicated STA thread that
/// blocks when idle (zero repaint at rest). Phase 1.5 migrates to DirectComposition.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPresenceRenderer : IPresenceRenderer, IDisposable
{
    private readonly ILogger _logger;
    private readonly BlockingCollection<Action> _commands = new();
    private Thread? _thread;
    private IntPtr _hwnd = IntPtr.Zero;
    private int _curX, _curY;
    private bool _visible = true;
    private const int WindowPad = 24;
    private const int MaxDiameter = 200;

    public WindowsPresenceRenderer(ILogger logger) => _logger = logger.ForContext<WindowsPresenceRenderer>();

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Loop()
    {
        try
        {
            CreateOverlayWindow();
            foreach (var cmd in _commands.GetConsumingEnumerable()) // blocks at rest = no CPU
            {
                try { cmd(); } catch (Exception ex) { _logger.Debug(ex, "presence cmd failed"); }
            }
        }
        catch (Exception ex) { _logger.Warning(ex, "presence renderer loop ended"); }
        finally { if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd); }
    }

    private void Enqueue(Action a) { if (!_commands.IsAddingCompleted) _commands.Add(a); }

    public void Glide(int fromX, int fromY, int toX, int toY, int durationMs, string easing, string tone, int diameterPx)
        => Enqueue(() =>
        {
            if (!_visible) return;
            var startedAt = Environment.TickCount64;
            while (true)
            {
                var elapsed = (int)(Environment.TickCount64 - startedAt);
                var progress = durationMs <= 0 ? 1.0 : Math.Clamp(elapsed / (double)durationMs, 0, 1);
                var eased = Ease(easing, progress);
                var x = (int)Math.Round(fromX + (toX - fromX) * eased);
                var y = (int)Math.Round(fromY + (toY - fromY) * eased);
                Paint(x, y, diameterPx, tone, ringOnly: false, haloScale: 1.0);
                if (progress >= 1.0) break;
                Thread.Sleep(16);
            }
            _curX = toX; _curY = toY;
        });

    public void Reticle(int x, int y, int diameterPx, string tone)
        => Enqueue(() =>
        {
            if (!_visible) return;
            _curX = x; _curY = y;
            Paint(x, y, diameterPx, tone, ringOnly: false, haloScale: 1.0);
        });

    public void ClickPulse(int x, int y, string tone)
        => Enqueue(() =>
        {
            if (!_visible) return;
            var startedAt = Environment.TickCount64;
            const int pulseMs = 260;
            while (true)
            {
                var elapsed = (int)(Environment.TickCount64 - startedAt);
                var p = Math.Clamp(elapsed / (double)pulseMs, 0, 1);
                Paint(x, y, 34, tone, ringOnly: true, haloScale: 1.0 + p * 1.4); // expanding ring
                if (p >= 1.0) break;
                Thread.Sleep(16);
            }
            Paint(x, y, 34, tone, ringOnly: false, haloScale: 1.0); // settle back to resting dot
        });

    public void Hide() => Enqueue(() => { _visible = false; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_HIDE); });
    public void Show() => Enqueue(() => { _visible = true; if (_hwnd != IntPtr.Zero) ShowWindow(_hwnd, SW_SHOWNOACTIVATE); });

    private void CreateOverlayWindow()
    {
        var size = MaxDiameter + WindowPad * 2;
        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOPMOST | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW,
            "STATIC", string.Empty, WS_POPUP, 0, 0, size, size,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
    }

    private void Paint(int cx, int cy, int diameterPx, string tone, bool ringOnly, double haloScale)
    {
        if (_hwnd == IntPtr.Zero) return;
        var winSize = MaxDiameter + WindowPad * 2;
        var color = ToneColor(tone);
        var outer = Math.Clamp((int)Math.Round(diameterPx * haloScale), 6, MaxDiameter);
        var inner = Math.Max(6, diameterPx / 3);
        var center = winSize / 2f;

        using var bitmap = new Bitmap(winSize, winSize, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var oRect = new RectangleF(center - outer / 2f, center - outer / 2f, outer, outer);
            using var halo = new SolidBrush(Color.FromArgb(70, color));
            using var ring = new Pen(Color.FromArgb(220, color), 2.5f);
            if (!ringOnly) g.FillEllipse(halo, oRect);
            g.DrawEllipse(ring, oRect);
            if (!ringOnly)
            {
                var iRect = new RectangleF(center - inner / 2f, center - inner / 2f, inner, inner);
                using var dot = new SolidBrush(Color.FromArgb(245, color));
                g.FillEllipse(dot, iRect);
            }
        }

        var screenDc = GetDC(IntPtr.Zero);
        var memDc = CreateCompatibleDC(screenDc);
        var hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        var old = SelectObject(memDc, hBitmap);
        try
        {
            var dst = new PointNative(cx - winSize / 2, cy - winSize / 2);
            var sz = new SizeNative(winSize, winSize);
            var src = new PointNative(0, 0);
            var blend = new BlendFunction
            {
                BlendOp = AC_SRC_OVER, BlendFlags = 0,
                SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA,
            };
            UpdateLayeredWindow(_hwnd, screenDc, ref dst, ref sz, memDc, ref src, 0, ref blend, ULW_ALPHA);
        }
        finally
        {
            SelectObject(memDc, old);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static double Ease(string easing, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return easing == "linear"
            ? t
            : (t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2); // ease-in-out-cubic
    }

    private static Color ToneColor(string tone) => tone switch
    {
        PresenceTones.Acting => Color.FromArgb(200, 169, 106),  // gold #C8A96A
        PresenceTones.Observing => Color.FromArgb(122, 158, 126), // sage
        PresenceTones.Confirm => Color.FromArgb(140, 40, 50),    // wine
        _ => Color.FromArgb(200, 169, 106),
    };

    public void Dispose()
    {
        try { _commands.CompleteAdding(); } catch { }
        _thread?.Join(500);
        _commands.Dispose();
    }

    // ── Win32 ──
    private const int WS_EX_TRANSPARENT = 0x20, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80,
        WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x8000000;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int SW_SHOWNOACTIVATE = 4, SW_HIDE = 0, ULW_ALPHA = 0x2;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;

    [StructLayout(LayoutKind.Sequential)] private struct PointNative { public int X, Y; public PointNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential)] private struct SizeNative { public int X, Y; public SizeNative(int x, int y) { X = x; Y = y; } }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int dwExStyle, string lpClassName, string lpWindowName, int dwStyle,
        int x, int y, int w, int h, IntPtr p, IntPtr m, IntPtr i, IntPtr lp);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int n);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dcDst, ref PointNative dst, ref SizeNative sz,
        IntPtr dcSrc, ref PointNative src, int crKey, ref BlendFunction blend, int flags);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release`
Expected: `Build succeeded. 0 Error(s)` (pre-existing CA1416/xUnit1026 warnings are fine).

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/WindowsPresenceRenderer.cs
git commit -m "feat(presence): Win32 persistent click-through overlay — idle-no-repaint, glide/reticle/click-pulse"
```

---

## Task 7: PresenceHotkeyListener (instant local hide)

**Files:**
- Create: `src/SuavoAgent.Helper/Presence/PresenceHotkeyListener.cs`

**Interfaces:**
- Consumes: `PresencePreferenceStore.SetVisible` / `.Current` (Task 3).
- Produces: `class PresenceHotkeyListener : IDisposable { PresenceHotkeyListener(PresencePreferenceStore store, ILogger logger); void Start(); }`

**Design:** dedicated thread with its own message pump + `RegisterHotKey` (Ctrl+Alt+H). On `WM_HOTKEY` → toggle `store.SetVisible(!store.Current.CursorVisible)`. Uses a distinct hotkey id so it never collides with `HotkeyKillSwitch`. Win32 — verified by build + box demo.

- [ ] **Step 1: Implement the listener**

```csharp
// src/SuavoAgent.Helper/Presence/PresenceHotkeyListener.cs
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Serilog;

namespace SuavoAgent.Helper.Presence;

/// <summary>Global hotkey (Ctrl+Alt+H) that instantly toggles cursor visibility,
/// cloud-independent — the over-the-shoulder panic-hide. Own thread + message pump.</summary>
[SupportedOSPlatform("windows")]
public sealed class PresenceHotkeyListener : IDisposable
{
    private const int HotkeyId = 0x5A01;          // distinct from HotkeyKillSwitch
    private const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
    private const uint VK_H = 0x48, WM_HOTKEY = 0x312, WM_QUIT = 0x12;

    private readonly PresencePreferenceStore _store;
    private readonly ILogger _logger;
    private Thread? _thread;
    private uint _threadId;

    public PresenceHotkeyListener(PresencePreferenceStore store, ILogger logger)
    {
        _store = store;
        _logger = logger.ForContext<PresenceHotkeyListener>();
    }

    public void Start()
    {
        if (!OperatingSystem.IsWindows() || _thread is not null) return;
        _thread = new Thread(Loop) { IsBackground = true };
        _thread.Start();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        if (!RegisterHotKey(IntPtr.Zero, HotkeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_H))
        {
            _logger.Warning("Presence: failed to register hide hotkey (Ctrl+Alt+H): {Err}", Marshal.GetLastWin32Error());
            return;
        }
        _logger.Information("Presence: hide hotkey registered (Ctrl+Alt+H)");
        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WM_HOTKEY && (int)msg.wParam == HotkeyId)
                {
                    var next = !_store.Current.CursorVisible;
                    _store.SetVisible(next);
                    _logger.Information("Presence: hotkey toggled cursor visible={Visible}", next);
                }
            }
        }
        finally { UnregisterHotKey(IntPtr.Zero, HotkeyId); }
    }

    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread?.Join(500);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int ptX, ptY; }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release`
Expected: `Build succeeded. 0 Error(s)`.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Helper/Presence/PresenceHotkeyListener.cs
git commit -m "feat(presence): Ctrl+Alt+H instant hide hotkey (own pump, distinct id)"
```

---

## Task 8: Wire-up (DI + actuation hooks + IPC verb) and box verification

**Files:**
- Modify: `src/SuavoAgent.Helper/Program.cs` (~lines 89-96 build; ~126 SendInputDriver ctor)
- Modify: `src/SuavoAgent.Helper/Actuation/SendInputDriver.cs` (ctor ~75-85; `TryGlow` site ~87-108)
- Modify: `src/SuavoAgent.Helper/IpcCommandServer.cs` (add `presence.set_visible` verb)

**Interfaces:**
- Consumes: `PresenceBootstrap.LoadConfig`, `PresencePreferenceStore`, `WindowsPresenceRenderer`, `PresenceController`, `PresenceHotkeyListener` (Tasks 2,3,5,6,7).

- [ ] **Step 1: Build the presence stack in Program.cs**

After the IntentCursor build line (`var intentCursor = ...IntentCursorBootstrap.Build(Log.Logger);`, ~line 96), add:

```csharp
    // Presence layer — persistent agentic cursor + preferences + instant hide.
    // Visual-only; never gates actuation. Operator opt-out via presence.json.
    var presencePrefs = SuavoAgent.Helper.Presence.PresenceBootstrap.LoadConfig(Log.Logger);
    var presenceStore = new SuavoAgent.Helper.Presence.PresencePreferenceStore(presencePrefs);
    var presenceRenderer = new SuavoAgent.Helper.Presence.WindowsPresenceRenderer(Log.Logger);
    presenceRenderer.Start();
    var presenceController = new SuavoAgent.Helper.Presence.PresenceController(
        presenceRenderer, presenceStore, Log.Logger,
        isSessionInteractive: () => GetSystemMetrics(SM_REMOTESESSION) == 0
            || presencePrefs.SuppressWhenSessionDisconnected == false);
    var presenceHotkey = new SuavoAgent.Helper.Presence.PresenceHotkeyListener(presenceStore, Log.Logger);
    presenceHotkey.Start();
```

At the top of `Program.cs` (with the other P/Invokes, or in the Program class body) add:

```csharp
    private const int SM_REMOTESESSION = 0x1000;
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
```

> Note: `SM_REMOTESESSION != 0` means an RDP session; treat console session as interactive. If `Program.cs` is top-level statements (no class), declare these via a local `static` partial helper class at file end instead.

- [ ] **Step 2: Pass the controller into SendInputDriver**

Modify the `SendInputDriver` ctor (`SendInputDriver.cs:75-85`) to accept the controller:

```csharp
    public SendInputDriver(
        ActuationGate gate,
        ActuationConfig config,
        ILogger logger,
        SuavoAgent.Helper.IntentCursor.IntentCursorController? intentCursor = null,
        SuavoAgent.Helper.Presence.PresenceController? presence = null)
    {
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _logger = (logger ?? throw new ArgumentNullException(nameof(logger))).ForContext<SendInputDriver>();
        _intentCursor = intentCursor;
        _presence = presence;
    }
```

Add the field near `_intentCursor`: `private readonly SuavoAgent.Helper.Presence.PresenceController? _presence;`

In `Program.cs` (~line 126) update the construction:

```csharp
sendInputDriver = new SendInputDriver(actuationGate, actuationConfig, Log.Logger, intentCursor, presenceController);
```

- [ ] **Step 3: Hook the presence cursor at the TryGlow site**

Replace the body of `TryGlow(int x, int y)` (`SendInputDriver.cs:94-108`) so the persistent presence cursor glides to + reticles the target (the legacy IntentCursor flash stays as a fallback when presence isn't wired):

```csharp
    private void TryGlow(int x, int y)
    {
        // Persistent presence cursor: glide to the target and land a reticle BEFORE the click.
        var pres = _presence;
        if (pres is not null)
        {
            try { pres.MoveTo(x, y); pres.Reticle(x, y); }
            catch { /* visual-only — never break actuation */ }
            return;
        }
        // Legacy fallback: one-shot IntentCursor flash.
        var ic = _intentCursor;
        if (ic is null) return;
        try
        {
            _ = ic.ShowAsync(
                new IntentCursorRequest(X: x, Y: y, CoordinateSpace: IntentCursorCoordinateSpaces.Screen,
                    DurationMs: 1500, DiameterPx: 48, Opacity: 0.85, Tone: IntentCursorTones.Agent),
                CancellationToken.None);
        }
        catch { /* visual-only */ }
    }
```

Find where the actual click fires (search `MoveAndClick` / the click method that calls `TryGlow`) and add a post-click pulse right after the click succeeds:

```csharp
        // after the real click lands:
        try { _presence?.Click(x, y); } catch { /* visual-only */ }
```

- [ ] **Step 4: Add the `presence.set_visible` IPC verb**

In `IpcCommandServer.cs`, add a field + ctor param `PresencePreferenceStore? presenceStore` (mirror how `intentCursor` is threaded), then add a handler and dispatch case (near `HandleIntentCursorAsync`):

```csharp
    private IpcResponse HandlePresenceSetVisible(IpcRequest request)
    {
        if (_presenceStore is null)
            return Error(request.Id, request.Command, "presence_unavailable", "Presence not configured");
        if (request.Data is null)
            return Error(request.Id, request.Command, "bad_request", "Missing data", IpcStatus.BadRequest);
        try
        {
            var visible = request.Data.Value.GetProperty("visible").GetBoolean();
            _presenceStore.SetVisible(visible);
            var payload = JsonSerializer.SerializeToElement(new { visible });
            return Ok(request.Id, request.Command, payload);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "presence.set_visible bad request");
            return Error(request.Id, request.Command, "bad_request", "Invalid presence payload", IpcStatus.BadRequest);
        }
    }
```

Add the dispatch case in the command switch (where `intent_cursor` is routed): `"presence.set_visible" => HandlePresenceSetVisible(request),`. Pass `presenceStore` into the `IpcCommandServer` ctor from `Program.cs` (~line 169-178, add `presenceStore: presenceStore,`).

- [ ] **Step 5: Build the whole Helper + run all presence tests**

Run:
```bash
dotnet build src/SuavoAgent.Helper/SuavoAgent.Helper.csproj -c Release
dotnet test tests/SuavoAgent.Helper.Tests/SuavoAgent.Helper.Tests.csproj -c Release --filter "FullyQualifiedName~Presence"
```
Expected: build `0 Error(s)`; tests `Passed` (Tasks 1-5 = 15 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Helper/Program.cs src/SuavoAgent.Helper/Actuation/SendInputDriver.cs src/SuavoAgent.Helper/IpcCommandServer.cs
git commit -m "feat(presence): wire persistent cursor into actuation + DI + presence.set_visible IPC verb"
```

- [ ] **Step 7: On-box verification (Joshua's box, no PioneerRx)**

1. OTA the build to the box (per the OTA push mechanism: tag → release.yml → heartbeat update).
2. Dispatch `run_workflow calc_verified` and confirm:
   - the cursor **glides** button-to-button on the calculator,
   - a **reticle lands before** each click and a **pulse** fires on click,
   - the cursor **persists** between clicks (never disappears),
   - the calc workflow still asserts "12" (actuation unaffected).
3. Press **Ctrl+Alt+H** mid-run → cursor hides instantly while the agent keeps clicking (proves cosmetic-never-gates). Press again → reappears.
4. Confirm near-zero Helper CPU at rest (idle-no-repaint) via Task Manager; a bounded spike only during glides.

---

## Self-Review

**Spec coverage (spec §5 Phase 1):**
- Persistent cursor (no spawn/destroy) → Task 6 (one session-long window). ✓
- Glide target→target → Tasks 4 (plan) + 6 (animate). ✓
- Pre-click reticle → Tasks 5/6/8 (Reticle before click at TryGlow). ✓
- Click pulse → Tasks 6/8. ✓
- Persists/parks → Task 5 `Park` + Task 6 resting dot. ✓
- Preferences system + presence.json → Tasks 1/2/3. ✓
- Instant hide (hotkey + tray + IPC) → Task 7 (hotkey) + Task 8 (IPC verb). *(Tray item deferred — see gap below.)*
- Cosmetic-never-gates invariant → Task 5 tests + Task 8 try-caught hooks. ✓
- Idle = no repaint → Task 6 (BlockingCollection.Take blocks at rest). ✓
- SuppressWhenSessionDisconnected → Task 5 predicate + Task 8 SM_REMOTESESSION wiring. ✓
- GDI-now/DComp-1.5 → Task 6 (GDI) + noted out of scope. ✓

**Gaps found & resolved:**
- **Tray hide item** (spec §5.1 mentions tray) is NOT in these tasks — the hotkey + IPC verb cover instant hide for Phase 1. Tray menu item folded into a Phase 1 follow-up (depends on `TrayIndicator.cs` API not yet read). Logged here, not silently dropped.
- **Rect-based reticle sizing** (spec §5.2 `MoveTo(ElementRect)`): Phase 1 is coordinate-based (matches the real `TryGlow(x,y)` call site); element-rect sizing is a Phase 2 refinement. Logged.

**Placeholder scan:** none — every step has complete code or an exact command.

**Type consistency:** `PresencePreferences`, `PresenceTones`, `IsCursorActive`, `PresencePreferenceStore.SetVisible/Replace/Changed`, `PresenceMotion.PlanGlide`, `IPresenceRenderer.Glide/Reticle/ClickPulse/Hide/Show`, `PresenceController.MoveTo/Reticle/Click/Park` are used consistently across Tasks 1-8. ✓

**Note for implementer:** Step 3 of Task 8 says "find where the actual click fires" — `SendInputDriver` has a click method (e.g. `MoveAndClick`) that calls `TryGlow`; confirm its exact name in the file before adding the post-click `_presence?.Click(x,y)` line. This is the one spot requiring a quick in-file confirmation, not a placeholder.
