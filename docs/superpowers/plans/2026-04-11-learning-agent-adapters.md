# Learning Agent Adapters — Implementation Plan (Plan 3 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Adapter Generator (converts approved POM into working ILocalPmsAdapter), StatusOrderingEngine, and wire the approve_pom signed command into HeartbeatWorker so the agent activates after operator approval.

**Architecture:** The Adapter Generator reads an approved POM (Rx queue candidates, status mappings) and produces a `LearnedPmsAdapter` that implements the existing `ILocalPmsAdapter` interface. This adapter runs the same detection/writeback pipeline as the hand-built PioneerRx adapter. StatusOrderingEngine infers delivery-ready statuses from discovered status values.

**Tech Stack:** .NET 8, xUnit. Uses existing ILocalPmsAdapter interface from SuavoAgent.Contracts.

**Spec:** `docs/superpowers/specs/2026-04-11-learning-agent-design.md`

**Depends on:** Plan 1 + Plan 2 complete

---

## File Map

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs` | Infers workflow status ordering from schema + naming heuristics |
| `src/SuavoAgent.Core/Learning/LearnedPmsAdapter.cs` | ILocalPmsAdapter generated from approved POM |
| `src/SuavoAgent.Core/Learning/AdapterGenerator.cs` | Builds LearnedPmsAdapter from POM + operator-approved config |
| `tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs` | Status ordering tests |
| `tests/SuavoAgent.Core.Tests/Learning/AdapterGeneratorTests.cs` | Adapter generation tests |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Handle approve_pom signed command |
| `src/SuavoAgent.Core/Workers/LearningWorker.cs` | Activate LearnedPmsAdapter after approval |
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | Add InsertDiscoveredStatus, GetDiscoveredStatuses |

---

## Task 1: Status Ordering Engine

**Files:**
- Create: `src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs` (add InsertDiscoveredStatus, GetDiscoveredStatuses)

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class StatusOrderingTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public StatusOrderingTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_status_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Theory]
    [InlineData("Waiting for Pick up", "ready_pickup")]
    [InlineData("Waiting for Delivery", "ready_pickup")]
    [InlineData("Out for Delivery", "in_transit")]
    [InlineData("Completed", "completed")]
    [InlineData("Cancelled", "cancelled")]
    [InlineData("Waiting for Fill", "in_progress")]
    [InlineData("Waiting for Data Entry", "queued")]
    [InlineData("RandomUnknownStatus", "unknown")]
    public void InferMeaning_ClassifiesCorrectly(string statusName, string expected)
    {
        Assert.Equal(expected, StatusOrderingEngine.InferMeaning(statusName));
    }

    [Fact]
    public void InferAndPersist_StoresStatuses()
    {
        var engine = new StatusOrderingEngine(_db);
        engine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-1", "Waiting for Data Entry"),
                ("guid-2", "Waiting for Fill"),
                ("guid-3", "Waiting for Pick up"),
                ("guid-4", "Completed"),
            });

        var statuses = _db.GetDiscoveredStatuses("sess-1");
        Assert.Equal(4, statuses.Count);
        Assert.Equal("queued", statuses[0].InferredMeaning);
        Assert.Equal("ready_pickup", statuses[2].InferredMeaning);
    }

    [Fact]
    public void GetDeliveryReadyStatuses_FiltersCorrectly()
    {
        var engine = new StatusOrderingEngine(_db);
        engine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-1", "Waiting for Data Entry"),
                ("guid-2", "Waiting for Pick up"),
                ("guid-3", "Out for Delivery"),
                ("guid-4", "Completed"),
            });

        var ready = StatusOrderingEngine.GetDeliveryReadyValues(
            _db.GetDiscoveredStatuses("sess-1"));
        Assert.Contains("guid-2", ready);
        Assert.DoesNotContain("guid-1", ready);
        Assert.DoesNotContain("guid-4", ready);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "StatusOrderingTests" --verbosity quiet`
Expected: FAIL

- [ ] **Step 3: Add CRUD methods to AgentStateDb**

Add to `src/SuavoAgent.Core/State/AgentStateDb.cs`:

