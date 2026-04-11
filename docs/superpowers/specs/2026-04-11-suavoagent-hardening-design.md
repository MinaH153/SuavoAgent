# SuavoAgent Hardening & Features Spec (v2 — Post-Review)

**Date:** 2026-04-11
**Author:** Joshua Henein + Claude + Codex (adversarial review, two rounds)
**Repo:** github.com/MinaH153/SuavoAgent
**Branch:** main
**Status:** Approved design, pending implementation
**Review:** 36 findings from Claude + Codex incorporated. All CRITICAL and HIGH issues resolved.

---

## Scope

13 items across three tracks: Security (5), HIPAA (4), Features (3), Bugfix (1).
Multi-pharmacy explicitly deferred to cloud/dashboard session — agent stays single-pharmacy, identified by `PharmacyId` + `MachineFingerprint` (WMI UUID, matching existing `bootstrap.ps1:333`).

## Constraints (NON-NEGOTIABLE)

- Read-only SQL against PioneerRx. Never write to PioneerRx DB.
- PHI never in logs. PHI never unencrypted at rest. Audit trail on every PHI access.
- Invisible behavior (low-frequency, single connection, no UI interaction unless explicitly commanded).
- No identity impersonation (no faking Application Name — Codex rejected as legally risky).
- Must survive reboots, crashes, network drops, sleep.
- Self-update must work even if HeartbeatWorker fails to start.
- Decommission must preserve audit data before wiping.
- No inbound network ports on the agent (flat pharmacy LAN = attack surface).

## Current State (Verified Against Code)

