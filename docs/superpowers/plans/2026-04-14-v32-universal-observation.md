# SuavoAgent v3.2 Universal Observation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Expand Helper from PMS-only observation to system-wide desktop intelligence — track all foreground apps, profile workstations, detect shift patterns, and aggregate temporal data.

**Architecture:** Split Helper into two observer tiers: system observers (always running, no PMS dependency) and PMS observers (existing, only active when PMS is attached). Three new observers run as lightweight background loops in the Helper process, sending events to Core via the existing IPC pipe. Core stores them in new SQLite tables.

**Tech Stack:** .NET 8, Win32 P/Invoke (GetForegroundWindow, SetWinEventHook, GetLastInputInfo), WMI (Win32_PnPEntity), SQLite

**Spec reference:** `docs/superpowers/specs/2026-04-14-v3-hardening-universal-intelligence-design.md` Section 3

---

### Task 1: Add New Event Types and IPC Commands

**Files:**
- Modify: `src/SuavoAgent.Contracts/Behavioral/BehavioralEvent.cs`
- Modify: `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Behavioral/BehavioralEventTests.cs`

- [ ] **Step 1: Add new BehavioralEventType variants**

In `BehavioralEvent.cs`, add to the `BehavioralEventType` enum:

```csharp
public enum BehavioralEventType
{
    TreeSnapshot,
    Interaction,
    KeystrokeCategory,
    AppFocusChange,     // NEW: foreground app switched
    SessionChange,      // NEW: login/logout/lock/unlock
    StationProfile      // NEW: one-time hardware fingerprint
}
```

- [ ] **Step 2: Add factory methods for new event types**

Add to `BehavioralEvent`:

```csharp
public static BehavioralEvent AppFocusChange(
    string fromProcessName, string toProcessName,
    string? windowTitleHash, long focusDurationMs) =>
    new()
    {
        Type = BehavioralEventType.AppFocusChange,
        Subtype = "focus_change",
        ElementId = toProcessName,     // current app
        ClassName = fromProcessName,   // previous app
        NameHash = windowTitleHash,
        KeystrokeCount = (int)Math.Min(focusDurationMs, int.MaxValue),
        OccurrenceCount = 1,
        Timestamp = DateTimeOffset.UtcNow
    };

public static BehavioralEvent SessionChange(string changeType, string? userSidHash) =>
    new()
    {
        Type = BehavioralEventType.SessionChange,
        Subtype = changeType,  // "logon", "logoff", "lock", "unlock"
        NameHash = userSidHash,
        OccurrenceCount = 1,
        Timestamp = DateTimeOffset.UtcNow
    };

public static BehavioralEvent StationProfileEvent(string profileJson) =>
    new()
    {
        Type = BehavioralEventType.StationProfile,
        Subtype = "station_profile",
        TreeHash = profileJson,  // JSON blob with hardware info
        OccurrenceCount = 1,
        Timestamp = DateTimeOffset.UtcNow
    };
```

- [ ] **Step 3: Add IPC command for system events**

In `IpcMessage.cs`, add:

```csharp
public const string SystemEvents = "system_events";
```

- [ ] **Step 4: Add tests**