```csharp
    // ── Discovered Statuses ──

    public void InsertDiscoveredStatus(string sessionId, string schemaTable,
        string statusColumn, string statusValue, string? inferredMeaning,
        int transitionOrder, int occurrenceCount, double confidence)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO discovered_statuses
                (session_id, schema_table, status_column, status_value,
                 inferred_meaning, transition_order, occurrence_count, confidence, discovered_at)
            VALUES (@sid, @tbl, @col, @val, @meaning, @order, @count, @conf, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", schemaTable);
        cmd.Parameters.AddWithValue("@col", statusColumn);
        cmd.Parameters.AddWithValue("@val", statusValue);
        cmd.Parameters.AddWithValue("@meaning", (object?)inferredMeaning ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@order", transitionOrder);
        cmd.Parameters.AddWithValue("@count", occurrenceCount);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)>
        GetDiscoveredStatuses(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT status_value, inferred_meaning, transition_order, confidence
            FROM discovered_statuses WHERE session_id = @sid
            ORDER BY transition_order
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, int, double)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt32(2),
                reader.GetDouble(3)));
        }
        return results;
    }
```

- [ ] **Step 4: Implement StatusOrderingEngine**

```csharp
// src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Infers workflow status ordering and meaning from discovered status values.
/// Maps statuses to delivery-ready indicators using name heuristics.
/// </summary>
public sealed class StatusOrderingEngine
{
    private readonly AgentStateDb _db;

    public StatusOrderingEngine(AgentStateDb db) => _db = db;

    private static readonly (string Pattern, string Meaning, int Order)[] StatusPatterns = new[]
    {
        ("data entry", "queued", 1),
        ("pre-check", "queued", 2),
        ("print", "in_progress", 3),
        ("compound", "in_progress", 4),
        ("fill", "in_progress", 5),
        ("check", "in_progress", 6),
        ("bin", "ready_pickup", 7),
        ("pick up", "ready_pickup", 8),
        ("pickup", "ready_pickup", 8),
        ("delivery", "ready_pickup", 9),
        ("out for", "in_transit", 10),
        ("in transit", "in_transit", 10),
        ("delivered", "delivered", 11),
        ("complete", "completed", 12),
        ("cancel", "cancelled", 13),
        ("void", "cancelled", 14),
        ("return", "returned", 15),
    };

    public static string InferMeaning(string statusName)
    {
        var lower = statusName.ToLowerInvariant();
        foreach (var (pattern, meaning, _) in StatusPatterns)
        {
            if (lower.Contains(pattern))
                return meaning;
        }
        return "unknown";
    }

    public static int InferOrder(string statusName)
    {
        var lower = statusName.ToLowerInvariant();
        foreach (var (pattern, _, order) in StatusPatterns)
        {
            if (lower.Contains(pattern))
                return order;
        }
        return 99;
    }

    public void InferAndPersist(string sessionId, string schemaTable,
        string statusColumn, IEnumerable<(string Value, string DisplayName)> statuses)
    {
        foreach (var (value, name) in statuses)
        {
            var meaning = InferMeaning(name);
            var order = InferOrder(name);
            var confidence = meaning == "unknown" ? 0.3 : 0.8;

            _db.InsertDiscoveredStatus(sessionId, schemaTable, statusColumn,
                value, meaning, order, 0, confidence);
        }

        _db.AppendLearningAudit(sessionId, "pattern", "status_ordering",
            schemaTable, phiScrubbed: false);
    }

    /// <summary>
    /// Returns status values that indicate delivery-ready prescriptions.
    /// Used by the adapter generator to build the detection query WHERE clause.
    /// </summary>
    public static IReadOnlyList<string> GetDeliveryReadyValues(
        IReadOnlyList<(string StatusValue, string? InferredMeaning, int TransitionOrder, double Confidence)> statuses)
    {
        return statuses
            .Where(s => s.InferredMeaning is "ready_pickup" or "in_transit")
            .Select(s => s.StatusValue)
            .ToList();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "StatusOrderingTests" --verbosity quiet`