- 49 tests green (6 Contracts + 19 Adapter + 24 Core)
- Live at 1 pharmacy (Nadim's on v1.5.0 Node.js agent, .NET v2 tested at Care before decommission)
- Three-process model (Broker/Core/Helper) built but IPC not wired end-to-end
- `IpcPipeServer.cs:107-112`: `SendCommandAsync` returns null with "not yet implemented" log
- `WritebackProcessor.cs:111`: logs "Would send writeback {TaskId} to Helper via IPC"
- ECDSA-signed updates, HMAC cloud comms, DPAPI key management in place
- Critical gaps: URL validation bypass, unsigned bootstrap downloads, PHI over-collection, audit chain broken

### Actual State Machine (from `WritebackStateMachine.cs`)

States: `Queued`, `BlockedInteractive`, `Claimed`, `InProgress`, `VerifyPending`, `Verified`, `Done`, `ManualReview`

Triggers: `Claim`, `UserActive`, `UserIdle`, `StartUia`, `WriteComplete`, `VerifyMatch`, `VerifyMismatch`, `SystemError`, `BusinessError`, `SyncComplete`, `HelperDisconnected`

MaxRetries: Already enforced at `WritebackStateMachine.cs:39` (`MaxRetries = 3`) with force-to-ManualReview at lines 97-102 via `BusinessError` trigger.

### Actual IPC Records (from `IpcMessage.cs`)

```csharp
// CURRENT (will be migrated)
record IpcMessage(int Version, string RequestId, string Command, string? Payload);
record IpcResponse(string RequestId, bool Success, string? Result, string? Error);

// CURRENT COMMANDS
IpcCommands: Ping, ReadGrid, WritebackDelivery, DiscoverScreen, DismissModal, CheckUserActivity, Drain
```

---

## Track 1: Security Hardening

### Item 1: SelfUpdater URL Validation Fix

**Problem:** `SelfUpdater.cs:42-46` uses `uri.Host.EndsWith("github.com")` for URL allowlist (inlined if-chain, not array). Attacker bypasses with `github.com.attacker.com`.

**Fix:** Replace with `Uri.Host` exact match + HTTPS scheme enforcement.

```csharp
// CURRENT CODE (SelfUpdater.cs:42-46, vulnerable)
|| (!uri.Host.EndsWith("github.com", StringComparison.OrdinalIgnoreCase)
    && !uri.Host.EndsWith("suavollc.com", StringComparison.OrdinalIgnoreCase)
    && !uri.Host.EndsWith("githubusercontent.com", StringComparison.OrdinalIgnoreCase))

// REPLACEMENT
private static readonly HashSet<string> AllowedHosts = new(StringComparer.OrdinalIgnoreCase)
{
    "github.com",
    "suavollc.com",
    "raw.githubusercontent.com",
    "objects.githubusercontent.com",
    "github-releases.githubusercontent.com"
};

// In ValidateUpdateUrl:
if (uri.Scheme != "https" || !AllowedHosts.Contains(uri.Host))
    throw new SecurityException($"Update URL host '{uri.Host}' not in allowlist");
```

**Files:** `SelfUpdater.cs`
**Tests:** Exact host match passes, subdomain attack (`github.com.evil.com`) rejected, non-HTTPS rejected, HTTP with valid host rejected, each allowed host passes, empty/null URL throws.

---

### Item 2: Package-Level Self-Update (All Three Binaries)

**Problem:** `SelfUpdater.cs:75-78` only swaps `SuavoAgent.Core.exe` (renames current→.old, .new→current). Broker and Helper stay at old versions. Guaranteed version skew.

**Design:**

New manifest format (ECDSA-signed, one line):
```
core_url|core_sha256|broker_url|broker_sha256|helper_url|helper_sha256|version|runtime|arch
```

Added `runtime` (e.g., `net8.0`) and `arch` (e.g., `win-x64`) fields per Codex finding — agent verifies these match before downloading.

**Update orchestration (resolves three-binary restart problem):**

```
1. HeartbeatWorker receives signed update manifest
2. SelfUpdater downloads all three binaries to .exe.new temp files
3. Verifies each SHA256 hash + max file size (200MB cap per binary)
4. Core sends "drain" IPC command to Helper (graceful shutdown)
5. Core writes sentinel file: install_dir/update-pending.flag
   Contents: { version, timestamp, binaryHashes }
6. Core swaps all three .exe files sequentially:
   a. Core.exe:   current → .old, .new → current
   b. Broker.exe: current → .old, .new → current
   c. Helper.exe: current → .old, .new → current
   If ANY rename fails: rollback all completed renames, delete remaining .new files
7. Core exits with code 1 → SCM restarts Core with new binary
8. New Core on startup: deletes update-pending.flag
9. Broker on next SessionWatcher poll: sees no flag, continues normally
   (Broker checks for flag before launching Helper — holds off if present)
10. Broker launches new Helper into user session
```

**Partial swap recovery:**
On startup, Core checks for inconsistent state:
- If any `.exe.old` exists but corresponding current is missing → restore from `.old`
- If `update-pending.flag` exists but Core version matches old version → swap failed, clean up `.new` files
- If Core crashes within 60s of startup (tracked via `startup_timestamp` in state DB) AND `.exe.old` files exist → rollback all three

**Files:** `SelfUpdater.cs` (orchestration rewrite), `UpdateManifest.cs` (new record type), `HeartbeatWorker.cs` (sentinel flag)
**Tests:** Three-binary download + verify, partial rename failure → rollback, max file size exceeded → abort, wrong runtime/arch → reject, sentinel flag lifecycle, crash-within-60s rollback.

---

### Item 3: Bootstrap Self-Update in Program.cs

**Problem:** Self-update only runs inside `HeartbeatWorker.cs:74-75`. If HeartbeatWorker fails to start (DI error, config missing), agent is stranded at old version forever.

**Design:**

Add pre-host update check at top of `Program.cs`, before `Host.CreateApplicationBuilder` (actual API per `Program.cs:24`):

```csharp
// Program.cs — FIRST THING, before any DI/config
using var earlyLog = new LoggerConfiguration()
    .WriteTo.File(Path.Combine(dataDir, "logs", "startup-.log"), rollingInterval: RollingInterval.Day)
    .CreateLogger();

if (SelfUpdater.CheckPendingUpdate(earlyLog))
{
    // .exe.new files found, ECDSA manifest verified, all three binaries swapped
    Environment.Exit(1); // SCM restarts with new binary
}

// Then normal host setup
var builder = Host.CreateApplicationBuilder(args);
```

`CheckPendingUpdate` is a static method — no DI, no config, no host. Uses Serilog's static `Log.Logger` for early logging. Steps:
1. Scan install directory for any `.exe.new` files
2. Read `update-pending.flag` sentinel (written by Item 2 during download)
3. Verify ECDSA signature of the manifest stored in sentinel
4. If valid: swap all three binaries (same logic as Item 2, step 6)
5. If invalid or no sentinel: delete `.exe.new` files, log warning, continue with current binary

**Relationship to Item 2:** Item 2 downloads and creates the `.exe.new` files + sentinel. Item 3 is the "on next boot, finish the swap" path. Both use the same swap logic (extracted to `SelfUpdater.SwapBinaries()`).

**Files:** `Program.cs`, `SelfUpdater.cs` (add static `CheckPendingUpdate` + shared `SwapBinaries`)
**Tests:** Sentinel + .new files → swap + exit, invalid sig → delete + continue, no .new files → no-op, missing sentinel but .new files exist → delete .new files (safety).

---

### Item 4: Signed Control-Plane Commands

**Problem:** Decommission and update are destructive actions trusted from raw JSON responses (`SuavoCloudClient.cs:34-53` returns unvalidated `JsonElement`, `HeartbeatWorker.cs:107-141` acts on it). Compromised cloud = fleet-wide destruction.

**Design:**

**Signed command envelope:**
```json
{
    "command": "decommission",
    "agentId": "agent-uuid",
    "machineFingerprint": "wmi-uuid",
    "timestamp": "2026-04-11T10:00:00Z",
    "nonce": "random-uuid",
    "keyId": "cmd-key-v1",
    "signature": "base64-ecdsa-signature"
}
```

**Key management (resolves Codex key rotation finding):**
- `keyId` field identifies which public key to verify against
- Agent ships with a key registry: `Dictionary<string, ECDsa>` mapping keyId → public key
- Rotation: new agent version includes new keyId + public key. Old key remains valid for 30 days.
- Revocation: agent update removes old keyId from registry. Cloud stops signing with revoked key.
- Server-side key stored in Supabase Vault (not env var — Codex flagged env var as too exposed).

**Verification on agent (`SignedCommandVerifier.cs`):**
1. Parse envelope from heartbeat response field `signedCommand`
2. Look up public key by `keyId` — unknown keyId → reject
3. Verify ECDSA signature over canonical form: `command|agentId|machineFingerprint|timestamp|nonce`
4. Verify `agentId` matches own `AgentOptions.AgentId`
5. Verify `machineFingerprint` matches own (WMI UUID from `bootstrap.ps1:333`, NOT a new formula — preserves existing enrollments)
6. Verify `timestamp` within 300s window (widened from 60s — pharmacy clocks drift, NTP not guaranteed)
7. Verify `nonce` not seen before (stored in SQLite `command_nonces` table, pruned entries older than 24h)

**Decommission two-phase confirmation:**
1. First signed `decommission` → enter `DecommissionPending` state, log to audit, report in next heartbeat
2. Second signed `decommission` received 5+ minutes after first (measured by agent monotonic clock, not wall clock — immune to clock drift) → execute decommission with audit preservation (Item 9)
3. No second command within 1 hour → cancel, resume normal operation, log cancellation

**Commands requiring signed envelopes:**
- `decommission` (two-phase)
- `update` (single-phase, ECDSA manifest is secondary verification)
- `force_sync` (single-phase)
- `run_diagnostics` (single-phase)
- `fetch_patient` (single-phase — **CRITICAL**: must be signed per Codex finding, see Item 6)

**New SQLite table:**
```sql
CREATE TABLE IF NOT EXISTS command_nonces (
    nonce TEXT PRIMARY KEY,
    received_at TEXT NOT NULL
);
```

**Files:** `SignedCommandVerifier.cs` (new), `HeartbeatWorker.cs` (envelope parsing), `SuavoCloudClient.cs` (response validation), `AgentStateDb.cs` (nonce table + pruning)
**Tests:** Valid sig accepted, invalid rejected, wrong agentId rejected, wrong fingerprint rejected, expired timestamp (>300s) rejected, nonce replay rejected, unknown keyId rejected, two-phase decommission flow, monotonic clock isolation, auto-cancel after 1h, key rotation with overlap period.

---

### Item 5: Bootstrap Installer Signature Verification

**Problem:** `bootstrap.ps1:311-323` downloads three EXEs from `https://github.com/MinaH153/SuavoAgent/releases/download/$ReleaseTag/` with no hash or signature verification. Account compromise = install-time RCE as Administrator.

**Design:**

**Hash + signature verification in bootstrap:**

The GitHub release includes `checksums.sha256` and `checksums.sha256.sig`:
```
abc123def456...  SuavoAgent.Core.exe
789012fed345...  SuavoAgent.Broker.exe
456789abc012...  SuavoAgent.Helper.exe
```

`bootstrap.ps1` changes (after line 310):
1. Download `checksums.sha256` and `checksums.sha256.sig`
2. Verify ECDSA P-256 signature using .NET `System.Security.Cryptography.ECDsa` (public key embedded in bootstrap script as Base64)
3. Download each EXE
4. Compute `Get-FileHash -Algorithm SHA256` for each
5. Compare against checksums file
6. ANY mismatch → delete all downloaded files, `Write-Error`, `exit 1`
7. All pass → proceed with install

**publish.ps1 changes (after line 55):**
1. Compute SHA256 of each built binary
2. Write `checksums.sha256` to publish directory
3. Sign with ECDSA private key (reads from `~/.suavo/signing-key.pem`) → write `checksums.sha256.sig`
4. Both files included in `gh release create` upload

**Authenticode:** Deferred. Requires code signing certificate ($200-500/yr). Not blocking — hash verification provides equivalent tamper detection for direct downloads.

**Files:** `bootstrap.ps1`, `publish.ps1`
**Tests:** Automated test in `tests/Bootstrap.Verification.Tests.ps1`: create temp binaries, sign, verify passes, tamper one binary, verify fails. Also manual Parallels VM test.

---

## Track 2: HIPAA Compliance

### Item 6: PHI Minimization Gate

**Problem:** `PioneerRxSqlEngine.cs:204-236` (`BuildFullDeliveryQuery`) joins `Person.Person` and queries `per.FirstName`, `per.Phone1`, `per.Address1` etc. for ALL ready prescriptions. `RxDetectionWorker.cs:165-184` syncs all of it to cloud every 5 minutes. Violates 45 CFR 164.502(b) minimum necessary standard.

**Design: Two-phase sync model**

**Phase 1 — Detection (every 5 min, zero PHI):**

New query method `PullReadyRxMetadata()`:
```sql
SELECT TOP 50
    rx.RxNumber, rx.DateFilled, rx.Quantity,
    item.TradeName, item.NDC,
    rx.StatusGuid
FROM Prescription.RxTransaction rx
LEFT JOIN Inventory.Item item ON rx.ItemId = item.Id
WHERE rx.StatusGuid IN (@status1, @status2, @status3)
  AND rx.DateFilled >= @cutoff
ORDER BY rx.DateFilled DESC
```

No Person JOIN. No patient data. Cloud creates inbox items with drug info only.

**Phase 2 — Patient fetch (on-demand, per Rx, signed command required):**

Triggered when pharmacist promotes an Rx to delivery order in dashboard. Cloud sends a **signed command** (Item 4 envelope) via heartbeat response:

```json
{
    "command": "fetch_patient",
    "agentId": "agent-uuid",
    "machineFingerprint": "wmi-uuid",
    "timestamp": "...",
    "nonce": "...",
    "keyId": "cmd-key-v1",
    "data": {
        "rxNumber": "12345",
        "deliveryOrderId": "order-uuid",
        "requesterId": "pharmacist-uuid"
    },
    "signature": "..."
}
```

**CRITICAL (Codex finding):** `fetch_patient` MUST be in the signed command list. Without signing, a compromised cloud endpoint can enumerate Rx numbers and exfiltrate patient data. The `deliveryOrderId` and `requesterId` fields provide audit context for HIPAA compliance.

Agent verifies signed envelope, then runs targeted query via `PullPatientForRx(rxNumber)`:
```sql
SELECT TOP 1
    p.FirstName, LEFT(p.LastName, 1) AS LastInitial,
    p.Phone, p.Address1, p.Address2, p.City, p.State, p.Zip
FROM Prescription.RxTransaction rx
JOIN Person.Person p ON rx.PatientId = p.Id
WHERE rx.RxNumber = @rxNumber
```

**This runs in Core (SQL), NOT via IPC to Helper.** Core has direct SQL access. No IPC dependency. (Corrects phantom dependency from v1 spec.)

**PHI blocklist handling:** `PioneerRxConstants.PhiColumnBlocklist` (lines 31-44) blocks `PatientFirstName`, `PatientPhone`, `PatientAddress1` — the exact columns `PullPatientForRx` needs. Solution: two query modes in `PioneerRxSqlEngine`:

```csharp
public enum QueryMode
{
    Detection,    // PHI blocklist enforced, no patient columns
    PatientFetch  // Blocklist bypassed, requires signed command auth token
}
```

`PullPatientForRx` accepts a `commandId` parameter (from the signed envelope nonce). Every call is audited:
```csharp
_stateDb.AppendAuditEntry(new AuditEntry
{
    EventType = "phi_access",
    CommandId = commandId,
    RequesterId = requesterId,
    RxNumber = rxNumber,
    Timestamp = DateTime.UtcNow
});
```

**Model split:**
- `RxMetadata` record — Rx number, drug, NDC, fill date, quantity, status (no PHI)
- `RxPatientDetails` record — first name, last initial, phone, address (PHI, only fetched on demand)
- Existing `RxReadyForDelivery` deprecated, replaced by `RxMetadata`

**Cloud endpoints:**
- `/api/agent/sync` now receives `RxMetadata[]` (no PHI)
- New: `/api/agent/patient-details` receives `RxPatientDetails` for a single Rx (PHI, requires signed command proof)

**New SuavoCloudClient methods:**
- `SyncRxMetadataAsync(RxMetadata[] batch)` — replaces current sync
- `SendPatientDetailsAsync(string rxNumber, RxPatientDetails details, string commandId)` — new

**Files:** `PioneerRxSqlEngine.cs` (split queries, QueryMode enum), `RxDetectionWorker.cs` (use metadata-only), `RxReadyForDelivery.cs` → split into `RxMetadata.cs` + `RxPatientDetails.cs`, `PioneerRxConstants.cs` (QueryMode awareness), `SuavoCloudClient.cs` (new methods), `AgentStateDb.cs` (phi_access audit entries), cloud routes
**Tests:** Detection query returns zero patient columns, patient query returns single row, unsigned fetch_patient rejected, blocklist enforced in Detection mode, blocklist bypassed in PatientFetch mode with valid commandId, audit entry written for every PHI access, cloud sync payload verified PHI-free.

---

### Item 7: Audit Trail Integrity

**Problem:** `AgentStateDb.cs:96-109` has `AppendAuditEntry` but `prev_hash` is always passed as null. `WritebackProcessor.cs:129-143` calls `UpsertWritebackState` but never `AppendAuditEntry`. HIPAA audit chain is broken (45 CFR 164.312(b), 164.312(c)(1)).

**Design:**

**Expanded audit schema:**
```sql
-- Add event_type column to existing audit_entries table
ALTER TABLE audit_entries ADD COLUMN event_type TEXT DEFAULT 'writeback_transition';
ALTER TABLE audit_entries ADD COLUMN command_id TEXT;
ALTER TABLE audit_entries ADD COLUMN requester_id TEXT;
ALTER TABLE audit_entries ADD COLUMN rx_number TEXT;
```

Event types: `writeback_transition` (existing), `phi_access` (Item 6), `rx_sync` (Item 12c), `decommission` (Item 9), `command_received` (Item 4).

**Chained hash algorithm:**
```csharp
string ComputeAuditHash(string prevHash, string taskId, string eventType,
    string fromState, string toState, string trigger, string timestamp)
{
    var payload = $"{prevHash}|{taskId}|{eventType}|{fromState}|{toState}|{trigger}|{timestamp}";
    return Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
}
```

Seed for empty chain: `Convert.ToBase64String(SHA256.HashData("SuavoAgent-audit-chain-v1"u8))`.

**AppendAuditEntry rewrite:**
```csharp
public string AppendAuditEntry(AuditEntry entry)
{
    var lastHash = GetLastAuditHash() ?? ComputeSeed();
    var newHash = ComputeAuditHash(lastHash, entry.TaskId, entry.EventType, ...);
    // INSERT with newHash as prev_hash
    return newHash;
}
```

**WritebackProcessor changes (`WritebackProcessor.cs:129-143`):**
Add `AppendAuditEntry` call inside `OnStateChanged` callback, after `UpsertWritebackState`:

Actual transitions to audit (using real state names):
- `Queued → Claimed` (task claimed for processing)
- `Claimed → InProgress` (UIA writeback started, triggered by `StartUia`)
- `InProgress → VerifyPending` (UIA completed, triggered by `WriteComplete`)
- `VerifyPending → Verified` (verification query confirms update, triggered by `VerifyMatch`)
- `VerifyPending → Queued` (verification mismatch, triggered by `VerifyMismatch`, retry)
- `Verified → Done` (sync complete, triggered by `SyncComplete`)
- Any → `ManualReview` (triggered by `BusinessError` when `_retryCount >= MaxRetries`)
- Any → `Queued` (triggered by `SystemError` when retryable)
- `Claimed → BlockedInteractive` (user is active, triggered by `UserIdle`)
- `BlockedInteractive → Claimed` (user went idle, triggered by `UserActive`)

**Chain verification:**
`VerifyAuditChain()` in `AgentStateDb`:
- Called on startup
- Called before audit archive upload (decommission, Item 9)
- Replays all entries from first to last, recomputes hashes, verifies chain
- Failure → log HIPAA integrity alert, include `auditChainValid: false` in heartbeat

**Files:** `AgentStateDb.cs` (hash computation, chain verification, schema migration), `WritebackProcessor.cs` (add AppendAuditEntry calls), `AuditEntry.cs` (new model with expanded fields)
**Tests:** Chain builds correctly across 10+ transitions using real state names, tampered entry detected, empty chain returns valid seed, seed is deterministic, phi_access events included in chain, verification fails on hash mismatch, startup verification runs.

---

### Item 8: Encryption at Rest (SQLCipher)

**Problem:** `Program.cs:50-51` comments say encryption activates after SQLCipher swap, but `SuavoAgent.Core.csproj:16` still ships `SQLitePCLRaw.bundle_e_sqlite3 Version="2.1.11"`. Audit entries and writeback states stored unencrypted. Violates 45 CFR 164.312(a)(2)(iv).

**Design:**

**NuGet change:**
```xml
<!-- CURRENT (SuavoAgent.Core.csproj:16) -->
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.11" />

<!-- REPLACEMENT -->
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlcipher" Version="2.1.11" />
```

**Key management:**
- DPAPI key generation already exists (`Program.cs:52-79`, `DataProtectionScope.LocalMachine`)
- Key stored at `%APPDATA%\SuavoAgent\state.key` (ACL-locked to SYSTEM + LocalService)
- On startup: read key via DPAPI Unprotect, pass as `PRAGMA key` to SQLCipher
- If key file missing: generate new key, create new encrypted DB

**Migration (unencrypted → encrypted):**
```csharp
if (DatabaseExistsUnencrypted("state.db"))
{
    // 1. Open unencrypted state.db
    // 2. Create new encrypted state.db.enc with PRAGMA key
    // 3. ATTACH unencrypted, copy writeback_states + audit_entries tables
    // 4. Close both
    // 5. Rename state.db → state.db.bak, state.db.enc → state.db
    // 6. Verify encrypted DB opens with key
    // 7. Securely delete state.db.bak (overwrite with zeros, then delete)
}
```

**Crash recovery (resolves Claude finding — unencrypted .bak persists):**
On every startup, check for `state.db.bak`. If exists → securely delete it (it's an unencrypted remnant from a crashed migration).

**Cross-compile verification:** Test SQLCipher bundle with `dotnet publish -r win-x64` from macOS. If it fails (per `feedback-cross-compile-sqlclient.md` precedent), use `SQLitePCLRaw.provider.e_sqlcipher` with platform-specific native library. Verify on Parallels VM before merging.

**Files:** `SuavoAgent.Core.csproj`, `AgentStateDb.cs` (migration + secure delete + PRAGMA key), `Program.cs` (key passing)
**Tests:** Encrypted DB created and accessible, unencrypted migration preserves all data, wrong key → access denied, crash recovery deletes .bak, Parallels VM build + test verification.

---

### Item 9: Decommission Audit Preservation

**Problem:** `HeartbeatWorker.cs:103-141` explicitly frames decommission as "zero trace on pharmacy PC" and deletes `ProgramData\SuavoAgent`. This is anti-forensic. HIPAA requires 6-year retention of audit logs (45 CFR 164.530(j)).

**Design: Decommission flow (replaces current `HeartbeatWorker.cs:115-141`):**

```
Step 1: Receive first signed decommission command (Item 4, two-phase)
        → Enter DecommissionPending state
        → AppendAuditEntry(eventType: "decommission", details: "phase1_received")
        → Report DecommissionPending in next heartbeat

Step 2: Receive second signed decommission command (5+ min later, monotonic clock)
        → VerifyAuditChain() — must pass
        → Sanitize Serilog log files (grep for any accidental PHI, redact)
        → Upload audit archive to cloud:
            POST /api/agent/audit-archive (HMAC-signed)
            Body: {
                agentId, pharmacyId, machineFingerprint, version,
                auditEntries: [...all rows from audit_entries...],
                writebackStates: [...all rows from writeback_states...],
                logFiles: [...last 30 days, sanitized...],
                archiveDigest: SHA256(canonical JSON of above)
            }
        → Wait for signed ACK from cloud

Step 3: Verify ACK:
        → ACK must contain: { archiveId, archiveDigest, timestamp, keyId, signature }
        → Verify signature
        → Verify archiveDigest matches what we sent (prevents partial-upload ACK)
        → Verify archiveId is non-empty

Step 4 (ACK verified):
        → Securely delete state.db (overwrite + delete)
        → Delete logs directory
        → Delete appsettings.json (contains API key)
        → Delete state.key (DPAPI key)
        → sc.exe stop + sc.exe delete services (Core, Broker)
        → Kill Helper process if running
        → Delete install directory
        → Environment.Exit(0)

Step 4 (failure): If audit upload fails OR ACK invalid OR chain verification fails:
        → PAUSE decommission, remain in DecommissionPending
        → Resume normal operation (heartbeat, detection, writeback)
        → Report "decommission_blocked: {reason}" in heartbeat
        → Manual intervention required (dashboard alert)

Timeout: No second command within 1 hour of first:
        → Cancel DecommissionPending
        → AppendAuditEntry(eventType: "decommission", details: "cancelled_timeout")
        → Resume normal operation
```

**Cloud-side:**
- `/api/agent/audit-archive` stores archive in Supabase `agent_audit_archives` table
- Archive encrypted at rest (Supabase default + column-level encryption for PHI)
- Retention policy: 6 years minimum, immutable (no UPDATE/DELETE on archive rows)
- Access logging: every SELECT on archive table logged
- Signed ACK includes `archiveDigest` for round-trip verification

**Files:** `HeartbeatWorker.cs` (decommission rewrite), `AgentStateDb.cs` (export methods, secure delete), `SuavoCloudClient.cs` (archive upload + ACK verification), `SignedCommandVerifier.cs` (ACK verification), cloud route
**Tests:** Two-phase flow end-to-end, audit upload success → verified ACK → cleanup, upload failure → pause + resume, invalid ACK → pause, chain verification failure → block, 1h timeout → cancel, secure delete overwrites file, archive contains all audit entries.

---

## Track 3: Features & Reliability

### Item 10: IPC Bring-Up + Structured Errors

**Problem:** Three-process model is architectural fiction. `IpcPipeServer.cs:107-112` returns null. `WritebackProcessor.cs:111` says "Would send". Helper never connects. This gates writeback and Helper health reporting.

**Design:**

**Framing protocol (replaces current line-based):**
```
[4 bytes: payload length, big-endian uint32] [UTF-8 JSON payload]
```
Max payload: 65,536 bytes (64KB). Messages exceeding this are rejected with connection close.

**IPC record migration (breaking change to `IpcMessage.cs`):**

```csharp
// NEW — replaces existing IpcMessage + IpcResponse
record IpcRequest(string Id, string Command, int Version, JsonElement? Data);
record IpcResponse(string Id, int Status, string Command, JsonElement? Data, IpcError? Error);
record IpcError(string Code, string Message, bool Retryable, int AttemptCount);
```

This is a breaking change to `SuavoAgent.Contracts`. All existing tests referencing `IpcMessage(Version, RequestId, Command, Payload)` or `IpcResponse(RequestId, Success, Result, Error)` must be updated. Version field initially set to `1`; no negotiation — just forward compatibility.

**Status codes:**
| Code | Meaning |
|------|---------|
| 200 | Success |
| 400 | Bad command / malformed request / unknown command |
| 404 | Target not found (PioneerRx not attached) |
| 408 | Timeout (Helper didn't respond in time) |
| 500 | Internal Helper error |

**Command catalog (preserves existing names where possible):**

| Command | Direction | Timeout | Purpose |
|---------|-----------|---------|---------|
| `ping` | Core → Helper | 5s | Health check (existing) |
| `attach_pioneerrx` | Core → Helper | 30s | Find and attach to PioneerRx window |
| `writeback_delivery` | Core → Helper | 30s | Click proof-of-delivery into PioneerRx UI (existing) |
| `discover_screen` | Core → Helper | 15s | Discover PioneerRx UI elements (existing) |
| `dismiss_modal` | Core → Helper | 10s | Dismiss RedSail SSL popups (existing) |
| `check_user_activity` | Core → Helper | 5s | Check if user is active at POS (existing) |
| `drain` | Core → Helper | 5s | Graceful shutdown (existing, used by Item 2) |
| `helper_status` | Helper → Core | 5s | Report attachment state + PioneerRx PID |
| `helper_error` | Helper → Core | 5s | Structured error report |

**NOTE:** `fetch_patient` is NOT an IPC command — it runs in Core via direct SQL (Item 6). Removed from IPC catalog (corrects v1 spec error).

**Pipe ACL fix (resolves trust boundary incompatibility):**

Current `IpcPipeServer.cs:129-139` only allows `LocalSystemSid` and `LocalServiceSid`. Helper runs in user session — pipe rejects it.

Fix: On pipe creation, query the active console session user SID and add it:

```csharp
var consoleSid = GetConsoleUserSid(); // WTSQueryUserToken → GetTokenInformation(TokenUser)
if (consoleSid != null)
{
    security.AddAccessRule(new PipeAccessRule(
        consoleSid,
        PipeAccessRights.ReadWrite,
        AccessControlType.Allow));
}
```

ACL becomes: SYSTEM (full), LocalService (full), console user (read+write). Other users blocked. If console session changes, recreate pipe with new user SID.

**Core → Broker communication (resolves missing channel):**

No new pipe needed. Use a shared named event:
```csharp
// Broker creates and waits on:
var restartEvent = EventWaitHandle.OpenExisting("Global\\SuavoAgentHelperRestart");

// Core signals when Helper needs relaunch:
EventWaitHandle.OpenExisting("Global\\SuavoAgentHelperRestart").Set();
```

Broker also watches for `update-pending.flag` (Item 2) — holds off launching Helper if present.

**Helper connection lifecycle:**
1. Core creates pipe on startup (existing, with fixed ACL)
2. Broker launches Helper via `Process.Start()` (current `SessionWatcher.cs:78-87` — keep as-is for now)
3. Helper connects to pipe on startup, sends `helper_status` immediately
4. Core pings Helper every 30s via pipe
5. Three consecutive ping timeouts → Core signals `Global\SuavoAgentHelperRestart` event → Broker relaunches
6. Helper reports PioneerRx attachment failures via `helper_error`
7. After 5 consecutive failures → Core includes error array in heartbeat → dashboard alert

**CreateProcessAsUser:** Deferred to field testing. Current `Process.Start()` works when Broker runs in same session. Full `CreateProcessAsUser` P/Invoke requires SYSTEM privileges that only work when installed as a real Windows service — must be tested at a pharmacy, not in VM. Spec does NOT include it.

**Files:** `IpcPipeServer.cs` (framing rewrite, ACL fix, push implementation), `IpcPipeClient.cs` (new — Helper-side client), `IpcMessage.cs` (breaking migration to new records), `SessionWatcher.cs` (named event signaling), `Helper/Program.cs` (pipe connection on startup), `WritebackProcessor.cs` (replace "Would send" with real IPC calls), all IPC test files (update for new record signatures)
**Tests:** Length-prefixed framing round-trip, oversized message rejected (>64KB), command routing returns correct status codes, timeout produces 408, ping/pong lifecycle, Helper reconnection after disconnect, console user SID added to ACL, named event signaling, breaking change — all existing IPC tests updated.

---

### Item 11: Writeback State Machine Hardening

**Problem (revised after code review):** Max retries already enforced (`WritebackStateMachine.cs:39,97-102`). But: `WritebackProcessor.cs:129-143` never calls `AppendAuditEntry`, exponential backoff not implemented, and verification step not wired.

**What's actually needed (targeted fixes, not redesign):**

**a) Wire audit entries (Item 7 integration):**
In `WritebackProcessor.OnStateChanged` (line 129), after `UpsertWritebackState`, add:
```csharp
_stateDb.AppendAuditEntry(new AuditEntry
{
    EventType = "writeback_transition",
    TaskId = taskId,
    FromState = previousState.ToString(),
    ToState = newState.ToString(),
    Trigger = trigger.ToString()
});
```

**b) Exponential backoff:**
Add `next_retry_at` column to `writeback_states` table:
```sql
ALTER TABLE writeback_states ADD COLUMN next_retry_at TEXT;
```

Backoff schedule (on `SystemError` trigger):
- Retry 1: `DateTime.UtcNow.AddMinutes(1)`
- Retry 2: `DateTime.UtcNow.AddMinutes(5)`
- Retry 3: `DateTime.UtcNow.AddMinutes(15)`

`WritebackProcessor.ProcessPendingAsync()` skips tasks where `next_retry_at > DateTime.UtcNow`.

**c) Wire real IPC (Item 10 integration):**
Replace `WritebackProcessor.cs:111` ("Would send") with actual IPC call:
```csharp
var response = await _ipcServer.SendCommandAsync(
    new IpcRequest(Guid.NewGuid().ToString(), IpcCommands.WritebackDelivery, 1, data),
    TimeSpan.FromSeconds(30));
```

