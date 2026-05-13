# SuavoAgent TODOS

Tracked work items deferred from in-flight design + reviews. Each entry follows the
What/Why/Pros/Cons/Context/Depends-on shape so a contributor in 3 months can pick
up the work without re-deriving the motivation.

When closing a TODO, replace with a one-line note + commit SHA / PR reference.

---

## Diagnostic Mesh (from `docs/architecture/diagnostic-mesh-queen-first.md`)

### TODO-MESH-1 — PhiScrubber false-positive corpus

- **What:** Build a "false-positive" PHI test set — strings that LOOK like PHI but
  aren't (Git SHAs that match SSN shape, NDC codes that match SSN shape, version
  strings like `1.2.3-rc.4-456-78-9012`, IPv6 addresses, GUID fragments).
- **Why:** Mesh PR 4 ships `PhiScrubberTests.cs` with 50+ true-positive patterns
  (strings that ARE PHI and must be redacted). The negative corpus is missing.
  Without false-positive tests, the scrubber over-redacts and damages
  debuggability — a stack frame containing `1.2.3-rc.4-456-78-9012` could be
  scrubbed to `[SSN]` falsely, masking the actual bug.
- **Pros:** Catches scrubber regressions when ruleset bumps tighten patterns
  too aggressively. Closes the missing half of the scrubber test suite.
- **Cons:** Requires real-codebase patterns. Hard to enumerate exhaustively
  without Queen producing real crash signal. ~0.5d once Queen has a corpus.
- **Context:** PR 4 `tests/SuavoAgent.Diagnostics.Tests/PhiScrubberTests.cs`
  currently has only positive coverage. The scrubber lives in
  `src/SuavoAgent.Diagnostics/PhiScrubber.cs`. Add a sibling
  `PhiScrubberFalsePositiveTests.cs` that asserts post-scrub output is identical
  to input for each non-PHI pattern.
- **Depends on:** Queen producing real crash signal (Phase 2 timing post cert ship
  + 48h burn-in).
- **Surfaced:** 2026-05-12 /plan-eng-review of Mesh spec v0.2.

### TODO-MESH-2 — Sentry SDK binary size impact measurement

- **What:** Measure single-file binary growth after Sentry .NET SDK is added to
  all 5 entry points (Core, Broker, Helper, Watchdog, Setup) under the production
  publish flags: `PublishReadyToRun=true` + `PublishSingleFile=true` +
  self-contained + `EnableCompressionInSingleFile=true`.
- **Why:** Current SuavoAgent binaries are ~50MB each (5 × ~50MB = ~250MB total
  install footprint). Sentry .NET SDK pulls in Polly + System.Reactive +
  transitive deps; rough order-of-magnitude estimate is +15–20MB per entry point.
  If that lands, total install footprint grows to ~325MB+. Over a pharmacy DSL
  line (Nadim's first install), that's another minute of download latency on top
  of an already-slow first install.
- **Pros:** Catches bloat before pharmacy onboarding. Sets a binary-size budget
  for Phase 2+ Sentry features (custom integrations, distributed tracing).
- **Cons:** 1h measurement task. No fix path pre-determined if it's bad —
  options would be selective Sentry usage (only top-level entry points), tree-
  shaking via aggressive trimming, or a thin custom HTTP client bypassing the
  SDK.
- **Context:** Add a `verify-binary-size.yml` workflow that compares
  `publish/*/SuavoAgent.*.exe` sizes against a baseline committed at
  `tests/baselines/binary-sizes.txt`. PR fails if any entry point grows by >25%
  vs baseline. Update baseline + open this TODO closed when PR 4 merges.
- **Depends on:** Mesh PR 4 merge.
- **Surfaced:** 2026-05-12 /plan-eng-review of Mesh spec v0.2.

### TODO-MESH-3 — Phase 2 fingerprint algorithm re-calibration

- **What:** After 7 days of real Queen crash signal (post cert + post 48h burn-in),
  re-validate fingerprint algorithm (`fp-v1`) against actual production crashes.
  Compare Sentry-grouped events vs algorithmic fingerprints; flag drift.
- **Why:** Mesh Phase 1 calibrated `fp-v1` against 3 synthetic crash
  reproductions (Bug 22 Win32(5), Bug 23 invariant violation, Bug 24 Avalonia
  InvalidCastException). Reality typically has more variance — different .NET
  runtime versions affect P/Invoke wrapper frames, COM/native interop has weird
  stack-walking edge cases, and ReadyToRun + tiered JIT replacement timing can
  shift the "first in-app non-wrapper frame" identification. This TODO is the
  formal "verify speculative calibration" task that captures the original
  brutal-check 1A/1B refinement that Joshua decided to ship in parallel.
- **Pros:** Catches calibration drift before Nadim's onboarding produces a
  ruleset-v2 bump that has to merge wrongly-grouped Phase 1 fingerprints. Avoids
  the Phase 1 → Phase 2 fingerprint-instability scenario.
- **Cons:** ~0.5d analysis pass. Could be partially automated via Sentry's own
  re-grouping diagnostic UI, but human review of the canonical-fingerprint
  divergence catches things Sentry won't.
- **Context:** Open new GitHub issue tracking the 7d burn-in window. After 7d,
  pull Sentry events grouped by `mesh.fingerprint` tag, compare to actual
  exception classes + Win32 errors + invariant IDs, and either (a) lock
  ruleset-v1 as-is, (b) issue ruleset-v1.1 with tightened/loosened rules, or
  (c) re-architect `fp-v1` algorithm if it's fundamentally broken at production
  scale.
- **Depends on:** Mesh PR 4 merged + cert-signed agent running on Queen for 7+
  consecutive days. Original Joshua-decision was "all 4 PRs in parallel,
  calibrate against Bug 22/23/24 reproductions"; this TODO closes the loop on
  the post-ship verification half.
- **Surfaced:** 2026-05-12 /plan-eng-review of Mesh spec v0.2.

---

## Closed

(none yet)
