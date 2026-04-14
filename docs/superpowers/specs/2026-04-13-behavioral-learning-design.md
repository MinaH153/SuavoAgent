# SuavoAgent v3 Spec B: Expanded Behavioral Learning — Design Spec

**Date:** 2026-04-13
**Author:** Claude + Joshua Henein
**Status:** Draft v1 — design approved, pending implementation plan
**Depends on:** Spec A (Writeback), Schema Canary
**Feeds into:** Spec C (Self-Improving Feedback), Spec D (Collective Intelligence)

## Problem

SuavoAgent's learning system observes SQL schema structure (INFORMATION_SCHEMA) and process lifecycle, but has zero visibility into how pharmacy technicians actually USE the PMS. It can discover that `Prescription.RxTransaction` exists and has a status column — but it can't discover that "the technician clicks btnComplete on the Rx Detail screen and 1.2 seconds later PioneerRx fires `UPDATE RxTransaction SET StatusTypeID = @p`." Without behavioral observation, the agent can learn WHAT data exists but not HOW the pharmacy operates on it. And without the UI↔SQL correlation, the agent can never learn writeback paths automatically.

## Solution

A three-tier behavioral observation system that captures UI structure, user interaction patterns, and SQL query execution — then correlates them to discover workflows and writeback paths. All observation is PHI-safe (structure-not-content, allowlist-not-denylist) with capture-time enforcement in the Helper process.

## Architecture

```
HELPER PROCESS (User Session)                         CORE PROCESS (Session 0)
+---------------------------------+                  +--------------------------------------+
|  UiaTreeObserver                |                  |  BehavioralEventReceiver             |
|  (60s tree walks, GREEN+YELLOW) |                  |  (deserialize, validate, persist)    |
|                                 |                  |                                      |
|  UiaInteractionObserver         |    IPC pipe      |  DmvQueryObserver                    |
|  (FocusChanged, InvokePattern)  |---(TrySendAsync)--->  (dm_exec_sql_text, tokenized)    |
|                                 |  fire-and-forget |                                      |
|  KeyboardCategoryHook           |                  |  ActionCorrelator                    |
|  (WH_KEYBOARD_LL, PMS-focus)   |                  |  (UI event + SQL event -> linked)    |
+---------------------------------+                  |                                      |
|  UiaElementScrubber             |                  |  RoutineDetector                     |
|  (allowlist filter BEFORE IPC)  |                  |  (sequence mining -> action graphs)  |
|                                 |                  +--------------------------------------+
|  BehavioralEventBuffer          |                  |  SQLCipher (AgentStateDb)            |
|  (batch, compress, TrySendAsync)|                  |  behavioral_events, correlated_actions|
+---------------------------------+                  |  learned_routines, dmv_query_obs     |
                                                     +--------------------------------------+
```

**Helper side** — three observers, one scrubber, one buffer. Each observer produces `BehavioralEvent` records. The scrubber enforces the HIPAA GREEN/YELLOW/RED boundary before any event enters the buffer. The buffer batches events (up to 50 or every 5 seconds, whichever comes first) and sends via `TrySendAsync`. If Core is down or slow, events are dropped — not queued indefinitely (bounded buffer, oldest-first eviction).

**Core side** — receiver validates and persists. DmvQueryObserver runs independently (SQL domain, not UI). ActionCorrelator joins UI events to SQL events by timestamp proximity (configurable window, default 2 seconds). RoutineDetector runs periodically (every 5 minutes during Pattern phase) consuming correlated action pairs to build the action sequence graph.

