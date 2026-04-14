# SuavoAgent v3.1 — Hardening & Universal Business Intelligence

**Date:** 2026-04-14
**Status:** Draft
**Scope:** Security hardening (remaining HIGH/MEDIUM), universal desktop observation, categorized data storage, LLM integration, Fleet Operator bridge

---

## 1. Strategic Context

SuavoAgent is a **universal business desktop intelligence agent**. Pharmacy is the HIPAA-compliant beachhead — the hardest compliance bar. Once cleared, every other industry (restaurant, accounting, dental, law) is easier.

**Flywheel:** Agent observes → data trains intelligence → intelligence improves fleet operations → fleet value drives more businesses to install → more data → smarter agent.

**Revenue model:** The real money is Fleet Operator integration. SuavoAgent makes businesses more inclined to use Suavo's delivery/fleet services. The agent is the Trojan horse. The fleet is the moat.

---

## 2. Remaining Security Fixes (from 26-finding audit)

### 2.1 Already Fixed (this session)

| ID | Fix | Status |
|----|-----|--------|
| C-1/1.1 | Removed Care Pharmacy fallback GUIDs | DONE |
| C-4/10.4/10.6 | Deprecated quick-install.ps1 | DONE |
| C-5 | Broker downgraded to NetworkService | DONE |
| C-1 | TrustServerCertificate made configurable | DONE |
| H-3 | HMAC salt privatized (no longer public AgentId) | DONE |
| 10.3 | Bootstrap fingerprint aligned to registry MachineGuid | DONE |
| 10.8 | Seed request uses discovered PMS type | DONE |

### 2.2 Remaining HIGH

| ID | Issue | Fix |
|----|-------|-----|
| H-1 | IPC pipe ACL grants ReadWrite to ALL authenticated users | Restrict ACL to SYSTEM + LocalService. Add `GetNamedPipeClientProcessId` verification — only accept connections from Helper binary path. |
| H-2 | No IPC message authentication | Add challenge-response handshake: Core generates nonce on pipe connect, Helper signs with shared secret passed by Broker at launch. |
| H-4 | CI/CD signing key written to /tmp | Use process substitution: `openssl dgst -sha256 -sign <(echo "$SIGNING_KEY")`. Remove file-based key handling from release.yml and hotfix.yml. |
| H-5 | Decommission via PowerShell | Replace `Process.Start("powershell.exe")` with direct `Directory.Delete()` + `sc.exe delete` calls. Remove `-ExecutionPolicy Bypass`. |
| H-6 | No IPC rate limiting | Cap at 500 events/second per connection. Drop excess with counter in heartbeat. Cap batch size at 200. |
| 10.1 | No ECDSA key rotation mechanism | Support key array: accept signatures from current + previous key. Ship new key via signed update using old key before revoking. |
| 9.1 | Status name exact-match fragile | Use `LIKE` patterns: `%pick%up%`, `%delivery%`, `%bin%`. Add appsettings override for custom status name mappings. |
| 6.2 | SELECT TOP 50 limit not configurable | Add `MaxDetectionBatchSize` to AgentOptions, default 100. Report in heartbeat when limit is hit. |

### 2.3 Remaining MEDIUM

| ID | Issue | Fix |
|----|-------|-----|
| M-1 | Rx numbers in cloud sync payloads | Hash with per-agent HmacSalt before sync. Cloud correlates via hash. |
| M-2 | SQLite no concurrent write protection | Add `PRAGMA busy_timeout = 5000` at connection init. |
| M-3 | Bootstrap transcript exposes SQL password | Wrap credential section in `Stop-Transcript` / `Start-Transcript`. Delete transcript on success. |
| M-4 | No certificate pinning for cloud HTTPS | Add `ServerCertificateCustomValidationCallback` with public key pin. Include backup pin for rotation. |
| M-5 | HMAC replay window 300s | Reduce to 120s. Add NTP drift detection in heartbeat. |
| M-7 | Decommission single-factor | Require pharmacy-specific confirmation token from cloud before phase 2. |
| M-8 | No log file size limit | Add `fileSizeLimitBytes: 50_000_000` and `rollOnFileSizeLimit: true` to Serilog config. |
| 7.1 | SQL ApplicationName impersonation | Already fixed (changed to "SuavoAgent"). |
| 9.4 | PHI column blocklist gaps | Add pattern matching: any column containing "patient", "ssn", "dob", "phone", "address", "email", "person" in addition to exact list. |
| 10.2 | vm-validate expects wrong service account | Already fixed (Broker now NetworkService). |
| 10.9 | Canary adapter type casing inconsistency | Normalize all adapter type strings to lowercase via `ToLowerInvariant()` at storage boundaries. |

