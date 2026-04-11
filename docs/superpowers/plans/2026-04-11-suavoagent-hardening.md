# SuavoAgent Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Harden SuavoAgent across security, HIPAA compliance, and reliability — 13 items in 4 waves.

**Architecture:** .NET 8 Windows service with three processes (Broker/Core/Helper). Changes touch Core (security, audit, IPC), Adapters.PioneerRx (PHI split), Contracts (model migration), and PowerShell installers (signing). All changes are additive or targeted rewrites — no architectural changes to the three-process model.

**Tech Stack:** .NET 8, xUnit, SQLite/SQLCipher, ECDSA P-256, HMAC-SHA256, named pipes, PowerShell 5.1+

**Spec:** `docs/superpowers/specs/2026-04-11-suavoagent-hardening-design.md`

**Test pattern:** xUnit with temp DB files. See `tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs` for the pattern.

**Build/test commands:**
```bash
cd ~/Documents/SuavoAgent
dotnet build
dotnet test
dotnet test --filter "FullyQualifiedName~ClassName"
```

---

## File Map

### New Files
| File | Responsibility |
|------|---------------|
| `src/SuavoAgent.Core/Cloud/UpdateManifest.cs` | Manifest record with parse + validate |
| `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs` | ECDSA command envelope verification |
| `src/SuavoAgent.Core/State/AuditEntry.cs` | Expanded audit model with event types |
| `src/SuavoAgent.Core/Ipc/IpcPipeClient.cs` | Helper-side pipe client |
| `src/SuavoAgent.Core/Ipc/IpcFraming.cs` | Length-prefixed framing read/write |
| `src/SuavoAgent.Contracts/Models/RxMetadata.cs` | PHI-free Rx detection record |
| `src/SuavoAgent.Contracts/Models/RxPatientDetails.cs` | PHI patient record (on-demand) |
| `tests/SuavoAgent.Core.Tests/Cloud/SelfUpdaterUrlTests.cs` | URL validation tests |
| `tests/SuavoAgent.Core.Tests/Cloud/UpdateManifestTests.cs` | Manifest parse + validate tests |
| `tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs` | Command signing tests |
| `tests/SuavoAgent.Core.Tests/State/AuditChainTests.cs` | Hash chain tests |
| `tests/SuavoAgent.Core.Tests/State/UnsyncedBatchTests.cs` | Batch persistence tests |
| `tests/SuavoAgent.Core.Tests/Ipc/IpcFramingTests.cs` | Framing protocol tests |
| `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs` | PHI split tests |

### Modified Files
| File | Changes |
|------|---------|
| `src/SuavoAgent.Core/Cloud/SelfUpdater.cs` | URL fix, package-level swap, CheckPendingUpdate |
| `src/SuavoAgent.Core/Program.cs` | Bootstrap update check, SQLCipher key passing |
| `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs` | Signed commands, health payload, decommission rewrite, Ed25519 log fix |
| `src/SuavoAgent.Core/Workers/WritebackProcessor.cs` | Audit entries, real IPC, backoff |
| `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs` | Metadata-only sync, batch persistence |
| `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs` | New endpoints, response validation |
| `src/SuavoAgent.Core/State/AgentStateDb.cs` | Schema migrations, hash chain, batch table, nonce table |
| `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs` | Length-prefixed framing, ACL fix, push implementation |
| `src/SuavoAgent.Core/SuavoAgent.Core.csproj` | SQLCipher NuGet swap |
| `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs` | QueryMode, split queries |
| `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs` | QueryMode awareness |
| `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs` | Breaking migration to new records |
| `src/SuavoAgent.Contracts/Models/RxReadyForDelivery.cs` | Deprecated, replaced by RxMetadata |
| `src/SuavoAgent.Broker/SessionWatcher.cs` | Named event for Helper restart |
| `bootstrap.ps1` | Hash verification of downloads |
| `publish.ps1` | Checksum generation + signing |

---

## Wave 1 — Standalone (Parallel)

### Task 1: URL Validation Fix (Item 1)

**Files:**
- Modify: `src/SuavoAgent.Core/Cloud/SelfUpdater.cs:41-46`
- Create: `tests/SuavoAgent.Core.Tests/Cloud/SelfUpdaterUrlTests.cs`

- [ ] **Step 1: Write failing URL validation tests**

Create `tests/SuavoAgent.Core.Tests/Cloud/SelfUpdaterUrlTests.cs`:
```csharp
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SelfUpdaterUrlTests
{
    [Theory]
    [InlineData("https://github.com/MinaH153/SuavoAgent/releases/download/v2.1.0/SuavoAgent.Core.exe", true)]
    [InlineData("https://raw.githubusercontent.com/MinaH153/SuavoAgent/main/file", true)]
    [InlineData("https://objects.githubusercontent.com/abc", true)]
    [InlineData("https://suavollc.com/updates/v2.1.0.exe", true)]
    [InlineData("https://github.com.attacker.com/evil.exe", false)]
    [InlineData("https://evil-github.com/evil.exe", false)]
    [InlineData("https://notssuavollc.com/evil.exe", false)]
    [InlineData("http://github.com/insecure.exe", false)]
    [InlineData("ftp://github.com/ftp.exe", false)]
    [InlineData("https://attacker.com/github.com/evil.exe", false)]
    [InlineData("", false)]
    public void IsAllowedUrl_ValidatesCorrectly(string url, bool expected)
    {
        Assert.Equal(expected, SelfUpdater.IsAllowedUrl(url));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SelfUpdaterUrlTests" -v n`
Expected: FAIL — `SelfUpdater.IsAllowedUrl` doesn't exist yet.

- [ ] **Step 3: Extract URL validation to public static method**

In `src/SuavoAgent.Core/Cloud/SelfUpdater.cs`, add after line 22:

```csharp
private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
{
    "github.com",
    "suavollc.com",
    "raw.githubusercontent.com",
    "objects.githubusercontent.com",
    "github-releases.githubusercontent.com"
};

public static bool IsAllowedUrl(string url)
{
    if (string.IsNullOrEmpty(url)) return false;
    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == "https" && AllowedHosts.Contains(uri.Host);
}
```

Then replace `SelfUpdater.cs:42-46` (the inline `EndsWith` checks) with:
```csharp
if (!IsAllowedUrl(downloadUrl))
{
    logger.LogWarning("Untrusted update URL rejected: {Url}", downloadUrl);
    return false;
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SelfUpdaterUrlTests" -v n`
Expected: All 11 tests PASS.

- [ ] **Step 5: Run full test suite for regression**

Run: `dotnet test`
Expected: All 49 existing tests still pass.

- [ ] **Step 6: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Cloud/SelfUpdater.cs tests/SuavoAgent.Core.Tests/Cloud/SelfUpdaterUrlTests.cs
git commit -m "fix: replace EndsWith URL validation with exact host allowlist

Prevents subdomain bypass (github.com.attacker.com).
Enforces HTTPS scheme. Extracts to testable static method."
```

---

### Task 2: Ed25519 Log Bug Fix (Item 13)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs:171`

- [ ] **Step 1: Fix the log message**

In `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs:171`, change:
```csharp
// BEFORE
_logger.LogWarning("Pending update has no Ed25519 signature — rejecting");
// AFTER
_logger.LogWarning("Pending update has no ECDSA P-256 signature — rejecting");
```

- [ ] **Step 2: Run full test suite**

Run: `dotnet test`
Expected: All tests pass (log message change only).

- [ ] **Step 3: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
git commit -m "fix: correct Ed25519 log message to ECDSA P-256

