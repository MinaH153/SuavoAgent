# SuavoAgent Learning Agent — Design Spec

**Date:** 2026-04-11
**Author:** Claude + Joshua Henein
**Status:** Draft v2 — post-Codex review (3 CRITICAL, 7 HIGH addressed)

## Problem

SuavoAgent currently works with one PMS (PioneerRx) via months of manual reverse engineering. To scale to every pharmacy in the US, we need the agent to learn ANY pharmacy management system by observing it — no vendor cooperation, no manual integration work.

## Solution

A 30-day behavioral learning system that observes everything on a pharmacy computer, builds a Pharmacy Operations Model (POM), and generates PMS adapters automatically. Human-in-the-loop: fleet operator reviews and approves the learned model before the agent activates data operations.

## Architecture

```
┌─────────────────────────────────────────────────────────┐
│                   Fleet Operator API                     │
│            (REST — any system integrates)                │
├─────────────────────────────────────────────────────────┤
│                  Approval Dashboard                      │
│   (shows learned POM, operator approves activation)     │
├─────────────────────────────────────────────────────────┤
│                  Adapter Generator                        │
│   (converts approved POM into working SQL/UIA adapter)  │
├─────────┬───────────┬────────────┬──────────────────────┤
│ Process │ SQL       │ UI Auto    │ Schedule             │
│Observer │ Observer  │ Observer   │ Observer             │
├─────────┴───────────┴────────────┴──────────────────────┤
│              Pattern Engine (local only)                  │
│   (sequence mining, workflow extraction, anomaly detect) │
├─────────────────────────────────────────────────────────┤
│          Pharmacy Operations Model (POM)                 │
│    (SQLCipher — encrypted, audited, PHI-free)           │
├─────────────────────────────────────────────────────────┤
│         Existing SuavoAgent Infrastructure               │
│  (heartbeat, signed commands, audit chain, IPC, etc.)   │
└─────────────────────────────────────────────────────────┘
```

## Learning Phases

### Phase 1: Discovery (Days 1-7)
**Goal:** Catalog the pharmacy's digital environment.

**Process Observer** — ETW provider `Microsoft-Windows-Kernel-Process`:
- Every process start/stop: name, path, parent PID, session ID, timestamps
- Window titles (scrubbed — strip anything matching PHI patterns)
- Service enumeration via WMI `Win32_Service`
- Scheduled tasks via `Win32_ScheduledJob` / Task Scheduler COM

**SQL Observer** — Reuse discovered connection strings:
- `INFORMATION_SCHEMA.COLUMNS` — full table/column catalog
- `sys.foreign_key_columns` — FK graph (recursive CTE)
- `sys.schemas` — schema enumeration (PioneerRx uses custom schemas)
- Column name pattern matching: `*_id` = FK, `*_date` = temporal, `*npi*`/`*dea*` = regulatory
- `sys.dm_db_index_usage_stats` — hot tables vs dead schema

**UI Automation Observer** — `TreeWalker.ControlViewWalker`:
- Periodic tree walks of pharmacy app windows (every 60s when app is in focus)
- Element catalog: control type, automation ID, name, bounding rect
- DataGrid column discovery (column headers = field names)
- NO interaction — read-only tree observation

**Schedule Observer** — WMI event subscriptions:
- Process creation/termination patterns by hour-of-day
- Recurring batch operations (end-of-day billing, inventory sync)

### Phase 2: Pattern Recognition (Days 8-21)
**Goal:** Extract workflows, routines, and access patterns.

**Process Lifecycle Patterns:**
- Which apps run during business hours vs overnight?
- Process dependency chains (A starts → B starts within 30s)
- Crash/restart patterns