---

## 3. Universal Desktop Observation Architecture

### 3.1 Design Principle: Structure, Not Content

Every observer collects WHAT TYPE of activity is happening, never WHAT CONTENT is being processed.

| Tier | What's captured | Storage | Example |
|------|----------------|---------|---------|
| GREEN | Structural metadata, app names, control types, durations, counts | Plaintext | "User spent 12 min in EXCEL.EXE" |
| YELLOW | Window titles, element names, file names, document names | HMAC-hashed | "Window title hash: a3f2b1..." |
| RED | Cell values, text content, passwords, PII/PHI fields | NEVER captured | Patient name, account number |

### 3.2 New Observers (Helper Process)

#### 3.2.1 ForegroundTracker

**Signal:** Which app has focus, transition sequences, duration per app.

| Field | Tier | Method |
|-------|------|--------|
| Process name | GREEN | `GetForegroundWindow` + `GetWindowThreadProcessId` |
| Window title hash | YELLOW | HMAC with pharmacySalt |
| Focus start/end timestamps | GREEN | Event-driven via `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` |
| Transition edge (from → to) | GREEN | Process name pairs |

**Polling:** Event-driven (hook), not polling. Fallback: 2s poll via `GetForegroundWindow`.

**Output:** `AppSession` events to Core via IPC.

#### 3.2.2 SpreadsheetStructureObserver

**Signal:** What spreadsheets exist, their column structure (not content).

| Field | Tier | Method |
|-------|------|--------|
| File name hash | YELLOW | HMAC of window title (contains filename) |
| Sheet tab count | GREEN | UIA TabItem count under Excel window |
| Column header hashes | YELLOW | HMAC of each header text via UIA HeaderItem |
| Row count | GREEN | UIA DataGrid.RowCount |
| File type (xlsx/csv/etc) | GREEN | Extension from window title pattern |

**Trigger:** When EXCEL.EXE gains foreground focus. One snapshot per focus event, deduplicated by schema fingerprint.

#### 3.2.3 BrowserDomainObserver

**Signal:** What categories of websites are visited (insurance, regulatory, supplier).

| Field | Tier | Method |
|-------|------|--------|
| Domain category | GREEN | Lookup in known-domain dictionary |
| Unknown domain hash | YELLOW | HMAC if domain not in known list |
| Time on domain | GREEN | Focus duration while browser active |

**Known domain dictionary:** Pre-populated with ~200 pharmacy/dental/accounting/restaurant domains categorized as: insurance, regulatory, supplier, clinical, financial, productivity, communication.

**Extraction:** Chrome address bar AutomationId `addressAndUrl`. Extract domain only, immediately hash or categorize, discard raw URL.

#### 3.2.4 PrintEventObserver

**Signal:** When something is printed, from which app, how many pages.

| Field | Tier | Method |
|-------|------|--------|
| Source process name | GREEN | ETW `Microsoft-PrintService/Operational` or WMI `Win32_PrintJob` |
| Printer name hash | YELLOW | HMAC (printer name may reveal printer type: label vs laser) |
| Document name hash | YELLOW | HMAC (may contain Rx number or patient name) |
| Page count | GREEN | From print event payload |
| Timestamp | GREEN | Event timestamp |

#### 3.2.5 StationProfiler (runs once at startup + daily refresh)

**Signal:** What kind of workstation this is.

