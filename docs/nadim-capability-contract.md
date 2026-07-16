# Nadim capability contract — what he expects vs what SuavoAgent does

**Grounded in his actual files (re-verified 2026-07-13):**
- `~/Library/.../Desktop/Suavo/Pioneer Nadim/Nadim automation.m4a` — 81s spoken spec (transcribed)
- `Pioneer Nadim/IMG_5917.MOV` / `~/Desktop/IMG_5917 2.MOV` — the same byte-identical 3-min live demo
- `~/Downloads/New Recording 39.m4a` — preferred-NDC-by-insurance request
- `Pioneer Nadim/PioneerRx Pricing Screenshots/` (15) + `PioneerRx System Footage/` (19) — UI truth
- `~/Downloads/top 500 generics jan 1 to may 30.xlsx` — the REAL input sheet (Better Life Pharmacy)

His real sheet shape is a paginated report export: the NDC/data header is row 8, NDC is column F,
`Total Dispensed` is on row 8, and the line-broken stacked `Acquisition Cost` header is Q6. The actual
file contains 500 valid normalized 11-digit NDC rows plus 17 repeated page-header rows. Its
`Acquisition Cost` is aggregate report spend, not a per-unit baseline.

## The core loop he asked for

| # | Nadim expects | Source | Status | Evidence |
|---|---|---|---|---|
| 1 | Read top-500 generics sheet (drug/strength/NDC per row) | audio + xlsx | ✅ local admission complete | The native Google-export normalizer creates a private, values-only, one-sheet execution snapshot, skips only exact repeated headers, and admits all 500 canonical NDC rows. The supplied source file stays byte-for-byte unchanged. |
| 2 | Per NDC → Item → Rx Item → Quick Search paste NDC → Pricing tab → Supplier Catalog | audio + video + frames | ◐ local, field-open | `PricingWorkflow` exact-NDC verification exists; current workstation selector/tree behavior and zero-wrong-item execution remain unproved. |
| 3 | Cheapest supplier — **true argmin, not "the one on top"** | audio + frames | ⚠ rule decision open | Local engine computes argmin over `Cost Per Unit` and excludes unusable rows. Nadim's footage points to pack `Cost`; pharmacist must select the rule before field acceptance. |
| 4 | Write supplier + cost back onto the sheet | audio + video | ✅ local | `ExcelPricingWriter` creates a sibling workbook with supplier, cost, and explicit status; it does not overwrite the source. |
| 5 | Seamless 1→500, no per-item pauses | video | ◐ local, field-open | Durable/resumable runner exists; a supervised 10→100→500 live run and reconciliation are still required. |

**Correctness gate:** `Cost` and `Cost Per Unit` can select different suppliers. The current engine
accepts only a dedicated `Cost Per Unit` field and refuses pack `Cost` rather than silently relabeling
it. The footage demonstrates pack `Cost`; Nadim/PIC must choose the intended rule before field acceptance.

The report's aggregate `Acquisition Cost` must never be copied into `BaselineCostPerUnit`. Savings stays
disabled until the live source and units are proved. The desired deliverable is one continuous table,
not the original 18-page report with new columns appended.

## Pharmacist authority and safe offboarding — local implementation

Feature A no longer trusts a configuration toggle as permission. The workstation stages an exact,
content-addressed policy proposal; the cloud records it append-only and only an active, MFA-authenticated
pharmacist-in-charge for the same pharmacy may approve it. The resulting command is signed; tenant-, device-,
and policy-bound; replay-protected; and installed into the local append-only ledger. The cockpit distinguishes
signed-but-not-delivered, confirmed active, revocation-pending, failed delivery, and confirmed revoked from
the exact native command acknowledgement; it does not claim authority changed merely because the cloud
created a row.

Revocation is prioritized ahead of ordinary pricing work and cancels older unstarted pricing commands. A
signed pre-grant revocation creates a durable local tombstone, so a delayed install response cannot resurrect
authority after restart. Paused/revoked/superseded lifecycle transitions preserve the mandatory revocation
path. The native runner additionally requires a fresh authenticated cloud-authority lease (15-minute offline
grace), persists a clock high-water mark, latches an exact inactive-binding response, and requires the exact
immutable, signed, unrevoked PIC grant before and after every SQL/UIA row and workbook publication boundary.
A durably applied signed revocation cancels the active detached pricing run before its command ACK. Result
publication and every recovery/outbox retry repeat the same grant-plus-lease check; evidence blocked by a
revocation is retained with a structural quarantine reason and is never sent. This bounds a disconnected
machine instead of letting a year-long PIC grant or a previously staged retry outlive pharmacist authority.

