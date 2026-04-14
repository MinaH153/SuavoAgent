# Collective Intelligence Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enable pharmacies to learn from each other without sharing PHI — cloud decomposes POMs into collective tables, agents pull seeds at phase transitions through a dual-gate trust model, and confirmation-gated phase acceleration cuts the 30-day learning cycle to ~12 days.

**Architecture:** Agent-side: SeedClient (HTTP), SeedApplicator (state insertion), PhaseGate (4-gate evaluation). Schema: 3 new columns on correlated_actions, 2 new tables (applied_seeds, seed_items). Cloud-side: contract only (endpoints + payload shapes). POM export gains pms_version_hash + seed provenance fields.

**Tech Stack:** .NET 8, C#, SQLCipher (AgentStateDb), xUnit, HMAC-signed HTTP (PostSignedAsync)

**Spec:** `docs/superpowers/specs/2026-04-14-collective-intelligence-design.md`

---

## File Map

### New Files
| File | Responsibility |
|---|---|
| `src/SuavoAgent.Core/Cloud/SeedClient.cs` | POST seed/pull, POST seed/confirm via PostSignedAsync. 304 handling. |
| `src/SuavoAgent.Core/Learning/SeedApplicator.cs` | Apply seed payloads to local state. Local-wins. Source tagging. seed_items insertion. |
| `src/SuavoAgent.Core/Learning/PhaseGate.cs` | 4-gate confirmation evaluation. Returns (ready, gates[]). |
| `src/SuavoAgent.Core/Learning/SeedModels.cs` | DTOs: SeedRequest, SeedResponse, SeedCorrelation, SeedQueryShape, SeedStatusMapping, SeedWorkflowHint, GateResult. |
| `tests/SuavoAgent.Core.Tests/Cloud/SeedClientTests.cs` | Mock HTTP, request payloads, 304 short-circuit. |
| `tests/SuavoAgent.Core.Tests/Learning/SeedApplicatorTests.cs` | Local-wins, source tagging, digest tracking, partial overlap. |
| `tests/SuavoAgent.Core.Tests/Learning/PhaseGateTests.cs` | Each gate independently, all-pass, fallback, rejection exclusion. |

### Modified Files
| File | Change |
|---|---|
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | Schema: 3 new columns on correlated_actions, 2 new tables. Query methods for seed_items and applied_seeds. |
| `src/SuavoAgent.Core/Workers/LearningWorker.cs` | Pull seeds at pattern/model entry. Call PhaseGate.Evaluate() each tick. |
| `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs` | RegisterSeededShapes() — co-occurrence threshold 3→2 for seeded shapes. |
| `src/SuavoAgent.Core/Learning/PomExporter.cs` | Add pms_version_hash to behavioral. Add origin/first_seed_digest/seeded_at to confidenceTrajectory. |
| `src/SuavoAgent.Core/HealthSnapshot.cs` | Add pms_version_hash to behavioral section. |
| `src/SuavoAgent.Core/Program.cs` | Register SeedClient, SeedApplicator, PhaseGate. |

---

## Task 1: Schema — New Columns and Tables

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Test: `tests/SuavoAgent.Core.Tests/State/AgentStateDbSeedTests.cs`

- [ ] **Step 1: Write failing tests for new schema**

Create `tests/SuavoAgent.Core.Tests/State/AgentStateDbSeedTests.cs`:

```csharp
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AgentStateDbSeedTests : IDisposable
{
    private readonly AgentStateDb _db;

    public AgentStateDbSeedTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession("sess-1", "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void CorrelatedAction_HasSourceColumn_DefaultsToLocal()
    {
        _db.UpsertCorrelatedAction("sess-1", "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        var actions = _db.GetCorrelatedActions("sess-1");
        var row = Assert.Single(actions);
        var source = _db.GetCorrelatedActionSource("sess-1", "t1:btn1:q1");
        Assert.Equal("local", source.Source);
        Assert.Null(source.SeedDigest);
        Assert.Null(source.SeededAt);
    }

    [Fact]
    public void InsertAppliedSeed_RoundTrips()
    {
        _db.InsertAppliedSeed("digest-abc", "pattern", "2026-04-14T00:00:00Z", 5, 2);
        var seed = _db.GetAppliedSeed("digest-abc");
        Assert.NotNull(seed);
        Assert.Equal("pattern", seed!.Phase);
        Assert.Equal(5, seed.CorrelationsApplied);
        Assert.Equal(2, seed.CorrelationsSkipped);
    }

    [Fact]
    public void InsertAppliedSeed_DuplicateDigest_Ignored()
    {
        _db.InsertAppliedSeed("digest-abc", "pattern", "2026-04-14T00:00:00Z", 5, 2);
        _db.InsertAppliedSeed("digest-abc", "model", "2026-04-14T01:00:00Z", 3, 1);
        var seed = _db.GetAppliedSeed("digest-abc");
        Assert.Equal("pattern", seed!.Phase); // first insert wins
    }

    [Fact]
    public void InsertSeedItem_And_Confirm()
    {
        _db.InsertSeedItem("digest-abc", "query_shape", "shape-hash-1", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "t1:btn1:q1", "2026-04-14T00:00:00Z");

        var items = _db.GetSeedItems("digest-abc");
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.Null(i.ConfirmedAt));

        _db.ConfirmSeedItem("digest-abc", "query_shape", "shape-hash-1", "2026-04-14T01:00:00Z");

        var confirmed = _db.GetSeedItems("digest-abc");
        var qs = confirmed.First(i => i.ItemType == "query_shape");
        Assert.NotNull(qs.ConfirmedAt);
        Assert.Equal(1, qs.LocalMatchCount);
    }

    [Fact]
    public void RejectSeedItem_ExcludedFromConfirmationRatio()
    {
        _db.InsertSeedItem("digest-abc", "correlation", "key-1", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "key-2", "2026-04-14T00:00:00Z");
        _db.InsertSeedItem("digest-abc", "correlation", "key-3", "2026-04-14T00:00:00Z");

        _db.ConfirmSeedItem("digest-abc", "correlation", "key-1", "2026-04-14T01:00:00Z");
        _db.RejectSeedItem("digest-abc", "correlation", "key-2", "2026-04-14T01:00:00Z");

        var ratio = _db.GetSeedConfirmationRatio("digest-abc");
        // 1 confirmed / 2 non-rejected = 0.5
        Assert.Equal(0.5, ratio, precision: 2);
    }

    [Fact]
    public void SetCorrelatedActionSource_UpdatesFields()
    {
        _db.UpsertCorrelatedAction("sess-1", "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource("sess-1", "t1:btn1:q1", "seed", "digest-abc", "2026-04-14T00:00:00Z");

        var source = _db.GetCorrelatedActionSource("sess-1", "t1:btn1:q1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-abc", source.SeedDigest);
        Assert.Equal("2026-04-14T00:00:00Z", source.SeededAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AgentStateDbSeedTests" -v n`
Expected: FAIL — methods not defined.

- [ ] **Step 3: Add schema migrations to AgentStateDb**