SelfUpdater uses ECDSA P-256, not Ed25519. Misleading log
could confuse incident response."
```

---

### Task 3: Unsynced Batch Persistence (Item 12c)

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Modify: `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`
- Create: `tests/SuavoAgent.Core.Tests/State/UnsyncedBatchTests.cs`

- [ ] **Step 1: Write failing batch persistence tests**

Create `tests/SuavoAgent.Core.Tests/State/UnsyncedBatchTests.cs`:
```csharp
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class UnsyncedBatchTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public UnsyncedBatchTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_batch_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void InsertBatch_RetrievesPending()
    {
        var payload = """{"v":1,"items":[{"rxNumber":"RX001"}]}""";
        _db.InsertUnsyncedBatch(payload);

        var pending = _db.GetPendingBatches();
        Assert.Single(pending);
        Assert.Equal(payload, pending[0].Payload);
        Assert.Equal(0, pending[0].RetryCount);
        Assert.Equal("pending", pending[0].Status);
    }

    [Fact]
    public void DeleteBatch_RemovesFromPending()
    {
        _db.InsertUnsyncedBatch("payload1");
        var pending = _db.GetPendingBatches();
        _db.DeleteBatch(pending[0].Id);

        Assert.Empty(_db.GetPendingBatches());
    }

    [Fact]
    public void IncrementRetry_UpdatesCount()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];
        _db.IncrementBatchRetry(batch.Id);

        var updated = _db.GetPendingBatches();
        Assert.Single(updated);
        Assert.Equal(1, updated[0].RetryCount);
    }

    [Fact]
    public void IncrementRetry_MovesToDeadLetterAt10()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];

        for (int i = 0; i < 10; i++)
            _db.IncrementBatchRetry(batch.Id);

        Assert.Empty(_db.GetPendingBatches());
        Assert.Equal(1, _db.GetDeadLetterCount());
    }

    [Fact]
    public void PurgeExpiredDeadLetters_RemovesOldEntries()
    {
        _db.InsertUnsyncedBatch("payload1");
        var batch = _db.GetPendingBatches()[0];
        for (int i = 0; i < 10; i++)
            _db.IncrementBatchRetry(batch.Id);

        // Manually backdate expires_at
        _db.BackdateExpiresAt(batch.Id, DateTimeOffset.UtcNow.AddDays(-31));
        _db.PurgeExpiredDeadLetters();

        Assert.Equal(0, _db.GetDeadLetterCount());
    }

    [Fact]
    public void MultipleBatches_ConsecutiveFailuresDontOverwrite()
    {
        _db.InsertUnsyncedBatch("batch1");
        _db.InsertUnsyncedBatch("batch2");

        var pending = _db.GetPendingBatches();
        Assert.Equal(2, pending.Count);
        Assert.Equal("batch1", pending[0].Payload);
        Assert.Equal("batch2", pending[1].Payload);
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~UnsyncedBatchTests" -v n`
Expected: FAIL — `InsertUnsyncedBatch`, `GetPendingBatches` etc. don't exist.

- [ ] **Step 3: Add batch table schema and methods to AgentStateDb**

In `src/SuavoAgent.Core/State/AgentStateDb.cs`, add to `InitSchema()` after the audit_entries CREATE:

```csharp
CREATE TABLE IF NOT EXISTS unsynced_batches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    payload TEXT NOT NULL,
    created_at TEXT NOT NULL,
    retry_count INTEGER DEFAULT 0,
    status TEXT DEFAULT 'pending',
    expires_at TEXT NOT NULL
);
```

Add these methods after `GetAuditEntryCount()`:

```csharp
public void InsertUnsyncedBatch(string payload)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO unsynced_batches (payload, created_at, retry_count, status, expires_at)
        VALUES (@payload, @now, 0, 'pending', @expires)
        """;
    var now = DateTimeOffset.UtcNow;
    cmd.Parameters.AddWithValue("@payload", payload);
    cmd.Parameters.AddWithValue("@now", now.ToString("o"));
    cmd.Parameters.AddWithValue("@expires", now.AddDays(30).ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(long Id, string Payload, int RetryCount, string Status)> GetPendingBatches()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT id, payload, retry_count, status FROM unsynced_batches
        WHERE status = 'pending' ORDER BY created_at ASC
        """;
    var results = new List<(long, string, int, string)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        results.Add((reader.GetInt64(0), reader.GetString(1), reader.GetInt32(2), reader.GetString(3)));
    return results;
}

public void DeleteBatch(long id)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "DELETE FROM unsynced_batches WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();
}

public void IncrementBatchRetry(long id)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        UPDATE unsynced_batches
        SET retry_count = retry_count + 1,
            status = CASE WHEN retry_count + 1 >= 10 THEN 'dead_letter' ELSE 'pending' END
        WHERE id = @id
        """;
    cmd.Parameters.AddWithValue("@id", id);
    cmd.ExecuteNonQuery();
}

public int GetDeadLetterCount()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT COUNT(*) FROM unsynced_batches WHERE status = 'dead_letter'";
    return Convert.ToInt32(cmd.ExecuteScalar());
}

public void BackdateExpiresAt(long id, DateTimeOffset expires)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE unsynced_batches SET expires_at = @expires WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@expires", expires.ToString("o"));
    cmd.ExecuteNonQuery();
}

public void PurgeExpiredDeadLetters()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        DELETE FROM unsynced_batches
        WHERE status = 'dead_letter' AND expires_at < @now
        """;
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 4: Run batch tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~UnsyncedBatchTests" -v n`
Expected: All 6 tests PASS.

- [ ] **Step 5: Run full suite for regression**

Run: `dotnet test`
Expected: All tests pass (existing + 6 new).

- [ ] **Step 6: Wire RxDetectionWorker to use batch persistence**

In `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`:
- Remove the `_unsyncedBatch` field (line 18)
- In the detection loop, after pulling Rx metadata, serialize to JSON and call `_stateDb.InsertUnsyncedBatch(json)`
- In the sync section, read from `_stateDb.GetPendingBatches()`, POST each, delete on success, increment retry on failure
- On startup, call `_stateDb.PurgeExpiredDeadLetters()`

The key change: replace in-memory batch with DB-backed batch. Crash recovery is automatic — pending batches survive restart.

- [ ] **Step 7: Run full suite**

Run: `dotnet test`
Expected: All tests pass. `RxDetectionWorkerTests` may need adjustment if they reference the removed field.

- [ ] **Step 8: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/State/AgentStateDb.cs src/SuavoAgent.Core/Workers/RxDetectionWorker.cs tests/SuavoAgent.Core.Tests/State/UnsyncedBatchTests.cs
git commit -m "feat: persist unsynced Rx batches to SQLite

Replaces in-memory _unsyncedBatch field with DB-backed storage.
Crash recovery replays pending batches on startup.
Dead letter after 10 retries, 30-day TTL with auto-purge."
```

---

### Task 4: SQLCipher Implementation (Item 8)

**Files:**
- Modify: `src/SuavoAgent.Core/SuavoAgent.Core.csproj:16`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Modify: `src/SuavoAgent.Core/Program.cs`

- [ ] **Step 1: Swap NuGet package**

In `src/SuavoAgent.Core/SuavoAgent.Core.csproj:16`, change:
```xml
<!-- BEFORE -->
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.11" />
<!-- AFTER -->
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlcipher" Version="2.1.11" />
```

- [ ] **Step 2: Verify build succeeds with SQLCipher**

Run: `dotnet build`
Expected: Build succeeds. If SQLCipher native lib fails on macOS, check if platform-specific package is needed.

- [ ] **Step 3: Add migration and secure delete to AgentStateDb**

Add to `AgentStateDb.cs` as a new static method:
```csharp
public static void MigrateToEncrypted(string dbPath, string password, ILogger logger)
{
    var bakPath = dbPath + ".bak";
    var encPath = dbPath + ".enc";

    // Check for leftover .bak from crashed migration
    if (File.Exists(bakPath))
    {
        SecureDelete(bakPath);
        logger.LogInformation("Cleaned up unencrypted backup from previous migration");
    }

    // Test if DB is already encrypted
    try
    {
        using var testDb = new AgentStateDb(dbPath, password);
        testDb.Dispose();
        return; // Already encrypted
    }
    catch { /* Not encrypted — proceed with migration */ }

    // Test if DB is unencrypted
    try
    {
        using var plainDb = new AgentStateDb(dbPath);
        plainDb.Dispose();
    }
    catch
    {
        logger.LogWarning("state.db is neither encrypted nor plain — recreating");
        SecureDelete(dbPath);
        return; // Will be created fresh on next open
    }

    logger.LogInformation("Migrating state.db to encrypted storage...");

    // Create encrypted copy
    using (var plain = new SqliteConnection($"Data Source={dbPath}"))
    {
        plain.Open();
        using var encConn = new SqliteConnection($"Data Source={encPath};Password={password}");
        encConn.Open();
        plain.BackupDatabase(encConn);
    }

    // Swap: plain → bak, enc → main
    File.Move(dbPath, bakPath);
    File.Move(encPath, dbPath);

    // Verify encrypted DB works
    using (var verify = new AgentStateDb(dbPath, password))
    {
        verify.GetAuditEntryCount(); // Quick sanity check
    }

    // Securely delete unencrypted backup
    SecureDelete(bakPath);
    logger.LogInformation("Migration complete — state.db is now encrypted");
}

private static void SecureDelete(string path)
{
    if (!File.Exists(path)) return;
    var length = new FileInfo(path).Length;
    using (var fs = File.OpenWrite(path))
    {
        var zeros = new byte[4096];
        var remaining = length;
        while (remaining > 0)
        {
            var chunk = (int)Math.Min(remaining, zeros.Length);
            fs.Write(zeros, 0, chunk);
            remaining -= chunk;
        }
    }
    File.Delete(path);
}
```

- [ ] **Step 4: Run all tests with SQLCipher bundle**

Run: `dotnet test`
Expected: All tests pass. Tests create temp DBs without passwords — SQLCipher is backward-compatible with unencrypted DBs.

- [ ] **Step 5: Verify cross-compile to win-x64**

Run: `dotnet publish src/SuavoAgent.Core -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o /tmp/suavo-publish-test`
Expected: Build succeeds. If it fails, check `feedback-cross-compile-sqlclient.md` for precedent — may need `RuntimeIdentifier` pinning.

- [ ] **Step 6: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/SuavoAgent.Core.csproj src/SuavoAgent.Core/State/AgentStateDb.cs
git commit -m "feat: implement SQLCipher encryption at rest

Swaps SQLitePCLRaw.bundle_e_sqlite3 for bundle_e_sqlcipher.
Adds migration from unencrypted to encrypted DB with
secure delete of unencrypted backup (HIPAA 164.312(a)(2)(iv))."
```

---

## Wave 2 — After Wave 1

### Task 5: Audit Chain Integrity (Item 7)

**Files:**
- Create: `src/SuavoAgent.Core/State/AuditEntry.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Modify: `src/SuavoAgent.Core/Workers/WritebackProcessor.cs:129-143`
- Create: `tests/SuavoAgent.Core.Tests/State/AuditChainTests.cs`

- [ ] **Step 1: Create AuditEntry model**

Create `src/SuavoAgent.Core/State/AuditEntry.cs`:
```csharp
namespace SuavoAgent.Core.State;

public record AuditEntry(
    string TaskId,
    string EventType,      // writeback_transition, phi_access, rx_sync, decommission, command_received
    string FromState,
    string ToState,
    string Trigger,
    string? CommandId = null,
    string? RequesterId = null,
    string? RxNumber = null);
```

- [ ] **Step 2: Write failing audit chain tests**

Create `tests/SuavoAgent.Core.Tests/State/AuditChainTests.cs`:
```csharp
using SuavoAgent.Core.State;
using Xunit;

namespace SuavoAgent.Core.Tests.State;

public class AuditChainTests : IDisposable
{
    private readonly string _dbPath;
    private readonly AgentStateDb _db;

    public AuditChainTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"suavo_audit_test_{Guid.NewGuid():N}.db");
        _db = new AgentStateDb(_dbPath);
    }

    [Fact]
    public void AppendAuditEntry_ComputesChainedHash()
    {
        var hash = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));

        Assert.NotNull(hash);
        Assert.NotEmpty(hash);
    }

    [Fact]
    public void AuditChain_SecondEntryLinksToFirst()
    {
        var hash1 = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        var hash2 = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void VerifyAuditChain_ValidChain_ReturnsTrue()
    {
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "InProgress", "VerifyPending", "WriteComplete"));

        Assert.True(_db.VerifyAuditChain());
    }

    [Fact]
    public void VerifyAuditChain_EmptyChain_ReturnsTrue()
    {
        Assert.True(_db.VerifyAuditChain());
    }

    [Fact]
    public void VerifyAuditChain_TamperedEntry_ReturnsFalse()
    {
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Claimed", "InProgress", "StartUia"));

        // Tamper with first entry
        _db.TamperAuditEntryForTest(1, "Queued", "ManualReview");

        Assert.False(_db.VerifyAuditChain());
    }

    [Fact]
    public void AuditChain_DeterministicSeed()
    {
        var db2Path = _dbPath + ".2";
        using var db2 = new AgentStateDb(db2Path);

        var hash1 = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));
        var hash2 = db2.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "writeback_transition", "Queued", "Claimed", "Claim"));

        // Same input on fresh chains → same hash (deterministic seed)
        Assert.Equal(hash1, hash2);

        db2.Dispose();
        try { File.Delete(db2Path); } catch { }
    }

    [Fact]
    public void AppendChainedAuditEntry_PhiAccessEvent()
    {
        var hash = _db.AppendChainedAuditEntry(new AuditEntry(
            "task-1", "phi_access", "", "", "",
            CommandId: "cmd-123", RequesterId: "pharmacist-1", RxNumber: "RX001"));

        Assert.NotNull(hash);
        Assert.Equal(1, _db.GetAuditEntryCount());
    }

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_dbPath); } catch { }
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AuditChainTests" -v n`
Expected: FAIL — `AppendChainedAuditEntry`, `VerifyAuditChain` don't exist.