| Field | Tier | Method |
|-------|------|--------|
| Monitor count + resolutions | GREEN | `Screen.AllScreens` |
| Printer types connected | GREEN | WMI device class (label printer vs laser) |
| Scanner connected | GREEN | WMI device class check |
| RAM bucket (4/8/16/32+GB) | GREEN | `Environment` |
| CPU core bucket | GREEN | `Environment.ProcessorCount` |
| Machine name hash | YELLOW | HMAC |

**Station role inference:** Label printer + scanner = dispensing station. No peripherals + single monitor = office station. Multiple monitors + no scanner = management station.

#### 3.2.6 UserSessionObserver

**Signal:** Shift patterns, login/logout timing.

| Field | Tier | Method |
|-------|------|--------|
| Session start/end time | GREEN | `SystemEvents.SessionSwitch` (Logon/Logoff) |
| Lock/unlock events | GREEN | `SystemEvents.SessionSwitch` (Lock/Unlock) |
| User SID hash | YELLOW | HMAC of `WindowsIdentity.GetCurrent().User` |
| Active duration (keyboard/mouse) | GREEN | `GetLastInputInfo()` delta |

### 3.3 Helper Architecture Change

Current: Helper only runs when PMS is attached. All observers are PMS-scoped.

New: Helper has two observer tiers:
- **System observers** (always running): ForegroundTracker, StationProfiler, UserSessionObserver, PrintEventObserver
- **App observers** (active per-app): UIA tree/interaction/keyboard for PMS, SpreadsheetStructureObserver for Excel, BrowserDomainObserver for browsers

System observers start immediately on Helper launch. App observers activate when their target process gains foreground focus.

### 3.4 Multi-App UIA Observation

Generalize existing `UiaTreeObserver` and `UiaInteractionObserver`:
- Remove hardcoded PMS process filter
- Track foreground app PID from ForegroundTracker
- Walk UIA tree of ANY foreground window (depth-limited to 6 levels for non-PMS apps)
- Apply same GREEN/YELLOW/RED scrubbing rules
- Tag all events with `appId` (process name) for per-app routine detection

**HIPAA gate:** For apps in the known-PMS or known-medical list, apply FULL scrubbing (current behavior). For unknown apps, apply conservative scrubbing (hash ALL Name properties, skip Value/Text entirely).

---

## 4. Categorized Data Storage

### 4.1 Schema (6 core tables)

```
AppSession {
  id, business_id, station_hash, app_id (process name),
  window_title_hash, start_ts, end_ts, focus_ms,
  preceding_app_id, following_app_id
}

ActionEvent {
  id, session_fk, action_type (click/keystroke_category/print/save/error),
  target_element_hash, tree_hash, ts, duration_ms
}

Routine {
  id, business_id, action_sequence_hash, app_sequence,
  frequency, avg_duration_ms, confidence,
  is_automation_candidate, automation_type (writeback/data_entry/report)
}

DocumentProfile {
  id, business_id, doc_hash (file name HMAC), file_type,
  schema_fingerprint (column headers hash), column_count, row_count_bucket,
  last_touched, touch_count, category (inventory/schedule/reconciliation/unknown)
}

TemporalProfile {
  id, business_id, period_type (hourly/daily/weekly),
  period_key, app_distribution_json, action_volume,
  peak_load_score, anomaly_flag
}

BusinessMeta {
  id, business_id, industry (pharmacy/restaurant/dental/accounting/law/unknown),
  detected_apps_json, station_role, software_stack_hash,
  onboard_ts, learning_phase, agent_version
}
```

### 4.2 Data Tiering

| Tier | Content | Storage | Retention | Access |
|------|---------|---------|-----------|--------|
| **Hot** | Active routines, current TemporalProfile, BusinessMeta, top 50 document schemas, last 24h AppSessions | SQLite WAL (in state.db) | 7 days rolling | Every LLM query, every heartbeat |
| **Warm** | Full routine history, 90-day ActionEvents (aggregated hourly), document schema evolution | SQLite on-disk (state.db) | 90 days | Batch analysis each learning cycle |
| **Cold** | Compressed daily summaries, routine version history, seasonal profiles | Append-only files, LZ4 compressed | 2 years | On-demand fleet benchmarking |

**Critical rule:** Raw ActionEvents aggregate or delete after 90 days. Never persist raw events beyond warm tier — storage and privacy constraint.