In `src/SuavoAgent.Core/State/AgentStateDb.cs`, add to `InitSchema()` after the existing `TryAlter` calls for correlated_actions (after line ~402):

```csharp
// Spec D: Collective Intelligence — seed provenance on correlated_actions
TryAlter("ALTER TABLE correlated_actions ADD COLUMN source TEXT NOT NULL DEFAULT 'local'");
TryAlter("ALTER TABLE correlated_actions ADD COLUMN seed_digest TEXT");
TryAlter("ALTER TABLE correlated_actions ADD COLUMN seeded_at TEXT");

// Spec D: applied_seeds — tracks which seed packages have been applied
using (var cmd = _conn.CreateCommand())
{
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS applied_seeds (
            seed_digest TEXT PRIMARY KEY,
            phase TEXT NOT NULL,
            applied_at TEXT NOT NULL,
            correlations_applied INTEGER NOT NULL,
            correlations_skipped INTEGER NOT NULL
        )";
    cmd.ExecuteNonQuery();
}

// Spec D: seed_items — tracks individual seed items for confirmation gating
using (var cmd = _conn.CreateCommand())
{
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS seed_items (
            id INTEGER PRIMARY KEY AUTOINCREMENT,
            seed_digest TEXT NOT NULL,
            item_type TEXT NOT NULL,
            item_key TEXT NOT NULL,
            applied_at TEXT NOT NULL,
            confirmed_at TEXT,
            local_match_count INTEGER NOT NULL DEFAULT 0,
            rejected_at TEXT,
            UNIQUE(seed_digest, item_type, item_key)
        )";
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 4: Add query methods to AgentStateDb**

Append to `AgentStateDb.cs`:

```csharp
// --- Spec D: Seed state methods ---

public record CorrelationSource(string Source, string? SeedDigest, string? SeededAt);

public CorrelationSource GetCorrelatedActionSource(string sessionId, string correlationKey)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT source, seed_digest, seeded_at FROM correlated_actions WHERE session_id = @sid AND correlation_key = @key";
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@key", correlationKey);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return new("local", null, null);
    return new(r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2));
}

public void SetCorrelatedActionSource(string sessionId, string correlationKey, string source, string? seedDigest, string? seededAt)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE correlated_actions SET source = @src, seed_digest = @dig, seeded_at = @at WHERE session_id = @sid AND correlation_key = @key";
    cmd.Parameters.AddWithValue("@src", source);
    cmd.Parameters.AddWithValue("@dig", (object?)seedDigest ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@at", (object?)seededAt ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@key", correlationKey);
    cmd.ExecuteNonQuery();
}

public record AppliedSeed(string SeedDigest, string Phase, string AppliedAt, int CorrelationsApplied, int CorrelationsSkipped);

public void InsertAppliedSeed(string seedDigest, string phase, string appliedAt, int correlationsApplied, int correlationsSkipped)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO applied_seeds (seed_digest, phase, applied_at, correlations_applied, correlations_skipped) VALUES (@d, @p, @a, @ca, @cs)";
    cmd.Parameters.AddWithValue("@d", seedDigest);
    cmd.Parameters.AddWithValue("@p", phase);
    cmd.Parameters.AddWithValue("@a", appliedAt);
    cmd.Parameters.AddWithValue("@ca", correlationsApplied);
    cmd.Parameters.AddWithValue("@cs", correlationsSkipped);
    cmd.ExecuteNonQuery();
}

public AppliedSeed? GetAppliedSeed(string seedDigest)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT seed_digest, phase, applied_at, correlations_applied, correlations_skipped FROM applied_seeds WHERE seed_digest = @d";
    cmd.Parameters.AddWithValue("@d", seedDigest);
    using var r = cmd.ExecuteReader();
    if (!r.Read()) return null;
    return new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3), r.GetInt32(4));
}

public record SeedItem(int Id, string SeedDigest, string ItemType, string ItemKey, string AppliedAt, string? ConfirmedAt, int LocalMatchCount, string? RejectedAt);

public void InsertSeedItem(string seedDigest, string itemType, string itemKey, string appliedAt)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO seed_items (seed_digest, item_type, item_key, applied_at) VALUES (@d, @t, @k, @a)";
    cmd.Parameters.AddWithValue("@d", seedDigest);
    cmd.Parameters.AddWithValue("@t", itemType);
    cmd.Parameters.AddWithValue("@k", itemKey);
    cmd.Parameters.AddWithValue("@a", appliedAt);
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<SeedItem> GetSeedItems(string seedDigest)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT id, seed_digest, item_type, item_key, applied_at, confirmed_at, local_match_count, rejected_at FROM seed_items WHERE seed_digest = @d";
    cmd.Parameters.AddWithValue("@d", seedDigest);
    var items = new List<SeedItem>();
    using var r = cmd.ExecuteReader();
    while (r.Read())
        items.Add(new(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
            r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6), r.IsDBNull(7) ? null : r.GetString(7)));
    return items;
}

public void ConfirmSeedItem(string seedDigest, string itemType, string itemKey, string confirmedAt)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = @"
        UPDATE seed_items SET confirmed_at = COALESCE(confirmed_at, @c), local_match_count = local_match_count + 1
        WHERE seed_digest = @d AND item_type = @t AND item_key = @k AND rejected_at IS NULL";
    cmd.Parameters.AddWithValue("@c", confirmedAt);
    cmd.Parameters.AddWithValue("@d", seedDigest);
    cmd.Parameters.AddWithValue("@t", itemType);
    cmd.Parameters.AddWithValue("@k", itemKey);
    cmd.ExecuteNonQuery();
}

public void RejectSeedItem(string seedDigest, string itemType, string itemKey, string rejectedAt)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE seed_items SET rejected_at = @r WHERE seed_digest = @d AND item_type = @t AND item_key = @k";
    cmd.Parameters.AddWithValue("@r", rejectedAt);
    cmd.Parameters.AddWithValue("@d", seedDigest);
    cmd.Parameters.AddWithValue("@t", itemType);
    cmd.Parameters.AddWithValue("@k", itemKey);
    cmd.ExecuteNonQuery();
}

public double GetSeedConfirmationRatio(string seedDigest)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = @"
        SELECT
            CAST(SUM(CASE WHEN confirmed_at IS NOT NULL THEN 1 ELSE 0 END) AS REAL) /
            NULLIF(SUM(CASE WHEN rejected_at IS NULL THEN 1 ELSE 0 END), 0)
        FROM seed_items WHERE seed_digest = @d";
    cmd.Parameters.AddWithValue("@d", seedDigest);
    var result = cmd.ExecuteScalar();
    return result is double d ? d : 0.0;
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AgentStateDbSeedTests" -v n`
Expected: ALL PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/State/AgentStateDbSeedTests.cs
git commit -m "feat(seed): add schema for seed provenance, applied_seeds, seed_items tables"
```

---

## Task 2: Seed DTOs

**Files:**
- Create: `src/SuavoAgent.Core/Learning/SeedModels.cs`

- [ ] **Step 1: Create DTOs**

Create `src/SuavoAgent.Core/Learning/SeedModels.cs`:

```csharp
using System.Text.Json.Serialization;