- [ ] **Step 4: Implement chained audit in AgentStateDb**

Add schema migration in `InitSchema()` — add columns to audit_entries:
```sql
-- After existing CREATE TABLE audit_entries, add migration:
ALTER TABLE audit_entries ADD COLUMN event_type TEXT DEFAULT 'writeback_transition';
ALTER TABLE audit_entries ADD COLUMN command_id TEXT;
ALTER TABLE audit_entries ADD COLUMN requester_id TEXT;
ALTER TABLE audit_entries ADD COLUMN rx_number TEXT;
```

Wrap in try/catch since ALTER fails if column already exists (idempotent migration).

Add methods:
```csharp
private static readonly string AuditChainSeed =
    Convert.ToBase64String(SHA256.HashData("SuavoAgent-audit-chain-v1"u8));

public string? GetLastAuditHash()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT prev_hash FROM audit_entries ORDER BY id DESC LIMIT 1";
    var result = cmd.ExecuteScalar();
    return result is DBNull or null ? null : (string)result;
}

public static string ComputeAuditHash(string prevHash, string taskId, string eventType,
    string fromState, string toState, string trigger, string timestamp)
{
    var payload = $"{prevHash}|{taskId}|{eventType}|{fromState}|{toState}|{trigger}|{timestamp}";
    return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}

public string AppendChainedAuditEntry(AuditEntry entry)
{
    var prevHash = GetLastAuditHash() ?? AuditChainSeed;
    var timestamp = DateTimeOffset.UtcNow.ToString("o");
    var newHash = ComputeAuditHash(prevHash, entry.TaskId, entry.EventType,
        entry.FromState, entry.ToState, entry.Trigger, timestamp);

    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        INSERT INTO audit_entries (task_id, from_state, to_state, trigger, timestamp, prev_hash,
                                   event_type, command_id, requester_id, rx_number)
        VALUES (@taskId, @from, @to, @trigger, @timestamp, @prevHash,
                @eventType, @commandId, @requesterId, @rxNumber)
        """;
    cmd.Parameters.AddWithValue("@taskId", entry.TaskId);
    cmd.Parameters.AddWithValue("@from", entry.FromState);
    cmd.Parameters.AddWithValue("@to", entry.ToState);
    cmd.Parameters.AddWithValue("@trigger", entry.Trigger);
    cmd.Parameters.AddWithValue("@timestamp", timestamp);
    cmd.Parameters.AddWithValue("@prevHash", newHash);
    cmd.Parameters.AddWithValue("@eventType", entry.EventType);
    cmd.Parameters.AddWithValue("@commandId", (object?)entry.CommandId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@requesterId", (object?)entry.RequesterId ?? DBNull.Value);
    cmd.Parameters.AddWithValue("@rxNumber", (object?)entry.RxNumber ?? DBNull.Value);
    cmd.ExecuteNonQuery();
    return newHash;
}

public bool VerifyAuditChain()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT task_id, event_type, from_state, to_state, trigger, timestamp, prev_hash
        FROM audit_entries ORDER BY id ASC
        """;
    using var reader = cmd.ExecuteReader();

    var expectedHash = AuditChainSeed;
    while (reader.Read())
    {
        var taskId = reader.GetString(0);
        var eventType = reader.IsDBNull(1) ? "writeback_transition" : reader.GetString(1);
        var from = reader.GetString(2);
        var to = reader.GetString(3);
        var trigger = reader.GetString(4);
        var timestamp = reader.GetString(5);
        var storedHash = reader.IsDBNull(6) ? null : reader.GetString(6);

        var computed = ComputeAuditHash(expectedHash, taskId, eventType, from, to, trigger, timestamp);
        if (storedHash != computed) return false;
        expectedHash = computed;
    }
    return true;
}

// Test helper only — tamper an audit entry to test chain verification
internal void TamperAuditEntryForTest(int id, string fromState, string toState)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE audit_entries SET from_state = @from, to_state = @to WHERE id = @id";
    cmd.Parameters.AddWithValue("@id", id);
    cmd.Parameters.AddWithValue("@from", fromState);
    cmd.Parameters.AddWithValue("@to", toState);
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 5: Run audit chain tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~AuditChainTests" -v n`
Expected: All 7 tests PASS.

- [ ] **Step 6: Wire WritebackProcessor to call AppendChainedAuditEntry**

In `src/SuavoAgent.Core/Workers/WritebackProcessor.cs`, at line 131 (inside `OnStateChanged` callback), after `UpsertWritebackState`:

```csharp
_stateDb.AppendChainedAuditEntry(new AuditEntry(
    taskId, "writeback_transition",
    previousState.ToString(), newState.ToString(), trigger.ToString()));
```

Add `using SuavoAgent.Core.State;` if not already imported.

- [ ] **Step 7: Run full suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 8: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/State/AuditEntry.cs src/SuavoAgent.Core/State/AgentStateDb.cs \
  src/SuavoAgent.Core/Workers/WritebackProcessor.cs tests/SuavoAgent.Core.Tests/State/AuditChainTests.cs
git commit -m "feat: implement chained audit trail with hash verification

SHA256 chain links every audit entry. Covers writeback transitions
and PHI access events. VerifyAuditChain() validates full chain on
startup. WritebackProcessor now writes audit entries on every
state change. (HIPAA 164.312(b), 164.312(c)(1))"
```

---

### Task 6: PHI Minimization — SQL Split (Item 6a)

**Files:**
- Create: `src/SuavoAgent.Contracts/Models/RxMetadata.cs`
- Create: `src/SuavoAgent.Contracts/Models/RxPatientDetails.cs`
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`
- Modify: `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`
- Create: `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs`

- [ ] **Step 1: Create RxMetadata record (PHI-free)**

Create `src/SuavoAgent.Contracts/Models/RxMetadata.cs`:
```csharp
namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Prescription metadata with zero patient data.
/// Used for detection sync (every 5 min). PHI-free by design.
/// </summary>
public record RxMetadata(
    string RxNumber,
    string? DrugName,
    string? Ndc,
    DateTime? DateFilled,
    int? Quantity,
    Guid StatusGuid);
```

- [ ] **Step 2: Create RxPatientDetails record (PHI, on-demand)**

Create `src/SuavoAgent.Contracts/Models/RxPatientDetails.cs`:
```csharp
namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Patient details fetched on-demand for approved deliveries.
/// Contains PHI — requires signed command authorization.
/// </summary>
public record RxPatientDetails(
    string RxNumber,
    string? FirstName,
    string? LastInitial,
    string? Phone,
    string? Address1,
    string? Address2,
    string? City,
    string? State,
    string? Zip);
```

- [ ] **Step 3: Add QueryMode enum to PioneerRxConstants**

In `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`, add:
```csharp
public enum QueryMode
{
    Detection,    // PHI blocklist enforced — no patient columns
    PatientFetch  // Blocklist bypassed — requires signed command auth
}
```

- [ ] **Step 4: Write failing PHI minimization tests**

