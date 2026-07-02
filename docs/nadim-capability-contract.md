# Nadim capability contract — what he expects vs what SuavoAgent does

**Grounded in his actual files (read 2026-07-02):**
- `~/Library/.../Desktop/Suavo/Pioneer Nadim/Nadim automation.m4a` — 81s spoken spec (transcribed)
- `~/Downloads/IMG_5917 3.MOV` — 3-min live demo + narration (audio transcribed, 30 frames read)
- `Pioneer Nadim/PioneerRx Pricing Screenshots/` (15) + `PioneerRx System Footage/` (19) — UI truth
- `~/Downloads/top 500 generics jan 1 to may 30.xlsx` — the REAL input sheet (Better Life Pharmacy)

His real sheet shape: 5-row preamble (title / "Better Life Pharmacy" / address / "Dispensed Item Brand/Generic: Generic, Dea Schedule: No Schedule") then header `# | Drug | Strength | NDC | Total Dispensed | Acquisition Cost`; NDCs 11-digit dashless (Fluticasone `60505082901`, Omeprazole `59651000205`, Atorvastatin `60505258008`).

## The core loop he asked for

| # | Nadim expects | Source | Status | Evidence |
|---|---|---|---|---|
| 1 | Read top-500 generics sheet (drug/strength/NDC per row) | audio + xlsx | ✅ **built (FIXED v3.82.0)** | `ExcelPricingReader` now detects the header past the preamble (was hardcoded row 1 → failed on his real file). Golden test = his exact layout. |
| 2 | Per NDC → Item → Rx Item → Quick Search paste NDC → Pricing tab → Supplier Catalog | audio + video + frames | ✅ built | `Helper/Workflows/PricingWorkflow.cs` (UiaFirst); NDC-verifies the loaded item before reading (`VerifyLoadedNdc`) so a slow search can't price the wrong drug |
| 3 | Cheapest supplier — **true argmin, not "the one on top"** | audio + frames | ✅ built | `PricingGridReader.SelectCheapest` (min Cost, excludes blank/≤0 + discontinued); SqlFirst `ORDER BY cost ASC`. Golden = his Omeprazole 55111-0645-01 grid where blank-cost McKesson rows sort on top → engine returns Real Value Rx $3.16 |
| 4 | Write supplier + cost back onto the sheet | audio + video | ✅ built | `ExcelPricingWriter` → `{stem}-priced-{ts}.xlsx` with `Best Supplier` + `Best Cost` (+ a `Status` col) |
| 5 | Seamless 1→500, no per-item pauses | video | ✅ built | `PricingJobRunner` crash-resumable (SQLite); halts only on failure streaks, all rows resumable |

**Correctness nuance captured from his real data:** "cheapest" is ambiguous — Omeprazole `55111-0645-01` cheapest *cost* = Real Value Rx $3.16, cheapest *cost-per-unit* = McKesson $0.0099 (a 500-count pack flips the winner). Engine optimizes Cost (his stated intent) and excludes blank-cost rows fail-closed. If per-unit becomes the goal, that's a one-line switch — flagged.

## What he showed but the agent does NOT yet do (roadmap)

| Capability | Source | Status |
|---|---|---|
| **Generate** the top-500 itself: Rx Binoculars → Transaction Search (Jan1–today, Generic, Rx-not-OTC, No Schedule) → Top-X report → export | video narration | ⚠️ not automated — Nadim does this manually to produce the input; agent consumes the sheet. Highest-value next build. |
| Name-vs-NDC search safety ("(Do Not Use)" pink rows, combo-drug HCTZ pollution) | frames | ✅ partial — `LooksLikeDoNotUse` guards the pricing path; general name-search UX not in scope |
| Broader daily PMS work (Rx dispensing, queues/ToDo, invoice-import-failed monitoring, Recent Work Items) | system footage | ▫️ out of current scope — his manual work; candidates for future agentic coverage |

## Connectivity & security (top-tier bar) — 2026-07-02
- **Connectivity:** Core↔Helper command-pipe strand FIXED + proven live on the box (v3.81.0): `commandPipeConnected=true`, actuation ready/interactive. See [[project-suavoagent-ipc-strand-fixed]].
- **Security:** token-SID client auth via `ImpersonateNamedPipeClient` at Identification (unforgeable, no privilege grant), ECDSA-signed command envelopes + replay protection, fail-closed OTA + PHI egress gates. Security-reviewed.

## Ships (2026-07-02): MinaH153/SuavoAgent #258, #259 (connectivity), #260 (pricing reader) → releases v3.80.0–v3.82.0.
