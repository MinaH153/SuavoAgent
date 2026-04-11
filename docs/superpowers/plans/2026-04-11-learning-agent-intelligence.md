# Learning Agent Intelligence — Implementation Plan (Plan 2 of 3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the Pattern Engine (Rx queue inference + status ordering), POM Export (sanitized model for cloud upload), and cloud-side Approval API + Dashboard components.

**Architecture:** Pattern Engine runs locally in LearningWorker during the Model phase. It reads discovered_schemas + table_access_patterns + observed_query_shapes to infer Rx queue candidates and status mappings. POM Export sanitizes the model (strips hashes, coarsens timestamps, removes credentials) and uploads via SuavoCloudClient. Cloud-side API receives the POM and exposes it to the Approval Dashboard.

**Tech Stack:** .NET 8 (agent), Next.js + TypeScript (dashboard), Supabase (storage), xUnit.

**Spec:** `docs/superpowers/specs/2026-04-11-learning-agent-design.md`

**Depends on:** Plan 1 complete (POM data model, observers, LearningWorker)

---

## File Map

### New Files (Agent — SuavoAgent repo)
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Core/Learning/RxQueueInferenceEngine.cs` | Scores tables as Rx queue candidates using schema + access heuristics |
| `src/SuavoAgent.Core/Learning/StatusOrderingEngine.cs` | Infers workflow status ordering from query patterns |
| `src/SuavoAgent.Core/Learning/PomExporter.cs` | Sanitizes POM for cloud upload (strips ePHI, coarsens timestamps) |
| `tests/SuavoAgent.Core.Tests/Learning/RxQueueInferenceTests.cs` | Rx queue scoring tests |
| `tests/SuavoAgent.Core.Tests/Learning/StatusOrderingTests.cs` | Status ordering tests |
| `tests/SuavoAgent.Core.Tests/Learning/PomExporterTests.cs` | Export sanitization tests |

### New Files (Cloud — Suavo repo)
| File | Responsibility |
|------|---------------|
| `src/app/api/agent/pom/route.ts` | Receives sanitized POM upload from agent |
| `src/app/api/agent/pom/approve/route.ts` | Operator approves POM, signs digest |
| `src/lib/pom-types.ts` | TypeScript types for POM data structures |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Core/Workers/LearningWorker.cs` | Run Pattern Engine during Model phase, trigger POM export |
| `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs` | Add UploadPomAsync method |
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | Add InsertRxQueueCandidate, GetRxQueueCandidates, InsertDiscoveredStatus, GetDiscoveredStatuses |

---

## Task 1: Rx Queue Inference Engine