```csharp
[Fact]
public void AppFocusChange_CreatesCorrectEvent()
{
    var evt = BehavioralEvent.AppFocusChange("EXCEL.EXE", "PioneerPharmacy.exe", "hash123", 5000);
    Assert.Equal(BehavioralEventType.AppFocusChange, evt.Type);
    Assert.Equal("PioneerPharmacy.exe", evt.ElementId);
    Assert.Equal("EXCEL.EXE", evt.ClassName);
    Assert.Equal(5000, evt.KeystrokeCount);
}

[Fact]
public void SessionChange_CreatesCorrectEvent()
{
    var evt = BehavioralEvent.SessionChange("logon", "sidhash123");
    Assert.Equal(BehavioralEventType.SessionChange, evt.Type);
    Assert.Equal("logon", evt.Subtype);
}
```

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Contracts.Tests --nologo -v q`
Commit: `git commit -m "feat: add AppFocusChange, SessionChange, StationProfile event types"`

---

### Task 2: ForegroundTracker Observer

**Files:**
- Create: `src/SuavoAgent.Helper/SystemObservers/ForegroundTracker.cs`
- Test: `tests/SuavoAgent.Core.Tests/Behavioral/ForegroundTrackerTests.cs`

- [ ] **Step 1: Create ForegroundTracker**

Create `src/SuavoAgent.Helper/SystemObservers/ForegroundTracker.cs`:

```csharp
using System.Diagnostics;
using System.Runtime.InteropServices;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Tracks foreground application transitions — which app has focus, how long, transition sequences.
/// GREEN tier: process names and durations. YELLOW tier: window title hashes.
/// Polls every 2 seconds via GetForegroundWindow.
/// </summary>
public sealed class ForegroundTracker : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private string? _currentProcessName;
    private string? _currentWindowTitleHash;
    private DateTimeOffset _focusStart;
    private volatile bool _disposed;

    public int TransitionCount { get; private set; }

    public ForegroundTracker(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
        _focusStart = DateTimeOffset.UtcNow;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        _logger.Information("ForegroundTracker started");
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                PollForeground();
            }
            catch (Exception ex)
            {
                _logger.Debug(ex, "ForegroundTracker poll error");
            }
            await Task.Delay(2000, ct);
        }
    }

    private void PollForeground()
    {
        if (!OperatingSystem.IsWindows()) return;

        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return;

        string processName;
        try
        {
            processName = Process.GetProcessById((int)pid).ProcessName;
        }
        catch { return; } // process may have exited

        if (processName == _currentProcessName) return;

        // Transition detected
        var now = DateTimeOffset.UtcNow;
        var duration = (long)(now - _focusStart).TotalMilliseconds;
        var prevProcess = _currentProcessName;

        // Hash the window title (YELLOW tier)
        string? titleHash = null;
        try
        {
            var sb = new System.Text.StringBuilder(256);
            GetWindowText(hwnd, sb, sb.Capacity);
            var title = sb.ToString();
            if (!string.IsNullOrEmpty(title))
                titleHash = UiaPropertyScrubber.HmacHash(title, _pharmacySalt);
        }
        catch { }

        _currentProcessName = processName;
        _currentWindowTitleHash = titleHash;
        _focusStart = now;

        if (prevProcess != null) // skip first observation
        {
            var evt = BehavioralEvent.AppFocusChange(prevProcess, processName, titleHash, duration);
            _buffer.Enqueue(evt);
            TransitionCount++;
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);

    public void Dispose() => _disposed = true;
}
```

- [ ] **Step 2: Add a unit test for the event factory (logic test, no Win32)**

```csharp
[Fact]
public void AppFocusChange_EncodesTransitionCorrectly()
{
    var evt = BehavioralEvent.AppFocusChange("chrome.exe", "EXCEL.EXE", "titlehash", 12000);
    Assert.Equal(BehavioralEventType.AppFocusChange, evt.Type);
    Assert.Equal("EXCEL.EXE", evt.ElementId);   // to-app
    Assert.Equal("chrome.exe", evt.ClassName);   // from-app
    Assert.Equal(12000, evt.KeystrokeCount);     // duration
    Assert.Equal("titlehash", evt.NameHash);
}
```

- [ ] **Step 3: Run tests, commit**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Commit: `git commit -m "feat: add ForegroundTracker system observer"`

---

### Task 3: StationProfiler Observer

**Files:**
- Create: `src/SuavoAgent.Helper/SystemObservers/StationProfiler.cs`

- [ ] **Step 1: Create StationProfiler**

```csharp
using System.Text.Json;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// One-shot hardware fingerprint — runs once at startup, daily refresh.
/// GREEN tier: monitor count, resolution, peripheral classes, RAM/CPU buckets.
/// YELLOW tier: machine name hash.
/// </summary>
public sealed class StationProfiler
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;

    public StationProfiler(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public void CaptureProfile()
    {
        try
        {
            var profile = new
            {
                machineNameHash = UiaPropertyScrubber.HmacHash(Environment.MachineName, _pharmacySalt),
                processorCount = Environment.ProcessorCount,
                ramBucketGb = GetRamBucket(),
                osVersion = Environment.OSVersion.VersionString,
                monitorCount = GetMonitorCount(),
                timestamp = DateTimeOffset.UtcNow.ToString("o")
            };

            var json = JsonSerializer.Serialize(profile);
            _buffer.Enqueue(BehavioralEvent.StationProfileEvent(json));
            _logger.Information("Station profile captured: {Cores} cores, {Ram}GB RAM, {Monitors} monitors",
                profile.processorCount, profile.ramBucketGb, profile.monitorCount);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Station profile capture failed");
        }
    }

    private static int GetRamBucket()
    {
        try
        {
            var totalBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            var gb = (int)(totalBytes / (1024L * 1024 * 1024));
            return gb switch
            {
                < 4 => 4,
                < 8 => 8,
                < 16 => 16,
                < 32 => 32,
                _ => 64
            };
        }
        catch { return 0; }
    }

    private static int GetMonitorCount()
    {
        // Fallback — Screen class requires WinForms reference
        // Use EnumDisplayMonitors for a lighter approach
        if (!OperatingSystem.IsWindows()) return 1;
        try
        {
            int count = 0;
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
                (IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data) => { count++; return true; },
                IntPtr.Zero);
            return count > 0 ? count : 1;
        }
        catch { return 1; }
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect rect, IntPtr data);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);
}
```

- [ ] **Step 2: Run build, commit**

Run: `dotnet build --nologo -v q`
Commit: `git commit -m "feat: add StationProfiler system observer"`

---

### Task 4: UserSessionObserver

**Files:**
- Create: `src/SuavoAgent.Helper/SystemObservers/UserSessionObserver.cs`

- [ ] **Step 1: Create UserSessionObserver**

```csharp
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Detects login/logout/lock/unlock events for shift pattern detection.
/// GREEN tier: event type, timestamps. YELLOW tier: user SID hash.
/// </summary>
public sealed class UserSessionObserver : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _disposed;

    public int EventCount { get; private set; }

    public UserSessionObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;

        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.SystemEvents.SessionSwitch += OnSessionSwitch;
            _logger.Information("UserSessionObserver subscribed to session events");
        }
    }

    private void OnSessionSwitch(object? sender, Microsoft.Win32.SessionSwitchEventArgs e)
    {
        if (_disposed) return;

        var changeType = e.Reason switch
        {
            Microsoft.Win32.SessionSwitchReason.SessionLogon => "logon",
            Microsoft.Win32.SessionSwitchReason.SessionLogoff => "logoff",
            Microsoft.Win32.SessionSwitchReason.SessionLock => "lock",
            Microsoft.Win32.SessionSwitchReason.SessionUnlock => "unlock",
            _ => null
        };

        if (changeType == null) return;

        string? userSidHash = null;
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            if (identity.User != null)
                userSidHash = UiaPropertyScrubber.HmacHash(identity.User.Value, _pharmacySalt);
        }
        catch { }

        var evt = BehavioralEvent.SessionChange(changeType, userSidHash);
        _buffer.Enqueue(evt);
        EventCount++;
        _logger.Information("Session event: {Type}", changeType);
    }

    public void Dispose()
    {
        _disposed = true;
        if (OperatingSystem.IsWindows())
        {
            Microsoft.Win32.SystemEvents.SessionSwitch -= OnSessionSwitch;
        }
    }
}
```

- [ ] **Step 2: Run build, commit**

Run: `dotnet build --nologo -v q`
Commit: `git commit -m "feat: add UserSessionObserver for shift pattern detection"`

---

### Task 5: Wire System Observers into Helper

**Files:**
- Modify: `src/SuavoAgent.Helper/Program.cs`

- [ ] **Step 1: Add system observer startup BEFORE the PMS attachment loop**

The key architecture change: system observers start immediately on Helper launch, independent of PMS attachment. Find the section right before `while (!cts.Token.IsCancellationRequested && !attached)` (line 139) and add:

```csharp
// ── System observers — always running, no PMS dependency ──
pharmacySalt = await FetchPharmacySaltAsync();

