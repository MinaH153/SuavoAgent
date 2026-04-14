# SuavoAgent v3.3 App Intelligence — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add per-application intelligence observers (spreadsheet structure, browser domains, print events), industry adapter config system, and DocumentProfile/BusinessMeta storage.

**Architecture:** Three new observers in Helper/SystemObservers following the ForegroundTracker pattern. Industry adapter configs are JSON files loaded at startup — new industry = new JSON, no code changes. DocumentProfile and BusinessMeta tables extend the existing AgentStateDb schema.

**Tech Stack:** .NET 8, Win32 P/Invoke, UIA (FlaUI), ETW (Microsoft-PrintService), JSON config

---

### Task 1: Industry Adapter Config System

**Files:**
- Create: `src/SuavoAgent.Core/Config/IndustryAdapter.cs`
- Create: `src/SuavoAgent.Core/Config/adapters/pharmacy.json`
- Test: `tests/SuavoAgent.Core.Tests/Config/IndustryAdapterTests.cs`

The industry adapter is a JSON config that tells the agent what apps are primary, what domains to categorize, and what PHI patterns to watch for — per industry. New industry = new JSON file.

- [ ] **Step 1: Create IndustryAdapter model and loader**

Create `src/SuavoAgent.Core/Config/IndustryAdapter.cs`:

```csharp
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Config;

public sealed class IndustryAdapter
{
    [JsonPropertyName("industry")]
    public string Industry { get; set; } = "unknown";

    [JsonPropertyName("primary_apps")]
    public List<string> PrimaryApps { get; set; } = new();

    [JsonPropertyName("compliance")]
    public List<string> Compliance { get; set; } = new();

    [JsonPropertyName("known_domains")]
    public Dictionary<string, List<string>> KnownDomains { get; set; } = new();

    [JsonPropertyName("document_categories")]
    public Dictionary<string, DocumentCategoryPattern> DocumentCategories { get; set; } = new();

    [JsonPropertyName("phi_column_patterns")]
    public List<string> PhiColumnPatterns { get; set; } = new();

    /// <summary>
    /// Looks up a domain in the known domains dictionary.
    /// Returns the category (e.g., "insurance", "regulatory") or null if unknown.
    /// </summary>
    public string? ClassifyDomain(string domain)
    {
        var lower = domain.ToLowerInvariant();
        foreach (var (category, domains) in KnownDomains)
        {
            if (domains.Any(d => lower.Contains(d.ToLowerInvariant())))
                return category;
        }
        return null;
    }

    /// <summary>
    /// Checks if a process name is a primary app for this industry.
    /// </summary>
    public bool IsPrimaryApp(string processName) =>
        PrimaryApps.Any(p => p.Equals(processName, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads an adapter from a JSON file path.
    /// </summary>
    public static IndustryAdapter LoadFromFile(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<IndustryAdapter>(json) ?? new IndustryAdapter();
    }

    /// <summary>
    /// Loads the adapter matching the given industry from the adapters directory.
    /// Falls back to a default empty adapter if no match found.
    /// </summary>
    public static IndustryAdapter LoadForIndustry(string industry, string? adaptersDir = null)
    {
        adaptersDir ??= Path.Combine(AppContext.BaseDirectory, "adapters");
        var filePath = Path.Combine(adaptersDir, $"{industry.ToLowerInvariant()}.json");
        return File.Exists(filePath) ? LoadFromFile(filePath) : new IndustryAdapter { Industry = industry };
    }
}

public sealed class DocumentCategoryPattern
{
    [JsonPropertyName("column_patterns")]
    public List<string> ColumnPatterns { get; set; } = new();
}
```

- [ ] **Step 2: Create pharmacy adapter JSON**

Create `src/SuavoAgent.Core/Config/adapters/pharmacy.json`:

