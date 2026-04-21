# SuavoAgent Self-Healing — Architecture Docs

This directory is the **single source of truth** for the self-healing architecture.
Every code artifact (agent-side verbs, cloud-side dispatcher, policy engine, audit
chain, fleet portal UI) must cite a doc in this directory as its specification.

## Read order for new engineers

1. `invariants.md` — what the system MUST guarantee. Read first, reference always.
2. `audit-schema.md` — the event substrate everything else rides on.
3. `key-custody.md` — the trust boundary between cloud and agent.
4. `action-grammar-v1.md` — how the agent executes remediation safely.

## Strategic context (read before touching any of these)

The strategy and positioning for this work lives in the memory system at
`~/.claude/projects/-Users-joshuahenein/memory/`:

- `suavoagent-mission-loop-architecture.md` — the "Claude Ultra-Review on cloud"
  coordination pattern that rides on top of the substrate specified here.
- `suavoagent-self-healing-9-phase-plan.md` — the execution roadmap.
- `suavoagent-self-healing-research-findings.md` — wisdom extracted from the
  four-agent competitive survey (OpenHands, Temporal, Cedar, MCP security arxiv,
  CrowdStrike 2024 RCA, Codex adversarial review).
- `suavoagent-self-healing-moat-positioning.md` — the investor / auditor / acquirer
  narrative. Why this architecture wins.

## Non-negotiable first principles (extracted from all four docs below)

1. **Invariants before dashboards.** Every observability surface exists to serve a
   specific invariant in `invariants.md`. If a dashboard doesn't map back to an
   invariant, it's chrome.
2. **PHI never enters a model prompt without schema-validated redaction.** No
   exceptions. Redaction coverage is itself audited.
3. **Every verb is signed, versioned, BAA-flagged, and canary-rolled.** If you
   ship a verb to production without passing all four gates, you've written the
   next CrowdStrike Channel File 291.
4. **Audit trail is a legal artifact, not a debug log.** Tamper-evident
   hash-chain + S3 Object Lock. 6-year retention. Per-pharmacy isolated.
5. **Humans approve every self-improvement proposal forever.** No autonomous
   rule drift. No exceptions.
6. **Autonomy ladder default = 0% on every new pharmacy.** Earn each notch.

## File status (as of 2026-04-22)

| Doc | Status | Locked date |
|---|---|---|
| `invariants.md` | v0.1 draft | 2026-04-22 |
| `audit-schema.md` | v0.1 draft | 2026-04-22 |
| `key-custody.md` | v0.1 draft | 2026-04-22 |
| `action-grammar-v1.md` | v0.1 draft | 2026-04-22 |

All four are **v0.1 drafts**. They lock to v1.0 after Saturday's Nadim pilot
post-mortem and a Codex + Joshua sign-off review. Breaking changes post-v1.0
require a PR + approval from both Joshua and a designated Security Officer.