var systemBuffer = new BehavioralEventBuffer(
    capacity: 200,
    batchSize: 20,
    flushAction: async events =>
    {
        var json = System.Text.Json.JsonSerializer.Serialize(events);
        await ipcClient.TrySendAsync(IpcCommands.SystemEvents, json, cts.Token);
    });

var foregroundTracker = new SuavoAgent.Helper.SystemObservers.ForegroundTracker(
    systemBuffer, pharmacySalt, Log.Logger);
var stationProfiler = new SuavoAgent.Helper.SystemObservers.StationProfiler(
    systemBuffer, pharmacySalt, Log.Logger);
var sessionObserver = new SuavoAgent.Helper.SystemObservers.UserSessionObserver(
    systemBuffer, pharmacySalt, Log.Logger);

// Station profile — one-shot on startup
stationProfiler.CaptureProfile();

// Foreground tracker — runs as async loop
var fgTrackerCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
_ = Task.Run(() => foregroundTracker.RunAsync(fgTrackerCts.Token), fgTrackerCts.Token);

Log.Information("System observers started (foreground tracker, station profiler, session observer)");
```

- [ ] **Step 2: Add cleanup in the final cleanup section**

Before `StopBehavioralObservers()` at the end (line 234), add:

```csharp
// Cleanup system observers
fgTrackerCts?.Cancel();
foregroundTracker?.Dispose();
sessionObserver?.Dispose();
systemBuffer?.Dispose();
```

- [ ] **Step 3: Add system_events IPC handler to Core's Program.cs**

In `src/SuavoAgent.Core/Program.cs`, in the IPC switch statement, add a case for the new SystemEvents command. It should process events the same way BehavioralEvents does:

```csharp
case SuavoAgent.Contracts.Ipc.IpcCommands.SystemEvents:
{
    var events = msg.Data.HasValue
        ? System.Text.Json.JsonSerializer.Deserialize<List<SuavoAgent.Contracts.Behavioral.BehavioralEvent>>(
            msg.Data.Value.GetRawText())
        : null;
    // Apply same batch cap as behavioral events
    if (events != null && events.Count > 200)
        events = events.Take(200).ToList();
    if (events is { Count: > 0 })
    {
        var receiver = sp.GetRequiredService<BehavioralEventReceiver>();
        receiver.ProcessBatch(events, 0);
    }
    return Task.FromResult(new SuavoAgent.Contracts.Ipc.IpcResponse(
        msg.Id, SuavoAgent.Contracts.Ipc.IpcStatus.Ok, msg.Command, default, null));
}
```

- [ ] **Step 4: Run build + full tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`
Expected: 0 errors, all tests pass

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: wire system observers into Helper — always-on desktop observation"
```

---

### Task 6: AppSession and TemporalProfile SQLite Tables

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs`