Map `IpcResponse.Status` to writeback triggers:
- 200 → `WriteComplete` trigger (advances to `VerifyPending`)
- 404 → `HelperDisconnected` trigger (back to `Queued`)
- 408/500 → `SystemError` trigger (increments retry, may escalate to `ManualReview`)

**d) Verification query:**
After `WriteComplete` → `VerifyPending`, Core runs:
```sql
SELECT StatusGuid FROM Prescription.RxTransaction WHERE RxNumber = @rxNumber
```
If status changed → `VerifyMatch` trigger → `Verified` → `Done` (after sync)
If unchanged → `VerifyMismatch` trigger → back to `Queued` (retry with backoff)

**Files:** `WritebackProcessor.cs` (audit + IPC + verification), `WritebackStateMachine.cs` (no changes — already correct), `AgentStateDb.cs` (next_retry_at column)
**Tests:** Audit entry written for every real transition, backoff delays correct (1m/5m/15m), IPC 200 → VerifyPending, IPC 404 → Queued, IPC 500 → SystemError, verification match → Done, verification mismatch → retry, MaxRetries still enforced at 3 (regression test).

---

### Item 12: Canary Updates + Health Enrichment + Batch Persistence

#### 12a: Canary Updates (Server-Controlled)

**Cloud-side only:**
- `pharmacies` table: add `update_channel TEXT DEFAULT 'stable'` column
- Release workflow: publish canary manifest → 24h monitoring → promote to stable
- Heartbeat response includes `updateChannel` matching pharmacy's channel