**SQL Access Patterns** — `sys.dm_exec_query_stats` + `sys.dm_exec_sql_text` (OPTIONAL — requires VIEW SERVER STATE):
- DMV access is capability-detected at startup. If unavailable, fall back to metadata-only discovery.
- Raw query text is TOXIC — may contain patient names, DOBs, Rx numbers as literals.
- **Fail-closed tokenizer**: parse SQL in-memory, extract only table/column references and query structure. If tokenizer cannot safely normalize a statement (unknown syntax, embedded literals it can't classify), DISCARD the entire statement. Never persist raw SQL text.
- Only persist: normalized query shape (`SELECT [cols] FROM [tables] WHERE [predicates]`), referenced tables, execution count.
- Table access frequency → rank tables by operational importance (from DMV stats, not query text).
- Write patterns: inferred from `sys.dm_db_index_usage_stats` user_updates column, NOT from parsing INSERT/UPDATE text.
- **Cache churn**: DMV rows disappear when plans leave cache or engine restarts. Frequency inferences are approximate, not exact. Mark confidence accordingly.

**UI Workflow Sequences:**
- Track screen transitions: which windows appear in what order
- Focus change events: app A → app B → app A
- DataGrid content change detection (new rows = new prescriptions)

**Temporal Patterns:**
- Daily routine extraction (morning open, midday rush, evening close)
- Weekly patterns (Monday inventory, Friday billing)
- Anomaly detection (unusual process at unusual time)

### Phase 3: Model Building (Days 22-30)
**Goal:** Compile the POM and prepare for human review.

**POM Components:**
1. **Process Catalog** — every observed process with confidence scores
2. **PMS Identification** — which process is the pharmacy management system (highest confidence match against known PMS signatures + behavioral heuristics)
3. **Schema Map** — tables, columns, types, FK graph, access frequency, inferred purpose
4. **Workflow Graph** — directed graph of state transitions (process mining Petri net style)
5. **Rx Queue Candidates** — tables/views that look like prescription queues (heuristic: has RxNumber-like column + status column + date column + high read frequency)
6. **Status Mapping** — discovered status values with inferred meaning (based on transition order and frequency)
7. **Integration Recommendations** — suggested SQL queries for Rx detection, suggested status GUIDs for "delivery-ready"

**Sanitization before upload:**
- Strip all row values (only schema structure + query shapes)
- Hash any identifiers with per-pharmacy salt
- Remove connection strings, credentials, file paths
- Keep: table names, column names, data types, FK relationships, access patterns, query shapes, workflow graphs

## POM Data Model (SQLCipher tables in AgentStateDb)

```sql
-- Learning phase + operational mode control
CREATE TABLE learning_session (
    id TEXT PRIMARY KEY,
    pharmacy_id TEXT NOT NULL,
    phase TEXT NOT NULL DEFAULT 'discovery',  -- discovery/pattern/model/approved/active
    mode TEXT NOT NULL DEFAULT 'observer',  -- observer/supervised/autonomous
    started_at TEXT NOT NULL,
    phase_changed_at TEXT NOT NULL,
    approved_at TEXT,
    approved_by TEXT,
    approved_model_digest TEXT,  -- SHA-256 over {pharmacy_id, session_id, pom_json, adapter_json}. Must match cloud-signed digest.
    schema_fingerprint TEXT,  -- hash of current schema surface for drift detection
    schema_epoch INTEGER DEFAULT 1,  -- increments on drift detection
    promoted_to_supervised_at TEXT,
    promoted_to_autonomous_at TEXT,
    supervised_success_count INTEGER DEFAULT 0,  -- tracks successful supervised cycles
    supervised_correction_count INTEGER DEFAULT 0,  -- tracks operator corrections
    promotion_threshold INTEGER DEFAULT 50,  -- cycles needed for autonomous
    config_json TEXT  -- learning parameters (intervals, thresholds)
);

-- Process observations (PHI-free: process names and paths are not PHI)
CREATE TABLE observed_processes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    process_name TEXT NOT NULL,
    exe_path TEXT NOT NULL,
    window_title_hash TEXT,  -- SHA-256 hash, never raw title (may contain patient names)
    window_title_scrubbed TEXT,  -- title with PHI patterns replaced with [REDACTED]
    parent_process TEXT,
    session_user_sid_hash TEXT,  -- hashed, never raw SID
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL,
    occurrence_count INTEGER DEFAULT 1,
    is_service INTEGER DEFAULT 0,
    is_pms_candidate INTEGER DEFAULT 0,
    confidence REAL DEFAULT 0.0
);

-- SQL schema discoveries (column names are NOT PHI per HHS Safe Harbor)
CREATE TABLE discovered_schemas (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    server_hash TEXT NOT NULL,  -- hashed server address
    database_name TEXT NOT NULL,
    schema_name TEXT NOT NULL,
    table_name TEXT NOT NULL,
    column_name TEXT NOT NULL,
    data_type TEXT NOT NULL,
    max_length INTEGER,
    is_nullable INTEGER,
    is_pk INTEGER DEFAULT 0,
    is_fk INTEGER DEFAULT 0,
    fk_target_table TEXT,
    fk_target_column TEXT,
    inferred_purpose TEXT,  -- 'identifier', 'status', 'temporal', 'regulatory', 'name', 'amount', 'unknown'
    discovered_at TEXT NOT NULL
);

-- Table access patterns (from DMVs — no row data, just counts)
CREATE TABLE table_access_patterns (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    schema_table TEXT NOT NULL,  -- schema.table
    read_count INTEGER DEFAULT 0,
    write_count INTEGER DEFAULT 0,
    avg_rows_returned REAL,
    last_accessed TEXT,
    is_hot INTEGER DEFAULT 0,  -- top 20% by access frequency
    observed_at TEXT NOT NULL
);

-- Query shape observations (parameterized — no PHI values)
CREATE TABLE observed_query_shapes (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    query_shape_hash TEXT NOT NULL,  -- SHA-256 of normalized query
    query_shape TEXT NOT NULL,  -- parameterized: SELECT ... FROM T WHERE col = @p1
    tables_referenced TEXT NOT NULL,  -- JSON array of schema.table
    execution_count INTEGER DEFAULT 1,
    avg_elapsed_ms REAL,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL
);

-- UI Automation tree snapshots (PHI-scrubbed)
CREATE TABLE ui_tree_snapshots (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    process_name TEXT NOT NULL,
    window_class TEXT,
    tree_hash TEXT NOT NULL,  -- hash of element structure (detect screen changes)
    element_count INTEGER,
    datagrid_columns TEXT,  -- JSON array of column headers (not PHI — they're field names)
    snapshot_json TEXT NOT NULL,  -- scrubbed tree: control types, automation IDs, names (no values)
    captured_at TEXT NOT NULL
);

-- Workflow event log (process mining format)
CREATE TABLE workflow_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    case_id TEXT NOT NULL,  -- hashed correlation key
    activity TEXT NOT NULL,  -- scrubbed activity name
    resource TEXT,  -- process name or user role (never user identity)
    timestamp TEXT NOT NULL,
    duration_ms INTEGER,
    metadata_json TEXT  -- PHI-free metadata
);

-- Rx queue candidates (inferred from schema + access patterns)
-- Supports multi-table join paths (PMS may split queue across header/detail/lookup tables)
CREATE TABLE rx_queue_candidates (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    primary_table TEXT NOT NULL,  -- main table (e.g. Prescription.RxTransaction)
    join_tables TEXT,  -- JSON array of {table, join_column, target_column} for multi-table paths
    rx_number_column TEXT,  -- inferred Rx number column (may be on joined table)
    rx_number_table TEXT,  -- which table the Rx number lives on
    status_column TEXT,  -- inferred status column
    status_table TEXT,  -- which table the status lives on
    status_is_lookup INTEGER DEFAULT 0,  -- 1 if status is FK to a lookup/enum table
    status_lookup_table TEXT,  -- the lookup table if applicable
    date_column TEXT,  -- inferred date column
    patient_fk_column TEXT,  -- inferred patient FK (marks PHI fence)
    patient_fk_table TEXT,
    composite_key_columns TEXT,  -- JSON array if PK is composite
    confidence REAL DEFAULT 0.0,  -- 0.0 to 1.0
    evidence_json TEXT NOT NULL,  -- why we think this is an Rx queue (with provenance)
    negative_evidence_json TEXT,  -- contradictory signals (surfaced to operator)
    stability_days INTEGER DEFAULT 0,  -- how many days this candidate has been stable
    discovered_at TEXT NOT NULL
);

-- Status value mapping (inferred from observed transitions)
CREATE TABLE discovered_statuses (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    schema_table TEXT NOT NULL,
    status_column TEXT NOT NULL,
    status_value TEXT NOT NULL,  -- the actual value (GUID, int, string)
    inferred_meaning TEXT,  -- 'queued', 'in_progress', 'ready_pickup', 'delivered', 'completed', 'cancelled'
    transition_order INTEGER,  -- observed ordering (1 = earliest in workflow)
    occurrence_count INTEGER DEFAULT 0,
    confidence REAL DEFAULT 0.0,
    discovered_at TEXT NOT NULL
);

-- Audit: every observation action logged (HIPAA 164.312(b))
CREATE TABLE learning_audit (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    observer TEXT NOT NULL,  -- 'process', 'sql', 'uia', 'schedule', 'pattern'
    action TEXT NOT NULL,  -- 'scan', 'discover', 'infer', 'scrub', 'hash'
    target TEXT,  -- what was observed (table name, process name — never PHI)
    phi_scrubbed INTEGER DEFAULT 0,  -- 1 if PHI was detected and scrubbed
    timestamp TEXT NOT NULL,
    prev_hash TEXT  -- chained hash for tamper evidence
);
```

## Observer Interfaces

All observers implement a common interface for lifecycle management:

```csharp
public interface IObserver : IDisposable
{
    string Name { get; }  // "process", "sql", "uia", "schedule"
    ObserverPhase ActivePhases { get; }  // which phases this observer runs in
    Task StartAsync(LearningSession session, CancellationToken ct);
    Task StopAsync();
    Task<ObserverHealth> CheckHealthAsync();
}

[Flags]
public enum ObserverPhase
{
    Discovery = 1,
    Pattern = 2,
    Model = 4,
    Active = 8,  // post-approval, ongoing monitoring
    All = Discovery | Pattern | Model | Active
}

public record ObserverHealth(
    string ObserverName,
    bool IsRunning,
    int EventsCollected,
    int PhiScrubCount,
    DateTimeOffset LastActivity);
```

### ProcessObserver
```csharp
public sealed class ProcessObserver : IObserver
{
    // ETW: Microsoft-Windows-Kernel-Process provider
    // Collects: process name, exe path, start/stop times
    // Scrubs: window titles (PHI regex → [REDACTED])
    // Stores: observed_processes table
    // Active phases: All (always monitoring)
}
```

### SqlSchemaObserver
```csharp
public sealed class SqlSchemaObserver : IObserver
{
    // Uses discovered connection strings (from bootstrap)
    // Phase 1: INFORMATION_SCHEMA + sys.foreign_keys + sys.schemas
    // Phase 2: sys.dm_exec_query_stats + sys.dm_exec_sql_text
    // Phase 3: infer Rx queue candidates, status mappings
    // Stores: discovered_schemas, table_access_patterns,
    //         observed_query_shapes, rx_queue_candidates, discovered_statuses
    // Active phases: Discovery, Pattern, Model
    // PHI handling: query shapes only (strip parameter values)
}
```

### UiAutomationObserver
```csharp
public sealed class UiAutomationObserver : IObserver
{
    // RUNS IN HELPER ONLY — not in Core service (Session 0 cannot access user UI)
    // Helper runs in user session via CreateProcessAsUser (already built)
    // Communicates observations to Core via IPC pipe
    // TreeWalker.ControlViewWalker on PMS windows
    // Captures: element tree structure, DataGrid columns
    // Scrubs: element values (names, text content → [REDACTED])
    // Keeps: control types, automation IDs, column headers
    // Stores: ui_tree_snapshots table (via IPC → Core writes to SQLCipher)
    // Active phases: Discovery, Pattern
}
```

**Session 0 constraint:** Windows services run in Session 0. UI Automation and window title enumeration require the interactive user session. All UI observation is routed through Helper (launched via Broker's CreateProcessAsUser into the user session). Core never directly accesses UI elements.

**ProcessObserver window titles:** ETW `Microsoft-Windows-Kernel-Process` provides process start/stop but NOT window titles. Window title collection is also Helper-only, reported to Core via IPC.

### ScheduleObserver
```csharp
public sealed class ScheduleObserver : IObserver
{
    // WMI process creation events bucketed by hour
    // Detects: daily routines, batch operations, maintenance windows
    // Stores: workflow_events table (process mining format)
    // Active phases: Pattern, Model
}
```

## Pattern Engine

Runs locally after Phase 2 data collection. Never sends raw data to cloud.

### Sequence Mining
- Input: `workflow_events` table (case_id, activity, timestamp)
- Algorithm: directly-follows graph → frequency-filtered Petri net
- Output: workflow graph with transition probabilities
- Implementation: custom C# (no pm4py dependency — agent is .NET, not Python)

### Rx Queue Inference
- Input: `discovered_schemas` + `table_access_patterns` + `observed_query_shapes`
- Heuristics:
  - Table has a column matching `/rx.*num|prescription.*id|rx.*id/i` → +0.3 confidence
  - Table has a column matching `/status|state|workflow/i` → +0.2
  - Table has a temporal column (datetime type) → +0.1
  - Table is in top 20% by read frequency → +0.2
  - Table has FK to a patient/person table → +0.1 (also marks the FK as PHI fence)
  - Observed query shapes reference this table with status-filtering WHERE clauses → +0.1
- Threshold: confidence >= 0.6 → candidate

### Status Ordering
- Input: `observed_query_shapes` referencing status columns + `workflow_events`
- Method: observe which status values appear earliest vs latest in time-ordered queries
- Output: ordered status list with inferred meanings (maps to delivery-ready detection)

## PHI Handling (HIPAA-First)

### Classification: Local POM store is ePHI
Per Codex review and 45 CFR 164.514: exact timestamps + hashed identifiers + scrubbed free text do NOT qualify as de-identified under Safe Harbor. The local learning store MUST be treated as ePHI and protected under 45 CFR 164.312 technical safeguards (encryption, access controls, audit logging). This is already the case — SQLCipher + ACLs + audit chain.

**Only the sanitized POM export (coarsened timestamps, no hashes, no free text) uploaded to cloud may be treated as de-identified.** The local store never leaves the machine in raw form.

### The 18 Safe Harbor Identifiers — NEVER persisted in raw form
Names, geographic data (below state), dates (below year), phone, fax, email, SSN, MRN, health plan ID, account numbers, certificate numbers, VIN, device IDs, URLs, IPs, biometrics, photos.

### Scrubbing Pipeline
Every observation passes through before storage:
1. **Regex scan** for known PHI patterns (SSN, phone, DOB, MRN formats)
2. **Name detection** — window titles and UI element text checked against heuristic (capitalized word pairs, "Patient:", "Name:")
3. **Hash** correlation identifiers with per-pharmacy HMAC-SHA256 salt (keyed hash — not dictionary-attackable like plain SHA-256)
4. **Coarsen timestamps** — workflow events stored with hour-of-day granularity only (not exact timestamps tied to patient interactions). Schema/process discovery timestamps can be exact (not patient-linked).
5. **Fail-closed SQL tokenizer** — parse query text in memory, extract only structure. Discard any statement that can't be safely normalized.
6. **Audit** — log every scrub action in `learning_audit` with `phi_scrubbed = 1`

### What IS stored (ePHI-protected locally, de-identified for export)
- Process names, exe paths (software names are not PHI)
- Table names, column names, data types (schema structure is not PHI)
- Query shapes with parameterized placeholders (fail-closed tokenizer)
- UI control types, automation IDs, DataGrid column headers (not values)
- Temporal patterns in hour-of-day buckets

### What is NEVER stored
- Patient names, DOBs, addresses, phone numbers
- Raw SQL query text (only tokenized shapes)
- Raw window title text (HMAC-hashed only, never plain SHA-256)
- UI element values/text content
- SQL row data
- Any data that could re-identify a patient when combined with the POM

### Export Sanitization (before upload to cloud)
The POM export strips everything that makes the local store ePHI:
- All HMAC hashes removed (not needed for adapter generation)
- All timestamps coarsened to day granularity
- All free text fields removed (only structured schema data)
- Connection strings, credentials, file paths stripped
- Result: de-identified operational model suitable for dashboard review

## Operational Modes (Tesla FSD Model)

Like Supervised FSD — the agent graduates through trust levels:

### 1. Observer Mode (default for new installs)
- Agent watches everything, touches nothing
- Builds the POM over 30 days
- Dashboard shows real-time learning progress
- Pharmacist sees: "Your agent is learning your pharmacy's systems"
- **No data operations.** Zero risk.

### 2. Supervised Mode (post-approval)
- Agent detects Rxs, proposes actions, but **waits for human confirmation**
- Dashboard shows: "5 prescriptions detected as delivery-ready. Approve sync?"
- Fleet operator or pharmacist one-tap approves each batch
- Writebacks proposed but held until confirmed
- Every action logged, every result verified against the POM
- **Training wheels.** Operator corrections are stored in a SEPARATE candidate model, NOT the approved production model.
- **Production model is FROZEN after approval.** It runs unchanged until explicitly replaced.
- To update the model: candidate model accumulates corrections → offline retrain → diff shown to operator → re-approval required. Same approval flow as initial 30-day review.

### 3. Autonomous Mode (earned trust)
- After N successful supervised cycles (configurable, default 50), agent can request promotion
- Fleet operator reviews accuracy metrics: "98% correct detection over 50 batches"
- Operator promotes to autonomous — agent syncs Rxs and writebacks without confirmation
- Dashboard still shows everything in real-time (monitoring, not approving)
- One-tap **pause** button — instantly reverts to Supervised if anything looks wrong
- **Full self-driving.** Human monitors but doesn't intervene unless needed.

### Mode Transitions
```
Observer → [30-day learning + POM approval] → Supervised
Supervised → [N successful cycles + operator promotion] → Autonomous
Autonomous → [operator pause / anomaly detected] → Supervised
Any mode → [operator override] → Observer (full reset)
```

### Anomaly-Triggered HARD STOP (not soft downgrade)
If ANY of the following occur during Autonomous mode, agent **immediately halts all data operations** and reverts to Supervised:
- PMS schema changed (new columns, renamed tables, FK changes)
- Status GUID/value changed (PMS update)
- Any observer health regression (UIA observer dies, DMV access lost, IPC broken)
- Audit logging gap detected (missed entries in learning_audit chain)
- Detection accuracy drops below threshold (from sampled re-verification)
- Permission loss (SQL connection fails, service account changed)
- **This is a HARD STOP, not a suggestion.** Agent ceases autonomous operations within one heartbeat cycle.
- Operator notified immediately. Must explicitly re-approve to resume.

### Drift Partitioning
- Schema surface fingerprinted continuously (hash of table/column/type structure)
- If fingerprint changes mid-learning: current epoch closes, new epoch opens
- Each epoch gets separate POM data, separate confidence scores
- PMS update mid-learning = automatic epoch split, NOT blended data
- Multiple PMS systems on same machine = separate partitions per PMS process, separate approval per partition
- RDP/Terminal Server sessions = separate Helper per session, observations tagged with session ID

### Anti-Poisoning
- Candidate scoring includes provenance: how long has this table existed? Consistent across epochs?
- **Stability requirement**: candidate must be stable for >= 7 days before reaching the approval dashboard
- Negative evidence surfaced to operator alongside positive (e.g. "this table looks like Rx queue BUT it was created 2 days ago and has unusual access patterns")
- Operator sees competing candidates with confidence scores, not just the top recommendation
- Longitudinal consistency: candidates that appear/disappear or change structure are flagged

## Approval Flow (Human-in-the-Loop)

### After 30 days:
1. Agent compiles POM summary: process catalog, PMS identification, schema map, Rx queue candidates, status mapping
2. POM summary uploaded to cloud (sanitized — no connection strings, no credentials, no PHI)
3. Dashboard shows the learned model to fleet operator:
   - "We found PioneerRx (98% confidence) running on this machine"
   - "Database has 324 Rx-related tables. Top candidate for Rx queue: `Prescription.RxTransaction`"
   - "Discovered 10 workflow statuses. 'Waiting for Pick up' (GUID: 53ce...) appears to be delivery-ready"
   - "Recommended detection query: `SELECT ... FROM Prescription.RxTransaction WHERE RxTransactionStatusTypeID IN (...)`"
4. Operator reviews, adjusts if needed, clicks Approve
5. Cloud computes `approved_model_digest` = SHA-256 over `{pharmacy_id, session_id, sanitized_pom_json, adapter_config_json}`
6. Signed approval command sent to agent (ECDSA) containing the `approved_model_digest`
7. Agent verifies local POM digest matches `approved_model_digest` exactly. **Refuse activation if mismatch** (TOCTOU protection — prevents activating a model that was mutated after review)
8. Agent transitions learning session from `model` → `approved` → `active`
9. Generated adapter begins Rx detection using the approved query/statuses

### Rejection/Correction:
- Operator can mark candidates as wrong ("that's not the Rx queue")
- Agent re-evaluates with human feedback as constraint
- New model presented for re-review

## Integration with Existing Architecture

### What stays the same:
- Three-process model (Broker/Core/Helper)
- Heartbeat + signed commands
- Audit chain
- SQLCipher encryption
- Self-update mechanism
- IPC framing

### What changes:
- `RxDetectionWorker` becomes adapter-driven (current PioneerRx logic becomes the first adapter, auto-generated adapters follow the same interface)
- `AgentStateDb` gains POM tables (migration in InitSchema)
- New `LearningWorker` BackgroundService orchestrates the 30-day phases
- `AgentOptions` gains `LearningMode` flag (true for new installs, false for existing PioneerRx installs)
- Helper's `PioneerRxUiaEngine` becomes one implementation of a generic `IPmsUiaAdapter`

### Backwards Compatibility:
- Existing PioneerRx installs continue working unchanged (LearningMode = false)
- PioneerRx adapter = the "gold standard" hand-built adapter
- Learning Agent generates adapters that match the same interface
- Over time, learned adapters may outperform hand-built ones (more data points)

## Known PMS Signatures (bootstrap for faster identification)

| PMS | Process Name | Window Title Pattern | Vendor |
|-----|-------------|---------------------|---------|
| PioneerRx | PioneerPharmacy.exe | "Point of Sale" | RedSail/New Tech |
| QS/1 NexGen | QS1NexGen.exe, NexGen*.exe | "NexGen" | QS/1 |
| Liberty | LibertyRx*.exe | "Liberty" | Liberty Software |
| Computer-Rx | ComputerRx.exe | "Computer-Rx" | Computer-Rx |
| BestRx | BestRx.exe | "BestRx" | BestRx |
| Rx30 | Rx30.exe | "Rx30" | Transaction Data |
| McKesson Pharmaserv | Pharmaserv.exe | "Pharmaserv" | McKesson |

These signatures accelerate Phase 1 identification but are not required — the observer learns unknown PMS systems from behavior alone.

## Success Criteria

1. Agent installs on a pharmacy running any PMS from the table above
2. After 30 days, the POM correctly identifies the PMS (>90% confidence)
3. Rx queue candidate matches the actual Rx table (verified by operator)
4. Status mapping correctly identifies delivery-ready statuses
5. Generated adapter produces the same Rx detection results as a hand-built adapter would
6. Zero PHI leakage during the entire learning phase (verified by audit log)
7. Learning phase uses <5% CPU and <128MB RAM on pharmacy hardware

## Implementation Order

1. **POM data model** — add tables to AgentStateDb
2. **Observer interfaces** — IObserver, ObserverPhase, LearningSession
3. **ProcessObserver** — ETW-based, with PHI scrubbing
4. **SqlSchemaObserver** — INFORMATION_SCHEMA + DMV-based
5. **LearningWorker** — orchestrates phases, manages transitions
6. **Pattern Engine** — sequence mining, Rx queue inference, status ordering
7. **POM export** — sanitized upload to cloud
8. **Approval dashboard** — cloud-side review UI
9. **Adapter Generator** — converts approved POM to working adapter
10. **UiAutomationObserver** — tree walking (lower priority — SQL path is stronger)
11. **ScheduleObserver** — temporal patterns (lower priority)