### 4.3 Industry Adapters (Config, Not Code)

```json
{
  "industry": "pharmacy",
  "primary_apps": ["PioneerPharmacy.exe", "QS1NexGen.exe"],
  "compliance": ["HIPAA"],
  "status_patterns": { "ready": "%pick%up%|%delivery%", "completed": "%complet%" },
  "known_domains": {
    "insurance": ["express-scripts.com", "optumrx.com", "covermymeds.com"],
    "regulatory": ["deadiversion.usdoj.gov", "nabp.pharmacy"],
    "supplier": ["mckesson.com", "cardinalhealth.com"]
  },
  "document_categories": {
    "controlled_substance_log": { "column_patterns": ["drug|medication", "schedule", "count|quantity"] },
    "inventory": { "column_patterns": ["ndc|sku", "quantity|stock", "expir"] }
  },
  "phi_column_patterns": ["patient", "ssn", "dob", "phone", "address", "email", "person"]
}
```

New industry = new JSON file + validation. No code changes to Core, Broker, or Helper.

---

## 5. Claude LLM Integration

### 5.1 Compliance Boundary

```
LOCAL (never leaves machine)          CLOUD (safe for LLM)
─────────────────────────────         ────────────────────
Window titles (raw)                   BusinessMeta (industry, stack)
Document content / cell values        Top 20 Routines (action sequences, no content)
File paths                            TemporalProfile (aggregated patterns)
PII/PHI fields                        Document schemas (column patterns, no values)
Raw ActionEvents with text            Error/retry summaries
User identity                         Efficiency benchmarks vs fleet average
```

### 5.2 LLM Context Assembly

When a business owner asks a question:

1. **Local preprocessing:** Agent assembles a sanitized context packet (~2K tokens):
   - BusinessMeta (industry, software stack, station role)
   - Top 20 routines by frequency (abstract action sequences)
   - TemporalProfile for relevant period
   - Document schema summaries relevant to the question
   - Efficiency scores vs fleet benchmark (anonymized)

2. **Cloud request:** Context packet + user question → Claude API
3. **Response:** Recommendation, automation suggestion, or answer
4. **Guardrails:** Every response includes data sources used, confidence level, "verify before acting" for actionable recommendations. No auto-execution of financial actions for first 30 days.

### 5.3 What the LLM Can Do

| Capability | Example | Data required |
|------------|---------|---------------|
| **Answer questions** | "How much time do we spend on insurance reconciliation?" | TemporalProfile + Routines matching insurance app patterns |
| **Suggest automations** | "You spend 45 min/day copying data from PMS to Excel. SuavoAgent can automate this." | Routine with cross-app clipboard/copy events |
| **Predict demand** | "Mondays have 40% more orders. Pre-position a driver by 8:30am." | TemporalProfile + PMS order volume patterns |
| **Benchmark** | "Your fill time is 23 min avg. Top quartile pharmacies do 15 min." | Local metrics vs anonymized fleet aggregate |
| **Flag anomalies** | "Error rate spiked 3x today vs baseline. Check PMS connection." | TemporalProfile anomaly detection |

---

## 6. Collective Intelligence (Cross-Business Learning)

### 6.1 What Transfers (Anonymized Templates Only)

| Data type | Transfer mechanism | Example |
|-----------|-------------------|---------|
| Routine templates | Frequency + duration distributions, no business identity | "Pharmacy businesses average 47 PMS→Excel transitions/day" |
| Efficiency benchmarks | Percentile bands per industry per routine | "Top quartile fill time = 15 min" |
| App adoption signals | Aggregate counts per industry | "73% of pharmacies use CoverMyMeds" |
| Document schema templates | Column pattern hashes with category labels | "Inventory spreadsheets in dental offices have 8-12 columns matching [pattern]" |
| Automation success rates | Per-routine-type automation outcomes | "SQL writeback succeeds 97% for status updates" |

### 6.2 What Never Transfers

- Any TemporalProfile tied to a specific business
- Document content, file paths, window titles
- Action velocity data (competitive intelligence)
- User/staff identity information
- Business financial data

