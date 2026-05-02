# PR #35 (UIA3 with UIA2 fallback) — Hold Decision

**Decision date:** 2026-05-01
**Decision:** Hold for Wave 4 pilot evidence
**Decided by:** Joshua Henein
**Context:** Per `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md`
Wave 0 deliverables, this PR is one of 5 open PRs requiring disposition. The other 4
(PR #33 Codex MEDIUM, PR #34 last CRITICAL, PR #36 + PR #37 PIAG-1 stack) merge in
Wave 0; this one holds.

## What PR #35 ships

UIA3 (UI Automation v3) extraction with UIA2 legacy fallback as a feature flag.
Default OFF. Operator opts in via `%ProgramData%\SuavoAgent\uia.json` with
`{"UseUia3": true}`. Auto-fallback: tries UIA3 → smoke-checks via `CheckHealth`
→ if menu-bar empty / 0 items, disposes + retries UIA2.

UIA3 is 5–10× faster than UIA2 but PioneerRx is WinForms with custom controls
that may only surface via UIA2's legacy interop. Verification needs onsite work.

## Why hold

Wave 0's gate is "clean baseline before new work." Merging PR #35 now means:

1. Adds an operator-toggled code path that's never been exercised at a real pharmacy
2. Cannot be validated until Wave 4 (Extraction E2E) when we run actual extraction
   evidence at a pilot pharmacy
3. Adds surface area to Wave 1 / Wave 2 / Wave 3 work without earning any
   reliability proof during those waves

Holding aligns the merge with the wave that proves it works.

## When this PR ships

Wave 4 (Extraction E2E + dashboard depth). Specifically: when live extraction
at the pilot pharmacy emits N≥10 `RxOrderCandidate` rows, switch the operator
flag at the pilot, observe extraction quality + speed for 48 hours, then merge
or close based on evidence.

If Wave 4 evidence shows UIA2 is sufficient at PioneerRx-current-version, close
the PR with that rationale. If UIA3 demonstrably outperforms UIA2 with no
regressions, merge.

## What we do NOT do

- Do not rebase the PR onto every wave's merge commits to keep it green.
  Let the branch go stale; rebase only when ready to evaluate.
- Do not silently merge during a different wave because "it's been open long enough."
  This decision is the explicit gate.
- Do not enable the flag at any pharmacy before Wave 4.

## Cross-references

- PR: https://github.com/MinaH153/SuavoAgent/pull/35
- Meta-roadmap: `docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` §4 (Wave 0 + Wave 4)
- Wave 0 gate definition: meta-roadmap §4 Wave 0