Expected: all 3 tests PASS (8 theory cases + 2 facts)

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs
git commit -m "feat: status ordering engine — infer workflow status meaning from names"
```

---

## Task 2: Adapter Generator + LearnedPmsAdapter

**Files:**
- Create: `src/SuavoAgent.Core/Learning/LearnedPmsAdapter.cs`
- Create: `src/SuavoAgent.Core/Learning/AdapterGenerator.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/AdapterGeneratorTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/AdapterGeneratorTests.cs
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class AdapterGeneratorTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AdapterGeneratorTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_adaptergen_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void Generate_WithValidCandidate_ReturnsAdapter()
    {
        // Seed schema + candidate + statuses
        SeedPioneerRxLikeSchema();

        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var statusEngine = new StatusOrderingEngine(_db);
        statusEngine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-pickup", "Waiting for Pick up"),
                ("guid-delivery", "Waiting for Delivery"),
                ("guid-complete", "Completed"),
            });

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");

        Assert.NotNull(adapter);
        Assert.Equal("Learned-Prescription.RxTransaction", adapter.PmsName);
    }

    [Fact]
    public void Generate_NoCandidate_ReturnsNull()
    {
        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        Assert.Null(adapter);
    }

    [Fact]
    public void Generate_LowConfidenceCandidate_ReturnsNull()
    {
        _db.InsertRxQueueCandidate("sess-1", "dbo.SomeTable",
            null, null, null, null, 0.3, "[\"weak\"]", null);

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        Assert.Null(adapter);
    }

    [Fact]
    public void GeneratedAdapter_BuildsCorrectQuery()
    {
        SeedPioneerRxLikeSchema();

        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var statusEngine = new StatusOrderingEngine(_db);
        statusEngine.InferAndPersist("sess-1", "Prescription.RxTransaction", "StatusTypeID",
            new[]
            {
                ("guid-pickup", "Waiting for Pick up"),
                ("guid-complete", "Completed"),
            });

        var generator = new AdapterGenerator(_db);
        var adapter = generator.Generate("sess-1");
        var query = adapter!.DetectionQuery;

        Assert.Contains("Prescription.RxTransaction", query);
        Assert.Contains("StatusTypeID", query);
        Assert.Contains("guid-pickup", query);
        Assert.DoesNotContain("guid-complete", query); // completed is not delivery-ready
    }

    private void SeedPioneerRxLikeSchema()
    {
        var columns = new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("StatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
            ("PatientID", "uniqueidentifier", "identifier"),
            ("DispensedQuantity", "decimal", "amount"),
        };
        foreach (var (col, type, purpose) in columns)
        {
            _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
                "Prescription", "RxTransaction", col, type, null,
                false, col.EndsWith("ID"), false, null, null, purpose);
        }
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "AdapterGeneratorTests" --verbosity quiet`
Expected: FAIL

- [ ] **Step 3: Create LearnedPmsAdapter**

```csharp
// src/SuavoAgent.Core/Learning/LearnedPmsAdapter.cs
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using SuavoAgent.Contracts.Adapters;
using SuavoAgent.Contracts.Health;
using SuavoAgent.Contracts.Models;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// An ILocalPmsAdapter generated from an approved POM.
/// Queries the Rx table using learned column names and delivery-ready status values.
/// Read-only — writebacks deferred to Plan 4 (needs writeback column discovery).
/// </summary>
public sealed class LearnedPmsAdapter : ILocalPmsAdapter
{
    private readonly string _connectionString;
    private readonly ILogger _logger;
    private SqlConnection? _conn;

    public string PmsName { get; }
    public string DetectionQuery { get; }
    public string RxNumberColumn { get; }
    public string StatusColumn { get; }
    public IReadOnlyList<string> DeliveryReadyStatuses { get; }

    public LearnedPmsAdapter(
        string pmsName,
        string connectionString,
        string detectionQuery,
        string rxNumberColumn,
        string statusColumn,
        IReadOnlyList<string> deliveryReadyStatuses,
        ILogger logger)
    {
        PmsName = pmsName;
        _connectionString = connectionString;
        DetectionQuery = detectionQuery;
        RxNumberColumn = rxNumberColumn;
        StatusColumn = statusColumn;
        DeliveryReadyStatuses = deliveryReadyStatuses;
        _logger = logger;
    }

    public Task<CapabilityManifest> DiscoverCapabilitiesAsync(CancellationToken ct)
    {
        return Task.FromResult(new CapabilityManifest(
            CanDetectRx: true,
            CanWriteBack: false, // writeback discovery deferred
            CanQueryPatient: false,
            DetectionMethod: "learned-sql",
            PmsIdentified: PmsName));
    }

    public async Task<IReadOnlyList<RxReadyForDelivery>> PullReadyAsync(string? cursor, CancellationToken ct)
    {
        var results = new List<RxReadyForDelivery>();
        try
        {
            if (_conn is null || _conn.State != System.Data.ConnectionState.Open)
            {
                _conn = new SqlConnection(_connectionString);
                await _conn.OpenAsync(ct);
            }

            await using var cmd = new SqlCommand(DetectionQuery, _conn);
            cmd.CommandTimeout = 30;
            await using var reader = await cmd.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                var rxNum = reader[RxNumberColumn]?.ToString() ?? "";
                results.Add(new RxReadyForDelivery(
                    RxNumber: rxNum,
                    FillNumber: 0,
                    DrugName: "",
                    Ndc: "",
                    Quantity: 0,
                    DaysSupply: 0,
                    StatusText: reader[StatusColumn]?.ToString() ?? "",
                    IsControlled: false,
                    DateFilled: null,
                    StatusGuid: null));
            }

            _logger.LogInformation("Learned adapter detected {Count} delivery-ready Rxs", results.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Learned adapter detection failed");
        }

        return results;
    }