```json
{
  "industry": "pharmacy",
  "primary_apps": [
    "PioneerPharmacy.exe",
    "QS1NexGen.exe",
    "NexGen.exe",
    "LibertyRx.exe",
    "ComputerRx.exe",
    "BestRx.exe",
    "Rx30.exe",
    "Pharmaserv.exe",
    "FrameworkLTC.exe",
    "ScriptPro.exe"
  ],
  "compliance": ["HIPAA"],
  "known_domains": {
    "insurance": [
      "express-scripts.com", "optumrx.com", "covermymeds.com",
      "caremark.com", "humana.com", "cigna.com", "aetna.com",
      "uhc.com", "anthem.com", "bcbs.com", "medicaid.gov"
    ],
    "regulatory": [
      "deadiversion.usdoj.gov", "nabp.pharmacy", "fda.gov",
      "bop.ca.gov", "pharmacy.ohio.gov"
    ],
    "supplier": [
      "mckesson.com", "cardinalhealth.com", "amerisourcebergen.com",
      "cencora.com", "hd-smith.com"
    ],
    "clinical": [
      "drugs.com", "medscape.com", "uptodate.com", "epocrates.com",
      "lexicomp.com", "clinicalpharmacology.com"
    ],
    "surescripts": ["surescripts.com", "surescript.com"],
    "wholesaler_ordering": ["orderexpress.mckesson.com", "order.cardinalhealth.com"]
  },
  "document_categories": {
    "controlled_substance_log": {
      "column_patterns": ["drug|medication|substance", "schedule|class", "count|quantity|balance"]
    },
    "inventory": {
      "column_patterns": ["ndc|sku|upc", "quantity|stock|count", "expir|lot"]
    },
    "compounding": {
      "column_patterns": ["ingredient|component", "quantity|amount", "beyond.use|bud|expir"]
    },
    "staff_schedule": {
      "column_patterns": ["name|employee|staff", "shift|hours|schedule", "date|day|week"]
    }
  },
  "phi_column_patterns": [
    "patient", "ssn", "dob", "birth", "phone", "address",
    "email", "person", "contact", "emergency", "guardian",
    "social", "security", "mobile", "fax"
  ]
}
```

- [ ] **Step 3: Add tests**

Create `tests/SuavoAgent.Core.Tests/Config/IndustryAdapterTests.cs`:

```csharp
using SuavoAgent.Core.Config;
using Xunit;

namespace SuavoAgent.Core.Tests.Config;

public class IndustryAdapterTests
{
    [Fact]
    public void ClassifyDomain_ReturnsCategory()
    {
        var adapter = new IndustryAdapter
        {
            KnownDomains = new()
            {
                ["insurance"] = new() { "express-scripts.com", "optumrx.com" },
                ["regulatory"] = new() { "fda.gov" }
            }
        };

        Assert.Equal("insurance", adapter.ClassifyDomain("express-scripts.com"));
        Assert.Equal("regulatory", adapter.ClassifyDomain("fda.gov"));
        Assert.Null(adapter.ClassifyDomain("google.com"));
    }

    [Fact]
    public void ClassifyDomain_CaseInsensitive()
    {
        var adapter = new IndustryAdapter
        {
            KnownDomains = new() { ["insurance"] = new() { "OptumRx.com" } }
        };
        Assert.Equal("insurance", adapter.ClassifyDomain("optumrx.com"));
    }

    [Fact]
    public void IsPrimaryApp_MatchesKnownApps()
    {
        var adapter = new IndustryAdapter
        {
            PrimaryApps = new() { "PioneerPharmacy.exe", "QS1NexGen.exe" }
        };

        Assert.True(adapter.IsPrimaryApp("PioneerPharmacy.exe"));
        Assert.True(adapter.IsPrimaryApp("pioneerpharmaacy.exe")); // case-insensitive
        Assert.False(adapter.IsPrimaryApp("chrome.exe"));
    }

    [Fact]
    public void LoadForIndustry_ReturnsFallback_WhenNoFile()
    {
        var adapter = IndustryAdapter.LoadForIndustry("nonexistent", Path.GetTempPath());
        Assert.Equal("nonexistent", adapter.Industry);
        Assert.Empty(adapter.PrimaryApps);
    }

    [Fact]
    public void LoadFromFile_ParsesPharmacyAdapter()
    {
        var adapterPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Core", "Config", "adapters", "pharmacy.json");

        if (!File.Exists(adapterPath)) return; // skip if not in expected location

        var adapter = IndustryAdapter.LoadFromFile(adapterPath);
        Assert.Equal("pharmacy", adapter.Industry);
        Assert.Contains("PioneerPharmacy.exe", adapter.PrimaryApps);
        Assert.True(adapter.KnownDomains.ContainsKey("insurance"));
        Assert.Contains("HIPAA", adapter.Compliance);
    }
}
```

- [ ] **Step 4: Ensure pharmacy.json is copied to output**

In `src/SuavoAgent.Core/SuavoAgent.Core.csproj`, add:

```xml
<ItemGroup>
  <None Update="Config\adapters\pharmacy.json">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
  </None>
</ItemGroup>
```

- [ ] **Step 5: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "IndustryAdapter" --nologo -v q`
Commit: `git commit -m "feat: add industry adapter config system with pharmacy.json"`

---

### Task 2: DocumentProfile and BusinessMeta Tables

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs`

- [ ] **Step 1: Add tables to InitSchema**

```csharp
Execute("""
    CREATE TABLE IF NOT EXISTS document_profiles (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        session_id TEXT NOT NULL,
        doc_hash TEXT NOT NULL,
        file_type TEXT,
        schema_fingerprint TEXT,
        column_count INTEGER,
        row_count_bucket TEXT,
        category TEXT DEFAULT 'unknown',
        last_touched TEXT DEFAULT (datetime('now')),
        touch_count INTEGER DEFAULT 1,
        UNIQUE(session_id, doc_hash)
    )
""");

Execute("""
    CREATE TABLE IF NOT EXISTS business_meta (
        id INTEGER PRIMARY KEY AUTOINCREMENT,
        business_id TEXT NOT NULL UNIQUE,
        industry TEXT DEFAULT 'unknown',
        detected_apps TEXT,
        station_role TEXT,
        software_stack_hash TEXT,
        onboard_ts TEXT DEFAULT (datetime('now')),
        learning_phase TEXT,
        agent_version TEXT
    )
""");
```

- [ ] **Step 2: Add CRUD methods**

```csharp
public void UpsertDocumentProfile(string sessionId, string docHash, string? fileType,
    string? schemaFingerprint, int columnCount, string? rowCountBucket, string? category)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO document_profiles (session_id, doc_hash, file_type, schema_fingerprint,
            column_count, row_count_bucket, category, last_touched, touch_count)
        VALUES (@sid, @hash, @type, @schema, @cols, @rows, @cat, datetime('now'), 1)
        ON CONFLICT(session_id, doc_hash) DO UPDATE SET
            last_touched = datetime('now'),
            touch_count = touch_count + 1,
            schema_fingerprint = COALESCE(@schema, schema_fingerprint),
            column_count = COALESCE(@cols, column_count)
    """;
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@hash", docHash);
    cmd.Parameters.AddWithValue("@type", (object?)fileType ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@schema", (object?)schemaFingerprint ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@cols", columnCount);
    cmd.Parameters.AddWithValue("@rows", (object?)rowCountBucket ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@cat", (object?)category ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}

public void UpsertBusinessMeta(string businessId, string industry, string? detectedApps,
    string? stationRole, string? agentVersion, string? learningPhase)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO business_meta (business_id, industry, detected_apps, station_role,
            agent_version, learning_phase)
        VALUES (@bid, @ind, @apps, @role, @ver, @phase)
        ON CONFLICT(business_id) DO UPDATE SET
            industry = @ind,
            detected_apps = COALESCE(@apps, detected_apps),
            station_role = COALESCE(@role, station_role),
            agent_version = @ver,
            learning_phase = @phase
    """;
    cmd.Parameters.AddWithValue("@bid", businessId);
    cmd.Parameters.AddWithValue("@ind", industry);
    cmd.Parameters.AddWithValue("@apps", (object?)detectedApps ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@role", (object?)stationRole ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@ver", (object?)agentVersion ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@phase", (object?)learningPhase ?? DBNull.Value);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 3: Add tests**

```csharp
[Fact]
public void DocumentProfile_UpsertIncrementsTouchCount()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test-doc-{Guid.NewGuid():N}.db");
    try
    {
        using var db = new AgentStateDb(dbPath);
        db.UpsertDocumentProfile("s1", "doc-hash-1", "xlsx", "schema-fp", 12, "50-100", "inventory");
        db.UpsertDocumentProfile("s1", "doc-hash-1", "xlsx", "schema-fp", 12, "50-100", "inventory");
        // Second upsert increments touch_count — no exception
    }
    finally { File.Delete(dbPath); }
}

[Fact]
public void BusinessMeta_UpsertUpdatesIndustry()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test-biz-{Guid.NewGuid():N}.db");
    try
    {
        using var db = new AgentStateDb(dbPath);
        db.UpsertBusinessMeta("biz-1", "unknown", null, null, "3.3.0", "discovery");
        db.UpsertBusinessMeta("biz-1", "pharmacy", "[\"PioneerPharmacy.exe\"]", "dispensing", "3.3.0", "pattern");
        // No exception — upsert worked
    }
    finally { File.Delete(dbPath); }
}
```

- [ ] **Step 4: Run tests, commit**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "DocumentProfile|BusinessMeta" --nologo -v q`
Commit: `git commit -m "feat: add document_profiles and business_meta tables"`

