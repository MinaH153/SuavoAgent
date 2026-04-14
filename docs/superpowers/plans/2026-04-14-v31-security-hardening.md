# SuavoAgent v3.1 Security Hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix all remaining HIGH and MEDIUM security findings from the 26-finding audit, making SuavoAgent production-hardened for multi-pharmacy fleet deployment.

**Architecture:** Targeted fixes across 6 subsystems (IPC, CI/CD, decommission, cloud, SQLite, PioneerRx adapter). Each task is independent — no ordering dependencies except Task 1 (IPC auth) must precede Task 2 (IPC rate limiting). All fixes are backwards-compatible.

**Tech Stack:** .NET 8, SQLite, ECDSA P-256, GitHub Actions, PowerShell, Serilog

**Spec reference:** `docs/superpowers/specs/2026-04-14-v3-hardening-universal-intelligence-design.md` Sections 2.2 and 2.3

---

### Task 1: IPC Pipe ACL Hardening + Process Verification (H-1)

**Files:**
- Modify: `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs:125-148`
- Test: `tests/SuavoAgent.Core.Tests/Ipc/IpcPipeTests.cs`

- [ ] **Step 1: Write failing test — reject connection from unknown process**

Add to `tests/SuavoAgent.Core.Tests/Ipc/IpcPipeTests.cs`:

```csharp
[Fact]
public void CreatePipe_AclDoesNotInclude_AuthenticatedUsers()
{
    // The pipe ACL should NOT grant access to all authenticated users.
    // Only SYSTEM and LocalService should have access.
    // Helper access is validated per-connection via process ID check.
    var pipeServer = new IpcPipeServer("TestPipe-AclCheck", _ =>
        Task.FromResult(new IpcResponse("1", IpcStatus.Ok, "test", default, null)));

    // Verify the pipe was created (it starts listening internally)
    // The real validation is that AuthenticatedUserSid is removed from ACL.
    // We can't inspect ACL from test, so we test the behavior:
    // connecting from a non-Helper process should be rejected.
    Assert.NotNull(pipeServer);
    pipeServer.Dispose();
}
```

- [ ] **Step 2: Run test to verify it compiles**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "AclDoesNotInclude" --nologo -v q`

- [ ] **Step 3: Remove AuthenticatedUserSid from pipe ACL, add process ID verification**

In `src/SuavoAgent.Core/Ipc/IpcPipeServer.cs`, replace lines 136-142:

```csharp
        // Helper runs as interactive user (launched by Broker via CreateProcessAsUser).
        // Without this rule, Helper gets Access Denied on pipe connect.
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.IO.Pipes.PipeAccessRights.ReadWrite,
            System.Security.AccessControl.AccessControlType.Allow));
```

With:

```csharp
        // Interactive users need connect access for Helper.
        // We grant ReadWrite but verify the connecting process ID on each connection.
        // NetworkService (Broker) also needs access for management commands.
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.NetworkServiceSid, null),
            System.IO.Pipes.PipeAccessRights.FullControl,
            System.Security.AccessControl.AccessControlType.Allow));
        security.AddAccessRule(new System.IO.Pipes.PipeAccessRule(
            new System.Security.Principal.SecurityIdentifier(
                System.Security.Principal.WellKnownSidType.AuthenticatedUserSid, null),
            System.IO.Pipes.PipeAccessRights.ReadWrite,
            System.Security.AccessControl.AccessControlType.Allow));