namespace SuavoAgent.Core.Learning;

public sealed record SeedRequest(
    [property: JsonPropertyName("adapter_type")] string AdapterType,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("contract_fingerprint")] string ContractFingerprint,
    [property: JsonPropertyName("pms_version_hash")] string PmsVersionHash,
    [property: JsonPropertyName("tree_hashes")] IReadOnlyList<string> TreeHashes,
    [property: JsonPropertyName("last_seed_digest")] string? LastSeedDigest);

public sealed record SeedResponse(
    [property: JsonPropertyName("seed_digest")] string SeedDigest,
    [property: JsonPropertyName("seed_version")] int SeedVersion,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("gates_passed")] IReadOnlyList<string> GatesPassed,
    [property: JsonPropertyName("ui_overlap")] UiOverlap? UiOverlap,
    [property: JsonPropertyName("correlations")] IReadOnlyList<SeedCorrelation>? Correlations,
    [property: JsonPropertyName("query_shapes")] IReadOnlyList<SeedQueryShape> QueryShapes,
    [property: JsonPropertyName("status_mappings")] IReadOnlyList<SeedStatusMapping> StatusMappings,
    [property: JsonPropertyName("workflow_hints")] IReadOnlyList<SeedWorkflowHint>? WorkflowHints);

public sealed record UiOverlap(
    [property: JsonPropertyName("matched")] int Matched,
    [property: JsonPropertyName("total_local")] int TotalLocal,
    [property: JsonPropertyName("overlap_ratio")] double OverlapRatio);

public sealed record SeedCorrelation(
    [property: JsonPropertyName("correlation_key")] string CorrelationKey,
    [property: JsonPropertyName("tree_hash")] string TreeHash,
    [property: JsonPropertyName("element_id")] string ElementId,
    [property: JsonPropertyName("control_type")] string ControlType,
    [property: JsonPropertyName("query_shape_hash")] string QueryShapeHash,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("aggregate_success_rate")] double AggregateSuccessRate,
    [property: JsonPropertyName("contributor_count")] int ContributorCount,
    [property: JsonPropertyName("seeded_confidence")] double SeededConfidence);

