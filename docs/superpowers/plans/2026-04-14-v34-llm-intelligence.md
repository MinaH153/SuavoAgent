# SuavoAgent v3.4 LLM Intelligence — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a local intelligence layer that assembles sanitized context from observation data, enforces a hard compliance boundary (no PHI/PII to cloud), and provides an interface for Claude LLM queries via the Suavo cloud proxy.

**Architecture:** Agent-side preprocessing assembles sanitized context packets from SQLite. A `ComplianceBoundary` validator ensures no PHI leaks before any cloud transmission. The cloud/dashboard proxies LLM requests — the agent never calls Claude directly (avoids storing API keys on pharmacy computers). Intelligence context is included in heartbeat payloads for the dashboard's AI features.

**Tech Stack:** .NET 8, SQLite, System.Text.Json

---

### Task 1: ComplianceBoundary Validator

**Files:**
- Create: `src/SuavoAgent.Core/Intelligence/ComplianceBoundary.cs`
- Test: `tests/SuavoAgent.Core.Tests/Intelligence/ComplianceBoundaryTests.cs`

- [ ] **Step 1: Create ComplianceBoundary**

Create `src/SuavoAgent.Core/Intelligence/ComplianceBoundary.cs`:

```csharp
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SuavoAgent.Core.Intelligence;

/// <summary>
/// Hard compliance boundary — validates that a context packet contains no PHI/PII
/// before it can be transmitted to the cloud or LLM.
/// Returns (isClean, violations) — if any violations found, the packet MUST NOT be sent.
/// </summary>
public static class ComplianceBoundary
{
    // Patterns that indicate PHI/PII presence
    private static readonly Regex[] PhiPatterns =
    {
        new(@"\b\d{3}-\d{2}-\d{4}\b", RegexOptions.Compiled),           // SSN
        new(@"\b\d{3}[-.\s]?\d{3}[-.\s]?\d{4}\b", RegexOptions.Compiled), // Phone
        new(@"\b[A-Z][a-z]+\s[A-Z][a-z]+\b", RegexOptions.Compiled),     // Name pairs (First Last)
        new(@"\b\d{1,2}/\d{1,2}/\d{2,4}\b", RegexOptions.Compiled),     // Dates (MM/DD/YYYY)
        new(@"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}", RegexOptions.Compiled), // Email
        new(@"\b\d{5}(-\d{4})?\b", RegexOptions.Compiled),              // ZIP code
    };

    // Known safe field names that should NOT trigger violations
    private static readonly HashSet<string> SafeFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "industry", "processorCount", "ramBucketGb", "monitorCount", "osVersion",
        "appName", "focusMs", "actionVolume", "peakLoadScore", "periodType",
        "periodKey", "confidence", "frequency", "avgDurationMs", "category",
        "columnCount", "fileType", "agentVersion", "learningPhase", "stationRole",
        "transitionCount", "eventCount", "totalDetected", "routineCount"
    };

    // Fields that MUST be hashed (YELLOW tier) — if they contain plain text, reject
    private static readonly HashSet<string> MustBeHashedFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "windowTitleHash", "nameHash", "machineNameHash", "docHash",
        "schemaFingerprint", "userSidHash", "treeHash"
    };

    /// <summary>
    /// Validates a JSON context packet for PHI/PII.
    /// Returns (true, empty) if clean, (false, violations) if contaminated.
    /// </summary>
    public static (bool IsClean, List<string> Violations) Validate(string json)
    {
        var violations = new List<string>();

        // Check for PHI patterns in the raw JSON
        foreach (var pattern in PhiPatterns)
        {
            var matches = pattern.Matches(json);
            foreach (Match match in matches)
            {
                // Skip if it's in a safe context (e.g., version numbers, timestamps)
                var value = match.Value;
                if (IsSafeValue(value, json, match.Index))
                    continue;
                violations.Add($"PHI pattern detected: '{Redact(value)}' (pattern: {pattern})");
            }
        }

        return (violations.Count == 0, violations);
    }

    /// <summary>
    /// Quick check — validates a dictionary of key-value pairs.
    /// Rejects any value that contains unmasked PHI patterns.
    /// </summary>
    public static (bool IsClean, List<string> Violations) ValidateFields(
        IDictionary<string, object?> fields)
    {
        var violations = new List<string>();

        foreach (var (key, value) in fields)
        {
            if (value == null) continue;
            var strVal = value.ToString() ?? "";

            // Must-be-hashed fields should look like hex hashes, not plain text
            if (MustBeHashedFields.Contains(key))
            {
                if (strVal.Length > 0 && strVal.Length < 32 && !strVal.All(c => "0123456789abcdef".Contains(c)))
                    violations.Add($"Field '{key}' appears unhashed: '{Redact(strVal)}'");
            }
        }

        return (violations.Count == 0, violations);
    }

    private static bool IsSafeValue(string value, string json, int index)
    {
        // Version numbers like "3.3.0" look like dates — skip
        if (Regex.IsMatch(value, @"^\d+\.\d+\.\d+$")) return true;
        // ISO timestamps are safe
        if (value.Contains('T') && value.Contains(':')) return true;
        // Check surrounding context for safe field names
        var start = Math.Max(0, index - 50);
        var context = json[start..index];
        return SafeFields.Any(f => context.Contains(f, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static string Redact(string value) =>
        value.Length <= 4 ? "****" : value[..2] + "****" + value[^2..];
}
```