**Agent-side (minimal):**
- HeartbeatWorker adds `updateChannel` to heartbeat payload (reads from heartbeat response, echoes back)
- No routing logic in agent — agent follows whatever signed update the server sends
- This IS a change to HeartbeatWorker (adds one field to payload JSON)

#### 12b: Health Enrichment

**Enriched heartbeat payload (HeartbeatWorker.cs):**
```json
{
    "agentId": "uuid",
    "version": "2.1.0",
    "updateChannel": "stable",
    "machineFingerprint": "wmi-uuid",
    "uptimeSeconds": 86400,
    "memoryMb": 45,
    "sql": {
        "connected": true,
        "tier": 1,
        "lastQueryAt": "2026-04-11T10:30:00Z",
        "lastQueryDurationMs": 120,
        "lastRxCount": 12
    },
    "helper": {
        "attached": true,
        "pioneerRxPid": 4532,
        "consecutiveFailures": 0
    },
    "writeback": {
        "pending": 2,
        "failed": 0,
        "manualReview": 0
    },
    "audit": {
        "chainValid": true,
        "entryCount": 847
    },
    "sync": {
        "unsyncedBatches": 0,
        "deadLetterCount": 0,
        "lastSyncAt": "2026-04-11T10:30:00Z"
    },
    "errors": []
}
```