### 6.3 Mechanism

Federated pattern: each agent computes local efficiency scores and routine fingerprints. Only fingerprints + scores travel to cloud. Cloud aggregates per industry. No individual business is reconstructable from the aggregate.

---

## 7. Fleet Operator Integration

### 7.1 Four Data Channels

| Channel | Desktop signal | Fleet optimization |
|---------|---------------|-------------------|
| **Order volume prediction** | TemporalProfile + PMS transaction counts per hour/day | Pre-position drivers before peak. Reduce idle time 30%. |
| **Pickup readiness timing** | Routine duration for fulfillment workflow (order-entered → bag-staged) | Dispatch driver at minute 18 of a 23-min avg fill. Driver arrives as bag hits shelf. **15-min savings per delivery.** |
| **Business hours reality** | Observed app activity vs posted hours | Stop routing after true last-order time, not posted close. |
| **Capacity signals** | Action velocity + error spikes in TemporalProfile | Delay non-urgent pickups when location is overwhelmed. |

### 7.2 Why This Wins

Nobody else has pickup readiness timing data. Every other delivery platform dispatches on order creation. SuavoAgent dispatches on observed fulfillment progress. The driver arrives when the order is ready, not 23 minutes early.

At 100 deliveries/day across the fleet, 15 min saved per delivery = **25 hours of driver time recovered daily.** That's the business case.

---

## 8. Risk Mitigation

| Risk | Severity | Mitigation |
|------|----------|------------|
| Employee monitoring lawsuits | CRITICAL | Station-level observation only, never track individuals. Mandatory disclosure notice in installer. Legal review per state before expansion. |
| Data volume explosion | HIGH | Aggressive edge aggregation via tiering (Section 4.2). Raw events die at 90 days. Budget: 500MB/station/month warm storage. |
| Industry compliance fragmentation | HIGH | Launch pharmacy-only (done). Add one industry per quarter. Legal review BEFORE writing adapter config. |
| LLM hallucination on ops data | MEDIUM | Every response cites data sources + confidence. No auto-execution of financial actions for 30 days. |
| Business owner pushback | MEDIUM | Lead with specific ROI: "We'll show you where 3 hrs/day goes to manual data entry." Don't lead with "we watch everything." |

---

## 9. Implementation Phases

### Phase 1: Security Hardening (v3.1.0)
- Fix all remaining HIGH/MEDIUM security issues (Section 2.2, 2.3)
- Add IPC authentication + rate limiting
- Add log file size limits
- Add SQLite busy_timeout
- ~15 files changed, ~20 tests added

### Phase 2: Universal Observation (v3.2.0)
- ForegroundTracker + StationProfiler + UserSessionObserver
- Helper architecture split (system vs app observers)
- New IPC event types for system-level observations
- AppSession + TemporalProfile tables in state.db
- ~12 new files, ~30 tests

### Phase 3: App Intelligence (v3.3.0)
- SpreadsheetStructureObserver + BrowserDomainObserver + PrintEventObserver
- Multi-app UIA observation (generalized from PMS-only)
- DocumentProfile + BusinessMeta tables
- Industry adapter config system (JSON-based)
- ~15 new files, ~40 tests

### Phase 4: LLM Integration (v3.4.0)
- Local preprocessing layer (sanitized context assembly)
- Claude API integration with compliance boundary
- Question answering + automation suggestions
- Fleet Operator data channels
- ~10 new files, ~25 tests

### Phase 5: Collective Intelligence (v3.5.0)
- Generalized seed/pull system (beyond PMS correlations)
- Federated efficiency benchmarks
- Cross-industry routine templates
- ~8 new files, ~20 tests

---

## 10. Non-Goals (Explicit)

- **No screenshot capture.** Ever. Screenshots are PHI/PII by definition.
- **No keystroke logging.** Categories only (already enforced).
- **No clipboard content.** Category + length bucket only (if implemented).
- **No individual employee tracking.** Station-level only. User SID is HMAC-hashed.
- **No auto-execution without approval.** All automations require operator approval for first 30 days.
- **No cross-business data sharing.** Only anonymized templates transfer.