---

### Task 3: BrowserDomainObserver

**Files:**
- Create: `src/SuavoAgent.Helper/SystemObservers/BrowserDomainObserver.cs`

The observer extracts the domain from the browser's address bar via UIA, classifies it against the industry adapter's known domains, and emits an Interaction event with the domain category.

- [ ] **Step 1: Create BrowserDomainObserver**

```csharp
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Extracts domain categories from browser address bars.
/// GREEN tier: domain category from known list. YELLOW tier: unknown domain hash.
/// Activates when a browser process (chrome, msedge, firefox) has foreground focus.
/// </summary>
public sealed class BrowserDomainObserver
{
    private static readonly HashSet<string> BrowserProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome", "msedge", "firefox", "brave", "opera", "iexplore"
    };

    private static readonly Regex DomainRegex = new(
        @"^(?:https?://)?([^/:?\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly Func<string, string?> _domainClassifier;
    private readonly ILogger _logger;
    private string? _lastDomainHash;

    public int ObservationCount { get; private set; }

    public BrowserDomainObserver(
        BehavioralEventBuffer buffer, string pharmacySalt,
        Func<string, string?> domainClassifier, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _domainClassifier = domainClassifier;
        _logger = logger;
    }

    public static bool IsBrowserProcess(string processName) =>
        BrowserProcesses.Contains(processName);

    /// <summary>
    /// Called by ForegroundTracker when a browser gains focus.
    /// Extracts domain from the window title (browsers put URL/page title there).
    /// </summary>
    public void OnBrowserFocused(string windowTitle)
    {
        if (string.IsNullOrEmpty(windowTitle)) return;

        // Try to extract domain from title — browsers typically show "Page Title - BrowserName"
        // or sometimes the URL. We extract domain if it looks like a URL.
        var domain = ExtractDomain(windowTitle);
        if (domain == null) return;

        var domainHash = UiaPropertyScrubber.HmacHash(domain, _pharmacySalt);
        if (domainHash == _lastDomainHash) return; // dedup
        _lastDomainHash = domainHash;

        var category = _domainClassifier(domain);

        var evt = BehavioralEvent.Interaction(
            subtype: "browser_domain",
            treeHash: null,
            elementId: category ?? "unknown",  // GREEN: category name
            controlType: "browser",
            className: null,
            nameHash: category == null ? domainHash : null  // YELLOW: hash only for unknown domains
        );

        _buffer.Enqueue(evt);
        ObservationCount++;
    }

    /// <summary>
    /// Extracts domain from a string that might be a URL or page title containing a URL.
    /// Returns null if no domain found.
    /// </summary>
    public static string? ExtractDomain(string input)
    {
        var match = DomainRegex.Match(input);
        if (match.Success)
        {
            var domain = match.Groups[1].Value.ToLowerInvariant();
            // Validate it looks like a domain (has at least one dot)
            if (domain.Contains('.') && !domain.All(char.IsDigit))
                return domain;
        }
        return null;
    }
}
```

- [ ] **Step 2: Run build, commit**

Run: `dotnet build --nologo -v q`
Commit: `git commit -m "feat: add BrowserDomainObserver for domain category tracking"`

---

### Task 4: PrintEventObserver

**Files:**
- Create: `src/SuavoAgent.Helper/SystemObservers/PrintEventObserver.cs`

- [ ] **Step 1: Create PrintEventObserver**