No inbound API on agent. Dashboard reads from heartbeat table in Supabase.
Health card: Green/Yellow/Red based on last heartbeat age, SQL connection, Helper attachment, error count, audit chain validity.

#### 12c: Unsynced Batch Persistence

**Problem:** `RxDetectionWorker.cs:18` stores `_unsyncedBatch` as in-memory `IReadOnlyList<RxReadyForDelivery>?`. Lost on crash. Also, current retry logic at lines 59-63 overwrites previous unsynced batch on consecutive failures — multiple failures lose earlier data.

**New SQLite table in AgentStateDb:**
```sql
CREATE TABLE IF NOT EXISTS unsynced_batches (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    payload TEXT NOT NULL,
    created_at TEXT NOT NULL,
    retry_count INTEGER DEFAULT 0,
    status TEXT DEFAULT 'pending',
    expires_at TEXT NOT NULL
);
```

Added `expires_at` column (resolves Claude finding — dead letters had no TTL). Default: `created_at + 30 days`. Dead letters older than 30 days are purged on startup.

**Payload format:** JSON array of `RxMetadata` objects (NOT `RxReadyForDelivery` — uses new PHI-free model from Item 6). Schema version embedded: `{ "v": 1, "items": [...] }`. On replay, if schema version doesn't match current, batch is moved to dead_letter with reason `schema_mismatch`.