- [ ] **Step 1: Add new tables to InitSchema**

In `AgentStateDb.cs`, in `InitSchema()`, add after the existing table creation:

```csharp
// ── Universal observation tables ──
Execute("""
    CREATE TABLE IF NOT EXISTS app_sessions (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        app_name TEXT NOT NULL,
        window_title_hash TEXT,
        start_ts TEXT NOT NULL,
        end_ts TEXT,
        focus_ms INTEGER DEFAULT 0,
        preceding_app TEXT,
        following_app TEXT,
        created_at TEXT DEFAULT (datetime('now'))
    )
""");

Execute("""
    CREATE TABLE IF NOT EXISTS temporal_profiles (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        period_type TEXT NOT NULL,
        period_key TEXT NOT NULL,
        app_distribution TEXT,
        action_volume INTEGER DEFAULT 0,
        peak_load_score REAL DEFAULT 0,
        updated_at TEXT DEFAULT (datetime('now')),
        UNIQUE(session_id, period_type, period_key)
    )
""");

Execute("""
    CREATE TABLE IF NOT EXISTS station_profiles (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        machine_hash TEXT NOT NULL,
        processor_count INTEGER,
        ram_bucket_gb INTEGER,
        monitor_count INTEGER,
        os_version TEXT,
        profile_json TEXT,
        captured_at TEXT DEFAULT (datetime('now'))
    )
""");
```