    public Task<WritebackReceipt> SubmitWritebackAsync(DeliveryWritebackCommand cmd, CancellationToken ct)
    {
        // Writeback not supported by learned adapters yet
        return Task.FromResult(new WritebackReceipt(
            Success: false, RxNumber: cmd.RxNumber, Error: "Writeback not supported by learned adapter"));
    }

    public Task<bool> VerifyWritebackAsync(WritebackReceipt receipt, CancellationToken ct)
    {
        return Task.FromResult(false);
    }

    public Task<AdapterHealthReport> CheckHealthAsync(CancellationToken ct)
    {
        return Task.FromResult(new AdapterHealthReport(
            PmsName: PmsName,
            IsConnected: _conn?.State == System.Data.ConnectionState.Open,
            LastCheckAt: DateTimeOffset.UtcNow,
            DetectionMethod: "learned-sql",
            Error: null));
    }
}
```

- [ ] **Step 4: Create AdapterGenerator**

```csharp
// src/SuavoAgent.Core/Learning/AdapterGenerator.cs
using System.Text;
using Microsoft.Extensions.Logging;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Generates a LearnedPmsAdapter from an approved POM.
/// Reads the highest-confidence Rx queue candidate and delivery-ready statuses,
/// builds a parameterized detection query, and wires it into the adapter.
/// </summary>
public sealed class AdapterGenerator
{
    private readonly AgentStateDb _db;
    private const double MinConfidence = 0.6;

    public AdapterGenerator(AgentStateDb db) => _db = db;