**Flow:**
1. `RxDetectionWorker` produces batch → writes to `unsynced_batches` with status `pending`
2. Sync attempt: read all `pending` batches ordered by `created_at`
3. Success → delete row
4. Failure → increment `retry_count`
5. `retry_count >= 10` → set status to `dead_letter`
6. On startup → replay all `pending` batches (crash recovery)
7. On startup → purge expired `dead_letter` entries
8. Counts included in health payload (Item 12b)

Audit: each sync (success or failure) gets an `AppendAuditEntry(eventType: "rx_sync")`.

**Files:** `HeartbeatWorker.cs` (payload enrichment, updateChannel echo), `AgentStateDb.cs` (unsynced_batches table + TTL), `RxDetectionWorker.cs` (write to DB, remove in-memory field), `SuavoCloudClient.cs` (batch sync from DB)
**Tests:** Batch persisted to DB, crash recovery replays pending, consecutive failures don't overwrite earlier batches, dead letter after 10 retries, TTL purge on startup, schema version mismatch → dead letter, health payload includes all fields, updateChannel echoed.

---

## Item 13: Ed25519 Log Bug Fix (Bonus)

**Problem:** `HeartbeatWorker.cs:171` logs "Pending update has no Ed25519 signature — rejecting" but `SelfUpdater.cs` uses ECDSA P-256. Misleading log could confuse incident response.