public sealed record SeedQueryShape(
    [property: JsonPropertyName("query_shape_hash")] string QueryShapeHash,
    [property: JsonPropertyName("parameterized_sql")] string ParameterizedSql,
    [property: JsonPropertyName("tables_referenced")] IReadOnlyList<string> TablesReferenced,
    [property: JsonPropertyName("aggregate_confidence")] double AggregateConfidence,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedStatusMapping(
    [property: JsonPropertyName("status_table")] string StatusTable,
    [property: JsonPropertyName("status_guid")] string StatusGuid,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedWorkflowHint(
    [property: JsonPropertyName("routine_hash")] string RoutineHash,
    [property: JsonPropertyName("path_length")] int PathLength,
    [property: JsonPropertyName("avg_frequency")] double AvgFrequency,
    [property: JsonPropertyName("has_writeback_candidate")] bool HasWritebackCandidate,
    [property: JsonPropertyName("contributor_count")] int ContributorCount);

public sealed record SeedConfirmRequest(
    [property: JsonPropertyName("seed_digest")] string SeedDigest,
    [property: JsonPropertyName("applied_at")] string AppliedAt,
    [property: JsonPropertyName("correlations_applied")] int CorrelationsApplied,
    [property: JsonPropertyName("correlations_skipped")] int CorrelationsSkipped);

public sealed record GateResult(string Name, bool Passed, string Detail);
```

- [ ] **Step 2: Verify build**

Run: `dotnet build src/SuavoAgent.Core`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add src/SuavoAgent.Core/Learning/SeedModels.cs
git commit -m "feat(seed): add DTOs for seed request/response, confirmation, gate results"
```

---

## Task 3: SeedClient

**Files:**
- Create: `src/SuavoAgent.Core/Cloud/SeedClient.cs`
- Create: `tests/SuavoAgent.Core.Tests/Cloud/SeedClientTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/SuavoAgent.Core.Tests/Cloud/SeedClientTests.cs`:

```csharp
using System.Net;
using System.Text.Json;
using SuavoAgent.Core.Cloud;
using SuavoAgent.Core.Learning;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SeedClientTests
{
    private static SeedResponse MakeResponse(string digest = "digest-1", string phase = "pattern") =>
        new(digest, 1, phase, new[] { "schema" }, null,
            null, Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

    [Fact]
    public async Task PullAsync_PostsToCorrectEndpoint()
    {
        string? capturedPath = null;
        var client = new SeedClient(new FakePostSigner((path, _) =>
        {
            capturedPath = path;
            return JsonSerializer.SerializeToElement(MakeResponse());
        }));

        var req = new SeedRequest("PioneerRx", "pattern", "fp-1", "ver-1", Array.Empty<string>(), null);
        await client.PullAsync(req, CancellationToken.None);

        Assert.Equal("/api/agent/seed/pull", capturedPath);
    }

    [Fact]
    public async Task PullAsync_Returns304_ReturnsNull()
    {
        var client = new SeedClient(new FakePostSigner((_, _) => null));

        var req = new SeedRequest("PioneerRx", "pattern", "fp-1", "ver-1", Array.Empty<string>(), "digest-1");
        var result = await client.PullAsync(req, CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    public async Task PullAsync_DeserializesResponse()
    {
        var expected = MakeResponse("digest-abc", "model");
        var client = new SeedClient(new FakePostSigner((_, _) => JsonSerializer.SerializeToElement(expected)));

        var req = new SeedRequest("PioneerRx", "model", "fp-1", "ver-1", new[] { "t1", "t2" }, null);
        var result = await client.PullAsync(req, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal("digest-abc", result!.SeedDigest);
        Assert.Equal("model", result.Phase);
    }

    [Fact]
    public async Task ConfirmAsync_PostsToCorrectEndpoint()
    {
        string? capturedPath = null;
        var client = new SeedClient(new FakePostSigner((path, _) =>
        {
            capturedPath = path;
            return JsonSerializer.SerializeToElement(new { ok = true });
        }));

        await client.ConfirmAsync(new SeedConfirmRequest("d-1", "2026-04-14T00:00:00Z", 5, 2), CancellationToken.None);
        Assert.Equal("/api/agent/seed/confirm", capturedPath);
    }
}

// Minimal fake replacing PostSignedAsync dependency
internal sealed class FakePostSigner : IPostSigner
{
    private readonly Func<string, object, JsonElement?> _handler;
    public FakePostSigner(Func<string, object, JsonElement?> handler) => _handler = handler;
    public Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct) =>
        Task.FromResult(_handler(path, payload));
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedClientTests" -v n`
Expected: FAIL — SeedClient, IPostSigner not defined.

- [ ] **Step 3: Extract IPostSigner interface from SuavoCloudClient**

In `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`, extract an interface and implement it:

```csharp
public interface IPostSigner
{
    Task<JsonElement?> PostSignedAsync(string path, object payload, CancellationToken ct);
}
```

Make `SuavoCloudClient` implement `IPostSigner`. The existing `PostSignedAsync` method already matches the signature — just change the access modifier from `private` to `public` and add `: IPostSigner` to the class declaration.

- [ ] **Step 4: Create SeedClient**

Create `src/SuavoAgent.Core/Cloud/SeedClient.cs`:

```csharp
using System.Text.Json;
using SuavoAgent.Core.Learning;

namespace SuavoAgent.Core.Cloud;

public sealed class SeedClient
{
    private readonly IPostSigner _signer;

    public SeedClient(IPostSigner signer) => _signer = signer;

    public async Task<SeedResponse?> PullAsync(SeedRequest request, CancellationToken ct)
    {
        var result = await _signer.PostSignedAsync("/api/agent/seed/pull", request, ct);
        if (result is null) return null; // 304

        return JsonSerializer.Deserialize<SeedResponse>(result.Value.GetRawText());
    }

    public async Task ConfirmAsync(SeedConfirmRequest request, CancellationToken ct)
    {
        await _signer.PostSignedAsync("/api/agent/seed/confirm", request, ct);
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedClientTests" -v n`
Expected: ALL PASS (4 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Cloud/IPostSigner.cs src/SuavoAgent.Core/Cloud/SeedClient.cs src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs tests/SuavoAgent.Core.Tests/Cloud/SeedClientTests.cs
git commit -m "feat(seed): add SeedClient with IPostSigner interface extraction"
```

---

## Task 4: SeedApplicator

**Files:**
- Create: `src/SuavoAgent.Core/Learning/SeedApplicator.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/SeedApplicatorTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/SuavoAgent.Core.Tests/Learning/SeedApplicatorTests.cs`:

```csharp
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SeedApplicatorTests : IDisposable
{
    private readonly AgentStateDb _db;
    private readonly SeedApplicator _applicator;
    private const string SessionId = "sess-1";

    public SeedApplicatorTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
        _applicator = new SeedApplicator(_db);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void ApplyPatternSeeds_InsertsQueryShapesAsSeedItems()
    {
        var response = new SeedResponse("digest-1", 1, "pattern", new[] { "schema" }, null, null,
            new[] { new SeedQueryShape("qs-1", "UPDATE X SET Y=@p", new[] { "X" }, 0.88, 12) },
            new[] { new SeedStatusMapping("StatusTable", "guid-1", "Completed", 15) },
            new[] { new SeedWorkflowHint("wf-1", 4, 35.0, true, 8) });

        var result = _applicator.ApplyPatternSeeds(SessionId, response);

        Assert.Equal(3, result.ItemsApplied); // 1 query_shape + 1 status_mapping + 1 workflow_hint
        var items = _db.GetSeedItems("digest-1");
        Assert.Equal(3, items.Count);
        Assert.Contains(items, i => i.ItemType == "query_shape" && i.ItemKey == "qs-1");
        Assert.Contains(items, i => i.ItemType == "status_mapping" && i.ItemKey == "guid-1");
        Assert.Contains(items, i => i.ItemType == "workflow_hint" && i.ItemKey == "wf-1");
    }

    [Fact]
    public void ApplyPatternSeeds_RecordsAppliedSeed()
    {
        var response = new SeedResponse("digest-1", 1, "pattern", new[] { "schema" }, null, null,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        _applicator.ApplyPatternSeeds(SessionId, response);

        var applied = _db.GetAppliedSeed("digest-1");
        Assert.NotNull(applied);
        Assert.Equal("pattern", applied!.Phase);
    }

    [Fact]
    public void ApplyPatternSeeds_AlreadyApplied_ReturnsEarly()
    {
        _db.InsertAppliedSeed("digest-1", "pattern", "2026-04-14T00:00:00Z", 3, 0);
        var response = new SeedResponse("digest-1", 1, "pattern", new[] { "schema" }, null, null,
            new[] { new SeedQueryShape("qs-1", "SQL", new[] { "T" }, 0.8, 5) },
            Array.Empty<SeedStatusMapping>(), null);

        var result = _applicator.ApplyPatternSeeds(SessionId, response);

        Assert.Equal(0, result.ItemsApplied);
        Assert.True(result.AlreadyApplied);
    }

    [Fact]
    public void ApplyModelSeeds_InsertsCorrelation_WithSeededConfidence()
    {
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = new SeedResponse("digest-2", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(1, result.CorrelationsApplied);
        Assert.Equal(0, result.CorrelationsSkipped);

        var source = _db.GetCorrelatedActionSource(SessionId, "t1:btn1:q1");
        Assert.Equal("seed", source.Source);
        Assert.Equal("digest-2", source.SeedDigest);

        var items = _db.GetSeedItems("digest-2");
        Assert.Contains(items, i => i.ItemType == "correlation" && i.ItemKey == "t1:btn1:q1");
    }

    [Fact]
    public void ApplyModelSeeds_LocalWins_SkipsExistingCorrelation()
    {
        // Pre-existing local correlation
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");

        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.91, 0.94, 14, 0.6)
        };
        var response = new SeedResponse("digest-2", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        var result = _applicator.ApplyModelSeeds(SessionId, response);

        Assert.Equal(0, result.CorrelationsApplied);
        Assert.Equal(1, result.CorrelationsSkipped);

        // Local source preserved
        var source = _db.GetCorrelatedActionSource(SessionId, "t1:btn1:q1");
        Assert.Equal("local", source.Source);

        // Still recorded in seed_items as rejected
        var items = _db.GetSeedItems("digest-2");
        var item = Assert.Single(items);
        Assert.NotNull(item.RejectedAt);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedApplicatorTests" -v n`
Expected: FAIL — SeedApplicator not defined.

- [ ] **Step 3: Implement SeedApplicator**

Create `src/SuavoAgent.Core/Learning/SeedApplicator.cs`:

```csharp
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

public sealed class SeedApplicator
{
    private readonly AgentStateDb _db;

    public SeedApplicator(AgentStateDb db) => _db = db;

    public record ApplyResult(int ItemsApplied, bool AlreadyApplied);
    public record ModelApplyResult(int CorrelationsApplied, int CorrelationsSkipped, bool AlreadyApplied);

    public ApplyResult ApplyPatternSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, AlreadyApplied: true);

        var now = DateTimeOffset.UtcNow.ToString("o");
        int applied = 0;

        foreach (var qs in response.QueryShapes)
        {
            _db.InsertSeedItem(response.SeedDigest, "query_shape", qs.QueryShapeHash, now);
            applied++;
        }

        foreach (var sm in response.StatusMappings)
        {
            _db.InsertSeedItem(response.SeedDigest, "status_mapping", sm.StatusGuid, now);
            applied++;
        }

        if (response.WorkflowHints is { } hints)
        {
            foreach (var wh in hints)
            {
                _db.InsertSeedItem(response.SeedDigest, "workflow_hint", wh.RoutineHash, now);
                applied++;
            }
        }

        _db.InsertAppliedSeed(response.SeedDigest, "pattern", now, applied, 0);
        return new(applied, AlreadyApplied: false);
    }

    public ModelApplyResult ApplyModelSeeds(string sessionId, SeedResponse response)
    {
        if (_db.GetAppliedSeed(response.SeedDigest) is not null)
            return new(0, 0, AlreadyApplied: true);

        var now = DateTimeOffset.UtcNow.ToString("o");
        int applied = 0, skipped = 0;

        // Apply query shapes and status mappings to seed_items (model phase includes updated versions)
        foreach (var qs in response.QueryShapes)
            _db.InsertSeedItem(response.SeedDigest, "query_shape", qs.QueryShapeHash, now);
        foreach (var sm in response.StatusMappings)
            _db.InsertSeedItem(response.SeedDigest, "status_mapping", sm.StatusGuid, now);

        if (response.Correlations is not { } correlations)
        {
            _db.InsertAppliedSeed(response.SeedDigest, "model", now, 0, 0);
            return new(0, 0, AlreadyApplied: false);
        }

        foreach (var c in correlations)
        {
            _db.InsertSeedItem(response.SeedDigest, "correlation", c.CorrelationKey, now);

            // Local-wins: check if correlation already exists
            var existing = _db.GetCorrelatedActions(sessionId);
            if (existing.Any(e => e.CorrelationKey == c.CorrelationKey))
            {
                _db.RejectSeedItem(response.SeedDigest, "correlation", c.CorrelationKey, now);
                skipped++;
                continue;
            }

            // Insert seeded correlation
            _db.UpsertCorrelatedAction(sessionId, c.CorrelationKey, c.TreeHash, c.ElementId,
                c.ControlType, c.QueryShapeHash, true, string.Join(",", Array.Empty<string>()));
            _db.SetCorrelatedActionSource(sessionId, c.CorrelationKey, "seed", response.SeedDigest, now);
            _db.UpdateCorrelationConfidence(sessionId, c.CorrelationKey, c.SeededConfidence);
            applied++;
        }

        _db.InsertAppliedSeed(response.SeedDigest, "model", now, applied, skipped);
        return new(applied, skipped, AlreadyApplied: false);
    }

    public IReadOnlyList<string> GetSeededShapeHashes(string seedDigest) =>
        _db.GetSeedItems(seedDigest)
            .Where(i => i.ItemType == "query_shape")
            .Select(i => i.ItemKey)
            .ToList();
}
```

- [ ] **Step 4: Add UpdateCorrelationConfidence to AgentStateDb if missing**

In `AgentStateDb.cs`, check if `UpdateCorrelationConfidence` exists. If not, add:

```csharp
public void UpdateCorrelationConfidence(string sessionId, string correlationKey, double confidence)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE correlated_actions SET confidence = @c WHERE session_id = @sid AND correlation_key = @key";
    cmd.Parameters.AddWithValue("@c", confidence);
    cmd.Parameters.AddWithValue("@sid", sessionId);
    cmd.Parameters.AddWithValue("@key", correlationKey);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedApplicatorTests" -v n`
Expected: ALL PASS (5 tests).

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Learning/SeedApplicator.cs tests/SuavoAgent.Core.Tests/Learning/SeedApplicatorTests.cs src/SuavoAgent.Core/State/AgentStateDb.cs
git commit -m "feat(seed): add SeedApplicator with local-wins rule and seed_items tracking"
```

---

## Task 5: PhaseGate

**Files:**
- Create: `src/SuavoAgent.Core/Learning/PhaseGate.cs`
- Create: `tests/SuavoAgent.Core.Tests/Learning/PhaseGateTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/SuavoAgent.Core.Tests/Learning/PhaseGateTests.cs`:

```csharp
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PhaseGateTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public PhaseGateTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Evaluate_NoSeeds_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: null,
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80), // > 72h
            canaryClean: true, unseededPatternCount: 10);

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "seeded_confirmation" && !g.Passed);
    }

    [Fact]
    public void Evaluate_CalendarFloorNotMet_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-24), // < 72h
            canaryClean: true, unseededPatternCount: 10);

        // Seed 80% confirmed
        _db.InsertSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "calendar_floor" && !g.Passed);
    }

    [Fact]
    public void Evaluate_AllGatesPass_Pattern()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 6);

        // 5 seeds, confirm 4 (80%), reject 1 (excluded from denominator)
        for (int i = 0; i < 5; i++)
            _db.InsertSeedItem("d-1", "query_shape", $"qs-{i}", "2026-04-14T00:00:00Z");

        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-1", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-2", "2026-04-14T01:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-3", "2026-04-14T01:00:00Z");
        _db.RejectSeedItem("d-1", "query_shape", "qs-4", "2026-04-14T01:00:00Z");
        // 4 confirmed / 4 non-rejected = 100% ≥ 80%

        var result = gate.Evaluate();
        Assert.True(result.Ready);
        Assert.All(result.Gates, g => Assert.True(g.Passed));
    }

    [Fact]
    public void Evaluate_CanaryWarning_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: false, // canary fired
            unseededPatternCount: 10);

        _db.InsertSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "canary_clean" && !g.Passed);
    }

    [Fact]
    public void Evaluate_UnseededBelowMinimum_ReturnsNotReady()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 3); // < 5

        _db.InsertSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.False(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "unseeded_minimum" && !g.Passed);
    }

    [Fact]
    public void Evaluate_ModelPhase_Uses48hFloor()
    {
        var gate = new PhaseGate(_db, SessionId, "model", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-50), // > 48h
            canaryClean: true, unseededPatternCount: 6);

        _db.InsertSeedItem("d-1", "correlation", "c-1", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "correlation", "c-1", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.True(result.Ready);
        Assert.Contains(result.Gates, g => g.Name == "calendar_floor" && g.Passed);
    }

    [Fact]
    public void Evaluate_ConfirmationBelow50Pct_TriggersAbort()
    {
        var gate = new PhaseGate(_db, SessionId, "pattern", seedDigest: "d-1",
            phaseStartedAt: DateTimeOffset.UtcNow.AddHours(-80),
            canaryClean: true, unseededPatternCount: 10);

        // 4 seeds, only 1 confirmed = 25%
        for (int i = 0; i < 4; i++)
            _db.InsertSeedItem("d-1", "query_shape", $"qs-{i}", "2026-04-14T00:00:00Z");
        _db.ConfirmSeedItem("d-1", "query_shape", "qs-0", "2026-04-14T01:00:00Z");

        var result = gate.Evaluate();
        Assert.True(result.AbortAcceleration);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~PhaseGateTests" -v n`
Expected: FAIL — PhaseGate not defined.

- [ ] **Step 3: Implement PhaseGate**

Create `src/SuavoAgent.Core/Learning/PhaseGate.cs`:

```csharp
using SuavoAgent.Core.State;

namespace SuavoAgent.Core.Learning;

public sealed class PhaseGate
{
    private readonly AgentStateDb _db;
    private readonly string _sessionId;
    private readonly string _phase;
    private readonly string? _seedDigest;
    private readonly DateTimeOffset _phaseStartedAt;
    private readonly bool _canaryClean;
    private readonly int _unseededPatternCount;

    private static readonly TimeSpan PatternFloor = TimeSpan.FromHours(72);
    private static readonly TimeSpan ModelFloor = TimeSpan.FromHours(48);
    private const double ConfirmationThreshold = 0.80;
    private const double AbortThreshold = 0.50;
    private const int UnseededMinimum = 5;

    public PhaseGate(AgentStateDb db, string sessionId, string phase, string? seedDigest,
        DateTimeOffset phaseStartedAt, bool canaryClean, int unseededPatternCount)
    {
        _db = db;
        _sessionId = sessionId;
        _phase = phase;
        _seedDigest = seedDigest;
        _phaseStartedAt = phaseStartedAt;
        _canaryClean = canaryClean;
        _unseededPatternCount = unseededPatternCount;
    }

    public record EvaluateResult(bool Ready, IReadOnlyList<GateResult> Gates, bool AbortAcceleration = false);

    public EvaluateResult Evaluate()
    {
        var floor = _phase == "model" ? ModelFloor : PatternFloor;
        var elapsed = DateTimeOffset.UtcNow - _phaseStartedAt;

        var calendarPassed = elapsed >= floor;
        var canaryPassed = _canaryClean;
        var unseededPassed = _unseededPatternCount >= UnseededMinimum;

        double confirmationRatio = 0.0;
        bool confirmationPassed = false;
        bool abort = false;

        if (_seedDigest is not null)
        {
            confirmationRatio = _db.GetSeedConfirmationRatio(_seedDigest);
            confirmationPassed = confirmationRatio >= ConfirmationThreshold;
            abort = confirmationRatio < AbortThreshold && elapsed > TimeSpan.FromHours(24);
        }

        var gates = new List<GateResult>
        {
            new("seeded_confirmation", confirmationPassed, $"{confirmationRatio:P0} confirmed"),
            new("unseeded_minimum", unseededPassed, $"{_unseededPatternCount} unseeded patterns"),
            new("calendar_floor", calendarPassed, $"{elapsed.TotalHours:F0}h elapsed, {floor.TotalHours}h required"),
            new("canary_clean", canaryPassed, canaryPassed ? "clean" : "warning detected"),
        };

        var ready = confirmationPassed && unseededPassed && calendarPassed && canaryPassed;
        return new(ready, gates, abort);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~PhaseGateTests" -v n`
Expected: ALL PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Learning/PhaseGate.cs tests/SuavoAgent.Core.Tests/Learning/PhaseGateTests.cs
git commit -m "feat(seed): add PhaseGate with 4-gate confirmation evaluation"
```

---

## Task 6: ActionCorrelator — Seeded Shape Hints

**Files:**
- Modify: `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`
- Modify: `tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs`

- [ ] **Step 1: Write failing test for seeded shape threshold reduction**

Add to `tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs`:

```csharp
[Fact]
public void TryCorrelateWithSql_SeededShape_ReachesHighConfidenceAt2Occurrences()
{
    var correlator = new ActionCorrelator(_db, _sessionId);
    correlator.RegisterSeededShapes(new[] { "seeded-shape-hash" });

    var uiTime = DateTimeOffset.UtcNow;
    var sqlTime = uiTime.AddSeconds(0.5);

    // First occurrence → 0.3
    correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
    correlator.TryCorrelateWithSql("seeded-shape-hash", true, "Tbl", sqlTime);

    // Second occurrence → should be 0.6 (threshold reduced from 3 to 2 for seeded shapes)
    correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime.AddSeconds(5));
    correlator.TryCorrelateWithSql("seeded-shape-hash", true, "Tbl", sqlTime.AddSeconds(5));

    var actions = _db.GetCorrelatedActions(_sessionId);
    var match = actions.First(a => a.QueryShapeHash == "seeded-shape-hash");
    Assert.True(match.Confidence >= 0.6);
}