- [ ] **Step 2: Create tests**

Create `tests/SuavoAgent.Core.Tests/Intelligence/ComplianceBoundaryTests.cs`:

```csharp
using SuavoAgent.Core.Intelligence;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class ComplianceBoundaryTests
{
    [Fact]
    public void Validate_CleanJson_ReturnsClean()
    {
        var json = """{"industry":"pharmacy","processorCount":4,"appName":"EXCEL.EXE","focusMs":12000}""";
        var (isClean, violations) = ComplianceBoundary.Validate(json);
        Assert.True(isClean, string.Join("; ", violations));
    }

    [Fact]
    public void Validate_WithSSN_RejectsPayload()
    {
        var json = """{"data":"Patient SSN is 123-45-6789"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void Validate_WithEmail_RejectsPayload()
    {
        var json = """{"contact":"john.doe@email.com"}""";
        var (isClean, _) = ComplianceBoundary.Validate(json);
        Assert.False(isClean);
    }

    [Fact]
    public void ValidateFields_HashedFieldsPass()
    {
        var fields = new Dictionary<string, object?>
        {
            ["windowTitleHash"] = "a3f2b1c4d5e6f7a8b9c0d1e2f3a4b5c6",
            ["appName"] = "EXCEL.EXE",
            ["focusMs"] = 5000
        };
        var (isClean, _) = ComplianceBoundary.ValidateFields(fields);
        Assert.True(isClean);
    }

    [Fact]
    public void ValidateFields_UnhashedField_Rejects()
    {
        var fields = new Dictionary<string, object?>
        {
            ["windowTitleHash"] = "Patient Record",  // should be hashed!
        };
        var (isClean, violations) = ComplianceBoundary.ValidateFields(fields);
        Assert.False(isClean);
        Assert.Contains(violations, v => v.Contains("windowTitleHash"));
    }
}
```

- [ ] **Step 3: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "ComplianceBoundary" --nologo -v q`
Commit: `git commit -m "feat: add ComplianceBoundary validator for PHI-free cloud transmission"`

---

### Task 2: ContextAssembler — Sanitized Intelligence Packets

**Files:**
- Create: `src/SuavoAgent.Core/Intelligence/ContextAssembler.cs`
- Test: `tests/SuavoAgent.Core.Tests/Intelligence/ContextAssemblerTests.cs`

- [ ] **Step 1: Create ContextAssembler**

```csharp
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Intelligence;

/// <summary>
/// Assembles sanitized context packets from observation data for LLM consumption.
/// All output passes through ComplianceBoundary before transmission.
/// Target size: ~2K tokens (under 8KB JSON).
/// </summary>
public sealed class ContextAssembler
{
    private readonly AgentStateDb _db;

    public ContextAssembler(AgentStateDb db)
    {
        _db = db;
    }

    /// <summary>
    /// Builds a sanitized intelligence context packet.
    /// Contains ONLY GREEN-tier data safe for cloud/LLM transmission.
    /// </summary>
    public IntelligenceContext AssembleContext(string businessId, string? sessionId = null)
    {
        return new IntelligenceContext
        {
            BusinessId = businessId,
            AssembledAt = DateTimeOffset.UtcNow,
            // Business metadata
            Industry = GetIndustry(businessId),
            // Recent app usage summary (last 24h, aggregated)
            AppUsageSummary = GetAppUsageSummary(sessionId),
            // Temporal profile (current day pattern)
            TemporalSnapshot = GetTemporalSnapshot(sessionId),
            // Station profile
            StationInfo = GetStationInfo(),
        };
    }

    private string GetIndustry(string businessId)
    {
        // Query business_meta for industry
        try
        {
            return "pharmacy"; // fallback — full query implementation depends on business_meta read method
        }
        catch { return "unknown"; }
    }

    private Dictionary<string, int> GetAppUsageSummary(string? sessionId)
    {
        // Aggregate app_sessions by app_name, return top 10 by focus_ms
        var summary = new Dictionary<string, int>();
        // Placeholder — will be populated when app_sessions has data
        return summary;
    }

    private Dictionary<string, int> GetTemporalSnapshot(string? sessionId)
    {
        // Return action_volume per period_key for today
        var snapshot = new Dictionary<string, int>();
        return snapshot;
    }