**Fix:** Change log message to "Pending update has no ECDSA P-256 signature — rejecting".

**Files:** `HeartbeatWorker.cs:171`
**Tests:** None needed (log message change only).

---

## Dependency Graph (Revised)

```
Item 1  (URL fix) ─────────────────────────────── standalone
Item 8  (SQLCipher) ────────────────────────────── standalone
Item 13 (Ed25519 log) ─────────────────────────── standalone
Item 12c (batch persistence) ───────────────────── standalone

Item 7  (audit chain) ← Item 8 (needs encrypted storage)

Item 4  (signed commands) ← Item 7 (audit logging for commands)

Item 6  (PHI minimization):
  6a: SQL split + model split ← standalone (Wave 2)
  6b: fetch_patient implementation ← Item 4 (must be signed) + Item 7 (audit)

Item 2  (package update) ← standalone
Item 3  (bootstrap update) ← Item 2 (shared SwapBinaries logic)
Item 5  (installer sig) ← Item 2 (publish.ps1 generates checksums)

Item 10 (IPC) ← standalone (pipe ACL fix independent)
Item 11 (writeback hardening) ← Item 10 (real IPC) + Item 7 (audit entries)
Item 9  (decommission) ← Item 4 (signed commands) + Item 7 (audit chain)

Item 12a+b (canary + health) ← Item 12c (batch stats in health payload)
```