[Fact]
public void TryCorrelateWithSql_NonSeededShape_Requires3ForHighConfidence()
{
    var correlator = new ActionCorrelator(_db, _sessionId);
    // No seeded shapes registered

    var uiTime = DateTimeOffset.UtcNow;
    var sqlTime = uiTime.AddSeconds(0.5);

    // Two occurrences → still 0.3 (needs 3 for 0.6)
    correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime);
    correlator.TryCorrelateWithSql("normal-shape", true, "Tbl", sqlTime);

    correlator.RecordUiEvent("tree1", "btn1", "Button", uiTime.AddSeconds(5));
    correlator.TryCorrelateWithSql("normal-shape", true, "Tbl", sqlTime.AddSeconds(5));

    var actions = _db.GetCorrelatedActions(_sessionId);
    var match = actions.First(a => a.QueryShapeHash == "normal-shape");
    Assert.True(match.Confidence < 0.6);
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~ActionCorrelatorTests" -v n`
Expected: FAIL — RegisterSeededShapes not defined.

- [ ] **Step 3: Add RegisterSeededShapes to ActionCorrelator**

In `src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs`, add:

```csharp
private readonly HashSet<string> _seededShapes = new();

public void RegisterSeededShapes(IEnumerable<string> shapeHashes)
{
    foreach (var h in shapeHashes) _seededShapes.Add(h);
}
```