**HIPAA boundary** — the UiaElementScrubber is the single enforcement point. It runs in Helper before serialization. Core trusts that what arrives over IPC is already scrubbed. Core never attempts to read UI properties directly (Session 0 can't). If a RED property accidentally reaches the wire, it's already a breach — hence capture-time enforcement, not storage-time.

**IPC model** — behavioral events use fire-and-forget (`TrySendAsync`), not request-response (`SendAsync`). Observation data is best-effort. Dropping a tree snapshot is acceptable. Blocking the Helper waiting for Core's response is not.

## HIPAA Boundary: GREEN / YELLOW / RED Matrix

Validated by Codex HIPAA compliance review. Enforced at capture time in Helper, before IPC serialization.

| Tier | Properties | Treatment | Rationale |
|------|-----------|-----------|-----------|
| **GREEN** | ControlType, AutomationId, ClassName, BoundingRectangle | Stored plain in SQLCipher | Developer-set structural properties. Never contain patient data. |
| **YELLOW** | Name, DataGrid column headers | HMAC-SHA256 with per-pharmacy salt. Never stored raw. | May contain patient-contextual data (e.g., "Smith, John - Rx #12345" as a grid row Name, or a PMS that puts patient names in column headers). Hash preserves structural identity (same label = same hash) while eliminating PHI. |
| **RED** | Value, Text, Selection, HelpText, ItemStatus, clipboard, screenshots, actual keystrokes | **NEVER captured. Hard block in code.** | Direct patient data. The UiaElementScrubber allowlists GREEN+YELLOW properties only — RED properties are not read, not serialized, not transmitted. |

**Key principle:** Allowlist enforcement happens at capture time in the Helper process, not at storage time in Core. If a RED property gets sent over IPC, it's already in memory on the wrong side of the boundary.

## Helper Components

### UiaTreeObserver

Periodic tree walker that snapshots the PMS window's UI Automation tree. Runs every 60 seconds when the PMS process is alive (not when PMS has focus — tree structure doesn't change based on focus, and we want to observe even when the technician is in another app).

**What it captures (GREEN tier):**
- `ControlType` (Button, DataGrid, Tab, MenuItem, etc.)
- `AutomationId` (developer-set, stable across sessions)
- `ClassName` (Win32/WPF class name)
- `BoundingRectangle` (pixel coordinates — layout fingerprint for tree snapshots)

**YELLOW tier (HMAC-hashed):**
- `Name` property — hashed with per-pharmacy salt before leaving Helper
- DataGrid column headers — treated as YELLOW (same reasoning as Name: most are structural field names like "Rx No", but some PMS systems may put patient-contextual data in headers)

**Tree depth limit:** Max 8 levels deep. PioneerRx's WinForms tree can be 15+ levels with container panels that carry no information. Cap at 8, which captures toolbar -> tabs -> content area -> DataGrid -> column headers.

**Structural fingerprint:** SHA-256 over `{ControlType + AutomationId + ClassName}` for each element, concatenated in tree order. Produces a `tree_hash` that detects screen changes. Same screen = same hash. New dialog opens = different hash. This is how the ActionCorrelator knows "we're on the Rx Detail screen" vs "we're on the Main Grid."

**Output:** `BehavioralEvent` of type `tree_snapshot` containing the scrubbed element catalog + tree_hash.

### UiaInteractionObserver

Subscribes to UIA events on the PMS window. Event-driven, not polling.

**Events subscribed:**
- `FocusChangedEvent` — fires when focus moves between elements. Captures the target element's GREEN properties + HMAC'd Name. Detects "technician tabbed from field A to field B."
- `InvokePattern.InvokedEvent` — fires when buttons/menu items are invoked. The "click" signal. Captures which element was invoked (by AutomationId + ControlType).
- `StructureChangedEvent` — fires when the tree changes (dialog opens, panel expands). Triggers an immediate tree re-snapshot via UiaTreeObserver.

**What it does NOT subscribe to:**
- `TextChangedEvent` — RED tier. Would capture text content.
- `ValuePattern` — RED tier. Would capture field values.
- `SelectionPattern` — RED tier. Would capture what's selected in dropdowns/grids.

**Click sequence recording:** Each InvokePattern event is recorded as `(timestamp, tree_hash, element_id, ControlType)`. Element identification uses AutomationId as primary key. If AutomationId is empty (some WinForms controls), fall back to `ClassName + tree_depth + child_index` as a tree-positional fingerprint, where `child_index` is the index among siblings of the same ControlType (not absolute child index — this prevents breakage when unlike siblings are reordered). This fallback survives window resize/DPI changes (unlike BoundingRect, which is screen-positional). BoundingRect is used only in tree snapshots as a layout fingerprint, never for cross-session element matching.

**Output:** `BehavioralEvent` of type `interaction` with subtype `focus_changed`, `invoked`, or `structure_changed`.

### KeyboardCategoryHook

Low-level keyboard hook via `SetWindowsHookEx(WH_KEYBOARD_LL)`. Only active when PMS window has foreground focus.