## Implementation Waves (Revised)

**Wave 1 — Standalone, parallel (no dependencies):**
- Item 1: URL validation fix
- Item 8: SQLCipher implementation + VM test
- Item 12c: Batch persistence
- Item 13: Ed25519 log fix

**Wave 2 — After Wave 1:**
- Item 7: Audit chain integrity (needs SQLCipher from W1)
- Item 6a: PHI minimization SQL split + model split (no fetch_patient yet)
- Item 2: Package-level self-update
- Item 10: IPC bring-up + pipe ACL fix

**Wave 3 — After Wave 2:**
- Item 4: Signed control-plane commands (needs audit chain from W2)
- Item 3: Bootstrap self-update (needs SwapBinaries from Item 2 in W2)
- Item 5: Installer signature verification (needs publish.ps1 from Item 2 in W2)
- Item 11: Writeback hardening (needs IPC from W2 + audit from W2)

**Wave 4 — After Wave 3:**
- Item 6b: fetch_patient implementation (needs signed commands from W3)
- Item 9: Decommission audit preservation (needs signed commands + audit from W3)
- Item 12a+b: Canary updates + health enrichment (needs batch persistence from W1)

## Test Requirements

Current: 49 tests.
Target: 110+ tests after hardening.

**New test files:**
- `SelfUpdater.UrlValidation.Tests.cs`
- `SelfUpdater.PackageUpdate.Tests.cs`
- `SelfUpdater.BootstrapUpdate.Tests.cs`
- `SignedCommandVerifier.Tests.cs`
- `AuditChain.Tests.cs`
- `PhiMinimization.Tests.cs`
- `IpcFraming.Tests.cs`
- `UnsyncedBatch.Tests.cs`
- `DecommissionFlow.Tests.cs`
- `tests/Bootstrap.Verification.Tests.ps1`

**Existing test updates:**
- All IPC tests (breaking record migration)
- `WritebackStateMachine.Tests.cs` (add audit verification)
- `RxDetectionWorker.Tests.cs` (metadata-only model)
- `PioneerRxSqlEngine.Tests.cs` (QueryMode, split queries)

**Property-based tests:**
- Writeback state machine cannot enter infinite loop (randomized trigger sequences)
- Audit chain hash is deterministic (same inputs → same hash, different inputs → different hash)
- URL validation rejects all non-allowlisted hosts (fuzzing)
- Signed command replay detection (randomized nonce sequences)

## Out of Scope (Deferred)

- Multi-pharmacy support (cloud orchestration, dashboard session)
- WiX MSI installer (PowerShell installer sufficient)
- Authenticode code signing (hash verification provides equivalent tamper detection)
- TLS certificate pinning for SQL Server (acceptable risk for pharmacy LAN)
- PioneerRx API enrollment (need 10+ pharmacies)
- Full CreateProcessAsUser P/Invoke (requires SYSTEM privileges, test at pharmacy)
- Command signing key HSM / hardware security module (Supabase Vault sufficient for current scale)
- Archive RBAC / access logging (Supabase RLS provides baseline, formal RBAC at scale)