- [ ] **Step 2: Add CRUD methods**

```csharp
public void InsertAppSession(string sessionId, string appName, string? windowTitleHash,
    DateTimeOffset startTs, long focusMs, string? precedingApp)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO app_sessions (session_id, app_name, window_title_hash, start_ts, focus_ms, preceding_app)
        VALUES (@sid, @app, @title, @start, @focus, @prev)
    """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@app", appName);
    cmd.Parameters.AddWithValue("@title", (object?)windowTitleHash ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@start", startTs.ToString("o"));
    cmd.Parameters.AddWithValue("@focus", focusMs);
    cmd.Parameters.AddWithValue("@prev", (object?)precedingApp ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}

public void UpsertTemporalProfile(string sessionId, string periodType, string periodKey,
    int actionVolume, double peakLoadScore)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO temporal_profiles (session_id, period_type, period_key, action_volume, peak_load_score, updated_at)
        VALUES (@sid, @type, @key, @vol, @peak, datetime('now'))
        ON CONFLICT(session_id, period_type, period_key) DO UPDATE SET
            action_volume = action_volume + @vol,
            peak_load_score = MAX(peak_load_score, @peak),
            updated_at = datetime('now')
    """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@type", periodType);
    cmd.Parameters.AddWithValue("@key", periodKey);
    cmd.Parameters.AddWithValue("@vol", actionVolume);
    cmd.Parameters.AddWithValue("@peak", peakLoadScore);
    cmd.ExecuteNonQuery();
}

public void InsertStationProfile(string machineHash, int processorCount, int ramBucketGb,
    int monitorCount, string osVersion, string profileJson)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO station_profiles (machine_hash, processor_count, ram_bucket_gb, monitor_count, os_version, profile_json)
        VALUES (@hash, @cpu, @ram, @mon, @os, @json)
    """;
    cmd.Parameters.AddWithValue("@hash", machineHash);
    cmd.Parameters.AddWithValue("@cpu", processorCount);
    cmd.Parameters.AddWithValue("@ram", ramBucketGb);
    cmd.Parameters.AddWithValue("@mon", monitorCount);
    cmd.Parameters.AddWithValue("@os", osVersion);
    cmd.Parameters.AddWithValue("@json", profileJson);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 3: Add tests**

```csharp
[Fact]
public void AppSession_InsertAndQuery()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test-appsession-{Guid.NewGuid():N}.db");
    try
    {
        using var db = new AgentStateDb(dbPath);
        db.InsertAppSession("s1", "EXCEL.EXE", "hash1", DateTimeOffset.UtcNow, 5000, "chrome.exe");
        db.InsertAppSession("s1", "PioneerPharmacy.exe", null, DateTimeOffset.UtcNow, 12000, "EXCEL.EXE");
        // Verify no exception and data persisted
    }
    finally { File.Delete(dbPath); }
}

[Fact]
public void TemporalProfile_Upsert_AccumulatesVolume()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test-temporal-{Guid.NewGuid():N}.db");
    try
    {
        using var db = new AgentStateDb(dbPath);
        db.UpsertTemporalProfile("s1", "hourly", "2026-04-14T09", 10, 0.5);
        db.UpsertTemporalProfile("s1", "hourly", "2026-04-14T09", 5, 0.8);
        // Second upsert should accumulate volume (10+5=15) and max peak (0.8)
    }
    finally { File.Delete(dbPath); }
}
```

- [ ] **Step 4: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Core.Tests --nologo -v q`
Commit: `git commit -m "feat: add app_sessions, temporal_profiles, station_profiles tables"`

---

### Task 7: Version Bump + Final Verification

- [ ] **Step 1: Update version**

In `src/SuavoAgent.Core/appsettings.json`, change `"Version": "3.1.0"` to `"Version": "3.2.0"`.

- [ ] **Step 2: Full build + test**

Run: `dotnet build --nologo -v q` — 0 errors
Run: `dotnet test --nologo -v q` — 0 failures

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: bump version to v3.2.0 — universal desktop observation"
```