Then modify the `UpsertCorrelatedAction` call in `TryCorrelateWithSql` to pass the seeded flag. The confidence scaling in `AgentStateDb.UpsertCorrelatedAction` SQL needs a variant:

In `AgentStateDb.cs`, modify the confidence CASE in the upsert (around line 1617):

```csharp
public void UpsertCorrelatedAction(string sessionId, string correlationKey, string treeHash,
    string elementId, string controlType, string queryShapeHash, bool isWrite, string? tablesReferenced,
    bool seededShape = false)
```

Update the SQL CASE:
```sql
CASE WHEN occurrence_count + 1 >= 10 THEN 0.9
     WHEN occurrence_count + 1 >= @threshold THEN 0.6
     ELSE 0.3 END
```
Where `@threshold` is 2 if seededShape, 3 otherwise.

In ActionCorrelator.TryCorrelateWithSql, pass `_seededShapes.Contains(queryShapeHash)` as the seededShape parameter.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~ActionCorrelatorTests" -v n`
Expected: ALL PASS.

- [ ] **Step 5: Run full test suite to check for regressions**

Run: `dotnet test tests/ -v n`
Expected: ALL PASS (existing tests unaffected — default seededShape=false preserves behavior).

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Behavioral/ActionCorrelator.cs src/SuavoAgent.Core/State/AgentStateDb.cs tests/SuavoAgent.Core.Tests/Behavioral/ActionCorrelatorTests.cs
git commit -m "feat(seed): add RegisterSeededShapes — reduce co-occurrence threshold 3→2 for seeded shapes"
```

---

## Task 7: POM Export — Add Provenance Fields

**Files:**
- Modify: `src/SuavoAgent.Core/Learning/PomExporter.cs`
- Modify: `src/SuavoAgent.Core/HealthSnapshot.cs`
- Test: `tests/SuavoAgent.Core.Tests/Learning/PomExporterSeedTests.cs`

- [ ] **Step 1: Write failing tests**

Create `tests/SuavoAgent.Core.Tests/Learning/PomExporterSeedTests.cs`:

```csharp
using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class PomExporterSeedTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";
    private const string PharmacyId = "pharm-1";

    public PomExporterSeedTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, PharmacyId);
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void Export_IncludesPmsVersionHash_InBehavioral()
    {
        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-hash-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var behavioral = doc.RootElement.GetProperty("behavioral");
        Assert.Equal("ver-hash-1", behavioral.GetProperty("pmsVersionHash").GetString());
    }

    [Fact]
    public void Export_ConfidenceTrajectory_IncludesSeedProvenance()
    {
        // Create a seeded correlation
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        _db.SetCorrelatedActionSource(SessionId, "t1:btn1:q1", "seed", "digest-abc", "2026-04-14T00:00:00Z");

        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var trajectory = doc.RootElement.GetProperty("feedback").GetProperty("confidenceTrajectory");

        var item = trajectory.EnumerateArray().First();
        Assert.Equal("seed", item.GetProperty("origin").GetString());
        Assert.Equal("digest-abc", item.GetProperty("firstSeedDigest").GetString());
        Assert.Equal("2026-04-14T00:00:00Z", item.GetProperty("seededAt").GetString());
    }

    [Fact]
    public void Export_LocalCorrelation_OriginIsLocal()
    {
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");
        // source defaults to 'local'

        var exporter = new PomExporter(_db, SessionId, PharmacyId, pmsVersionHash: "ver-1");
        var (json, _) = exporter.Export();
        var doc = JsonDocument.Parse(json);
        var trajectory = doc.RootElement.GetProperty("feedback").GetProperty("confidenceTrajectory");

        var item = trajectory.EnumerateArray().First();
        Assert.Equal("local", item.GetProperty("origin").GetString());
        Assert.False(item.TryGetProperty("firstSeedDigest", out var d) && d.ValueKind != JsonValueKind.Null);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~PomExporterSeedTests" -v n`
Expected: FAIL — PomExporter constructor doesn't accept pmsVersionHash.

- [ ] **Step 3: Modify PomExporter**

In `src/SuavoAgent.Core/Learning/PomExporter.cs`:

1. Add `pmsVersionHash` parameter to constructor
2. Add `pmsVersionHash` to behavioral section output
3. In the feedback section's `confidenceTrajectory` builder, for each correlation read `GetCorrelatedActionSource()` and include `origin`, `firstSeedDigest`, `seededAt`

The key changes:
- Constructor gains `string? pmsVersionHash = null`
- Behavioral anonymous object gains `pmsVersionHash = _pmsVersionHash`
- ConfidenceTrajectory builder reads source info per correlation key and adds the 3 provenance fields

- [ ] **Step 4: Add pms_version_hash to HealthSnapshot**

In `src/SuavoAgent.Core/HealthSnapshot.cs`, add `pmsVersionHash` to the `behavioral` section of the `Take()` method. This requires the PMS version hash to be available — passed in via constructor or options. Add it alongside existing behavioral fields.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~PomExporterSeedTests" -v n`
Expected: ALL PASS (3 tests).

- [ ] **Step 6: Run full test suite**

Run: `dotnet test tests/ -v n`
Expected: ALL PASS. Existing PomExporter tests should still pass (pmsVersionHash defaults to null).

- [ ] **Step 7: Commit**

```bash
git add src/SuavoAgent.Core/Learning/PomExporter.cs src/SuavoAgent.Core/HealthSnapshot.cs tests/SuavoAgent.Core.Tests/Learning/PomExporterSeedTests.cs
git commit -m "feat(seed): add pms_version_hash and seed provenance to POM/heartbeat export"
```

---

## Task 8: LearningWorker Integration

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`
- Modify: `src/SuavoAgent.Core/Program.cs`

- [ ] **Step 1: Register new services in DI**

In `src/SuavoAgent.Core/Program.cs`, after the existing SuavoCloudClient registration:

```csharp
if (!string.IsNullOrWhiteSpace(agentOpts.ApiKey))
{
    builder.Services.AddSingleton(new SuavoCloudClient(agentOpts));
    builder.Services.AddSingleton<IPostSigner>(sp => sp.GetRequiredService<SuavoCloudClient>());
    builder.Services.AddSingleton<SeedClient>();
}
builder.Services.AddSingleton<SeedApplicator>(sp => new SeedApplicator(sp.GetRequiredService<AgentStateDb>()));
```

- [ ] **Step 2: Add seed pull at pattern phase entry in LearningWorker**

In `LearningWorker.cs`, at the point where the agent transitions into pattern phase (after discovery completes), add:

```csharp
// Spec D: Pull schema-gated seeds at pattern phase entry
if (_seedClient is not null)
{
    var seedReq = new SeedRequest(
        _adapterType,
        "pattern",
        _contractFingerprint ?? "",
        _pmsVersionHash ?? "",
        Array.Empty<string>(),
        _lastSeedDigest);
    try
    {
        var seedResp = await _seedClient.PullAsync(seedReq, stoppingToken);
        if (seedResp is not null)
        {
            var result = _applicator.ApplyPatternSeeds(_sessionId, seedResp);
            _lastSeedDigest = seedResp.SeedDigest;
            _correlator?.RegisterSeededShapes(_applicator.GetSeededShapeHashes(seedResp.SeedDigest));
            _log.LogInformation("Applied {Count} pattern seeds from digest {Digest}", result.ItemsApplied, seedResp.SeedDigest);
        }
    }
    catch (Exception ex) { _log.LogWarning(ex, "Seed pull failed at pattern entry — continuing without seeds"); }
}
```

- [ ] **Step 3: Add seed pull at model phase entry**

At model phase entry (after pattern phase completes), add similar code with `phase: "model"` and `treeHashes` populated from the screen catalog, calling `_applicator.ApplyModelSeeds()`.

- [ ] **Step 4: Add PhaseGate evaluation to tick loop**

In the tick loop, when the current phase is pattern or model and seeds have been applied, evaluate PhaseGate:

```csharp
if (_seedDigest is not null && (phase == "pattern" || phase == "model"))
{
    var gate = new PhaseGate(_db, _sessionId, phase, _seedDigest,
        _phaseStartedAt, canaryClean: !_canary.HasWarnings,
        unseededPatternCount: _unseededPatternCount);
    var eval = gate.Evaluate();

    if (eval.AbortAcceleration)
    {
        _log.LogWarning("Seed acceleration aborted — reverting to time-based phase duration");
        _seedDigest = null; // fall back to time-based
    }
    else if (eval.Ready)
    {
        _log.LogInformation("PhaseGate passed — advancing from {Phase}", phase);
        // Transition to next phase
        await AdvancePhaseAsync(stoppingToken);
        // Send confirmation
        if (_seedClient is not null)
            await _seedClient.ConfirmAsync(new SeedConfirmRequest(_seedDigest, DateTimeOffset.UtcNow.ToString("o"),
                _appliedCount, _skippedCount), stoppingToken);
    }
}
```

- [ ] **Step 5: Build and run full test suite**

Run: `dotnet build src/SuavoAgent.Core && dotnet test tests/ -v n`
Expected: Build succeeds, all tests pass.

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/LearningWorker.cs src/SuavoAgent.Core/Program.cs
git commit -m "feat(seed): wire SeedClient + SeedApplicator + PhaseGate into LearningWorker"
```

---

## Task 9: Integration Test — Full Seed Lifecycle

**Files:**
- Create: `tests/SuavoAgent.Core.Tests/Learning/SeedLifecycleIntegrationTests.cs`

- [ ] **Step 1: Write integration test**

```csharp
using System.Text.Json;
using SuavoAgent.Core.Learning;
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.Learning;

public class SeedLifecycleIntegrationTests : IDisposable
{
    private readonly AgentStateDb _db;
    private const string SessionId = "sess-1";

    public SeedLifecycleIntegrationTests()
    {
        _db = new AgentStateDb(":memory:");
        _db.CreateLearningSession(SessionId, "pharm-1");
    }

    public void Dispose() => _db.Dispose();

    [Fact]
    public void FullCycle_PatternSeed_Apply_Confirm_GatePass()
    {
        var applicator = new SeedApplicator(_db);

        // 1. Apply pattern seeds
        var patternResponse = new SeedResponse("digest-p", 1, "pattern", new[] { "schema" }, null, null,
            new[] {
                new SeedQueryShape("qs-1", "UPDATE X SET Y=@p", new[] { "X" }, 0.9, 10),
                new SeedQueryShape("qs-2", "SELECT * FROM Y", new[] { "Y" }, 0.8, 8),
            },
            new[] { new SeedStatusMapping("ST", "guid-1", "Completed", 15) },
            new[] { new SeedWorkflowHint("wf-1", 3, 20, true, 5) });

        var result = applicator.ApplyPatternSeeds(SessionId, patternResponse);
        Assert.Equal(4, result.ItemsApplied);

        // 2. Simulate confirmations (agent independently observes 3 of 4)
        _db.ConfirmSeedItem("digest-p", "query_shape", "qs-1", "2026-04-14T12:00:00Z");
        _db.ConfirmSeedItem("digest-p", "status_mapping", "guid-1", "2026-04-14T12:00:00Z");
        _db.ConfirmSeedItem("digest-p", "workflow_hint", "wf-1", "2026-04-14T12:00:00Z");
        // qs-2 not confirmed yet

        // 3. Evaluate gate — 75% < 80%, should not pass
        var gate1 = new PhaseGate(_db, SessionId, "pattern", "digest-p",
            DateTimeOffset.UtcNow.AddHours(-80), canaryClean: true, unseededPatternCount: 6);
        Assert.False(gate1.Evaluate().Ready);

        // 4. Confirm last one
        _db.ConfirmSeedItem("digest-p", "query_shape", "qs-2", "2026-04-14T13:00:00Z");

        // 5. Re-evaluate — 100% ≥ 80%, should pass
        var gate2 = new PhaseGate(_db, SessionId, "pattern", "digest-p",
            DateTimeOffset.UtcNow.AddHours(-80), canaryClean: true, unseededPatternCount: 6);
        Assert.True(gate2.Evaluate().Ready);
    }

    [Fact]
    public void FullCycle_ModelSeed_LocalWins_RejectDoesNotBlockGate()
    {
        var applicator = new SeedApplicator(_db);

        // Pre-existing local correlation
        _db.UpsertCorrelatedAction(SessionId, "t1:btn1:q1", "t1", "btn1", "Button", "q1", true, "Tbl");

        // Apply model seeds — one overlaps with local
        var correlations = new[] {
            new SeedCorrelation("t1:btn1:q1", "t1", "btn1", "Button", "q1", 0.9, 0.95, 12, 0.6), // will be skipped
            new SeedCorrelation("t2:btn2:q2", "t2", "btn2", "Button", "q2", 0.85, 0.9, 10, 0.55), // will be applied
        };
        var modelResponse = new SeedResponse("digest-m", 2, "model", new[] { "schema", "ui" },
            new UiOverlap(8, 10, 0.8), correlations,
            Array.Empty<SeedQueryShape>(), Array.Empty<SeedStatusMapping>(), null);

        var result = applicator.ApplyModelSeeds(SessionId, modelResponse);
        Assert.Equal(1, result.CorrelationsApplied);
        Assert.Equal(1, result.CorrelationsSkipped);

        // Confirm the one that was applied
        _db.ConfirmSeedItem("digest-m", "correlation", "t2:btn2:q2", "2026-04-14T12:00:00Z");

        // Rejected item (t1:btn1:q1) excluded from denominator → 1/1 = 100%
        var ratio = _db.GetSeedConfirmationRatio("digest-m");
        Assert.Equal(1.0, ratio, precision: 2);

        var gate = new PhaseGate(_db, SessionId, "model", "digest-m",
            DateTimeOffset.UtcNow.AddHours(-50), canaryClean: true, unseededPatternCount: 6);
        Assert.True(gate.Evaluate().Ready);
    }

    [Fact]
    public void Idempotency_SecondApply_NoOps()
    {
        var applicator = new SeedApplicator(_db);
        var response = new SeedResponse("digest-1", 1, "pattern", new[] { "schema" }, null, null,
            new[] { new SeedQueryShape("qs-1", "SQL", new[] { "T" }, 0.8, 5) },
            Array.Empty<SeedStatusMapping>(), null);

        var first = applicator.ApplyPatternSeeds(SessionId, response);
        Assert.Equal(1, first.ItemsApplied);

        var second = applicator.ApplyPatternSeeds(SessionId, response);
        Assert.True(second.AlreadyApplied);
        Assert.Equal(0, second.ItemsApplied);
    }
}
```

- [ ] **Step 2: Run integration tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SeedLifecycleIntegrationTests" -v n`
Expected: ALL PASS (3 tests).

- [ ] **Step 3: Run full test suite**

Run: `dotnet test tests/ -v n`
Expected: ALL PASS.

- [ ] **Step 4: Commit**

```bash
git add tests/SuavoAgent.Core.Tests/Learning/SeedLifecycleIntegrationTests.cs
git commit -m "test(seed): add full seed lifecycle integration tests"
```

---

## Task 10: Final — Full Build Verification

- [ ] **Step 1: Full build**

Run: `dotnet build`
Expected: Build succeeded, 0 warnings related to seed code.

- [ ] **Step 2: Full test suite**

Run: `dotnet test tests/ -v n`
Expected: ALL PASS. Count should be previous total + ~23 new tests.

- [ ] **Step 3: Verify no regressions in existing test files**

Run: `dotnet test tests/ -v n --logger "console;verbosity=detailed" 2>&1 | grep -c "Passed"`
Expected: Count matches or exceeds the prior 441.

- [ ] **Step 4: Commit any cleanup**

If any lint or formatting issues found, fix and commit.