The immutable cloud receipt must use the same execution modality as the exact signed grant. SQL, UIA, and
VisionFirst are separate authorities; VisionFirst remains `vision`, and manual cannot be uploaded as an
authorized pricing result. Before any result bytes enter the network, the native ledger records the exact
job/payload/approval/grant send attempt and holds the shared authority gate through signed-response verification
and local receipt commit. If the cloud committed but the response was lost, the agent can recover only that
exact minimum-necessary receipt through a credential-hash-bound route. Recovery is capped at three attempts
and ends in visible manual reconciliation rather than resending altered evidence after revocation or expiry.
Overlapping signed revocations are reference-counted before the shared authority gate; the deterministic
stale-revoke -> send -> valid-revoke race proves one caller cannot clear another caller's send barrier. On
upgrade, a completed legacy `manual` result intent is append-only quarantined and terminalized without
relabeling, startup failure, or unauthorized upload.

These controls are local code and test evidence only. The cloud migrations are not applied, the release is
not deployed, and the signed bundle has not passed the real Windows/PioneerRx security-offboarding drill.
The combined native closeout passed 162 tests; the full Core suite passed 3,851 with two known encrypted-DB
skips, the full Helper suite passed 927 with seven platform skips, and both builds completed with zero
warnings or errors. The companion web worktree passed 59 focused files and 424 tests; a zero-state local
PostgreSQL reset applied migrations through `04892`, followed by 133 of 133 passing pgTAP checks. Approved
staging application and concurrency evidence remain release gates.

## Preferred-NDC-by-insurance request

The strict read-only offline path is locally implemented: private workbook snapshot, exact schema,
canonical/unique NDCs, affirmative eligibility, common amount basis, separately named and fresh cost /
reimbursement evidence, bounded arithmetic, fail-closed recommendation, and atomic non-overwriting
report publication. Its calculation is an expected gross-margin proxy (`reimbursement - acquisition`),
not net profit. No live reimbursement source, runtime registration, signed command, durable evidence,
dashboard trace, PioneerRx writeback, rollback, or auto-block exists. Report-only/manual entry remains
the correct first field mode; clinical interchangeability and any mutation remain pharmacist-controlled.

## Generating the top-500 (his last manual step) — signed chain built, field proof open

| Capability | Source | Status | Evidence |
|---|---|---|---|
| **Generate & price** the top generic NDC list: approved local PioneerRx aggregate source → ranked Top-X → local pricing input → existing verified pricing executor | video narration + his sheet's filter header | ◐ signed generate→price→result chain built; live source proof open | Versioned pack `pharmacy_rx_generate_v2` stays under signed `find_and_run_pricing_job`, PIC approval binding, dormant/observation authority, replay, outbox, and audit controls. `TopDispensedWorklistBuilder` resolves bounded metadata, runs parameterized aggregate SQL, canonicalizes NDCs, atomically publishes a protected local workbook, re-reads it, then hands it to the existing executor; no patient/Rx rows or source workbook upload. Unresolved statuses, schema, Rx/OTC, or schedule mappings fail closed. Production remains NO-GO until Nadim's exact PioneerRx columns/values/statuses are ground-truthed and a clean-install synthetic run returns a verified result. No verified Rx Binoculars UI/export selector path exists. |

## Other capabilities he showed (out of current scope)
| Name-vs-NDC search safety ("(Do Not Use)" pink rows, combo-drug HCTZ pollution) | frames | ✅ partial — `LooksLikeDoNotUse` guards the pricing path; general name-search UX not in scope |
| Broader daily PMS work (Rx dispensing, queues/ToDo, invoice-import-failed monitoring, Recent Work Items) | system footage | ▫️ out of current scope — his manual work; candidates for future agentic coverage |

Historical PR/release records do not replace a fresh signed-install and live PioneerRx field proof.

## Connectivity & security (top-tier bar) — 2026-07-02
- **Connectivity:** Core↔Helper command-pipe strand FIXED + proven live on the box (v3.81.0): `commandPipeConnected=true`, actuation ready/interactive. See [[project-suavoagent-ipc-strand-fixed]].
- **Security:** token-SID client auth via `ImpersonateNamedPipeClient` at Identification (unforgeable, no privilege grant), ECDSA-signed command envelopes + replay protection, fail-closed OTA + PHI egress gates. Security-reviewed.