Create `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs`:
```csharp
using SuavoAgent.Adapters.PioneerRx;
using SuavoAgent.Adapters.PioneerRx.Sql;
using Xunit;

namespace SuavoAgent.Adapters.PioneerRx.Tests.Sql;

public class PhiMinimizationTests
{
    [Fact]
    public void BuildMetadataQuery_ContainsNoPersonJoin()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);

        Assert.DoesNotContain("Person", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("FirstName", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Phone", query, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Address", query, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("RxNumber", query);
        Assert.Contains("TradeName", query);
    }

    [Fact]
    public void BuildPatientQuery_ContainsPersonJoin()
    {
        var query = PioneerRxSqlEngine.BuildPatientQuery();

        Assert.Contains("Person", query);
        Assert.Contains("FirstName", query);
        Assert.Contains("Phone", query);
        Assert.Contains("@rxNumber", query);
        Assert.Contains("TOP 1", query);
    }

    [Fact]
    public void BuildMetadataQuery_HasTop50Limit()
    {
        var query = PioneerRxSqlEngine.BuildMetadataQuery(
            PioneerRxConstants.DeliveryReadyStatusNames);
        Assert.Contains("TOP 50", query);
    }
}
```

- [ ] **Step 5: Run tests to verify they fail**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "FullyQualifiedName~PhiMinimizationTests" -v n`
Expected: FAIL — `BuildMetadataQuery` and `BuildPatientQuery` don't exist.

- [ ] **Step 6: Implement split queries in PioneerRxSqlEngine**

In `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`, add two new public static methods:

```csharp
public static string BuildMetadataQuery(IReadOnlyList<string> statusNames)
{
    var statusParams = string.Join(", ", statusNames.Select((_, i) => $"@status{i}"));
    return $"""
        SELECT TOP 50
            rx.RxNumber, rx.DateFilled, rx.Quantity,
            item.TradeName, item.NDC,
            rx.StatusGuid
        FROM Prescription.RxTransaction rx
        LEFT JOIN Inventory.Item item ON rx.ItemId = item.Id
        LEFT JOIN Prescription.Rx prx ON rx.RxId = prx.Id
        LEFT JOIN Prescription.RxTransactionStatus st ON rx.StatusGuid = st.Guid
        WHERE st.Name IN ({statusParams})
          AND rx.DateFilled >= @cutoff
        ORDER BY rx.DateFilled DESC
        """;
}

public static string BuildPatientQuery()
{
    return """
        SELECT TOP 1
            p.FirstName, LEFT(p.LastName, 1) AS LastInitial,
            p.Phone1 AS Phone,
            p.Address1, p.Address2, p.City, p.State, p.Zip
        FROM Prescription.RxTransaction rx
        JOIN Prescription.Rx prx ON rx.RxId = prx.Id
        JOIN Person.Person p ON prx.PatientId = p.Id
        WHERE rx.RxNumber = @rxNumber
        """;
}
```

- [ ] **Step 7: Run PHI tests**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "FullyQualifiedName~PhiMinimizationTests" -v n`
Expected: All 3 PASS.

- [ ] **Step 8: Run full suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 9: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Contracts/Models/RxMetadata.cs src/SuavoAgent.Contracts/Models/RxPatientDetails.cs \
  src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs \
  tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs
git commit -m "feat: split Rx queries into metadata-only and patient-fetch

Detection polls (every 5min) use BuildMetadataQuery — zero Person
JOIN, zero PHI. Patient details fetched on-demand via BuildPatientQuery
for approved deliveries only. (HIPAA 164.502(b) minimum necessary)"
```

---

### Task 7: Package-Level Self-Update (Item 2)

**Files:**
- Create: `src/SuavoAgent.Core/Cloud/UpdateManifest.cs`
- Modify: `src/SuavoAgent.Core/Cloud/SelfUpdater.cs`
- Create: `tests/SuavoAgent.Core.Tests/Cloud/UpdateManifestTests.cs`

- [ ] **Step 1: Create UpdateManifest record**

Create `src/SuavoAgent.Core/Cloud/UpdateManifest.cs`:
```csharp
namespace SuavoAgent.Core.Cloud;

public record UpdateManifest(
    string CoreUrl, string CoreSha256,
    string BrokerUrl, string BrokerSha256,
    string HelperUrl, string HelperSha256,
    string Version, string Runtime, string Arch)
{
    private const int FieldCount = 9;

    public static UpdateManifest? Parse(string manifest)
    {
        var parts = manifest.Split('|');
        if (parts.Length != FieldCount) return null;
        if (parts.Any(string.IsNullOrWhiteSpace)) return null;
        return new UpdateManifest(
            parts[0], parts[1], parts[2], parts[3],
            parts[4], parts[5], parts[6], parts[7], parts[8]);
    }

    public string ToCanonical() =>
        $"{CoreUrl}|{CoreSha256}|{BrokerUrl}|{BrokerSha256}|{HelperUrl}|{HelperSha256}|{Version}|{Runtime}|{Arch}";

    public bool MatchesRuntime(string expectedRuntime, string expectedArch) =>
        string.Equals(Runtime, expectedRuntime, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Arch, expectedArch, StringComparison.OrdinalIgnoreCase);
}
```

- [ ] **Step 2: Write failing manifest tests**

Create `tests/SuavoAgent.Core.Tests/Cloud/UpdateManifestTests.cs`:
```csharp
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class UpdateManifestTests
{
    private const string ValidManifest =
        "https://github.com/core.exe|abc123|https://github.com/broker.exe|def456|https://github.com/helper.exe|789012|2.1.0|net8.0|win-x64";

    [Fact]
    public void Parse_ValidManifest_ReturnsRecord()
    {
        var m = UpdateManifest.Parse(ValidManifest);
        Assert.NotNull(m);
        Assert.Equal("2.1.0", m.Version);
        Assert.Equal("abc123", m.CoreSha256);
        Assert.Equal("def456", m.BrokerSha256);
        Assert.Equal("789012", m.HelperSha256);
    }

    [Fact]
    public void Parse_WrongFieldCount_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse("a|b|c"));
    }

    [Fact]
    public void Parse_EmptyField_ReturnsNull()
    {
        Assert.Null(UpdateManifest.Parse("a|b|c|d|e|f|g||i"));
    }

    [Fact]
    public void ToCanonical_RoundTrips()
    {
        var m = UpdateManifest.Parse(ValidManifest);
        Assert.Equal(ValidManifest, m!.ToCanonical());
    }

    [Fact]
    public void MatchesRuntime_Correct()
    {
        var m = UpdateManifest.Parse(ValidManifest)!;
        Assert.True(m.MatchesRuntime("net8.0", "win-x64"));
        Assert.False(m.MatchesRuntime("net8.0", "linux-x64"));
    }
}
```

- [ ] **Step 3: Run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~UpdateManifestTests" -v n`
Expected: All 5 PASS.

- [ ] **Step 4: Add SwapBinaries and CheckPendingUpdate to SelfUpdater**

In `src/SuavoAgent.Core/Cloud/SelfUpdater.cs`, add:

```csharp
public static bool SwapBinaries(string installDir, ILogger logger)
{
    var binaries = new[] { "SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe" };
    var swapped = new List<string>();

    try
    {
        foreach (var bin in binaries)
        {
            var current = Path.Combine(installDir, bin);
            var newFile = current + ".new";
            var oldFile = current + ".old";

            if (!File.Exists(newFile)) continue;

            if (File.Exists(oldFile)) File.Delete(oldFile);
            if (File.Exists(current)) File.Move(current, oldFile);
            File.Move(newFile, current);
            swapped.Add(bin);
            logger.LogInformation("Swapped {Binary}", bin);
        }
        return swapped.Count > 0;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Binary swap failed after {Count} swaps — rolling back", swapped.Count);
        // Rollback swapped binaries
        foreach (var bin in swapped)
        {
            var current = Path.Combine(installDir, bin);
            var oldFile = current + ".old";
            try
            {
                if (File.Exists(current)) File.Delete(current);
                if (File.Exists(oldFile)) File.Move(oldFile, current);
            }
            catch { /* Best effort rollback */ }
        }
        // Clean up remaining .new files
        foreach (var bin in binaries)
        {
            var newFile = Path.Combine(installDir, bin + ".new");
            try { if (File.Exists(newFile)) File.Delete(newFile); } catch { }
        }
        return false;
    }
}

public static bool CheckPendingUpdate(ILogger logger)
{
    var installDir = Path.GetDirectoryName(Environment.ProcessPath);
    if (string.IsNullOrEmpty(installDir)) return false;

    var sentinel = Path.Combine(installDir, "update-pending.flag");
    if (!File.Exists(sentinel))
    {
        // Clean up orphaned .new files (no sentinel = unverified)
        foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
        {
            try { File.Delete(f); } catch { }
        }
        return false;
    }

    logger.LogInformation("Found update-pending sentinel — applying update");

    // Read and verify sentinel (contains manifest signature)
    try
    {
        var sentinelData = File.ReadAllText(sentinel);
        // Sentinel format: manifest_canonical\nsignature_hex
        var lines = sentinelData.Split('\n', 2);
        if (lines.Length < 2)
        {
            logger.LogWarning("Malformed sentinel — discarding");
            File.Delete(sentinel);
            return false;
        }

        var manifestCanonical = lines[0].Trim();
        var signatureHex = lines[1].Trim();

        var manifest = UpdateManifest.Parse(manifestCanonical);
        if (manifest == null)
        {
            logger.LogWarning("Cannot parse manifest from sentinel — discarding");
            File.Delete(sentinel);
            return false;
        }

        // Verify ECDSA signature
        if (!VerifyManifestSignature(manifest.ToCanonical(), signatureHex, logger))
        {
            logger.LogWarning("Sentinel manifest signature invalid — discarding update");
            File.Delete(sentinel);
            foreach (var f in Directory.GetFiles(installDir, "*.exe.new"))
                try { File.Delete(f); } catch { }
            return false;
        }

        if (SwapBinaries(installDir, logger))
        {
            File.Delete(sentinel);
            logger.LogInformation("Bootstrap update applied — v{Version}", manifest.Version);
            return true;
        }

        logger.LogWarning("Binary swap failed during bootstrap update");
        File.Delete(sentinel);
        return false;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Bootstrap update check failed");
        try { File.Delete(sentinel); } catch { }
        return false;
    }
}
```