**Lifecycle:**
1. Helper registers a `WinEventProc` for `EVENT_SYSTEM_FOREGROUND` (window activation)
2. When PMS window gains foreground -> install keyboard hook
3. When PMS loses foreground -> uninstall keyboard hook immediately
4. On Helper shutdown -> uninstall hook (finally block, no leaks)

**What it captures:**
- Keystroke **category** only: `alpha`, `digit`, `tab`, `enter`, `escape`, `function_key`, `navigation` (arrows/home/end/pgup/pgdn), `modifier` (shift/ctrl/alt), `other`
- **Never** the actual key code, character, or scan code
- Coarse timing bucket between keystrokes: `rapid` (<500ms), `normal` (500ms-2s), `pause` (>2s)
- Digit sequence counter, **capped at 3**. "User typed 7 digits" is recorded as "digit x3" — prevents reconstructing Rx numbers, phone numbers, NDCs

**What it blocks:**
- Virtual key code -> hard-mapped to category enum, VK code discarded immediately in the hook callback
- No buffering of raw keycodes anywhere — category is computed inline in the `LowLevelKeyboardProc`

**Event coalescing:** Rapid same-category keystrokes become one event with a count in the BehavioralEventBuffer, not individual events per keystroke. This reduces event volume during active typing from ~300/min to ~20/min.

**Output:** `BehavioralEvent` of type `keystroke_category` with `(category, timing_bucket, sequence_count)`.

### UiaElementScrubber

The HIPAA enforcement point. Static utility class, called by every observer before any event enters the BehavioralEventBuffer.

**Allowlist (exhaustive — anything not listed is dropped):**
```
GREEN:  ControlType, AutomationId, ClassName, BoundingRectangle
YELLOW: Name -> HMAC-SHA256(Name, pharmacySalt)
        DataGrid column headers -> HMAC-SHA256(header, pharmacySalt)
RED:    Value, Text, Selection, HelpText, ItemStatus -> BLOCKED (never read)
```

**Implementation:** Takes an `AutomationElement`, returns a `ScrubbedElement` record containing only allowlisted properties. If an element has no AutomationId AND no ClassName (completely anonymous), it's dropped entirely — can't be stably identified across sessions.

**Audit:** Every scrub increments a counter. Every YELLOW hash increments a separate counter. Both reported in observer health.

### BehavioralEventBuffer

Bounded in-memory ring buffer in Helper. Decouples observer event rates from IPC throughput.

- **Capacity:** 500 events (oldest-first eviction when full)
- **Flush trigger:** 50 events accumulated OR 5 seconds elapsed, whichever first
- **Flush method:** `TrySendAsync` — fire-and-forget over IPC pipe
- **Drop counter:** `droppedEventCount` incremented on eviction, reported in heartbeat telemetry. If drop rate exceeds 5%, signals that Core is too slow or Helper observation frequency is too high — Spec C's self-improvement engine uses this to auto-adjust scan intervals.
- **Serialization:** Events serialized to JSON before entering buffer (scrubbing already done)
- **Backpressure:** None. If Core can't keep up, events drop. By design — observation is best-effort.

## Core Components

### BehavioralEventReceiver

Handles the new `behavioral_events` IPC command in `IpcPipeServer`. Receives batched events from Helper, validates, persists.

**Validation rules:**
- Event must have a `session_id` matching the active learning session
- Event must have a valid `type` (one of: `tree_snapshot`, `interaction`, `keystroke_category`)
- Tree snapshot events must have a non-empty `tree_hash`
- Interaction events must have a non-empty `element_id` (AutomationId or fallback key)
- Reject any event containing a `value` or `text` field — defense-in-depth behind Helper's scrubber

**Persistence:** Writes to `behavioral_events` table in AgentStateDb. Each event gets a monotonic sequence number (for ordering) and the wall-clock timestamp from Helper (not Core's receive time — Helper's timestamp is when the action happened).

**Deduplication:** Tree snapshots with the same `tree_hash` within the same 60-second window are deduplicated (store once, increment `occurrence_count`). Interaction events are never deduplicated — every click matters for sequence mining.

### DmvQueryObserver

New `ILearningObserver` implementation. Polls SQL Server DMVs for recently executed queries, processes them through the fail-closed SqlTokenizer, persists normalized shapes.

**Active phases:** Pattern, Model (same as SqlSchemaObserver, but different cadence).

**Poll interval:** Every 10 seconds during active phases. DMV query plan cache entries can evict quickly on busy servers — 10s minimizes missed queries without overloading the SQL Server.