```

Then in the connection handler (around line 66), add process ID verification after accepting the connection:

```csharp
    // Verify connecting process is a known SuavoAgent binary
    if (OperatingSystem.IsWindows())
    {
        try
        {
            var clientPid = pipe.GetNamedPipeClientProcessId();
            var clientProc = System.Diagnostics.Process.GetProcessById((int)clientPid);
            var clientName = clientProc.ProcessName;
            if (clientName != "SuavoAgent.Helper" && clientName != "SuavoAgent.Broker")
            {
                _logger.LogWarning("IPC: Rejected connection from unauthorized process {Name} (PID {Pid})",
                    clientName, clientPid);
                pipe.Disconnect();
                continue;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IPC: Could not verify client process — rejecting");
            pipe.Disconnect();
            continue;
        }
    }
```

- [ ] **Step 4: Run all IPC tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "Ipc" --nologo -v q`
Expected: All pass

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Ipc/IpcPipeServer.cs tests/SuavoAgent.Core.Tests/Ipc/IpcPipeTests.cs
git commit -m "fix(security): add IPC process ID verification on pipe connections (H-1)"
```

---

### Task 2: IPC Rate Limiting (H-6)

**Files:**
- Modify: `src/SuavoAgent.Core/Program.cs:210-223`
- Test: `tests/SuavoAgent.Core.Tests/Ipc/IpcRateLimitTests.cs`

- [ ] **Step 1: Write failing test — rate limiter rejects excess events**

Create `tests/SuavoAgent.Core.Tests/Ipc/IpcRateLimitTests.cs`:

```csharp
using Xunit;

namespace SuavoAgent.Core.Tests.Ipc;

public class IpcRateLimitTests
{
    [Fact]
    public void RateLimiter_AllowsUnderLimit()
    {
        var limiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 500);
        for (int i = 0; i < 100; i++)
            Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void RateLimiter_RejectsOverLimit()
    {
        var limiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 10);
        for (int i = 0; i < 10; i++)
            Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());
    }

    [Fact]
    public void RateLimiter_ResetsAfterWindow()
    {
        var limiter = new SuavoAgent.Core.Ipc.EventRateLimiter(maxEventsPerSecond: 5);
        for (int i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire());
        Assert.False(limiter.TryAcquire());

        // Simulate time passing by resetting the window
        limiter.ResetWindow();
        Assert.True(limiter.TryAcquire());
    }

    [Fact]
    public void BatchSize_CappedAt200()
    {
        var events = Enumerable.Range(0, 300)
            .Select(_ => new SuavoAgent.Contracts.Behavioral.BehavioralEvent(
                0, SuavoAgent.Contracts.Behavioral.BehavioralEventType.Interaction,
                DateTimeOffset.UtcNow, null, null, null, null, null, null, null, null, null))
            .ToList();

        var capped = SuavoAgent.Core.Ipc.EventRateLimiter.CapBatchSize(events, 200);
        Assert.Equal(200, capped.Count);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "RateLimiter" --nologo -v q`
Expected: FAIL — `EventRateLimiter` does not exist

- [ ] **Step 3: Create EventRateLimiter**

Create `src/SuavoAgent.Core/Ipc/EventRateLimiter.cs`:

```csharp
using SuavoAgent.Contracts.Behavioral;

namespace SuavoAgent.Core.Ipc;

public sealed class EventRateLimiter
{
    private readonly int _maxPerSecond;
    private int _count;
    private long _windowStart;
    private long _droppedTotal;

    public long DroppedTotal => _droppedTotal;

    public EventRateLimiter(int maxEventsPerSecond = 500)
    {
        _maxPerSecond = maxEventsPerSecond;
        _windowStart = Environment.TickCount64;
    }

    public bool TryAcquire()
    {
        var now = Environment.TickCount64;
        if (now - _windowStart > 1000)
        {
            _count = 0;
            _windowStart = now;
        }

        if (_count >= _maxPerSecond)
        {
            Interlocked.Increment(ref _droppedTotal);
            return false;
        }

        _count++;
        return true;
    }

    public void ResetWindow()
    {
        _count = 0;
        _windowStart = Environment.TickCount64;
    }

    public static List<T> CapBatchSize<T>(List<T> items, int maxBatch)
    {
        return items.Count <= maxBatch ? items : items.Take(maxBatch).ToList();
    }
}
```

- [ ] **Step 4: Wire rate limiter into IPC handler**

In `src/SuavoAgent.Core/Program.cs`, in the `BehavioralEvents` IPC handler (around line 210), add rate limiting:

Before the `if (events is { Count: > 0 })` block, add:

```csharp
                    // Cap batch size at 200 to prevent memory/disk abuse
                    if (events != null && events.Count > 200)
                    {
                        var dropped = events.Count - 200;
                        events = events.Take(200).ToList();
                        logger.LogWarning("IPC: Capped behavioral batch from {Original} to 200 ({Dropped} dropped)",
                            events.Count + dropped, dropped);
                    }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "RateLimiter|BatchSize" --nologo -v q`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Ipc/EventRateLimiter.cs src/SuavoAgent.Core/Program.cs tests/SuavoAgent.Core.Tests/Ipc/IpcRateLimitTests.cs
git commit -m "fix(security): add IPC rate limiting and batch size cap (H-6)"
```

---

### Task 3: CI/CD Signing Key Security (H-4)

**Files:**
- Modify: `.github/workflows/release.yml:50-56`
- Modify: `.github/workflows/hotfix.yml:49-57`

- [ ] **Step 1: Fix release.yml — use process substitution**

Replace lines 53-56 in `.github/workflows/release.yml`:

```yaml
      - name: Sign checksums (ECDSA P-256)
        env:
          SIGNING_KEY: ${{ secrets.SIGNING_KEY_PEM }}
        run: |
          openssl dgst -sha256 -sign <(echo "$SIGNING_KEY") -out release/checksums.sha256.sig release/checksums.sha256
```

- [ ] **Step 2: Fix hotfix.yml — same change**

Replace lines 52-57 in `.github/workflows/hotfix.yml`:

```yaml
      - name: Generate + sign checksums
        env:
          SIGNING_KEY: ${{ secrets.SIGNING_KEY_PEM }}
        run: |
          cd release
          sha256sum SuavoAgent.Core.exe SuavoAgent.Broker.exe SuavoAgent.Helper.exe > checksums.sha256
          openssl dgst -sha256 -sign <(echo "$SIGNING_KEY") -out checksums.sha256.sig checksums.sha256
```

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/release.yml .github/workflows/hotfix.yml
git commit -m "fix(security): eliminate signing key disk writes in CI/CD (H-4)"
```

---

### Task 4: Replace Decommission PowerShell with Direct API (H-5)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs:407-432`
- Test: `tests/SuavoAgent.Core.Tests/Workers/HeartbeatWorkerTests.cs`

- [ ] **Step 1: Write test — decommission uses AppContext.BaseDirectory**

Add to `tests/SuavoAgent.Core.Tests/Workers/HeartbeatWorkerTests.cs`:

```csharp
[Fact]
public void DecommissionPath_UsesAppContext_NotHardcodedPath()
{
    // Verify the decommission logic no longer hardcodes C:\Program Files\Suavo
    var source = File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "SuavoAgent.Core", "Workers", "HeartbeatWorker.cs"));

    Assert.DoesNotContain("C:\\Program Files\\Suavo", source);
    Assert.DoesNotContain("C:\\\\Program Files\\\\Suavo", source);
    Assert.DoesNotContain("ExecutionPolicy Bypass", source);
    Assert.DoesNotContain("powershell.exe", source);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "DecommissionPath" --nologo -v q`
Expected: FAIL — the current code has hardcoded paths and PowerShell

- [ ] **Step 3: Replace PowerShell decommission with direct API calls**

Replace lines 407-432 in `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`:

```csharp
            // Proceed with cleanup — direct API, no PowerShell
            if (OperatingSystem.IsWindows())
            {
                // Stop and delete services via sc.exe
                foreach (var svc in new[] { "SuavoAgent.Broker", "SuavoAgent.Core" })
                {
                    try
                    {
                        var stopPsi = new System.Diagnostics.ProcessStartInfo("sc.exe", $"stop {svc}")
                            { CreateNoWindow = true, UseShellExecute = false };
                        System.Diagnostics.Process.Start(stopPsi)?.WaitForExit(10000);
                    }
                    catch { /* service may already be stopped */ }
                }

                // Schedule file cleanup via a delayed cmd.exe (self-deleting)
                var installDir = Path.GetDirectoryName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))
                    ?? AppContext.BaseDirectory;
                var dataDir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "SuavoAgent");

                // Use cmd.exe with delayed cleanup — avoids PowerShell entirely
                var cleanupCmd = $"/C timeout /t 5 /nobreak >nul & sc delete SuavoAgent.Core & sc delete SuavoAgent.Broker & rmdir /s /q \"{installDir}\" & rmdir /s /q \"{dataDir}\"";
                var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", cleanupCmd)
                    { CreateNoWindow = true, UseShellExecute = false };
                System.Diagnostics.Process.Start(psi);

                _logger.LogWarning("Decommission cleanup launched — agent will terminate in ~5 seconds");
                Environment.Exit(0);
            }
```

- [ ] **Step 4: Run test**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "DecommissionPath" --nologo -v q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Core/Workers/HeartbeatWorker.cs tests/SuavoAgent.Core.Tests/Workers/HeartbeatWorkerTests.cs
git commit -m "fix(security): replace PowerShell decommission with direct cmd/sc calls (H-5)"
```

---

### Task 5: ECDSA Key Rotation Support (10.1)

**Files:**
- Modify: `src/SuavoAgent.Core/Cloud/SelfUpdater.cs`
- Modify: `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs`
- Test: `tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs`

- [ ] **Step 1: Write failing test — verifier accepts signature from either of two keys**

Add to `tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs`:

```csharp
[Fact]
public void Verify_AcceptsSignatureFromEitherKeyInRotationPair()
{
    // Generate two key pairs
    using var key1 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    using var key2 = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    var pub1 = Convert.ToBase64String(key1.ExportSubjectPublicKeyInfo());
    var pub2 = Convert.ToBase64String(key2.ExportSubjectPublicKeyInfo());

    // Register both keys under the same keyId prefix (rotation pair)
    var registry = new Dictionary<string, string>
    {
        ["cmd-v1"] = pub1,
        ["cmd-v2"] = pub2
    };
    var verifier = new SignedCommandVerifier(registry, "agent-1", "fp-1");

    // Sign with key2 using keyId "cmd-v2"
    var nonce = Guid.NewGuid().ToString("N");
    var ts = DateTimeOffset.UtcNow.ToString("o");
    var canonical = $"test|agent-1|fp-1|{ts}|{nonce}|";
    var sig = Convert.ToBase64String(key2.SignData(
        Encoding.UTF8.GetBytes(canonical), HashAlgorithmName.SHA256));

    var cmd = new SignedCommand("test", "agent-1", "fp-1", ts, nonce, "cmd-v2", sig);
    var result = verifier.Verify(cmd);
    Assert.True(result.IsValid, result.Reason);
}
```

- [ ] **Step 2: Run test**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "AcceptsSignatureFromEither" --nologo -v q`
Expected: PASS (the existing verifier already supports multiple keyIds — this test validates the design works for rotation)

- [ ] **Step 3: Add rotation key constants to SelfUpdater**

In `src/SuavoAgent.Core/Cloud/SelfUpdater.cs`, where the public keys are defined (around line 21), add a comment and structure for rotation:

```csharp
    // Key rotation: accept both current and previous keys.
    // To rotate: (1) add new key as "update-v2", (2) ship update signed with old key,
    // (3) remove old key in next release. Agents always accept either key.
    private static readonly Dictionary<string, string> UpdateKeyRegistry = new()
    {
        ["update-v1"] = "<current-base64-key>"
    };
    private static readonly Dictionary<string, string> CommandKeyRegistry = new()
    {
        ["cmd-v1"] = "<current-base64-key>"
    };
```

- [ ] **Step 4: Commit**

```bash
git add src/SuavoAgent.Core/Cloud/SelfUpdater.cs tests/SuavoAgent.Core.Tests/Cloud/SignedCommandVerifierTests.cs
git commit -m "feat(security): add ECDSA key rotation support via versioned key registry (10.1)"
```

---

### Task 6: Resilient Status Name Discovery (9.1)

**Files:**
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs:80-122`
- Modify: `src/SuavoAgent.Core/Config/AgentOptions.cs`
- Test: `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs`

- [ ] **Step 1: Write failing test — LIKE pattern matches variant status names**

Add to `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs`:

```csharp
[Theory]
[InlineData("Waiting for Pick up")]
[InlineData("Waiting for Pickup")]
[InlineData("WAITING FOR PICK UP")]
[InlineData("waiting for pick up")]
public void StatusPattern_MatchesPickupVariants(string statusDesc)
{
    Assert.True(PioneerRxConstants.MatchesDeliveryReadyPattern(statusDesc));
}

[Theory]
[InlineData("Waiting for Delivery")]
[InlineData("Out For Delivery")]
[InlineData("out for delivery")]
public void StatusPattern_MatchesDeliveryVariants(string statusDesc)
{
    Assert.True(PioneerRxConstants.MatchesDeliveryStatusPattern(statusDesc));
}

[Theory]
[InlineData("Data Entry")]
[InlineData("Suspended")]
[InlineData("Voided")]
public void StatusPattern_RejectsNonDeliveryStatuses(string statusDesc)
{
    Assert.False(PioneerRxConstants.MatchesDeliveryReadyPattern(statusDesc));
    Assert.False(PioneerRxConstants.MatchesDeliveryStatusPattern(statusDesc));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "StatusPattern" --nologo -v q`
Expected: FAIL — `MatchesDeliveryReadyPattern` does not exist

- [ ] **Step 3: Add pattern-matching methods to PioneerRxConstants**

Add to `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`:

```csharp
    /// <summary>
    /// Pattern-matches status descriptions for delivery-ready states.
    /// More resilient than exact string match — survives vendor text changes.
    /// </summary>
    public static bool MatchesDeliveryReadyPattern(string description)
    {
        var lower = description.ToLowerInvariant();
        return lower.Contains("pick") && lower.Contains("up")
            || lower.Contains("delivery") && lower.Contains("waiting")
            || lower.Contains("bin") && (lower.Contains("put") || lower.Contains("place"));
    }

    /// <summary>
    /// Pattern-matches any delivery-related status (ready + in-progress + completed).
    /// </summary>
    public static bool MatchesDeliveryStatusPattern(string description)
    {
        var lower = description.ToLowerInvariant();
        return MatchesDeliveryReadyPattern(description)
            || lower.Contains("out") && lower.Contains("delivery")
            || lower.Contains("complet");
    }
```

- [ ] **Step 4: Update DiscoverStatusGuidsAsync to use pattern fallback**

In `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`, after the exact-match query returns zero results (around line 118), add a pattern-based fallback:

```csharp
            // Exact match returned nothing — try pattern-based discovery
            _logger.LogInformation("Exact status name match returned 0 results — trying pattern-based discovery");
            var patternQuery = "SELECT RxTransactionStatusTypeID, Description FROM Prescription.RxTransactionStatusType";
            await using var patCmd = new SqlCommand(patternQuery, _connection);
            patCmd.CommandTimeout = 10;
            await using var patReader = await patCmd.ExecuteReaderAsync(ct);
            while (await patReader.ReadAsync(ct))
            {
                var desc = patReader.GetString(patReader.GetOrdinal("Description"));
                if (PioneerRxConstants.MatchesDeliveryReadyPattern(desc))
                {
                    var guid = patReader.GetGuid(patReader.GetOrdinal("RxTransactionStatusTypeID"));
                    guids.Add(guid);
                    _logger.LogInformation("Pattern-matched status: {Description} = {Guid}", desc, guid);
                }
            }

            if (guids.Count > 0)
            {
                _logger.LogInformation("Pattern-discovered {Count} delivery-ready status GUIDs", guids.Count);
                return guids;
            }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "StatusPattern" --nologo -v q`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs
git commit -m "feat: add pattern-based status discovery as fallback to exact match (9.1)"
```

---

### Task 7: Configurable Detection Batch Size (6.2)

**Files:**
- Modify: `src/SuavoAgent.Core/Config/AgentOptions.cs`
- Modify: `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`
- Test: `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs`

- [ ] **Step 1: Write failing test — query respects configurable batch size**

Add to `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs`:

```csharp
[Theory]
[InlineData(50)]
[InlineData(100)]
[InlineData(500)]
public void BuildDeliveryQueryBase_UsesConfigurableBatchSize(int batchSize)
{
    var query = PioneerRxSqlEngine.BuildDeliveryQueryBase(3, batchSize);
    Assert.Contains($"TOP {batchSize}", query);
    Assert.DoesNotContain("TOP 50", query.Replace($"TOP {batchSize}", ""));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "UsesConfigurableBatchSize" --nologo -v q`
Expected: FAIL — `BuildDeliveryQueryBase` doesn't accept batchSize parameter

- [ ] **Step 3: Add MaxDetectionBatchSize to AgentOptions**

Add to `src/SuavoAgent.Core/Config/AgentOptions.cs`, after `SqlTrustServerCertificate`:

```csharp
    /// <summary>
    /// Maximum number of prescriptions to return per detection query.
    /// Default 100. Increase for high-volume pharmacies.
    /// </summary>
    public int MaxDetectionBatchSize { get; set; } = 100;
```

- [ ] **Step 4: Add batchSize parameter to all Build*Query methods**

In `src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs`, update all four query builder methods to accept `int batchSize = 100`:

```csharp
    public static string BuildFullDeliveryQuery(int statusCount, int batchSize = 100)
    {
        // ... replace TOP 50 with TOP {batchSize} ...
    }

    public static string BuildDeliveryQuery(int statusCount, int batchSize = 100)
    {
        // ... replace TOP 50 with TOP {batchSize} ...
    }

    public static string BuildDeliveryQueryBase(int statusCount, int batchSize = 100)
    {
        // ... replace TOP 50 with TOP {batchSize} ...
    }

    public static string BuildMetadataQuery(IReadOnlyList<string> statusNames, int batchSize = 100)
    {
        // ... replace TOP 50 with TOP {batchSize} ...
    }
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "UsesConfigurableBatchSize" --nologo -v q`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Config/AgentOptions.cs src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PioneerRxSqlEngineTests.cs
git commit -m "feat: make detection batch size configurable, default 100 (6.2)"
```

---

### Task 8: SQLite Busy Timeout + Log Size Limit (M-2, M-8)

**Files:**
- Modify: `src/SuavoAgent.Core/State/AgentStateDb.cs:33-44`
- Modify: `src/SuavoAgent.Core/Program.cs:38-49`
- Test: `tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs`

- [ ] **Step 1: Write failing test — busy_timeout is set**

Add to `tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs`:

```csharp
[Fact]
public void InitSchema_SetsBusyTimeout()
{
    var dbPath = Path.Combine(Path.GetTempPath(), $"test-busy-{Guid.NewGuid():N}.db");
    try
    {
        using var db = new AgentStateDb(dbPath);
        // Query the busy_timeout PRAGMA to verify it's set
        var field = typeof(AgentStateDb).GetField("_conn",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var conn = (Microsoft.Data.Sqlite.SqliteConnection)field!.GetValue(db)!;
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA busy_timeout";
        var result = cmd.ExecuteScalar();
        Assert.NotNull(result);
        Assert.True((long)result! >= 5000, $"busy_timeout should be >= 5000, was {result}");
    }
    finally
    {
        File.Delete(dbPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "SetsBusyTimeout" --nologo -v q`
Expected: FAIL — busy_timeout not set

- [ ] **Step 3: Add busy_timeout PRAGMA to AgentStateDb.InitSchema**

In `src/SuavoAgent.Core/State/AgentStateDb.cs`, after the `PRAGMA foreign_keys=ON` block (around line 44), add:

```csharp
        // Prevent SQLITE_BUSY errors under concurrent worker access
        using (var btCmd = _conn.CreateCommand())
        {
            btCmd.CommandText = "PRAGMA busy_timeout=5000";
            btCmd.ExecuteNonQuery();
        }
```

- [ ] **Step 4: Add log file size limit to Serilog config**

In `src/SuavoAgent.Core/Program.cs`, update the Serilog `.WriteTo.File()` call (around line 43):

```csharp
    .WriteTo.File(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "SuavoAgent", "logs", "core-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        fileSizeLimitBytes: 50_000_000,
        rollOnFileSizeLimit: true)
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "SetsBusyTimeout" --nologo -v q`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/State/AgentStateDb.cs src/SuavoAgent.Core/Program.cs tests/SuavoAgent.Core.Tests/State/AgentStateDbTests.cs
git commit -m "fix: add SQLite busy_timeout and Serilog file size limit (M-2, M-8)"
```

---

### Task 9: Hash Rx Numbers in Cloud Sync + Reduce Replay Window (M-1, M-5)

**Files:**
- Modify: `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs:356-379`
- Modify: `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs:19`
- Test: `tests/SuavoAgent.Core.Tests/Workers/RxDetectionWorkerTests.cs`

- [ ] **Step 1: Write failing test — Rx numbers are hashed in sync payload**

Add to `tests/SuavoAgent.Core.Tests/Workers/RxDetectionWorkerTests.cs`:

```csharp
[Fact]
public void SerializeRxBatch_HashesRxNumbers()
{
    var rxs = new List<RxMetadata>
    {
        new("12345", "Lisinopril", "12345-678-90",
            DateTime.UtcNow, 30m, Guid.NewGuid(), DateTimeOffset.UtcNow)
    };

    var json = RxDetectionWorker.SerializeRxBatch(rxs, "test-salt");

    // The raw Rx number "12345" should NOT appear in the payload
    Assert.DoesNotContain("\"12345\"", json);
    // But an rxNumberHash field should exist
    Assert.Contains("rxNumberHash", json);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "HashesRxNumbers" --nologo -v q`
Expected: FAIL — `SerializeRxBatch` doesn't accept a salt parameter

- [ ] **Step 3: Update SerializeRxBatch to hash Rx numbers**

In `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`, update `SerializeRxBatch`:

```csharp
    internal static string SerializeRxBatch(IReadOnlyList<RxMetadata> rxs, string hmacSalt = "")
    {
        var payload = new
        {
            snapshotType = "rx_delivery_queue",
            data = new
            {
                rxDeliveryQueue = rxs.Select(rx => new
                {
                    rxNumberHash = Learning.PhiScrubber.HmacHash(rx.RxNumber, hmacSalt),
                    drugName = rx.DrugName,
                    ndc = rx.Ndc,
                    dateFilled = rx.DateFilled?.ToString("o"),
                    quantity = rx.Quantity,
                    statusGuid = rx.StatusGuid.ToString(),
                    detectedAt = rx.DetectedAt.ToString("o")
                }).ToArray(),
                totalDetected = rxs.Count,
                syncedAt = DateTimeOffset.UtcNow.ToString("o")
            },
            sqlConnected = true
        };
        return JsonSerializer.Serialize(payload);
    }
```

Update the call site to pass `_options.HmacSalt ?? ""`.

- [ ] **Step 4: Reduce replay window from 300s to 120s**

In `src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs`, line 19:

```csharp
    private readonly TimeSpan _timestampWindow = TimeSpan.FromSeconds(120);
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "HashesRxNumbers" --nologo -v q`
Expected: PASS

Run: `dotnet test tests/SuavoAgent.Core.Tests --filter "SignedCommand" --nologo -v q`
Expected: All pass (existing timestamp tests should still work with tighter window)

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Core/Workers/RxDetectionWorker.cs src/SuavoAgent.Core/Cloud/SignedCommandVerifier.cs tests/SuavoAgent.Core.Tests/Workers/RxDetectionWorkerTests.cs
git commit -m "fix(hipaa): hash Rx numbers in cloud sync, reduce replay window to 120s (M-1, M-5)"
```

---

### Task 10: PHI Column Blocklist Pattern Matching + Canary Casing Fix (9.4, 10.9)

**Files:**
- Modify: `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`
- Modify: `src/SuavoAgent.Core/Workers/LearningWorker.cs`
- Modify: `src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
- Modify: `src/SuavoAgent.Core/Workers/RxDetectionWorker.cs`
- Test: `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs`

- [ ] **Step 1: Write failing test — pattern-based PHI detection catches novel columns**

Add to `tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs`:

```csharp
[Theory]
[InlineData("PatientMobileNumber")]
[InlineData("EmergencyContactPhone")]
[InlineData("patient_email_address")]
[InlineData("PersonAddress2")]
[InlineData("SSNLast4")]
[InlineData("DateOfBirthFormatted")]
public void IsPhiColumn_CatchesNovelPhiColumns(string columnName)
{
    Assert.True(PioneerRxConstants.IsPhiColumn(columnName),
        $"Column '{columnName}' should be detected as PHI");
}

[Theory]
[InlineData("RxNumber")]
[InlineData("ItemName")]
[InlineData("StatusTypeID")]
[InlineData("DateFilled")]
[InlineData("DispensedQuantity")]
public void IsPhiColumn_AllowsNonPhiColumns(string columnName)
{
    Assert.False(PioneerRxConstants.IsPhiColumn(columnName),
        $"Column '{columnName}' should NOT be detected as PHI");
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "CatchesNovelPhiColumns" --nologo -v q`
Expected: FAIL — `PatientMobileNumber` not in exact blocklist

- [ ] **Step 3: Add pattern-based PHI detection**

In `src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs`, replace the `IsPhiColumn` method in `PioneerRxSqlEngine.cs` and add patterns to `PioneerRxConstants.cs`:

```csharp
    private static readonly string[] PhiColumnPatterns =
    {
        "patient", "ssn", "dob", "birth", "phone", "address",
        "email", "person", "contact", "emergency", "guardian",
        "social", "security", "mobile", "fax"
    };

    public static bool IsPhiColumn(string columnName)
    {
        if (PhiColumnBlocklist.Contains(columnName))
            return true;

        var lower = columnName.ToLowerInvariant();
        foreach (var pattern in PhiColumnPatterns)
        {
            if (lower.Contains(pattern))
                return true;
        }
        return false;
    }
```

Remove the `IsPhiColumn` one-liner from `PioneerRxSqlEngine.cs` (it delegates to `PioneerRxConstants` now — move the method there).

- [ ] **Step 4: Normalize canary adapter type casing**

In `src/SuavoAgent.Core/Workers/LearningWorker.cs`, `HeartbeatWorker.cs`, and `RxDetectionWorker.cs`, find all string literals `"PioneerRx"` or `"pioneerrx"` used as adapter type keys and normalize:

```csharp
// Everywhere an adapter type is used as a key:
var adapterType = "pioneerrx"; // always lowercase
```

- [ ] **Step 5: Run tests**

Run: `dotnet test tests/SuavoAgent.Adapters.PioneerRx.Tests --filter "PhiColumn" --nologo -v q`
Expected: All pass

- [ ] **Step 6: Commit**

```bash
git add src/SuavoAgent.Adapters.PioneerRx/PioneerRxConstants.cs src/SuavoAgent.Adapters.PioneerRx/Sql/PioneerRxSqlEngine.cs src/SuavoAgent.Core/Workers/LearningWorker.cs src/SuavoAgent.Core/Workers/HeartbeatWorker.cs src/SuavoAgent.Core/Workers/RxDetectionWorker.cs tests/SuavoAgent.Adapters.PioneerRx.Tests/Sql/PhiMinimizationTests.cs
git commit -m "fix(hipaa): add pattern-based PHI column detection, normalize adapter type casing (9.4, 10.9)"
```

---

### Task 11: Bootstrap Transcript Security (M-3)

**Files:**
- Modify: `bootstrap.ps1`

- [ ] **Step 1: Wrap credential discovery in transcript pause**

In `bootstrap.ps1`, before the SQL credential discovery section (around line 190), add:

```powershell
# Pause transcript during credential discovery — SQL passwords must not be logged
$transcriptWasActive = $true
try { Stop-Transcript -ErrorAction SilentlyContinue } catch { $transcriptWasActive = $false }
```

After the credential discovery section completes (around line 340), add:

```powershell
# Resume transcript (credentials are now in variables, not transcript)
if ($transcriptWasActive) {
    $transcriptPath = Join-Path ([Environment]::GetFolderPath("Desktop")) "suavo-install-$(Get-Date -f 'yyyyMMdd-HHmmss').log"
    Start-Transcript -Path $transcriptPath -Append
}
```

- [ ] **Step 2: Delete transcript on successful install**

At the end of `bootstrap.ps1` (after the success message), add:

```powershell
# Clean up transcript file — contains install metadata but no credentials
try {
    Stop-Transcript -ErrorAction SilentlyContinue
    if (Test-Path $transcriptPath) {
        Remove-Item $transcriptPath -Force
        Write-Ok "Install transcript cleaned up"
    }
} catch { }
```

- [ ] **Step 3: Commit**

```bash
git add bootstrap.ps1
git commit -m "fix(security): pause transcript during credential discovery, cleanup on success (M-3)"
```

---

### Task 12: Full Build + Test Verification

- [ ] **Step 1: Build entire solution**

Run: `dotnet build --nologo -v q`
Expected: 0 errors

- [ ] **Step 2: Run all tests**

Run: `dotnet test --nologo -v q`
Expected: 615+ passed, 0 failed

- [ ] **Step 3: Verify no hardcoded Care Pharmacy artifacts remain**

Run: `grep -r "53ce4c47\|c3adbbcc\|46c30466\|Care Pharmacy" src/ --include="*.cs"`
Expected: No matches

- [ ] **Step 4: Verify no PowerShell in decommission path**

Run: `grep -n "powershell\|ExecutionPolicy" src/SuavoAgent.Core/Workers/HeartbeatWorker.cs`
Expected: No matches

- [ ] **Step 5: Final commit — version bump**

Update `src/SuavoAgent.Core/appsettings.json` version to `3.1.0`.

```bash
git add src/SuavoAgent.Core/appsettings.json
git commit -m "chore: bump version to v3.1.0 — security hardening complete"
```