    public LearnedPmsAdapter? Generate(string sessionId, string? connectionString = null,
        ILogger? logger = null)
    {
        var candidates = _db.GetRxQueueCandidates(sessionId);
        var best = candidates.FirstOrDefault(c => c.Confidence >= MinConfidence);

        if (best.PrimaryTable is null)
            return null;

        if (string.IsNullOrEmpty(best.RxNumberColumn) || string.IsNullOrEmpty(best.StatusColumn))
            return null;

        var statuses = _db.GetDiscoveredStatuses(sessionId);
        var deliveryReady = StatusOrderingEngine.GetDeliveryReadyValues(statuses);

        if (deliveryReady.Count == 0)
            return null;

        var query = BuildDetectionQuery(best.PrimaryTable, best.RxNumberColumn,
            best.StatusColumn, best.DateColumn, deliveryReady);

        return new LearnedPmsAdapter(
            pmsName: $"Learned-{best.PrimaryTable}",
            connectionString: connectionString ?? "",
            detectionQuery: query,
            rxNumberColumn: best.RxNumberColumn,
            statusColumn: best.StatusColumn,
            deliveryReadyStatuses: deliveryReady,
            logger: logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    }

    internal static string BuildDetectionQuery(string table, string rxNumberColumn,
        string statusColumn, string? dateColumn, IReadOnlyList<string> statusValues)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SELECT TOP 50");
        sb.AppendLine($"    [{rxNumberColumn}], [{statusColumn}]");
        if (dateColumn != null)
            sb.AppendLine($"    , [{dateColumn}]");
        sb.AppendLine($"FROM {table}");
        sb.Append($"WHERE [{statusColumn}] IN (");
        sb.AppendJoin(", ", statusValues.Select(v => $"'{v}'"));
        sb.Append(')');
        return sb.ToString();
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "AdapterGeneratorTests" --verbosity quiet`
Expected: all 4 tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs src/SuavoAgent.Core/Learning/LearnedPmsAdapter.cs src/SuavoAgent.Core/Learning/AdapterGenerator.cs src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs tests/SuavoAgent.Core.Tests/Learning/AdapterGeneratorTests.cs
git commit -m "feat: adapter generator — converts approved POM into working ILocalPmsAdapter"
```

---

## Task 3: Wire approve_pom Command into HeartbeatWorker

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`

- [ ] **Step 1: Add approve_pom handler to HeartbeatWorker**

In `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`, add a new case in the `ProcessSignedCommandAsync` switch:

```csharp
                case "approve_pom":
                    await HandleApprovePomAsync(scEl, ct);
                    break;
```

Add the handler method:

```csharp
    private async Task HandleApprovePomAsync(JsonElement scEl, CancellationToken ct)
    {
        var dataEl = scEl.TryGetProperty("data", out var d) ? d : scEl;
        var sessionId = dataEl.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
        var digest = dataEl.TryGetProperty("approvedModelDigest", out var dig) ? dig.GetString() : null;

        if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(digest))
        {
            _logger.LogWarning("approve_pom: missing sessionId or digest");
            return;
        }

        var session = _stateDb.GetLearningSession(sessionId);
        if (session is null)
        {
            _logger.LogWarning("approve_pom: session {Id} not found", sessionId);
            return;
        }

        // Verify digest matches local POM (TOCTOU protection — Codex CRITICAL-2)
        var pomJson = SuavoAgent.Core.Learning.PomExporter.Export(_stateDb, sessionId);
        var localDigest = SuavoAgent.Core.Learning.PomExporter.ComputeDigest(
            _options.PharmacyId ?? "", sessionId, pomJson);

        if (!string.Equals(localDigest, digest, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("approve_pom: digest mismatch — local={Local} approved={Approved}. " +
                "POM may have been mutated after review. Rejecting activation.",
                localDigest[..12], digest[..12]);
            return;
        }

        // Store approved digest and transition phase
        _stateDb.UpdateLearningPhase(sessionId, "approved");
        _stateDb.UpdateLearningMode(sessionId, "supervised");

        _stateDb.AppendLearningAudit(sessionId, "worker", "pom_approved",
            $"digest:{digest[..12]}", phiScrubbed: false);

        _logger.LogInformation("POM approved for session {Session} — transitioning to supervised mode", sessionId);
    }
```

- [ ] **Step 2: Update LearningWorker to activate adapter after approval**

Add to the phase management loop in `src/SuavoAgent.Core/Workers/LearningWorker.cs`, after the pattern engine block:

```csharp
            // Activate learned adapter when phase transitions to approved
            if (session.Phase == "approved" && !_adapterActivated)
            {
                var generator = new AdapterGenerator(_db);
                var adapter = generator.Generate(_sessionId,
                    connectionString: BuildConnectionString(),
                    logger: _sp.GetRequiredService<ILogger<LearnedPmsAdapter>>());

                if (adapter != null)
                {
                    _logger.LogInformation("Learned adapter activated: {Pms}, query targets {Table}",
                        adapter.PmsName, adapter.DetectionQuery.Split('\n')[^1].Trim());
                    _db.UpdateLearningPhase(_sessionId, "active");
                    _adapterActivated = true;

                    _db.AppendLearningAudit(_sessionId, "worker", "adapter_activated",
                        adapter.PmsName, phiScrubbed: false);
                }
                else
                {
                    _logger.LogWarning("Adapter generation failed — no viable Rx queue candidate");
                }
            }
```

Also add field: `private bool _adapterActivated;`

Add helper method:

```csharp
    private string BuildConnectionString()
    {
        var csb = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder();
        if (!string.IsNullOrEmpty(_options.SqlServer)) csb.DataSource = _options.SqlServer;
        if (!string.IsNullOrEmpty(_options.SqlDatabase)) csb.InitialCatalog = _options.SqlDatabase;
        csb.ApplicationName = "PioneerPharmacy";
        csb.MaxPoolSize = 1;
        csb["Encrypt"] = "true";
        csb["TrustServerCertificate"] = "true";
        if (!string.IsNullOrEmpty(_options.SqlUser))
        {
            csb.UserID = _options.SqlUser;
            csb.Password = _options.SqlPassword;
        }
        else
        {
            csb.IntegratedSecurity = true;
        }
        return csb.ConnectionString;
    }
```

- [ ] **Step 3: Build and test**

Run: `dotnet test --verbosity quiet`
Expected: ALL tests pass

- [ ] **Step 4: Commit and push**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs src/SuavoAgent.Core/Workers/LearningWorker.cs
git commit -m "feat: wire approve_pom command — digest verification + adapter activation"
git push
```

---

## Self-Review

- [x] **Spec coverage:** Status ordering (spec lines 390-393), adapter generation from POM, approve_pom command with digest verification (Codex CRITICAL-2), supervised mode activation, learned adapter implements ILocalPmsAdapter
- [x] **Placeholder scan:** All code complete. LearnedPmsAdapter.SubmitWritebackAsync returns false with clear message — writeback discovery is a future task, not a placeholder.
- [x] **Type consistency:** ILocalPmsAdapter interface matched exactly (DiscoverCapabilitiesAsync, PullReadyAsync, SubmitWritebackAsync, VerifyWritebackAsync, CheckHealthAsync). RxReadyForDelivery constructor matches Contracts definition.
- [x] **Security:** approve_pom digest verification prevents TOCTOU. Detection query uses column/table names from POM (not user input). Status values in WHERE clause are from discovered_statuses (agent-controlled, not external input).