**Files:**
- Create: `src/SuavoAgent.Core/Learning/RxQueueInferenceEngine.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/RxQueueInferenceTests.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs` (add InsertRxQueueCandidate, GetRxQueueCandidates)

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/RxQueueInferenceTests.cs
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class RxQueueInferenceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public RxQueueInferenceTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_rxinfer_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void ScoreTable_WithRxNumberAndStatus_HighConfidence()
    {
        // Table with RxNumber + StatusTypeID + DateFilled = strong Rx queue candidate
        SeedSchema("Prescription", "RxTransaction", new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("RxTransactionStatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
            ("PatientID", "uniqueidentifier", "identifier"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        Assert.NotEmpty(candidates);
        var top = candidates[0];
        Assert.Equal("Prescription.RxTransaction", top.PrimaryTable);
        Assert.True(top.Confidence >= 0.6);
        Assert.Equal("RxNumber", top.RxNumberColumn);
        Assert.Equal("RxTransactionStatusTypeID", top.StatusColumn);
    }

    [Fact]
    public void ScoreTable_NoRxColumn_LowConfidence()
    {
        SeedSchema("dbo", "Users", new[]
        {
            ("UserID", "int", "identifier"),
            ("UserName", "varchar", "name"),
            ("CreatedAt", "datetime", "temporal"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        Assert.All(candidates, c => Assert.True(c.Confidence < 0.6));
    }

    [Fact]
    public void ScoreTable_PatientFK_MarkedAsPhiFence()
    {
        SeedSchema("Prescription", "Rx", new[]
        {
            ("RxID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("PatientID", "uniqueidentifier", "identifier"),
            ("StatusID", "int", "status"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        var candidates = engine.InferCandidates("sess-1");

        var rx = candidates.FirstOrDefault(c => c.PrimaryTable == "Prescription.Rx");
        Assert.NotNull(rx);
        Assert.Equal("PatientID", rx.PatientFkColumn);
    }

    [Fact]
    public void InferCandidates_PersistsToDb()
    {
        SeedSchema("Prescription", "RxTransaction", new[]
        {
            ("RxTransactionID", "uniqueidentifier", "identifier"),
            ("RxNumber", "int", "identifier"),
            ("StatusTypeID", "uniqueidentifier", "status"),
            ("DateFilled", "datetime", "temporal"),
        });

        var engine = new RxQueueInferenceEngine(_db);
        engine.InferAndPersist("sess-1");

        var stored = _db.GetRxQueueCandidates("sess-1");
        Assert.NotEmpty(stored);
    }

    private void SeedSchema(string schema, string table, (string col, string type, string purpose)[] columns)
    {
        foreach (var (col, type, purpose) in columns)
        {
            _db.InsertDiscoveredSchema("sess-1", "svr", "TestDB",
                schema, table, col, type, null,
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

Run: `dotnet test --filter "RxQueueInferenceTests" --verbosity quiet`
Expected: FAIL

- [ ] **Step 3: Add CRUD methods to AgentStateDb**

Add to `src/SuavoAgent.Core/State/AgentStateDb.cs`:

```csharp
    // ── Rx Queue Candidates ──

    public void InsertRxQueueCandidate(string sessionId, string primaryTable,
        string? rxNumberColumn, string? statusColumn, string? dateColumn,
        string? patientFkColumn, double confidence, string evidenceJson,
        string? negativeEvidenceJson = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO rx_queue_candidates
                (session_id, primary_table, rx_number_column, status_column,
                 date_column, patient_fk_column, confidence, evidence_json,
                 negative_evidence_json, stability_days, discovered_at)
            VALUES (@sid, @tbl, @rxCol, @statusCol, @dateCol, @patientCol,
                    @conf, @evidence, @negEvidence, 0, @now)
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        cmd.Parameters.AddWithValue("@tbl", primaryTable);
        cmd.Parameters.AddWithValue("@rxCol", (object?)rxNumberColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@statusCol", (object?)statusColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@dateCol", (object?)dateColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@patientCol", (object?)patientFkColumn ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@evidence", evidenceJson);
        cmd.Parameters.AddWithValue("@negEvidence", (object?)negativeEvidenceJson ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<(string PrimaryTable, string? RxNumberColumn, string? StatusColumn,
        string? DateColumn, string? PatientFkColumn, double Confidence, string EvidenceJson)>
        GetRxQueueCandidates(string sessionId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT primary_table, rx_number_column, status_column, date_column,
                   patient_fk_column, confidence, evidence_json
            FROM rx_queue_candidates WHERE session_id = @sid
            ORDER BY confidence DESC
            """;
        cmd.Parameters.AddWithValue("@sid", sessionId);
        var results = new List<(string, string?, string?, string?, string?, double, string)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetDouble(5),
                reader.GetString(6)));
        }
        return results;
    }
```

- [ ] **Step 4: Implement RxQueueInferenceEngine**

```csharp
// src/SuavoAgent.Core/Learning/RxQueueInferenceEngine.cs
using System.Text.Json;
using System.Text.RegularExpressions;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Scores discovered tables as potential Rx queue candidates using
/// schema structure, column names, and access pattern heuristics.
/// Runs locally during the Model phase. Never touches row data.
/// </summary>
public sealed partial class RxQueueInferenceEngine
{
    private readonly AgentStateDb _db;

    [GeneratedRegex(@"rx.*num|prescription.*id|rx.*id|rxnumber", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex RxNumberPattern();

    [GeneratedRegex(@"status|state|workflow", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex StatusPattern();

    [GeneratedRegex(@"patient.*id|person.*id", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex PatientFkPattern();

    public RxQueueInferenceEngine(AgentStateDb db)
    {
        _db = db;
    }

    public record RxCandidate(
        string PrimaryTable,
        string? RxNumberColumn,
        string? StatusColumn,
        string? DateColumn,
        string? PatientFkColumn,
        double Confidence,
        List<string> Evidence,
        List<string> NegativeEvidence);

    public IReadOnlyList<RxCandidate> InferCandidates(string sessionId)
    {
        var schemas = _db.GetDiscoveredSchemas(sessionId);

        // Group columns by schema.table
        var tables = new Dictionary<string, List<(string Column, string DataType, string? Purpose)>>();
        foreach (var col in schemas)
        {
            var key = $"{col.SchemaName}.{col.TableName}";
            if (!tables.ContainsKey(key))
                tables[key] = new();
            tables[key].Add((col.ColumnName, col.DataType, col.InferredPurpose));
        }

        var candidates = new List<RxCandidate>();

        foreach (var (table, columns) in tables)
        {
            double confidence = 0;
            var evidence = new List<string>();
            var negEvidence = new List<string>();
            string? rxCol = null, statusCol = null, dateCol = null, patientCol = null;

            // Rx number column? +0.3
            var rxMatch = columns.FirstOrDefault(c => RxNumberPattern().IsMatch(c.Column));
            if (rxMatch != default)
            {
                confidence += 0.3;
                rxCol = rxMatch.Column;
                evidence.Add($"Column '{rxCol}' matches Rx number pattern");
            }

            // Status column? +0.2
            var statusMatch = columns.FirstOrDefault(c => StatusPattern().IsMatch(c.Column));
            if (statusMatch != default)
            {
                confidence += 0.2;
                statusCol = statusMatch.Column;
                evidence.Add($"Column '{statusCol}' matches status pattern");
            }

            // Temporal column? +0.1
            var dateMatch = columns.FirstOrDefault(c =>
                c.Purpose == "temporal" ||
                c.DataType.Contains("date", StringComparison.OrdinalIgnoreCase) ||
                c.DataType.Contains("time", StringComparison.OrdinalIgnoreCase));
            if (dateMatch != default)
            {
                confidence += 0.1;
                dateCol = dateMatch.Column;
                evidence.Add($"Column '{dateCol}' is temporal ({dateMatch.DataType})");
            }

            // Patient FK? +0.1 (also marks PHI fence)
            var patientMatch = columns.FirstOrDefault(c => PatientFkPattern().IsMatch(c.Column));
            if (patientMatch != default)
            {
                confidence += 0.1;
                patientCol = patientMatch.Column;
                evidence.Add($"Column '{patientCol}' is patient FK (PHI fence)");
            }

            // Negative evidence
            if (columns.Count < 3)
                negEvidence.Add($"Table has only {columns.Count} columns (unusually few for Rx queue)");
            if (table.Contains("Log", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("Audit", StringComparison.OrdinalIgnoreCase) ||
                table.Contains("History", StringComparison.OrdinalIgnoreCase))
                negEvidence.Add("Table name suggests log/audit/history, not active queue");

            if (confidence > 0)
            {
                candidates.Add(new RxCandidate(table, rxCol, statusCol, dateCol,
                    patientCol, Math.Round(confidence, 2), evidence, negEvidence));
            }
        }

        return candidates.OrderByDescending(c => c.Confidence).ToList();
    }

    public void InferAndPersist(string sessionId)
    {
        var candidates = InferCandidates(sessionId);
        foreach (var c in candidates)
        {
            _db.InsertRxQueueCandidate(sessionId, c.PrimaryTable,
                c.RxNumberColumn, c.StatusColumn, c.DateColumn, c.PatientFkColumn,
                c.Confidence,
                JsonSerializer.Serialize(c.Evidence),
                c.NegativeEvidence.Count > 0 ? JsonSerializer.Serialize(c.NegativeEvidence) : null);
        }
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "RxQueueInferenceTests" --verbosity quiet`
Expected: all 4 tests PASS

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Learning/RxQueueInferenceEngine.cs src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Learning/RxQueueInferenceTests.cs
git commit -m "feat: Rx queue inference engine — heuristic scoring from discovered schemas"
```

---

## Task 2: POM Exporter (Sanitized Cloud Upload)

**Files:**
- Create: `src/SuavoAgent.Core/Learning/PomExporter.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/PomExporterTests.cs`

- [ ] **Step 1: Write failing tests**

```csharp
// tests/SuavoAgent.Core.Tests/Learning/PomExporterTests.cs
using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PomExporterTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public PomExporterTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_pomexp_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    [Fact]
    public void Export_ContainsSessionMetadata()
    {
        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        Assert.Equal("sess-1", doc.RootElement.GetProperty("sessionId").GetString());
        Assert.Equal("pharm-1", doc.RootElement.GetProperty("pharmacyId").GetString());
    }

    [Fact]
    public void Export_ContainsProcessCatalog()
    {
        _db.UpsertObservedProcess("sess-1", "PioneerPharmacy.exe",
            @"C:\PioneerRx\PioneerPharmacy.exe", isPmsCandidate: true);

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var procs = doc.RootElement.GetProperty("processes");
        Assert.Equal(1, procs.GetArrayLength());
        Assert.Equal("PioneerPharmacy.exe", procs[0].GetProperty("processName").GetString());
    }

    [Fact]
    public void Export_StripsHashes()
    {
        _db.UpsertObservedProcess("sess-1", "Test.exe", @"C:\Test.exe",
            windowTitleHash: "abc123hash");

        var export = PomExporter.Export(_db, "sess-1");
        Assert.DoesNotContain("abc123hash", export);
    }

    [Fact]
    public void Export_StripsExePaths()
    {
        _db.UpsertObservedProcess("sess-1", "Test.exe",
            @"C:\Program Files\Secret\Test.exe");

        var export = PomExporter.Export(_db, "sess-1");
        Assert.DoesNotContain(@"C:\Program Files\Secret", export);
    }

    [Fact]
    public void Export_ContainsSchemaStructure()
    {
        _db.InsertDiscoveredSchema("sess-1", "svr-hash", "TestDB",
            "Prescription", "Rx", "RxID", "uniqueidentifier",
            16, false, true, false, null, null, "identifier");

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var schemas = doc.RootElement.GetProperty("schemas");
        Assert.True(schemas.GetArrayLength() > 0);

        // Server hash must be stripped
        Assert.DoesNotContain("svr-hash", export);
    }

    [Fact]
    public void Export_ContainsRxCandidates()
    {
        _db.InsertRxQueueCandidate("sess-1", "Prescription.RxTransaction",
            "RxNumber", "StatusTypeID", "DateFilled", "PatientID",
            0.8, "[\"evidence\"]", null);

        var export = PomExporter.Export(_db, "sess-1");
        var doc = JsonDocument.Parse(export);
        var candidates = doc.RootElement.GetProperty("rxQueueCandidates");
        Assert.Equal(1, candidates.GetArrayLength());
    }

    [Fact]
    public void ComputeDigest_Deterministic()
    {
        var json = "{\"test\": true}";
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", json);
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", json);
        Assert.Equal(d1, d2);
    }

    [Fact]
    public void ComputeDigest_DifferentInputs_DifferentDigest()
    {
        var d1 = PomExporter.ComputeDigest("pharm-1", "sess-1", "{\"a\":1}");
        var d2 = PomExporter.ComputeDigest("pharm-1", "sess-1", "{\"a\":2}");
        Assert.NotEqual(d1, d2);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test --filter "PomExporterTests" --verbosity quiet`
Expected: FAIL

- [ ] **Step 3: Implement PomExporter**

```csharp
// src/SuavoAgent.Core/Learning/PomExporter.cs
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

/// <summary>
/// Exports the Pharmacy Operations Model in a de-identified format for cloud upload.
/// Strips all ePHI artifacts: HMAC hashes, exact timestamps, file paths, credentials.
/// The export is suitable for dashboard review and operator approval.
///
/// Per Codex CRITICAL-2: ComputeDigest produces the approved_model_digest that
/// binds the approval to the exact reviewed model.
/// </summary>
public static class PomExporter
{
    public static string Export(AgentStateDb db, string sessionId)
    {
        var session = db.GetLearningSession(sessionId);
        if (session is null)
            throw new InvalidOperationException($"Learning session {sessionId} not found");

        var processes = db.GetObservedProcesses(sessionId);
        var schemas = db.GetDiscoveredSchemas(sessionId);
        var candidates = db.GetRxQueueCandidates(sessionId);

        var export = new
        {
            sessionId,
            pharmacyId = session.Value.PharmacyId,
            phase = session.Value.Phase,
            mode = session.Value.Mode,
            exportedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"), // day granularity only

            processes = processes.Select(p => new
            {
                processName = p.ProcessName,
                // exePath STRIPPED — may reveal pharmacy directory structure
                isPmsCandidate = p.IsPmsCandidate,
                occurrenceCount = p.OccurrenceCount,
                // windowTitleHash STRIPPED
                // windowTitleScrubbed STRIPPED (may contain residual PHI)
            }).ToArray(),

            schemas = schemas.Select(s => new
            {
                // serverHash STRIPPED
                schemaName = s.SchemaName,
                tableName = s.TableName,
                columnName = s.ColumnName,
                dataType = s.DataType,
                isPk = s.IsPk,
                isFk = s.IsFk,
                inferredPurpose = s.InferredPurpose,
            }).ToArray(),

            rxQueueCandidates = candidates.Select(c => new
            {
                primaryTable = c.PrimaryTable,
                rxNumberColumn = c.RxNumberColumn,
                statusColumn = c.StatusColumn,
                dateColumn = c.DateColumn,
                patientFkColumn = c.PatientFkColumn,
                confidence = c.Confidence,
                evidence = c.EvidenceJson,
            }).ToArray(),
        };

        return JsonSerializer.Serialize(export, new JsonSerializerOptions
        {
            WriteIndented = false,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        });
    }

    /// <summary>
    /// Computes SHA-256 digest over {pharmacyId, sessionId, pomJson}.
    /// This digest is signed by the cloud during approval and verified by the agent
    /// before activating the model (TOCTOU protection — Codex CRITICAL-2).
    /// </summary>
    public static string ComputeDigest(string pharmacyId, string sessionId, string pomJson)
    {
        var input = $"{pharmacyId}|{sessionId}|{pomJson}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test --filter "PomExporterTests" --verbosity quiet`
Expected: all 8 tests PASS

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/PomExporter.cs tests/SuavoAgent.Core.Tests/Learning/PomExporterTests.cs
git commit -m "feat: POM exporter — sanitized de-identified model export with digest"
```

---

## Task 3: Cloud Upload + LearningWorker Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`

- [ ] **Step 1: Add UploadPomAsync to SuavoCloudClient**

Add to `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`:

```csharp
    public async Task<string?> UploadPomAsync(string pomJson, string digest, CancellationToken ct)
    {
        var content = new StringContent(
            System.Text.Json.JsonSerializer.Serialize(new { pom = pomJson, digest }),
            System.Text.Encoding.UTF8, "application/json");

        var response = await SendAuthenticatedAsync("/api/agent/pom", content, ct);
        if (response == null) return null;

        var body = await response.Content.ReadAsStringAsync(ct);
        var doc = System.Text.Json.JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("pomId", out var id) ? id.GetString() : null;
    }
```

- [ ] **Step 2: Wire Pattern Engine + POM Export into LearningWorker**

Add to the phase management loop in `src/SuavoAgent.Core/Workers/LearningWorker.cs`, after observer health checks:

```csharp
            // Auto-trigger Pattern Engine when entering Model phase
            if (session.Phase == "model" && !_patternEngineRan)
            {
                _logger.LogInformation("Model phase — running Pattern Engine");
                var inference = new RxQueueInferenceEngine(_db);
                inference.InferAndPersist(_sessionId);
                _patternEngineRan = true;

                _db.AppendLearningAudit(_sessionId, "pattern", "rx_inference",
                    $"candidates:{_db.GetRxQueueCandidates(_sessionId).Count}", phiScrubbed: false);

                // Export and upload POM
                var pomJson = PomExporter.Export(_db, _sessionId);
                var digest = PomExporter.ComputeDigest(
                    _options.PharmacyId ?? "", _sessionId, pomJson);

                var cloudClient = _sp.GetService<SuavoCloudClient>();
                if (cloudClient != null)
                {
                    var pomId = await cloudClient.UploadPomAsync(pomJson, digest, stoppingToken);
                    if (pomId != null)
                        _logger.LogInformation("POM uploaded (id={PomId}, digest={Digest})", pomId, digest[..12]);
                    else
                        _logger.LogWarning("POM upload failed — operator cannot review until upload succeeds");
                }

                _db.AppendLearningAudit(_sessionId, "worker", "pom_exported",
                    $"digest:{digest[..12]}", phiScrubbed: false);
            }
```

Also add field: `private bool _patternEngineRan;`

- [ ] **Step 3: Build and test**

Run: `dotnet test --verbosity quiet`
Expected: all tests PASS

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs src/SuavoAgent.Core/Workers/LearningWorker.cs
git commit -m "feat: wire Pattern Engine + POM export into LearningWorker Model phase"
```

---

## Task 4: Cloud-Side POM API (Suavo Dashboard)

**Files:**
- Create: `src/lib/pom-types.ts` (Suavo repo)
- Create: `src/app/api/agent/pom/route.ts` (Suavo repo)
- Create: `src/app/api/agent/pom/approve/route.ts` (Suavo repo)

- [ ] **Step 1: Create POM types**

```typescript
// src/lib/pom-types.ts
export interface PomProcess {
  processName: string;
  isPmsCandidate: boolean;
  occurrenceCount: number;
}

export interface PomSchema {
  schemaName: string;
  tableName: string;
  columnName: string;
  dataType: string;
  isPk: boolean;
  isFk: boolean;
  inferredPurpose: string | null;
}

export interface PomRxCandidate {
  primaryTable: string;
  rxNumberColumn: string | null;
  statusColumn: string | null;
  dateColumn: string | null;
  patientFkColumn: string | null;
  confidence: number;
  evidence: string;
}

export interface PomExport {
  sessionId: string;
  pharmacyId: string;
  phase: string;
  mode: string;
  exportedAt: string;
  processes: PomProcess[];
  schemas: PomSchema[];
  rxQueueCandidates: PomRxCandidate[];
}

export interface PomApproval {
  pomId: string;
  pharmacyId: string;
  sessionId: string;
  digest: string;
  approvedBy: string;
  approvedAt: string;
}
```

- [ ] **Step 2: Create POM upload endpoint**

```typescript
// src/app/api/agent/pom/route.ts
import { NextRequest, NextResponse } from "next/server";
import { verifyAgentRequest } from "@/lib/agent-auth";
import { getAgentServiceClient } from "@/lib/agent-service-client";
import { checkRateLimit, apiRateLimit } from "@/lib/rate-limit";
import { rateLimitResponse } from "@/lib/api-auth";

export async function POST(request: NextRequest) {
  const rawBody = await request.text();

  const auth = await verifyAgentRequest(request.headers, rawBody);
  if (!auth.valid) {
    return NextResponse.json({ success: false, error: auth.error }, { status: 401 });
  }

  const { pharmacyId, agentId } = auth;

  const rl = await checkRateLimit(`agent-pom:${agentId}`, apiRateLimit, 5, 60_000);
  if (!rl.success) return rateLimitResponse(rl.reset);

  let body: { pom: string; digest: string };
  try {
    body = JSON.parse(rawBody);
  } catch {
    return NextResponse.json({ success: false, error: "Invalid JSON" }, { status: 400 });
  }

  if (!body.pom || !body.digest) {
    return NextResponse.json({ success: false, error: "Missing pom or digest" }, { status: 400 });
  }

  const supabase = getAgentServiceClient();

  const { data, error } = await supabase
    .from("agent_pom_uploads")
    .insert({
      agent_id: agentId,
      pharmacy_id: pharmacyId,
      pom_json: body.pom,
      digest: body.digest,
      status: "pending_review",
    })
    .select("id")
    .single();

  if (error) {
    return NextResponse.json({ success: false, error: "Upload failed" }, { status: 500 });
  }

  return NextResponse.json({ success: true, pomId: data.id });
}
```

- [ ] **Step 3: Create POM approval endpoint**

```typescript
// src/app/api/agent/pom/approve/route.ts
import { NextRequest, NextResponse } from "next/server";
import { createRouteHandlerClient } from "@supabase/auth-helpers-nextjs";
import { cookies } from "next/headers";
import { signCommand } from "@/lib/agent-command-signer";

export async function POST(request: NextRequest) {
  const supabase = createRouteHandlerClient({ cookies });

  const { data: { user } } = await supabase.auth.getUser();
  if (!user) {
    return NextResponse.json({ success: false, error: "Unauthorized" }, { status: 401 });
  }

  const body = await request.json();
  const { pomId } = body;

  if (!pomId) {
    return NextResponse.json({ success: false, error: "Missing pomId" }, { status: 400 });
  }

  // Fetch POM
  const { data: pom, error } = await supabase
    .from("agent_pom_uploads")
    .select("*")
    .eq("id", pomId)
    .single();

  if (error || !pom) {
    return NextResponse.json({ success: false, error: "POM not found" }, { status: 404 });
  }

  // Parse POM to get agent details
  let pomData: { sessionId: string; pharmacyId: string };
  try {
    pomData = JSON.parse(pom.pom_json);
  } catch {
    return NextResponse.json({ success: false, error: "Invalid POM data" }, { status: 400 });
  }

  // Get agent's machine fingerprint for signing the approval command
  const { data: agent } = await supabase
    .from("agent_instances")
    .select("id, config_json")
    .eq("id", pom.agent_id)
    .single();

  const fingerprint = (agent?.config_json as Record<string, unknown>)?.stats
    ? ((agent.config_json as Record<string, unknown>).stats as Record<string, unknown>)?.machine_fingerprint as string ?? ""
    : "";

  // Sign approval command with digest
  const approvalCommand = signCommand("approve_pom", pom.agent_id, fingerprint, {
    sessionId: pomData.sessionId,
    approvedModelDigest: pom.digest,
  });

  // Update POM status
  await supabase
    .from("agent_pom_uploads")
    .update({
      status: "approved",
      approved_by: user.id,
      approved_at: new Date().toISOString(),
    })
    .eq("id", pomId);

  // Store approval command for next heartbeat
  if (approvalCommand) {
    const { data: currentAgent } = await supabase
      .from("agent_instances")
      .select("config_json")
      .eq("id", pom.agent_id)
      .single();

    const existingConfig = currentAgent?.config_json ?? {};

    await supabase
      .from("agent_instances")
      .update({
        config_json: {
          ...(existingConfig as Record<string, unknown>),
          pending_pom_approval: {
            sessionId: pomData.sessionId,
            digest: pom.digest,
          },
        },
      })
      .eq("id", pom.agent_id);
  }

  return NextResponse.json({
    success: true,
    approval: {
      pomId,
      digest: pom.digest,
      approvedBy: user.id,
      signedCommand: approvalCommand,
    },
  });
}
```

- [ ] **Step 4: Type-check**

Run: `cd ~/Documents/Suavo && npx tsc --noEmit`
Expected: 0 errors

- [ ] **Step 5: Commit and push both repos**

```bash
# Suavo repo
cd ~/Documents/Suavo
git add src/lib/pom-types.ts src/app/api/agent/pom/route.ts src/app/api/agent/pom/approve/route.ts
git commit -m "feat: POM upload + approval API endpoints"
git push

# SuavoAgent repo
cd ~/Documents/SuavoAgent
git push
```

---

## Self-Review

- [x] **Spec coverage:** Rx queue inference (spec lines 379-388), status ordering (deferred — needs workflow_events populated by ScheduleObserver in Plan 3), POM export sanitization (spec lines 429-435), approval digest binding (Codex CRITICAL-2), cloud API for POM upload/approval
- [x] **Placeholder scan:** All code complete. No TBD.
- [x] **Type consistency:** AgentStateDb methods match between Task 1 (InsertRxQueueCandidate/GetRxQueueCandidates) and inference engine usage. PomExporter.Export/ComputeDigest consistent.
- [x] **Deferred to Plan 3:** StatusOrderingEngine (needs workflow_events from ScheduleObserver), Adapter Generator, UIA/Schedule observers, approval dashboard UI components