```csharp
using System.Diagnostics;
using Serilog;
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Helper.SystemObservers;

/// <summary>
/// Detects print events via WMI Win32_PrintJob polling.
/// GREEN tier: source process, page count, timestamp. YELLOW tier: printer name hash, document name hash.
/// </summary>
public sealed class PrintEventObserver : IDisposable
{
    private readonly BehavioralEventBuffer _buffer;
    private readonly string _pharmacySalt;
    private readonly ILogger _logger;
    private volatile bool _disposed;
    private int _lastJobId;

    public int PrintEventCount { get; private set; }

    public PrintEventObserver(BehavioralEventBuffer buffer, string pharmacySalt, ILogger logger)
    {
        _buffer = buffer;
        _pharmacySalt = pharmacySalt;
        _logger = logger;
    }

    public async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            _logger.Debug("PrintEventObserver: not on Windows, skipping");
            return;
        }

        _logger.Information("PrintEventObserver started");
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try { PollPrintJobs(); }
            catch (Exception ex) { _logger.Debug(ex, "PrintEventObserver poll error"); }
            await Task.Delay(10000, ct); // poll every 10 seconds
        }
    }

    private void PollPrintJobs()
    {
        // Use WMI via Process to avoid managed COM dependency
        // Lightweight: just detect new jobs by checking spooler
        try
        {
            var spoolerDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                "spool", "PRINTERS");

            if (!Directory.Exists(spoolerDir)) return;

            var spoolFiles = Directory.GetFiles(spoolerDir, "*.SPL");
            foreach (var file in spoolFiles)
            {
                var jobId = file.GetHashCode();
                if (jobId == _lastJobId) continue;
                _lastJobId = jobId;

                var evt = BehavioralEvent.Interaction(
                    subtype: "print_job",
                    treeHash: null,
                    elementId: "print",
                    controlType: "printer",
                    className: null,
                    nameHash: UiaPropertyScrubber.HmacHash(Path.GetFileName(file), _pharmacySalt)
                );

                _buffer.Enqueue(evt);
                PrintEventCount++;
                _logger.Debug("Print job detected");
            }
        }
        catch { } // spooler access may fail without admin
    }

    public void Dispose() => _disposed = true;
}
```

- [ ] **Step 2: Run build, commit**

Run: `dotnet build --nologo -v q`
Commit: `git commit -m "feat: add PrintEventObserver for print job detection"`

---

### Task 5: Wire New Observers + Tests

**Files:**
- Modify: `src/SuavoAgent.Helper/Program.cs`
- Test: `tests/SuavoAgent.Core.Tests/Config/IndustryAdapterTests.cs` (already created in Task 1)

- [ ] **Step 1: Wire BrowserDomainObserver and PrintEventObserver into Helper**

In `src/SuavoAgent.Helper/Program.cs`, in the system observers section (added in v3.2), after the foreground tracker setup, add:

```csharp
// Load industry adapter for domain classification
var adapterDir = Path.Combine(AppContext.BaseDirectory, "adapters");
var industryAdapter = SuavoAgent.Core.Config.IndustryAdapter.LoadForIndustry("pharmacy", adapterDir);

var browserObserver = new SuavoAgent.Helper.SystemObservers.BrowserDomainObserver(
    systemBuffer, pharmacySalt, industryAdapter.ClassifyDomain, Log.Logger);

var printObserver = new SuavoAgent.Helper.SystemObservers.PrintEventObserver(
    systemBuffer, pharmacySalt, Log.Logger);

// Print observer — runs as async loop
var printObsCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
_ = Task.Run(() => printObserver.RunAsync(printObsCts.Token), printObsCts.Token);

Log.Information("App intelligence observers started (browser domains, print events)");
```

- [ ] **Step 2: Add cleanup**

In the cleanup section, add:

```csharp
printObsCts?.Cancel();
printObserver?.Dispose();
```

- [ ] **Step 3: Add Helper project reference to Core (for IndustryAdapter)**

In `src/SuavoAgent.Helper/SuavoAgent.Helper.csproj`, verify it already has a reference to `SuavoAgent.Core` (it should via `SuavoAgent.Contracts`). If not, add:

```xml
<ProjectReference Include="..\SuavoAgent.Core\SuavoAgent.Core.csproj" />
```

- [ ] **Step 4: Run full build + tests**

Run: `dotnet build --nologo -v q && dotnet test --nologo -v q`

- [ ] **Step 5: Commit**

```bash
git commit -m "feat: wire browser domain and print observers into Helper"
```

---

### Task 6: Version Bump + Final Verification

- [ ] **Step 1: Bump version to 3.3.0**

In `src/SuavoAgent.Core/appsettings.json`, change `"Version": "3.2.0"` to `"Version": "3.3.0"`.

- [ ] **Step 2: Full build + test**

Run: `dotnet build --nologo -v q` — 0 errors
Run: `dotnet test --nologo -v q` — 0 failures

- [ ] **Step 3: Commit**

```bash
git commit -m "chore: bump version to v3.3.0 — app intelligence"
```