Also refactor `VerifyManifestSignature` to accept canonical string + signature (overload):
```csharp
internal static bool VerifyManifestSignature(string manifestCanonical, string signatureHex, ILogger logger)
{
    if (string.IsNullOrEmpty(signatureHex))
    {
        logger.LogWarning("Update manifest has no signature — rejecting");
        return false;
    }
    if (ContainsControlChars(manifestCanonical))
    {
        logger.LogWarning("Manifest contains control characters — rejecting");
        return false;
    }
    try
    {
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdatePublicKeyDer), out _);
        var valid = ecdsa.VerifyData(
            Encoding.UTF8.GetBytes(manifestCanonical),
            Convert.FromHexString(signatureHex),
            HashAlgorithmName.SHA256);
        if (!valid) logger.LogWarning("Update manifest signature INVALID — rejecting");
        else logger.LogInformation("Update manifest signature verified (ECDSA P-256)");
        return valid;
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Signature verification failed");
        return false;
    }
}
```

- [ ] **Step 5: Run full suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Cloud/UpdateManifest.cs src/SuavoAgent.Core/Cloud/SelfUpdater.cs \
  tests/SuavoAgent.Core.Tests/Cloud/UpdateManifestTests.cs
git commit -m "feat: package-level self-update for all three binaries

New manifest format with Core+Broker+Helper URLs and SHA256 hashes.
SwapBinaries does sequential rename with rollback on failure.
CheckPendingUpdate runs pre-host for bootstrap self-update.
Sentinel file coordinates update across processes."
```

---

### Task 8: IPC Framing + Pipe ACL Fix (Item 10)

**Files:**
- Create: `src/SuavoAgent.Core/Ipc/IpcFraming.cs`
- Modify: `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs`
- Modify: `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs`
- Create: `tests/SuavoAgent.Core.Tests/Ipc/IpcFramingTests.cs`

- [ ] **Step 1: Create IpcFraming utility**

Create `src/SuavoAgent.Core/Ipc/IpcFraming.cs`:
```csharp
using System.Buffers.Binary;
using System.Text;

namespace SuavoAgent.Core.Ipc;

public static class IpcFraming
{
    public const int MaxPayloadSize = 65536; // 64KB
    public const int HeaderSize = 4; // 4-byte big-endian uint32

    public static async Task WriteFrameAsync(Stream stream, string json, CancellationToken ct = default)
    {
        var payload = Encoding.UTF8.GetBytes(json);
        if (payload.Length > MaxPayloadSize)
            throw new InvalidOperationException($"Payload {payload.Length} bytes exceeds max {MaxPayloadSize}");

        var header = new byte[HeaderSize];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)payload.Length);

        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string?> ReadFrameAsync(Stream stream, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        var read = await ReadExactAsync(stream, header, ct);
        if (read < HeaderSize) return null; // Connection closed

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > MaxPayloadSize)
            throw new InvalidOperationException($"Frame size {length} exceeds max {MaxPayloadSize}");
        if (length == 0) return "";

        var payload = new byte[length];
        read = await ReadExactAsync(stream, payload, ct);
        if (read < (int)length) return null; // Connection closed mid-frame

        return Encoding.UTF8.GetString(payload);
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(offset), ct);
            if (n == 0) return offset;
            offset += n;
        }
        return offset;
    }
}
```

- [ ] **Step 2: Write failing framing tests**

Create `tests/SuavoAgent.Core.Tests/Ipc/IpcFramingTests.cs`:
```csharp
using SuavoAgent.Core.Ipc;
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IpcFramingTests
{
    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        using var ms = new MemoryStream();
        var json = """{"command":"ping","id":"123"}""";

        await IpcFraming.WriteFrameAsync(ms, json);
        ms.Position = 0;
        var result = await IpcFraming.ReadFrameAsync(ms);

        Assert.Equal(json, result);
    }

    [Fact]
    public async Task ReadFrame_EmptyStream_ReturnsNull()
    {
        using var ms = new MemoryStream();
        var result = await IpcFraming.ReadFrameAsync(ms);
        Assert.Null(result);
    }

    [Fact]
    public async Task WriteFrame_OversizedPayload_Throws()
    {
        using var ms = new MemoryStream();
        var huge = new string('x', IpcFraming.MaxPayloadSize + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IpcFraming.WriteFrameAsync(ms, huge));
    }

    [Fact]
    public async Task ReadFrame_OversizedHeader_Throws()
    {
        using var ms = new MemoryStream();
        var header = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, (uint)(IpcFraming.MaxPayloadSize + 1));
        ms.Write(header);
        ms.Write(new byte[100]); // dummy payload
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            IpcFraming.ReadFrameAsync(ms));
    }

    [Fact]
    public async Task MultipleFrames_ReadSequentially()
    {
        using var ms = new MemoryStream();
        await IpcFraming.WriteFrameAsync(ms, "frame1");
        await IpcFraming.WriteFrameAsync(ms, "frame2");
        await IpcFraming.WriteFrameAsync(ms, "frame3");

        ms.Position = 0;
        Assert.Equal("frame1", await IpcFraming.ReadFrameAsync(ms));
        Assert.Equal("frame2", await IpcFraming.ReadFrameAsync(ms));
        Assert.Equal("frame3", await IpcFraming.ReadFrameAsync(ms));
        Assert.Null(await IpcFraming.ReadFrameAsync(ms)); // EOF
    }

    [Fact]
    public async Task EmptyPayload_RoundTrips()
    {
        using var ms = new MemoryStream();
        await IpcFraming.WriteFrameAsync(ms, "");
        ms.Position = 0;
        Assert.Equal("", await IpcFraming.ReadFrameAsync(ms));
    }
}
```

- [ ] **Step 3: Run framing tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~IpcFramingTests" -v n`
Expected: All 6 PASS.

- [ ] **Step 4: Migrate IpcMessage records (breaking change)**

In `src/SuavoAgent.Contracts/Ipc/IpcMessage.cs`, replace entire file:

```csharp
using System.Text.Json;

namespace SuavoAgent.Contracts.Ipc;

// v2 IPC records — breaking change from v1 (IpcMessage/IpcResponse)
public record IpcRequest(string Id, string Command, int Version, JsonElement? Data);
public record IpcResponse(string Id, int Status, string Command, JsonElement? Data, IpcError? Error);
public record IpcError(string Code, string Message, bool Retryable, int AttemptCount);

public static class IpcCommands
{
    public const string Ping = "ping";
    public const string AttachPioneerRx = "attach_pioneerrx";
    public const string WritebackDelivery = "writeback_delivery";
    public const string DiscoverScreen = "discover_screen";
    public const string DismissModal = "dismiss_modal";
    public const string CheckUserActivity = "check_user_activity";
    public const string Drain = "drain";
    public const string HelperStatus = "helper_status";
    public const string HelperError = "helper_error";
}

public static class IpcStatus
{
    public const int Ok = 200;
    public const int BadRequest = 400;
    public const int NotFound = 404;
    public const int Timeout = 408;
    public const int InternalError = 500;
}
```

- [ ] **Step 5: Update all files that reference old IpcMessage/IpcResponse**

Files to update:
- `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs` — change `Func<IpcMessage, ...>` to `Func<IpcRequest, ...>`, update handler
- `src/SuavoAgent.Core/Program.cs:84-92` — update handler lambda to use `IpcRequest`/`IpcResponse`
- `tests/SuavoAgent.Contracts.Tests/Ipc/IpcMessageTests.cs` — update test assertions for new record shapes
- `tests/SuavoAgent.Core.Tests/Ipc/IpcPipeTests.cs` — update to use `IpcRequest`/`IpcResponse`

In each file, replace:
- `IpcMessage` → `IpcRequest`
- `message.RequestId` → `message.Id`
- `message.Payload` → `message.Data`
- `new IpcResponse(msg.RequestId, true, "ack", null)` → `new IpcResponse(msg.Id, IpcStatus.Ok, msg.Command, null, null)`

- [ ] **Step 6: Update IpcPipeServer to use length-prefixed framing**

In `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs`, replace `HandleConnection` method:

```csharp
private async Task HandleConnection(NamedPipeServerStream pipe, CancellationToken ct)
{
    while (!ct.IsCancellationRequested && pipe.IsConnected)
    {
        try
        {
            var json = await IpcFraming.ReadFrameAsync(pipe, ct);
            if (json == null) break;

            var request = JsonSerializer.Deserialize<IpcRequest>(json);
            if (request == null) continue;

            _logger.LogDebug("IPC received: {Command} [{Id}]", request.Command, request.Id);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));

            var response = await _handler(request);
            var responseJson = JsonSerializer.Serialize(response);
            await IpcFraming.WriteFrameAsync(pipe, responseJson, timeoutCts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("exceeds max"))
        {
            _logger.LogWarning("Oversized IPC message rejected");
        }
        catch (IOException) { _logger.LogInformation("Helper disconnected"); break; }
        catch (Exception ex) { _logger.LogWarning(ex, "IPC error"); }
    }
    _isConnected = false;
}
```

- [ ] **Step 7: Run full test suite**

Run: `dotnet test`
Expected: All tests pass after fixing the IPC record migration.

