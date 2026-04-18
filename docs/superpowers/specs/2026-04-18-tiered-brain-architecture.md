# SuavoAgent Tiered Brain — Architecture Spec

**Status:** Active
**Date:** 2026-04-18
**Owner:** Joshua Henein
**Phase:** Foundation for v4+ (post v3.9.6)

---

## Goal

Transform SuavoAgent from a workflow executor into an engineered autonomous
system that gets smarter over time while reducing LLM dependence asymptotically
toward zero.

## Immovable Constraints

1. **HIPAA super-compliant.** PHI never leaves the machine unless
   (a) encrypted end-to-end to a BAA-covered recipient and (b) scrubbed of
   patient identifiers. Screenshots are PHI. They stay local.
2. **Very low margin of error.** Every probabilistic output passes a
   deterministic verifier before any action. LLM proposes; rules dispose.
3. **Vendor-invisible.** No DLL injection, no binary tampering, no direct
   PMS SQL writes. Operate exclusively at the UIA + keystroke layer —
   indistinguishable from a fast human user.
4. **Runs on typical pharmacy PCs.** i3/i5, 8–16 GB RAM, no GPU, integrated
   graphics, spinning disk possible.

## Non-Goals

- CRD-class remote pixel streaming
- Cloud-only LLM dependency
- Any action PioneerRx would detect in its audit logs
- Python runtime on pharmacy machines

## Architecture — Three-Tier Brain

```
Decision request
    │
    ├──▶ TIER 1: RuleEngine (target ≥ 90% of decisions)
    │      C# deterministic. <1 ms. $0. 100% predictable.
    │      Rules indexed by (skill, state-signature).
    │      If matched → execute. If not → escalate.
    │
    ├──▶ TIER 2: LocalInference (target 8%)
    │      Llama-3.2-1B (classification) + Phi-3.5-mini (generation)
    │      via llama.cpp (C++, no Python). CPU only. ~500 ms.
    │      Output constrained by JSON grammar. Never free-text.
    │      Every output → Verifier → execute OR escalate.
    │
    └──▶ TIER 3: CloudClaude (target ≤ 2%)
           Only novel states. Prompt ≤ 500 input / ≤ 200 output tokens.
           Scrubbed structured JSON, never pixels. BAA-covered.
           Response hash-cached 30 d per pharmacy.
```

### Tier 1 — RuleEngine

- `Rule` = `{ id, when: Predicate, then: ActionSpec, guards: Guard[] }`
- Rules loaded from `~/ProgramData/SuavoAgent/rules/*.yaml` at startup.
- Rules shipped with agent + mined rules downloaded from cloud (signed).
- Evaluator is a simple for-loop with early-exit; indexes by skill for speed.
- First rule catalog: every workflow we've already written (pricing lookup,
  writeback, schema canary checks) restated as rules.

### Tier 2 — LocalInference

- **Runtime:** llama.cpp compiled as DLL, bundled with agent (~2 MB).
- **Models** (Q4_K_M quantized):
  - Llama-3.2-1B (~800 MB) — default workhorse, tool selection, classification.
  - Phi-3.5-mini 3.8B (~2.3 GB) — escalation for harder reasoning, skill gen.
- **Loading:** lazy, per-request; unload after 60 s idle.
- **Output:** GBNF grammar-constrained JSON. No free-text responses. Ever.
- **Verifier:** every LLM proposal checked against:
  1. action ∈ whitelist for current skill
  2. target element actually exists in UIA tree
  3. confidence ≥ threshold (class-specific)
  4. no side-effect outside current skill's declared scope
- **Fail-closed:** verifier failure → escalate to operator, never silently retry.

### Tier 3 — CloudClaude

- Route: `POST /api/agent/reason` — HMAC-signed, same auth as heartbeat.
- Input shape: `{ state: ScrubbedJson, question: string, skillId: string }`.
  PHI scrubbed *before* request via PhiScrubber. JSON schema validated.
- Output: action plan conforming to same grammar as Tier 2.
- **Caching:** `(hashOfScrubbedState + question) → response`, 30-day TTL.
  Cache local (SQLite) — cheaper than round-tripping to cloud.
- **Token budget enforced server-side:** request rejected if >500 input tokens.
- **Call rate limited:** max 50 calls/pharmacy/day. Above that → operator-approval queue.

## Self-Improvement Loop (the IP)

```
Every decision logged: (state, tier, action, outcome, confidence)
                         │
                         ▼
                Pattern miner (weekly)
                         │
     ┌───────────────────┼───────────────────┐
     ▼                   ▼                   ▼
Tier 3 → Tier 2     Tier 2 → Tier 1     Cluster → new rule
"50 identical       "Model consistently   "40 cases, 1 outcome —
 Claude calls →      picks action X for   promote pattern to
 prompt template     state Y → extract    deterministic rule."
 to local model."    decision tree."
```

Rule extraction is gated: candidate rule must pass on historical data and
shadow-execute cleanly for 24 h before promotion.