**Query:**
```sql
SELECT qs.execution_count, qs.last_execution_time,
       SUBSTRING(st.text, (qs.statement_start_offset/2)+1,
           ((CASE qs.statement_end_offset WHEN -1 THEN DATALENGTH(st.text)
             ELSE qs.statement_end_offset END - qs.statement_start_offset)/2)+1)
FROM sys.dm_exec_query_stats qs
CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) st
WHERE qs.last_execution_time > @since
ORDER BY qs.last_execution_time DESC
```

**Processing pipeline:**
1. Raw SQL text retrieved in-memory (never persisted raw)
2. `SqlTokenizer.TryNormalize()` — fail-closed tokenizer strips literals, extracts shape + tables
3. If tokenizer returns null -> entire statement discarded
4. Normalized shape persisted to `dmv_query_observations` table with `last_execution_time` and `execution_count`

**Capability gating:** DMV access requires `VIEW SERVER STATE` permission. Calls `SqlSchemaObserver.CheckDmvAccessAsync()` at startup. If unavailable, observer logs a warning and goes dormant — never errors, never retries (permission won't change without admin action). Reports `hasDmvAccess: false` in heartbeat telemetry.

**Output:** Writes to `dmv_query_observations` table. Also emits internal events consumed by ActionCorrelator (Core-internal, not over IPC).

### ActionCorrelator

Links UI events to SQL events by timestamp proximity. The writeback discovery engine.

**Correlation logic:**
- Maintains a sliding window of recent UI events (last 30 seconds)
- When a new DMV query observation arrives with `last_execution_time` within the window, scan for UI events within +/-2 seconds of the SQL execution time
- If a match is found: create a `CorrelatedAction` record linking the UI event (which button was clicked) to the SQL event (which query fired)
- Correlation key: `(tree_hash, element_id, query_shape_hash)` — "on this screen, clicking this element triggers this query"

**Correlation window:** Default 2 seconds (not 500ms). PioneerRx is a .NET WinForms app on often-slow pharmacy hardware. The gap between UI click and SQL execution can be 1-2 seconds if PioneerRx does validation, displays a confirmation dialog, or runs business logic before hitting SQL. Spec C's self-improvement engine tunes this window down based on observed latency distributions.

**Confidence scoring:**
- Single co-occurrence: `0.3` — could be coincidence
- 3+ co-occurrences of same correlation key: `0.6` — likely causal
- 10+ co-occurrences: `0.9` — high confidence
- Confidence decays if the correlation stops appearing (query shape evicted from DMV cache, or workflow changed)

**Write detection:** CorrelatedActions where the SQL query is a write (`SqlTokenizer.NormalizedQuery.IsWrite = true`) are flagged as **writeback candidates**. These are the golden signals — "clicking this button triggers this UPDATE." Spec C's self-improvement engine consumes these to propose automated writebacks.

**Clock alignment:** UI event timestamps come from Helper (wall clock). SQL execution times come from DMV (`last_execution_time`, SQL Server's clock). These clocks may differ. On first correlation attempt, ActionCorrelator measures the delta between Helper's `DateTime.UtcNow` and SQL Server's `GETUTCDATE()` and applies a correction offset. Re-calibrates every hour.

**Clock calibration fallback:** If calibration fails (SQL connection down), use default offset of 0 and widen the correlation window to +/-5 seconds until calibration succeeds. Correlation degrades gracefully, never blocks.

### RoutineDetector

Consumes the correlated action stream and mines repeatable sequences using a directly-follows graph (DFG).

**Input:** Stream of `CorrelatedAction` records, ordered by timestamp. Each record is a node: `(tree_hash, element_id, query_shape_hash?)`. The `query_shape_hash` is optional — some actions don't trigger SQL.

**Edge construction:** If action B follows action A within 30 seconds and on the same or immediately subsequent screen (same `tree_hash` or a `structure_changed` event bridges them), create a directed edge A->B with a frequency count.

**Routine extraction:**
- A routine is a path through the DFG with frequency >= 5 (observed at least 5 times)
- Minimum path length: 3 actions (below that it's just "click a button")
- Maximum path length: 20 actions (longer sequences are likely multiple routines concatenated)
- Routines are identified by their hash: SHA-256 of the ordered `(tree_hash, element_id)` pairs in the path

**Cadence:** Runs every 5 minutes during Pattern phase. During Model phase, runs once on phase entry to produce the final routine set for POM export.

**Output:** `learned_routines` table — each routine is a JSON-serialized path through the DFG with frequency, confidence, and a list of correlated SQL queries (by query_shape_hash).

## Data Model

New tables added to AgentStateDb (SQLCipher). Follows existing migration pattern — `CREATE TABLE IF NOT EXISTS` + `TryAlter` for additions.

### behavioral_events
```sql
CREATE TABLE IF NOT EXISTS behavioral_events (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    sequence_num INTEGER NOT NULL,
    event_type TEXT NOT NULL,               -- 'tree_snapshot', 'interaction', 'keystroke_category'
    event_subtype TEXT,                     -- interaction: 'focus_changed', 'invoked', 'structure_changed'
                                            -- keystroke: category value
    tree_hash TEXT,                         -- structural fingerprint of current screen
    element_id TEXT,                        -- AutomationId or ClassName+depth+childIdx fallback
    element_control_type TEXT,
    element_class_name TEXT,
    element_name_hash TEXT,                 -- HMAC-SHA256 of Name property (YELLOW tier)
    element_bounding_rect TEXT,             -- populated for tree_snapshot events only, NULL for interactions
    keystroke_category TEXT,                -- alpha/digit/tab/enter/escape/function/nav/modifier/other
    keystroke_timing_bucket TEXT,           -- rapid/normal/pause
    keystroke_sequence_count INTEGER,       -- capped at 3 for digit sequences
    occurrence_count INTEGER DEFAULT 1,     -- dedup counter for tree snapshots
    helper_timestamp TEXT NOT NULL,
    received_at TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_be_session_seq ON behavioral_events(session_id, sequence_num);
CREATE INDEX IF NOT EXISTS idx_be_session_type ON behavioral_events(session_id, event_type);
CREATE INDEX IF NOT EXISTS idx_be_tree_hash ON behavioral_events(session_id, tree_hash);
```

### dmv_query_observations
```sql
CREATE TABLE IF NOT EXISTS dmv_query_observations (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    query_shape_hash TEXT NOT NULL,
    query_shape TEXT NOT NULL,
    tables_referenced TEXT NOT NULL,         -- JSON array
    is_write INTEGER NOT NULL DEFAULT 0,
    execution_count INTEGER DEFAULT 1,
    last_execution_time TEXT NOT NULL,        -- SQL Server clock
    clock_offset_ms INTEGER DEFAULT 0,       -- Helper-SQLServer delta at capture
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_dqo_session_time ON dmv_query_observations(session_id, last_execution_time);
CREATE INDEX IF NOT EXISTS idx_dqo_shape ON dmv_query_observations(session_id, query_shape_hash);
```

### correlated_actions
```sql
CREATE TABLE IF NOT EXISTS correlated_actions (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    correlation_key TEXT NOT NULL,           -- "tree_hash:element_id:query_shape_hash"
    tree_hash TEXT NOT NULL,
    element_id TEXT NOT NULL,
    element_control_type TEXT,
    query_shape_hash TEXT,                   -- null if no SQL correlation
    query_is_write INTEGER DEFAULT 0,
    tables_referenced TEXT,                  -- JSON array
    occurrence_count INTEGER DEFAULT 1,
    confidence REAL DEFAULT 0.3,
    first_seen TEXT NOT NULL,
    last_seen TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_ca_session_key ON correlated_actions(session_id, correlation_key);
CREATE INDEX IF NOT EXISTS idx_ca_writeback ON correlated_actions(session_id, query_is_write)
    WHERE query_is_write = 1;
```

### learned_routines
```sql
CREATE TABLE IF NOT EXISTS learned_routines (
    id INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id TEXT NOT NULL,
    routine_hash TEXT NOT NULL,
    path_json TEXT NOT NULL,                 -- JSON array of {tree_hash, element_id, control_type, query_shape_hash?}
    path_length INTEGER NOT NULL,
    frequency INTEGER NOT NULL,
    confidence REAL DEFAULT 0.0,
    start_element_id TEXT,
    end_element_id TEXT,
    correlated_write_queries TEXT,           -- JSON array of query_shape_hashes
    has_writeback_candidate INTEGER DEFAULT 0,
    discovered_at TEXT NOT NULL,
    last_observed TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_lr_session ON learned_routines(session_id);
CREATE INDEX IF NOT EXISTS idx_lr_writeback ON learned_routines(session_id, has_writeback_candidate)
    WHERE has_writeback_candidate = 1;
```

### Data Retention

Raw `behavioral_events` are intermediate data. `PruneBehavioralEvents(sessionId, olderThanDays)` deletes raw events older than N days where the corresponding `tree_hash` has at least one stable routine (frequency >= 5) in `learned_routines`. Events whose screen hasn't produced a stable routine are retained (still being mined). Runs once per day during Pattern/Model phases. After Model phase completes and POM is frozen, all raw events are eligible for pruning. `correlated_actions` and `learned_routines` persist indefinitely — they are the distilled intelligence.

### Heartbeat Telemetry Extension

Added to `HealthSnapshot.Take()`:
```json
{
  "behavioral": {
    "treeSnapshotCount": 142,
    "uniqueScreens": 8,
    "interactionEventCount": 1847,
    "keystrokeCategoryCount": 5230,
    "droppedEventCount": 12,
    "dropRatePercent": 0.2,
    "dmvQueryShapes": 34,
    "dmvWriteShapes": 6,
    "correlatedActions": 89,
    "writebackCandidates": 3,
    "learnedRoutines": 5,
    "routinesWithWriteback": 2,
    "clockOffsetMs": -45,
    "clockCalibrated": true,
    "hasDmvAccess": true
  }
}
```

## SqlTokenizer Hardening

Two priority tiers. PHI safety first (closes bypass paths in the fail-closed design), then parsing completeness (extracts more structure from complex queries).

### Tier 1: PHI Safety (from Codex HIPAA review)

**1. Hex literal detection.**
`0xDEADBEEF` bypasses current string/numeric literal checks. Hex literals can encode patient data in stored procedures. Add regex `0x[0-9a-fA-F]+` — if found, discard entire statement.

**2. N'unicode' prefix and escaped quote handling.**
Strengthen string literal pattern to handle Unicode prefix and doubled single-quote escaping: `N?'(?:[^']|'')*'`. Covers `N'John Smith'` and `N'O''Brien'`. Any string literal found (including Unicode) -> discard.

**3. SQL comment stripping.**
`-- patient name: John Smith` and `/* DOB: 01/15/1990 */` embed PHI in comments. Strip all comments before tokenization:
- Line comments: `--.*$` (multiline mode)
- Block comments: `/\*[\s\S]*?\*/` (non-greedy, iterative stripping for nested blocks)
Comments stripped first, then cleaned SQL goes through existing tokenizer logic.

**4. OPENQUERY / OPENROWSET / OPENDATASOURCE blocking.**
Inner query strings in linked server calls are opaque to the tokenizer. Add all three keywords to the blocked keyword set alongside existing DDL/EXEC blocklist.

**5. LIKE pattern awareness.**
LIKE operands that are string literals are already caught by rule 2. Add LIKE token awareness so the tokenizer doesn't misparse the pattern keyword or its operand as a table reference.

### Tier 2: Parsing Completeness

**6. Nested subqueries.**
Track parenthesis depth. When entering `(SELECT ...`, recursively extract table references. Max depth 4 to prevent pathological nesting from hanging the tokenizer.

**7. CTE handling.**
Detect `WITH <name> AS (` pattern. Extract tables from CTE body. Add CTE name to local alias set so it's not counted as a table reference in the consuming query.

**8. UNION / INTERSECT / EXCEPT.**
Split on these keywords (respecting parenthesis depth), tokenize each branch independently, merge table reference lists.

**9. Aliased table references.**
After extracting a table reference from FROM/JOIN/INTO/UPDATE, skip the next token if it's a short identifier matching common alias patterns. Don't add aliases to the table list.

**10. Cross-database references.**
Extend table reference pattern to handle three-part names: `[database].[schema].[table]`. Extract only `schema.table` (database name may reveal infrastructure details).

## IPC Protocol Extension

New IPC command:

```csharp
public const string BehavioralEvents = "behavioral_events";
```

**Request payload:**
```json
{
  "batch_id": "b-20260413-001",
  "event_count": 47,
  "dropped_since_last": 0,
  "events": [
    {
      "seq": 1042,
      "type": "tree_snapshot",
      "tree_hash": "a1b2c3...",
      "elements": [
        {
          "control_type": "Button",
          "automation_id": "btnComplete",
          "class_name": "WindowsForms10.BUTTON",
          "name_hash": "d4e5f6...",
          "bounding_rect": "100,200,50,25",
          "depth": 3,
          "child_index": 2
        }
      ],
      "ts": "2026-04-13T14:30:00.123Z"
    },
    {
      "seq": 1043,
      "type": "interaction",
      "subtype": "invoked",
      "tree_hash": "a1b2c3...",
      "element_id": "btnComplete",
      "control_type": "Button",
      "class_name": "WindowsForms10.BUTTON",
      "name_hash": "d4e5f6...",
      "ts": "2026-04-13T14:30:02.456Z"
    },
    {
      "seq": 1044,
      "type": "keystroke_category",
      "category": "digit",
      "timing": "rapid",
      "count": 3,
      "ts": "2026-04-13T14:30:05.789Z"
    }
  ]
}
```

**Response:** None expected (`TrySendAsync` — fire-and-forget). Core processes asynchronously. If Core needs to signal Helper (e.g., reduce observation frequency), it uses the existing signed command pathway, not IPC responses.

**Wire format:** Same `IpcFraming` length-prefixed JSON as existing commands. Batch size self-regulates via BehavioralEventBuffer (max 50 events or 5 seconds).

## POM Export Extension

`PomExporter.Export()` gains a `behavioral` section:

```json
{
  "behavioral": {
    "uniqueScreens": 8,
    "screenFingerprints": ["a1b2c3...", "d4e5f6..."],
    "routines": [
      {
        "routineHash": "abc123...",
        "path": [
          {"treeHash": "a1b2c3...", "elementId": "btnComplete", "controlType": "Button", "queryShapeHash": null},
          {"treeHash": "a1b2c3...", "elementId": "tabWorkflow", "controlType": "Tab", "queryShapeHash": null},
          {"treeHash": "d4e5f6...", "elementId": "btnDelivery", "controlType": "Button", "queryShapeHash": "xyz789..."}
        ],
        "pathLength": 3,
        "frequency": 47,
        "confidence": 0.92,
        "hasWritebackCandidate": true,
        "correlatedWriteQueries": ["xyz789..."]
      }
    ],
    "writebackCandidates": [
      {
        "correlationKey": "a1b2c3:btnComplete:xyz789",
        "elementId": "btnComplete",
        "controlType": "Button",
        "queryShapeHash": "xyz789...",
        "queryShape": "UPDATE [Prescription].[RxTransaction] SET [RxTransactionStatusTypeID] = @p WHERE [RxNumber] = @p",
        "tablesReferenced": ["Prescription.RxTransaction"],
        "occurrences": 47,
        "confidence": 0.92
      }
    ],
    "dmvAccess": true,
    "observationDays": 14,
    "totalInteractions": 1847,
    "droppedEventRate": 0.002
  }
}
```

**What's exported:** AutomationIds, tree hashes, control types, query shapes (already parameterized), frequencies, confidence scores. The `queryShape` field contains parameterized SQL (PHI-free) — valuable for Spec D's collective intelligence to generate learned adapters at other pharmacies.

**What's NOT exported:** Name hashes (YELLOW — not needed for adapter generation), bounding rects, raw event data, keystroke categories (intermediate signal consumed by routine detection), clock offsets.

**Dashboard display note (for Spec D):** The dashboard should display writeback candidates as "Writes to: Prescription.RxTransaction" rather than raw parameterized SQL. The operator doesn't need to read SQL to approve — they need to know which table is affected. The raw `queryShape` stays in the export for collective intelligence consumption.

## BAA & Disclosure

### BAA Behavioral Observation Clauses

1. **UI Automation Observation.** Agent observes the structural properties (control type, automation identifier, class name, bounding rectangle) of user interface elements in pharmacy management software windows. Element content, values, and text are never captured.

2. **Element Name Hashing.** The Name property of UI elements, which may incidentally contain patient-contextual information, is cryptographically hashed using a per-pharmacy keyed hash (HMAC-SHA256) before storage. The raw Name value is never persisted, transmitted, or logged.

3. **Keyboard Category Monitoring.** When the pharmacy management software window has foreground focus, the agent classifies keystrokes into categories (alphabetic, numeric, navigation, function) to detect data entry patterns. Individual key codes, characters, and typed content are never captured. Numeric digit sequences are capped at a count of three to prevent reconstruction of identifiers.

4. **SQL Query Shape Observation.** When database server permissions allow, the agent observes the structural shape of SQL queries executed by the pharmacy management software. All literal values (strings, numbers, identifiers) are stripped before storage. Queries that cannot be safely normalized are discarded entirely.

5. **Low-Level Keyboard Hook Disclosure.** The agent uses the Windows `SetWindowsHookEx(WH_KEYBOARD_LL)` API to classify keystroke categories. This system-level hook is installed only when the pharmacy management software has foreground window focus and is immediately uninstalled when focus is lost. Endpoint protection software may detect this hook installation. The hook captures keystroke categories only, never individual key codes or characters.

### Installer Disclosure Screen

Added to the installer's consent flow (before installation proceeds):

> **Behavioral Learning:** During the learning period, SuavoAgent observes the structure of your pharmacy software's screens and the patterns of how it's used (which buttons are clicked, which screens are visited, what types of data are entered). It does NOT capture what you type, patient information, or screen contents. A low-level keyboard classification hook is active only when your pharmacy software is in the foreground. Your endpoint protection software may detect this hook — it is expected behavior.

## Integration with Existing Architecture

### What stays the same:
- Three-process model (Broker/Core/Helper)
- Heartbeat + signed commands
- Audit chain
- SQLCipher encryption
- Self-update mechanism
- IPC framing protocol
- Existing observers (ProcessObserver, SqlSchemaObserver)
- LearningWorker phase management

### What changes:
- **Helper** gains UiaTreeObserver, UiaInteractionObserver, KeyboardCategoryHook, UiaElementScrubber, BehavioralEventBuffer
- **Core** gains BehavioralEventReceiver, DmvQueryObserver, ActionCorrelator, RoutineDetector
- **IpcCommands** gains `BehavioralEvents` constant
- **AgentStateDb** gains 4 new tables + retention pruning
- **HealthSnapshot** gains behavioral telemetry section
- **PomExporter** gains behavioral export section
- **SqlTokenizer** hardened with 10 items (5 PHI safety + 5 parsing completeness)
- **LearningWorker** starts DmvQueryObserver alongside existing observers, runs RoutineDetector during Pattern/Model phases

### PMS-agnostic design:
All behavioral observation components are PMS-agnostic. They observe ANY window identified as a PMS candidate (via ProcessObserver's `KnownPmsSignatures` or behavioral heuristics). The existing `PioneerRxUiaEngine` remains unchanged — it's the PMS-specific interaction engine for writeback. The new observers watch any PMS. When we add QS/1 support, the behavioral observers work without modification.

## Success Criteria

1. UiaTreeObserver produces stable tree_hash fingerprints for each PMS screen (same screen = same hash across sessions)
2. UiaInteractionObserver captures click sequences using AutomationId as primary key (or tree-positional fallback)
3. KeyboardCategoryHook correctly classifies keystroke categories with zero actual key/character leakage
4. DmvQueryObserver captures and normalizes query shapes when DMV access is available, goes dormant otherwise
5. SqlTokenizer passes all 10 hardening items with test coverage for each bypass vector
6. ActionCorrelator correctly links UI clicks to SQL queries with increasing confidence over time
7. RoutineDetector extracts stable routines (frequency >= 5) that match actual technician workflows
8. At least one writeback candidate discovered in a PioneerRx environment with DMV access
9. Zero PHI leakage through the entire observation pipeline (verified by audit log)
10. Behavioral telemetry reports accurately in heartbeat
11. Raw event retention pruning keeps behavioral_events table under 60K rows
12. Helper observation adds <3% CPU and <32MB RAM on pharmacy hardware

## Implementation Order

1. UiaElementScrubber (HIPAA boundary — everything depends on this)
2. BehavioralEvent types + BehavioralEventBuffer
3. IPC protocol extension (BehavioralEvents command)
4. BehavioralEventReceiver in Core
5. AgentStateDb schema migration (4 new tables)
6. UiaTreeObserver
7. UiaInteractionObserver
8. KeyboardCategoryHook
9. SqlTokenizer hardening (Tier 1 PHI safety first, then Tier 2 parsing)
10. DmvQueryObserver
11. ActionCorrelator
12. RoutineDetector
13. PomExporter behavioral section
14. HealthSnapshot behavioral telemetry
15. Data retention pruning
16. LearningWorker integration (wire everything together)
17. BAA + installer disclosure text