- [ ] **Step 8: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Ipc/IpcFraming.cs src/SuavoAgent.Contracts/Ipc/IpcMessage.cs \
  src/SuavoAgent.Core/Ipc/IpcPipeServer.cs src/SuavoAgent.Core/Program.cs \
  tests/SuavoAgent.Core.Tests/Ipc/IpcFramingTests.cs tests/SuavoAgent.Core.Tests/Ipc/IpcPipeTests.cs \
  tests/SuavoAgent.Contracts.Tests/Ipc/IpcMessageTests.cs
git commit -m "feat: length-prefixed IPC framing with migrated records

Replaces line-based JSON framing with 4-byte length-prefixed protocol.
64KB max payload. Breaking migration from IpcMessage to IpcRequest/IpcResponse
with status codes. Adds IpcFraming utility with full test coverage."
```

---

## Wave 3 — After Wave 2

### Task 9: Signed Control-Plane Commands (Item 4)

**Files:**
- Create: `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Create: `tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs`

- [ ] **Step 1: Write failing signed command tests**

Create `tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs`:
```csharp
using System.Security.Cryptography;
using System.Text;
using SuavoAgent.Core.Cloud;
using Xunit;

namespace SuavoAgent.Core.Tests.Cloud;

public class SignedCommandVerifierTests
{
    private readonly ECDsa _signingKey;
    private readonly SignedCommandVerifier _verifier;
    private const string AgentId = "agent-test-123";
    private const string Fingerprint = "fp-test-456";

    public SignedCommandVerifierTests()
    {
        _signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pubKeyDer = Convert.ToBase64String(_signingKey.ExportSubjectPublicKeyInfo());
        _verifier = new SignedCommandVerifier(
            new Dictionary<string, string> { { "test-key-v1", pubKeyDer } },
            AgentId, Fingerprint);
    }

    private SignedCommand CreateSignedCommand(string command, string? agentId = null,
        string? fingerprint = null, string? keyId = null, DateTimeOffset? timestamp = null)
    {
        agentId ??= AgentId;
        fingerprint ??= Fingerprint;
        keyId ??= "test-key-v1";
        var ts = (timestamp ?? DateTimeOffset.UtcNow).ToString("o");
        var nonce = Guid.NewGuid().ToString();
        var canonical = $"{command}|{agentId}|{fingerprint}|{ts}|{nonce}";
        var sig = Convert.ToBase64String(
            _signingKey.SignData(Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));
        return new SignedCommand(command, agentId, fingerprint, ts, nonce, keyId, sig);
    }

    [Fact]
    public void Verify_ValidCommand_Succeeds()
    {
        var cmd = CreateSignedCommand("force_sync");
        var result = _verifier.Verify(cmd);
        Assert.True(result.IsValid);
    }

    [Fact]
    public void Verify_WrongAgentId_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", agentId: "wrong-agent");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_WrongFingerprint_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", fingerprint: "wrong-fp");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_ExpiredTimestamp_Fails()
    {
        var cmd = CreateSignedCommand("force_sync",
            timestamp: DateTimeOffset.UtcNow.AddSeconds(-400));
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_UnknownKeyId_Fails()
    {
        var cmd = CreateSignedCommand("force_sync", keyId: "unknown-key");
        Assert.False(_verifier.Verify(cmd).IsValid);
    }

    [Fact]
    public void Verify_ReplayedNonce_Fails()
    {
        var cmd = CreateSignedCommand("force_sync");
        _verifier.Verify(cmd); // First use
        Assert.False(_verifier.Verify(cmd).IsValid); // Replay
    }

    [Fact]
    public void Verify_TamperedSignature_Fails()
    {
        var cmd = CreateSignedCommand("force_sync") with { Signature = "AAAA" };
        Assert.False(_verifier.Verify(cmd).IsValid);
    }
}
```

- [ ] **Step 2: Create SignedCommand record and verifier**

Create `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs`:
```csharp
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SuavoAgent.Core.Cloud;

public record SignedCommand(
    string Command, string AgentId, string MachineFingerprint,
    string Timestamp, string Nonce, string KeyId, string Signature);

public record VerificationResult(bool IsValid, string? Reason = null);

public class SignedCommandVerifier
{
    private readonly Dictionary<string, ECDsa> _keys = new();
    private readonly string _agentId;
    private readonly string _fingerprint;
    private readonly HashSet<string> _usedNonces = new();
    private readonly TimeSpan _timestampWindow = TimeSpan.FromSeconds(300);
    private readonly Stopwatch _uptime = Stopwatch.StartNew();

    public SignedCommandVerifier(
        Dictionary<string, string> keyRegistry,
        string agentId, string fingerprint)
    {
        _agentId = agentId;
        _fingerprint = fingerprint;

        foreach (var (keyId, pubKeyDer) in keyRegistry)
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(pubKeyDer), out _);
            _keys[keyId] = ecdsa;
        }
    }

    public VerificationResult Verify(SignedCommand cmd)
    {
        // 1. Key lookup
        if (!_keys.TryGetValue(cmd.KeyId, out var key))
            return new(false, $"Unknown keyId: {cmd.KeyId}");

        // 2. Agent identity
        if (!string.Equals(cmd.AgentId, _agentId, StringComparison.Ordinal))
            return new(false, "AgentId mismatch");

        // 3. Machine fingerprint
        if (!string.Equals(cmd.MachineFingerprint, _fingerprint, StringComparison.Ordinal))
            return new(false, "Fingerprint mismatch");

        // 4. Timestamp window
        if (!DateTimeOffset.TryParse(cmd.Timestamp, out var ts))
            return new(false, "Invalid timestamp format");
        if (DateTimeOffset.UtcNow - ts > _timestampWindow)
            return new(false, "Timestamp expired");

        // 5. Nonce replay
        lock (_usedNonces)
        {
            if (!_usedNonces.Add(cmd.Nonce))
                return new(false, "Nonce replay detected");
        }

        // 6. ECDSA signature
        var canonical = $"{cmd.Command}|{cmd.AgentId}|{cmd.MachineFingerprint}|{cmd.Timestamp}|{cmd.Nonce}";
        try
        {
            var valid = key.VerifyData(
                Encoding.UTF8.GetBytes(canonical),
                Convert.FromBase64String(cmd.Signature),
                HashAlgorithmName.SHA256);
            return valid ? new(true) : new(false, "Invalid signature");
        }
        catch
        {
            return new(false, "Signature verification error");
        }
    }

    public void PruneNonces(TimeSpan maxAge)
    {
        // Simple prune — in production, nonces would be stored in SQLite with timestamps
        // For now, clear all if uptime exceeds maxAge
        if (_uptime.Elapsed > maxAge)
        {
            lock (_usedNonces) { _usedNonces.Clear(); }
            _uptime.Restart();
        }
    }
}
```

- [ ] **Step 3: Run signed command tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "FullyQualifiedName~SignedCommandVerifierTests" -v n`
Expected: All 7 PASS.

- [ ] **Step 4: Add nonce table to AgentStateDb**

In `AgentStateDb.InitSchema()`, add:
```sql
CREATE TABLE IF NOT EXISTS command_nonces (
    nonce TEXT PRIMARY KEY,
    received_at TEXT NOT NULL
);
```

Add methods:
```csharp
public bool TryRecordNonce(string nonce)
{
    try
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "INSERT INTO command_nonces (nonce, received_at) VALUES (@nonce, @now)";
        cmd.Parameters.AddWithValue("@nonce", nonce);
        cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));
        cmd.ExecuteNonQuery();
        return true;
    }
    catch { return false; } // UNIQUE violation = replay
}

public void PruneOldNonces(TimeSpan maxAge)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "DELETE FROM command_nonces WHERE received_at < @cutoff";
    cmd.Parameters.AddWithValue("@cutoff", DateTimeOffset.UtcNow.Subtract(maxAge).ToString("o"));
    cmd.ExecuteNonQuery();
}
```

- [ ] **Step 5: Run full suite**

Run: `dotnet test`
Expected: All tests pass.

- [ ] **Step 6: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs src/SuavoAgent.Core/State/AgentStateDb.cs \
  tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs
git commit -m "feat: ECDSA-signed control-plane command verification

Signed envelope with agentId + fingerprint binding. 300s timestamp
window. Nonce replay prevention. Key rotation via keyId registry.
Covers decommission, update, force_sync, fetch_patient."
```

---

### Task 10: Bootstrap Self-Update in Program.cs (Item 3)

**Files:**
- Modify: `src/SuavoAgent.Core/Program.cs`

- [ ] **Step 1: Add CheckPendingUpdate call at top of Program.cs**

At the very top of `Program.cs`, before `Host.CreateApplicationBuilder`:

```csharp
// Bootstrap self-update — runs before any DI/config
{
    using var earlyLog = new Serilog.LoggerConfiguration()
        .WriteTo.Console()
        .WriteTo.File(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SuavoAgent", "logs", "startup-.log"),
            rollingInterval: Serilog.RollingInterval.Day,
            retainedFileCountLimit: 7)
        .CreateLogger();

    if (SuavoAgent.Core.Cloud.SelfUpdater.CheckPendingUpdate(earlyLog))
    {
        earlyLog.Information("Bootstrap update applied — restarting");
        Environment.Exit(1); // SCM restarts with new binary
    }
}
```

- [ ] **Step 2: Run full suite**

Run: `dotnet test`
Expected: All tests pass (CheckPendingUpdate returns false when no sentinel exists).

- [ ] **Step 3: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Program.cs
git commit -m "feat: bootstrap self-update in Program.cs pre-host