    private StationInfo GetStationInfo()
    {
        return new StationInfo
        {
            ProcessorCount = Environment.ProcessorCount,
            MonitorCount = 1, // default — populated from station_profiles
            OsVersion = Environment.OSVersion.VersionString
        };
    }

    /// <summary>
    /// Serializes context and validates through compliance boundary.
    /// Returns null if compliance check fails.
    /// </summary>
    public string? SerializeAndValidate(IntelligenceContext context)
    {
        var json = JsonSerializer.Serialize(context, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });

        var (isClean, violations) = ComplianceBoundary.Validate(json);
        if (!isClean)
            return null; // PHI detected — do not transmit

        return json;
    }
}

public sealed class IntelligenceContext
{
    public string BusinessId { get; set; } = "";
    public DateTimeOffset AssembledAt { get; set; }
    public string Industry { get; set; } = "unknown";
    public Dictionary<string, int> AppUsageSummary { get; set; } = new();
    public Dictionary<string, int> TemporalSnapshot { get; set; } = new();
    public StationInfo StationInfo { get; set; } = new();
}

public sealed class StationInfo
{
    public int ProcessorCount { get; set; }
    public int MonitorCount { get; set; }
    public string OsVersion { get; set; } = "";
}
```

- [ ] **Step 2: Create tests**

```csharp
using SuavoAgent.Core.Intelligence;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Intelligence;

public class ContextAssemblerTests
{
    [Fact]
    public void AssembleContext_ProducesValidPacket()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var context = assembler.AssembleContext("test-pharmacy");

            Assert.Equal("test-pharmacy", context.BusinessId);
            Assert.NotEqual(default, context.AssembledAt);
            Assert.NotNull(context.StationInfo);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void SerializeAndValidate_CleanContext_ReturnsJson()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx2-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var context = assembler.AssembleContext("test-pharmacy");
            var json = assembler.SerializeAndValidate(context);

            Assert.NotNull(json);
            Assert.Contains("test-pharmacy", json);
            Assert.DoesNotContain("SSN", json);
        }
        finally { File.Delete(dbPath); }
    }

    [Fact]
    public void SerializeAndValidate_ContextUnder8KB()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"test-ctx3-{Guid.NewGuid():N}.db");
        try
        {
            using var db = new AgentStateDb(dbPath);
            var assembler = new ContextAssembler(db);
            var context = assembler.AssembleContext("test-pharmacy");
            var json = assembler.SerializeAndValidate(context);

            Assert.NotNull(json);
            Assert.True(json!.Length < 8192, $"Context too large: {json.Length} bytes");
        }
        finally { File.Delete(dbPath); }
    }
}
```

- [ ] **Step 3: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "ContextAssembler" --nologo -v q`
Commit: `git commit -m "feat: add ContextAssembler for sanitized LLM context packets"`

---

### Task 3: Intelligence Context in Heartbeat

**Files:**
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`

- [ ] **Step 1: Add intelligence_context to heartbeat payload**

In `HeartbeatWorker.cs`, find where the heartbeat payload is assembled (the object sent to `_cloudClient.HeartbeatAsync`). Add an `intelligence_context` field that includes the serialized context from ContextAssembler.

Near the top of HeartbeatWorker's fields, add:

```csharp
private readonly ContextAssembler? _contextAssembler;
private DateTimeOffset _lastContextSync = DateTimeOffset.MinValue;
```

In the constructor, after `_stateDb = stateDb;`:

```csharp
_contextAssembler = new ContextAssembler(stateDb);
```

In `ExecuteAsync`, in the heartbeat payload assembly, add the intelligence context every 5 minutes (not every heartbeat — too expensive):

```csharp
// Include intelligence context every 5 minutes
string? intelligenceContext = null;
if (_contextAssembler != null && DateTimeOffset.UtcNow - _lastContextSync > TimeSpan.FromMinutes(5))
{
    var ctx = _contextAssembler.AssembleContext(_options.PharmacyId ?? "unknown");
    intelligenceContext = _contextAssembler.SerializeAndValidate(ctx);
    if (intelligenceContext != null)
        _lastContextSync = DateTimeOffset.UtcNow;
}
```

Then include `intelligenceContext` in the heartbeat payload object.

- [ ] **Step 2: Build + test**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`

- [ ] **Step 3: Commit**

```bash
git commit -m "feat: include sanitized intelligence context in heartbeat every 5 minutes"
```

---

### Task 4: Version Bump + Final Verification

- [ ] **Step 1: Bump version**

In `src/SuavoAgent.Core/appsettings.json`, change `"Version": "3.3.0"` to `"Version": "3.4.0"`.

- [ ] **Step 2: Full build + test**

Run: `dotnet build --nologo -v q` — 0 errors
Run: `dotnet test --nologo -v q` — 0 failures

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: bump version to v3.4.0 — LLM intelligence layer"
```