**The north star:** rule coverage percentage climbs over time. LLM calls
per pharmacy per day fall. System grows *more* deterministic, *less* expensive,
*more* reliable.

## Vision Pipeline (HIPAA-safe)

```
Helper captures screen (BitBlt, user session)
    │ encrypted via DPAPI (LocalMachine) → state.db / BLOB store
    ▼
Local Phi-3.5-vision (4 B Q4, ~2.5 GB) OR Tesseract OCR
    │ structured extraction
    ▼
PhiScrubber (already exists) on extracted text
    │
    ▼
Scrubbed JSON → Tier 1 rules consume → maybe Tier 2 → rarely Tier 3
```

Raw pixels **never** transmitted. Only the agent operator can view a stored
screenshot, and only via end-to-end encrypted channel from the agent machine.
Dashboard sees metadata only (timestamp, skill, hash). Screenshots auto-purge
after 24 h unless flagged for debug.

## Vendor-Invisible Action Layer

Replace `WritebackProcessor` (direct SQL writes) with UIA-driven updates.
This is the single biggest stealth win: zero fingerprint in PMS audit.

Rules for every action:

- UIA clicks only. Zero `SendInput`/`keybd_event` unless UIA lacks the pattern.
- 100–300 ms pacing between actions. No 20 ms bursts.
- Pause if operator keyboard/mouse active within last 2 s.
- Never steal window focus from the active foreground.
- No process-name-targeted queries (`"PioneerRx.exe"` → use control-type + title
  pattern).
- No DLL injection into any third-party process. Ever.
- SQL **reads** to PMS allowed (passive, existing). **Writes forbidden.**

## Safety Layers (low error margin)

1. Pre-condition verify before every action.
2. Post-condition verify after every action.
3. Shadow mode 24 h per new skill per pharmacy.
4. Confidence threshold per action class (destructive = 0.98, read-only = 0.85).
5. Rollback plan mandatory in every skill YAML.
6. Circuit breaker — 3 consecutive failures pauses the skill + notifies operator.
7. Operator override always — any physical input pauses agent immediately.

## Milestones

### Week 1 — RuleEngine foundation
- `SuavoAgent.Reasoning.RuleEngine` class + YAML loader
- 30 hand-written rules covering known workflows
- Shadow-mode executor
- Tests: >50 unit, >10 integration with mock UIA tree
- Deliverable: agent runs rules-only, existing workflows migrated

### Week 2 — LocalInference tier
- llama.cpp DLL bundled in release
- `SuavoAgent.Reasoning.LocalInference` service
- Llama-3.2-1B model downloaded on first run (background)
- GBNF grammar schemas for each action class
- Verifier wrapper around every LLM output
- Deliverable: agent escalates from Tier 1 → Tier 2 seamlessly

### Week 3 — Vision (local only)
- `ScreenCaptureService` in Helper (BitBlt, DPAPI-encrypted storage)
- Phi-3.5-vision bundled + extraction pipeline
- Tesseract fallback for pure text
- PhiScrubber wired at extraction boundary
- Deliverable: any screen understandable via Tier 2 without cloud

### Week 4 — CloudClaude + pattern miner + SQL writeback deprecation
- `/api/agent/reason` endpoint (HMAC, scrubbed input, cached)
- Pattern miner cron — logs decisions, proposes rules
- `WritebackProcessor` replaced with UIA-driven flow
- Deliverable: full three-tier brain + vendor-invisible action layer

## Cost Model

Per pharmacy per month, steady state:

| Item | Cost |
|------|------|
| Tier 1 (rules) | $0 |
| Tier 2 (local LLM) | $0 (electricity) |
| Tier 3 (Claude) — Year 1 | ~$0.60–2.40 |
| Tier 3 (Claude) — Year 2 | ~$0.20–0.50 |
| Model storage (their disk) | $0 |
| Our cloud storage (logs, cache) | ~$0.05 |

At 1000 pharmacies Year 1: **~$1500–2400/month total Claude spend.**
At 1000 pharmacies Year 2: **~$300–600/month** (pattern miner has migrated
decisions left).

Open-weight models have no licensing fees. Llama 3.2 (Llama-3 Community License)
and Phi-3.5 (MIT) are both compatible with our commercial use.

## Open Questions

1. Local vision model choice — Phi-3.5-vision vs smaller alternatives?
   (Bench week 3 on 100 real PioneerRx screenshots; pick best accuracy/speed.)
2. First-run model download path — include in installer vs background fetch?
   (Decision: background fetch, agent runs rules-only until ready.)
3. Rule YAML format — authored by hand, by LLM, or hybrid?
   (Hybrid: hand-seed the catalog, LLM proposes additions, human approves.)

## Supersedes

- Direct SQL writeback workflow (deprecated in Week 4)
- LLM-first execution model (never built — this spec replaces it pre-emptively)

## References

- `src/SuavoAgent.Core/Learning/PhiScrubber.cs` — reused at extraction boundary
- Spec D (Collective Intelligence) — dual-gate trust for cross-pharmacy rule transfer
- Schema Canary — fails closed if UI changes, protects rule stability