Runs before Host.CreateApplicationBuilder — if pending .exe.new
files exist with valid ECDSA signature, swap and restart.
Agent can never be stranded at old version."
```

---

### Task 11: Installer Signature Verification (Item 5)

**Files:**
- Modify: `publish.ps1`
- Modify: `bootstrap.ps1`

- [ ] **Step 1: Add checksum generation to publish.ps1**

At the end of `publish.ps1`, after all three EXEs are built, add:

```powershell
# Generate checksums
$checksumFile = Join-Path $publishDir "checksums.sha256"
$checksums = @()
foreach ($bin in @("SuavoAgent.Core.exe", "SuavoAgent.Broker.exe", "SuavoAgent.Helper.exe")) {
    $path = Join-Path $publishDir $bin
    if (Test-Path $path) {
        $hash = (Get-FileHash -Path $path -Algorithm SHA256).Hash.ToLower()
        $checksums += "$hash  $bin"
        Write-Host "  $bin: $hash" -ForegroundColor Gray
    }
}
$checksums | Out-File -FilePath $checksumFile -Encoding UTF8
Write-Host "Checksums written to $checksumFile" -ForegroundColor Green

# Sign checksums with ECDSA (if signing key available)
$signingKeyPath = Join-Path $env:HOME ".suavo" "signing-key.pem"
if (Test-Path $signingKeyPath) {
    Write-Host "Signing checksums..." -ForegroundColor Yellow
    $sigFile = "$checksumFile.sig"
    # Use dotnet to sign via a helper
    $checksumBytes = [System.IO.File]::ReadAllBytes($checksumFile)
    $keyPem = Get-Content $signingKeyPath -Raw
    $ecdsa = [System.Security.Cryptography.ECDsa]::Create()
    $ecdsa.ImportFromPem($keyPem)
    $sig = $ecdsa.SignData($checksumBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
    [System.Convert]::ToHexString($sig).ToLower() | Out-File -FilePath $sigFile -Encoding UTF8 -NoNewline
    Write-Host "Signature written to $sigFile" -ForegroundColor Green
} else {
    Write-Host "WARNING: No signing key at $signingKeyPath — checksums unsigned" -ForegroundColor Red
}
```

- [ ] **Step 2: Add hash verification to bootstrap.ps1**

In `bootstrap.ps1`, before the binary download loop (around line 310), add verification:

```powershell
# Download and verify checksums
$checksumUrl = "$base/checksums.sha256"
$checksumSigUrl = "$base/checksums.sha256.sig"
$checksumPath = Join-Path $installDir "checksums.sha256"
$checksumSigPath = Join-Path $installDir "checksums.sha256.sig"

Write-Host "  Downloading checksums..." -ForegroundColor Gray
Invoke-WebRequest -Uri $checksumUrl -OutFile $checksumPath -UseBasicParsing
Invoke-WebRequest -Uri $checksumSigUrl -OutFile $checksumSigPath -UseBasicParsing

# Verify ECDSA signature of checksums
$publicKeyDer = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEJJO30pUIre7wuMN5I1FQmlEDpTIM0dmhPjaGtlG7gm+47G7lKHuJV4lQ3eWhZNqe1eviOZkt+9VnWnQUSJGvsg=="
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$ecdsa.ImportSubjectPublicKeyInfo([System.Convert]::FromBase64String($publicKeyDer), [ref]$null)
$checksumBytes = [System.IO.File]::ReadAllBytes($checksumPath)
$sigHex = (Get-Content $checksumSigPath -Raw).Trim()
$sigBytes = [System.Convert]::FromHexString($sigHex)
$valid = $ecdsa.VerifyData($checksumBytes, $sigBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)

if (-not $valid) {
    Write-Error "CRITICAL: Checksum signature verification FAILED — aborting install"
    Remove-Item $checksumPath, $checksumSigPath -Force -ErrorAction SilentlyContinue
    exit 1
}
Write-Host "  Checksum signature verified (ECDSA P-256)" -ForegroundColor Green

# Parse expected hashes
$expectedHashes = @{}
Get-Content $checksumPath | ForEach-Object {
    $parts = $_ -split "  ", 2
    if ($parts.Count -eq 2) { $expectedHashes[$parts[1].Trim()] = $parts[0].Trim() }
}
```

Then, after each binary download in the existing loop, add verification:

```powershell
# After Invoke-WebRequest download of each $bin:
$actualHash = (Get-FileHash -Path $dst -Algorithm SHA256).Hash.ToLower()
if ($expectedHashes.ContainsKey($bin) -and $actualHash -ne $expectedHashes[$bin]) {
    Write-Error "CRITICAL: SHA256 mismatch for $bin — expected $($expectedHashes[$bin]), got $actualHash"
    # Clean up all downloaded files
    foreach ($b in $binaries) { Remove-Item (Join-Path $installDir $b) -Force -ErrorAction SilentlyContinue }
    exit 1
}
Write-Host "  $bin verified: $actualHash" -ForegroundColor Green
```

- [ ] **Step 3: Commit**

```bash
cd ~/Documents/SuavoAgent
git add publish.ps1 bootstrap.ps1
git commit -m "feat: ECDSA-signed checksum verification in bootstrap installer

publish.ps1 generates checksums.sha256 + signs with ECDSA P-256.
bootstrap.ps1 verifies signature before install, checks SHA256 of
each downloaded binary. Abort on any mismatch."
```

---

### Task 12: Writeback Hardening (Item 11)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/WritebackProcessor.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`

- [ ] **Step 1: Add next_retry_at column to AgentStateDb**

In `AgentStateDb.InitSchema()`, add migration:
```sql
ALTER TABLE writeback_states ADD COLUMN next_retry_at TEXT;
```
(Wrapped in try/catch for idempotency.)

Add method:
```csharp
public void UpdateNextRetryAt(string taskId, DateTimeOffset nextRetry)
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "UPDATE writeback_states SET next_retry_at = @nextRetry WHERE task_id = @taskId";
    cmd.Parameters.AddWithValue("@taskId", taskId);
    cmd.Parameters.AddWithValue("@nextRetry", nextRetry.ToString("o"));
    cmd.ExecuteNonQuery();
}

public IReadOnlyList<(string TaskId, WritebackState State, string RxNumber, int RetryCount, DateTimeOffset? NextRetryAt)>
    GetDueWritebacks()
{
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = """
        SELECT task_id, state, rx_number, retry_count, next_retry_at FROM writeback_states
        WHERE state NOT IN ('Done', 'ManualReview')
          AND (next_retry_at IS NULL OR next_retry_at <= @now)
        ORDER BY created_at ASC
        """;
    cmd.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToString("o"));

    var results = new List<(string, WritebackState, string, int, DateTimeOffset?)>();
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var stateStr = reader.GetString(1);
        if (!Enum.TryParse<WritebackState>(stateStr, out var state)) continue;
        DateTimeOffset? nextRetry = reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4));
        results.Add((reader.GetString(0), state, reader.GetString(2), reader.GetInt32(3), nextRetry));
    }
    return results;
}
```

- [ ] **Step 2: Wire exponential backoff in WritebackProcessor**

In `WritebackProcessor.cs`, in the `OnStateChanged` callback (where `SystemError` is handled), add backoff calculation:

```csharp
// After _retryCount increment in OnSystemError:
var delays = new[] { 60, 300, 900 }; // 1min, 5min, 15min
var delaySeconds = _retryCount <= delays.Length ? delays[_retryCount - 1] : delays[^1];
_stateDb.UpdateNextRetryAt(taskId, DateTimeOffset.UtcNow.AddSeconds(delaySeconds));
```

Change `GetPendingWritebacks()` calls to `GetDueWritebacks()` to skip not-yet-due tasks.

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/State/AgentStateDb.cs src/SuavoAgent.Core/Workers/WritebackProcessor.cs
git commit -m "feat: exponential backoff for writeback retries

1min/5min/15min delays between retries. next_retry_at column in
writeback_states. GetDueWritebacks skips tasks not yet due.
MaxRetries=3 already enforced by WritebackStateMachine."
```

---

## Wave 4 — After Wave 3

### Task 13: fetch_patient Implementation (Item 6b)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`
- Modify: `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`

- [ ] **Step 1: Add fetch_patient command handler to HeartbeatWorker**

In `HeartbeatWorker.cs`, in the signed command processing section, add a case for `fetch_patient`:

```csharp
case "fetch_patient":
    var rxNumber = cmd.Data?.GetProperty("rxNumber").GetString();
    var deliveryOrderId = cmd.Data?.GetProperty("deliveryOrderId").GetString();
    var requesterId = cmd.Data?.GetProperty("requesterId").GetString();

    if (string.IsNullOrEmpty(rxNumber)) break;

    // Audit PHI access
    _stateDb.AppendChainedAuditEntry(new AuditEntry(
        rxNumber, "phi_access", "", "", "",
        CommandId: cmd.Nonce, RequesterId: requesterId, RxNumber: rxNumber));

    // Run targeted patient query
    var details = await _sqlEngine.PullPatientForRxAsync(rxNumber, ct);
    if (details != null)
        await _cloudClient.SendPatientDetailsAsync(rxNumber, details, cmd.Nonce);
    break;
```

- [ ] **Step 2: Implement PullPatientForRxAsync in PioneerRxSqlEngine**

Add to `PioneerRxSqlEngine`:
```csharp
public async Task<RxPatientDetails?> PullPatientForRxAsync(string rxNumber, CancellationToken ct)
{
    var query = BuildPatientQuery();
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = query;
    cmd.Parameters.AddWithValue("@rxNumber", rxNumber);

    using var reader = await cmd.ExecuteReaderAsync(ct);
    if (!await reader.ReadAsync(ct)) return null;

    return new RxPatientDetails(
        rxNumber,
        reader.IsDBNull(0) ? null : reader.GetString(0),
        reader.IsDBNull(1) ? null : reader.GetString(1),
        reader.IsDBNull(2) ? null : reader.GetString(2),
        reader.IsDBNull(3) ? null : reader.GetString(3),
        reader.IsDBNull(4) ? null : reader.GetString(4),
        reader.IsDBNull(5) ? null : reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7));
}
```

- [ ] **Step 3: Add SendPatientDetailsAsync to SuavoCloudClient**

```csharp
public async Task SendPatientDetailsAsync(string rxNumber, RxPatientDetails details, string commandId)
{
    var body = JsonSerializer.Serialize(new { rxNumber, details, commandId });
    await PostSignedAsync("/api/agent/patient-details", body);
}
```

- [ ] **Step 4: Run full suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs \
  src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs \
  src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs
git commit -m "feat: signed fetch_patient command with PHI audit trail

Patient details fetched on-demand for approved deliveries only.
Requires signed command envelope. Every PHI access logged as
audit entry with commandId, requesterId, rxNumber.
(HIPAA 164.502(b) minimum necessary)"
```

---

### Task 14: Decommission Audit Preservation (Item 9)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs`
- Modify: `src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs`

- [ ] **Step 1: Add audit export methods to AgentStateDb**

```csharp
public string ExportAuditArchiveJson()
{
    var entries = new List<Dictionary<string, object?>>();
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM audit_entries ORDER BY id ASC";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        entries.Add(row);
    }
    return JsonSerializer.Serialize(entries);
}

public string ExportWritebackStatesJson()
{
    var states = new List<Dictionary<string, object?>>();
    using var cmd = _conn.CreateCommand();
    cmd.CommandText = "SELECT * FROM writeback_states";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var row = new Dictionary<string, object?>();
        for (int i = 0; i < reader.FieldCount; i++)
            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        states.Add(row);
    }
    return JsonSerializer.Serialize(states);
}
```

- [ ] **Step 2: Rewrite decommission flow in HeartbeatWorker**

Replace the existing decommission block (`HeartbeatWorker.cs:107-141`) with two-phase logic:

```csharp
case "decommission":
    if (_decommissionPendingSince == null)
    {
        // Phase 1: Enter pending state
        _decommissionPendingSince = Stopwatch.GetTimestamp();
        _stateDb.AppendChainedAuditEntry(new AuditEntry(
            _agentId, "decommission", "", "DecommissionPending", "decommission_phase1"));
        _logger.LogWarning("DECOMMISSION phase 1 — awaiting confirmation (5+ min)");
        break;
    }

    // Phase 2: Verify 5+ minutes elapsed (monotonic clock)
    var elapsed = Stopwatch.GetElapsedTime(_decommissionPendingSince.Value);
    if (elapsed < TimeSpan.FromMinutes(5))
    {
        _logger.LogInformation("Decommission phase 2 too early ({Elapsed}) — waiting", elapsed);
        break;
    }

    _logger.LogWarning("DECOMMISSION phase 2 — archiving audit data");

    // Verify audit chain
    var chainValid = _stateDb.VerifyAuditChain();

    // Export and upload archive
    var archive = new {
        agentId = _agentId,
        pharmacyId = _options.PharmacyId,
        machineFingerprint = _options.MachineFingerprint,
        version = _options.Version,
        auditEntries = _stateDb.ExportAuditArchiveJson(),
        writebackStates = _stateDb.ExportWritebackStatesJson(),
        auditChainValid = chainValid,
        archiveDigest = "" // computed below
    };
    var archiveJson = JsonSerializer.Serialize(archive);
    var digest = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(archiveJson)));

    var ack = await _cloudClient.UploadAuditArchiveAsync(archiveJson, digest);
    if (ack == null || ack.ArchiveDigest != digest)
    {
        _logger.LogWarning("Decommission BLOCKED — archive upload failed or ACK mismatch");
        _decommissionPendingSince = null; // Reset, try again
        break;
    }

    // ACK verified — proceed with cleanup
    _stateDb.AppendChainedAuditEntry(new AuditEntry(
        _agentId, "decommission", "DecommissionPending", "Decommissioned", "decommission_phase2"));

    _logger.LogWarning("Audit archived (id={ArchiveId}) — removing agent", ack.ArchiveId);

    // Cleanup (secure delete handled by existing decommission script, updated for audit preservation)
    // ... existing PowerShell cleanup, but WITHOUT deleting audit first
    Environment.Exit(0);
    break;
```

Add field: `private long? _decommissionPendingSince;`

Add 1-hour timeout check in the heartbeat loop:
```csharp
if (_decommissionPendingSince != null &&
    Stopwatch.GetElapsedTime(_decommissionPendingSince.Value) > TimeSpan.FromHours(1))
{
    _logger.LogInformation("Decommission timed out — cancelling");
    _stateDb.AppendChainedAuditEntry(new AuditEntry(
        _agentId, "decommission", "DecommissionPending", "", "decommission_cancelled_timeout"));
    _decommissionPendingSince = null;
}
```

- [ ] **Step 3: Add UploadAuditArchiveAsync to SuavoCloudClient**

```csharp
public record AuditArchiveAck(string ArchiveId, string ArchiveDigest, string Timestamp, string Signature);

public async Task<AuditArchiveAck?> UploadAuditArchiveAsync(string archiveJson, string digest)
{
    var body = JsonSerializer.Serialize(new { archive = archiveJson, archiveDigest = digest });
    var response = await PostSignedAsync("/api/agent/audit-archive", body);
    if (response == null) return null;
    return JsonSerializer.Deserialize<AuditArchiveAck>(response.GetRawText());
}
```

- [ ] **Step 4: Run full suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 5: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs src/SuavoAgent.Core/State/AgentStateDb.cs \
  src/SuavoAgent.Core/Cloud/SuavoCloudClient.cs
git commit -m "feat: two-phase decommission with audit archive preservation

Phase 1: enter pending state, audit logged.
Phase 2: 5+ min later (monotonic clock), archive audit chain to cloud,
verify signed ACK with digest match, then cleanup.
Blocks if archive upload fails. 1h timeout auto-cancels.
(HIPAA 164.530(j) — 6-year audit retention)"
```

---

### Task 15: Canary Updates + Health Enrichment (Item 12a+b)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`

- [ ] **Step 1: Enrich heartbeat payload**

In `HeartbeatWorker.cs`, where the heartbeat payload is constructed, replace with enriched version:

```csharp
var payload = new
{
    agentId = _options.AgentId,
    version = _options.Version,
    updateChannel = _lastUpdateChannel ?? "stable",
    machineFingerprint = _options.MachineFingerprint,
    uptimeSeconds = (long)(DateTimeOffset.UtcNow - _startTime).TotalSeconds,
    memoryMb = Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024),
    sql = new
    {
        connected = _sqlEngine?.IsConnected ?? false,
        tier = _sqlEngine?.CurrentTier ?? 0,
        lastQueryAt = _sqlEngine?.LastQueryAt?.ToString("o"),
        lastQueryDurationMs = _sqlEngine?.LastQueryDurationMs ?? 0,
        lastRxCount = _lastRxCount
    },
    helper = new
    {
        attached = _ipcServer?.IsConnected ?? false,
        consecutiveFailures = _helperConsecutiveFailures
    },
    writeback = new
    {
        pending = _stateDb.GetPendingWritebacks().Count,
        failed = 0, // TODO: count by state
        manualReview = 0
    },
    audit = new
    {
        chainValid = _lastAuditChainValid,
        entryCount = _stateDb.GetAuditEntryCount()
    },
    sync = new
    {
        unsyncedBatches = _stateDb.GetPendingBatches().Count,
        deadLetterCount = _stateDb.GetDeadLetterCount(),
        lastSyncAt = _lastSyncAt?.ToString("o")
    }
};
```

Add fields:
```csharp
private string? _lastUpdateChannel;
private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;
private int _helperConsecutiveFailures;
private bool _lastAuditChainValid = true;
private int _lastRxCount;
private DateTimeOffset? _lastSyncAt;
```

After processing heartbeat response, store update channel:
```csharp
if (response.TryGetProperty("updateChannel", out var channel))
    _lastUpdateChannel = channel.GetString();
```

- [ ] **Step 2: Add startup audit chain verification**

In `HeartbeatWorker.StartAsync`, add:
```csharp
_lastAuditChainValid = _stateDb.VerifyAuditChain();
if (!_lastAuditChainValid)
    _logger.LogWarning("HIPAA ALERT: Audit chain integrity verification FAILED");
```

- [ ] **Step 3: Run full suite**

Run: `dotnet test`
Expected: All pass.

- [ ] **Step 4: Commit**

```bash
cd ~/Documents/SuavoAgent
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs
git commit -m "feat: enriched heartbeat with health metrics + canary channel

Heartbeat now includes SQL status, Helper attachment, writeback stats,
audit chain validity, sync metrics. Reports updateChannel from server
for canary rollout tracking. Startup audit chain verification."
```

---

## Final Verification

- [ ] **Run complete test suite**
```bash
cd ~/Documents/SuavoAgent && dotnet test -v n
```
Expected: 90+ tests pass (49 existing + ~45 new).

- [ ] **Cross-compile verification**
```bash
dotnet publish src/SuavoAgent.Core -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/SuavoAgent.Broker -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
dotnet publish src/SuavoAgent.Helper -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```
Expected: All three build successfully with SQLCipher bundle.

- [ ] **Parallels VM test (if available)**
Transfer binaries to Windows VM, verify:
1. Service starts
2. SQLCipher migration runs
3. Audit chain builds
4. Heartbeat succeeds with enriched payload
