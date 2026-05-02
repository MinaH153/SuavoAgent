# Wave 0: Baseline Reset — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Land the meta-roadmap's audit infrastructure (two new `wave.*` event types + `roadmap_gates` Postgres table) and clean the open-PR baseline so subsequent waves have a verifiable platform to build on.

**Architecture:** Two repos coordinated in lockstep. SuavoAgent (.NET 8 / xUnit) adds `WaveGateTrippedPayload` + `WaveGateFailedPayload` sealed records to `SuavoAgent.Contracts.Models` (matching the existing `PatientDetailsPayload` pattern), mirroring the canonical event shapes declared in `docs/self-healing/event-registry.md`. Suavo (Next.js / vitest / Supabase) adds the `roadmap_gates` append-only table migration plus Zod schemas mirroring the C# records. Open-PR merge coordination is handled operationally by Joshua outside this plan.

**Tech Stack:** C# .NET 8, xUnit 2.9 + sealed records, PostgreSQL with append-only triggers, Supabase migrations, TypeScript, Zod, vitest 4.

---

## Source spec

`docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md` §4 Wave 0 + §8 (audit table + new event types).

---

## Scope

This plan implements only Wave 0's net-new code:

1. `wave.gate_tripped` + `wave.gate_failed` event types in canonical event registry
2. C# `WaveGateTrippedPayload` + `WaveGateFailedPayload` records + tests
3. `roadmap_gates` Postgres migration (committed but NOT applied — application is a Wave 1 deliverable)
4. TS Zod schemas mirroring the C# records + vitest tests
5. PR #35 (UIA3 fallback) hold-decision rationale doc

**Out of scope** (Joshua handles operationally — not implementation tasks):
- Merging PR #33, PR #34
- Synchronizing PR #36 + PR #37 + paired Suavo PR `SuavoLLC/MKM#192` for unit-merge
- Pushing the `docs/v4-roadmap-2026-05-01` branch + opening the meta-roadmap PR

These remain on the Wave 0 gate but are "click merge if green CI" decisions.

---

## Branch strategy

- **SuavoAgent**: branch off `main` as `feat/wave-0-suavoagent-events`
- **Suavo**: branch off `main` as `feat/wave-0-roadmap-gates`
- Each branch produces an independent PR
- Both PRs together close the implementation portion of Wave 0

---

## File structure

### SuavoAgent (`~/Code/SuavoAgent`)

**Modify:**
- `docs/self-healing/event-registry.md` — append `wave.*` section before "Adding a new event type"

**Create:**
- `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs`
- `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs`
- `tests/SuavoAgent.Contracts.Tests/Models/WaveGateTrippedPayloadTests.cs`
- `tests/SuavoAgent.Contracts.Tests/Models/WaveGateFailedPayloadTests.cs`
- `docs/hardening/2026-05-01-pr-35-uia3-fallback-hold-decision.md` (mkdir `docs/hardening/` if it doesn't exist on `main`)

### Suavo (`~/Code/Suavo`)

**Create:**
- `supabase/migrations/20260501020000_roadmap_gates.sql`
- `src/lib/wave-event-payloads.ts`
- `src/lib/__tests__/wave-event-payloads.test.ts`

---

## Tasks

### Task 1: Register `wave.gate_tripped` + `wave.gate_failed` in event-registry.md

**Repo:** SuavoAgent
**Files:**
- Modify: `docs/self-healing/event-registry.md`

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/SuavoAgent
git checkout main
git pull --ff-only origin main
git checkout -b feat/wave-0-suavoagent-events
```

- [ ] **Step 2: Append the `wave.*` section**

Open `docs/self-healing/event-registry.md`. Locate the line `## Adding a new event type` (near the bottom, before the change log). Insert the following section directly **above** that heading:

```markdown
---

## `wave.*` — Roadmap meta-gate events (added 2026-05-01)

Events emitted to track wave gate progress for the meta-roadmap defined in
`docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md`. Persisted
to the `roadmap_gates` operational table (Suavo cloud — Wave 1 deliverable to
apply).

### `wave.gate_tripped`
- Category: `governance`
- Severity: `info`
- Actor: `system` | `operator`
- Payload: `{wave_id: string, evidence_summary: string, certified_by: string, evidence_event_ids: string[], tripped_at: timestamptz}`
- Notes: `wave_id` is `"W0"` | `"W1"` | ... | `"MASTER"`. `certified_by` is `"ci"` | `"pilot:<pharmacy_id_hash>"` | `"joshua"`. `evidence_event_ids` are pointers to supporting `audit_events` rows.

### `wave.gate_failed`
- Category: `governance`
- Severity: `warn`
- Actor: `system` | `operator`
- Payload: `{wave_id: string, attempt_number: number, failure_summary: string, root_cause_class: string, remediation_plan_committed_at: string|null, next_attempt_estimated: string}`
- Notes: `root_cause_class` enum: `"code-bug"` | `"scope-error"` | `"blocker-external"` | `"architectural-error"` | `"pilot-crash-midsoak"`. `next_attempt_estimated` enum: `"unknown"` | `"when-blocker-clears"` | `"after-fix"`.

```

- [ ] **Step 3: Update change log entry at bottom of file**

Append a new line under the existing change log:

```markdown
- **2026-05-01 v0.2** — Added `wave.*` namespace (`wave.gate_tripped`, `wave.gate_failed`) for meta-roadmap gate tracking.
```

- [ ] **Step 4: Commit**

```bash
git add docs/self-healing/event-registry.md
git commit -m "docs(events): register wave.gate_tripped + wave.gate_failed for meta-roadmap"
```

---

### Task 2: Add `WaveGateTrippedPayload` C# record + test

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Models/WaveGateTrippedPayloadTests.cs`

Pattern: match `src/SuavoAgent.Contracts/Models/PatientDetailsPayload.cs` — sealed record, XML doc comments, namespace `SuavoAgent.Contracts.Models`.

- [ ] **Step 1: Write the failing test**

Create `tests/SuavoAgent.Contracts.Tests/Models/WaveGateTrippedPayloadTests.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class WaveGateTrippedPayloadTests
{
    [Fact]
    public void Construct_AssignsAllFields()
    {
        var trippedAt = DateTimeOffset.UtcNow;
        var evidence = new[] { "audit-1", "audit-2" };

        var payload = new WaveGateTrippedPayload(
            WaveId: "W0",
            EvidenceSummary: "5 open PRs resolved",
            CertifiedBy: "joshua",
            EvidenceEventIds: evidence,
            TrippedAt: trippedAt);

        Assert.Equal("W0", payload.WaveId);
        Assert.Equal("5 open PRs resolved", payload.EvidenceSummary);
        Assert.Equal("joshua", payload.CertifiedBy);
        Assert.Equal(evidence, payload.EvidenceEventIds);
        Assert.Equal(trippedAt, payload.TrippedAt);
    }

    [Theory]
    [InlineData("ci")]
    [InlineData("joshua")]
    [InlineData("pilot:abc123")]
    public void CertifiedBy_AcceptsCanonicalShapes(string certifier)
    {
        var payload = new WaveGateTrippedPayload(
            WaveId: "W3",
            EvidenceSummary: "7-day soak passed",
            CertifiedBy: certifier,
            EvidenceEventIds: Array.Empty<string>(),
            TrippedAt: DateTimeOffset.UtcNow);

        Assert.Equal(certifier, payload.CertifiedBy);
    }

    [Fact]
    public void RecordEquality_StructuralOnScalarFields()
    {
        var t = DateTimeOffset.UtcNow;
        var a = new WaveGateTrippedPayload("W1", "sum", "ci", new[] { "x" }, t);
        var b = new WaveGateTrippedPayload("W1", "sum", "ci", new[] { "x" }, t);
        // Records compare by value for scalar fields. EvidenceEventIds is a
        // reference-typed list, so we test scalar equality here and rely on
        // the audit chain for evidence-id semantics.
        Assert.Equal(a.WaveId, b.WaveId);
        Assert.Equal(a.EvidenceSummary, b.EvidenceSummary);
        Assert.Equal(a.CertifiedBy, b.CertifiedBy);
        Assert.Equal(a.TrippedAt, b.TrippedAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~WaveGateTrippedPayloadTests" 2>&1 | tail -15
```

Expected: build fails with `error CS0246: The type or namespace name 'WaveGateTrippedPayload' could not be found`.

- [ ] **Step 3: Implement `WaveGateTrippedPayload`**

Create `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload emitted when a meta-roadmap wave gate trips. Mirrors the
/// canonical shape declared in <c>docs/self-healing/event-registry.md</c>
/// under the <c>wave.gate_tripped</c> entry. Consumed by the cloud's
/// <c>roadmap_gates</c> table writer plus the audit chain ingest.
///
/// Field semantics (see meta-roadmap §8):
///   <list type="bullet">
///     <item><c>WaveId</c> — "W0", "W1", ..., "MASTER".</item>
///     <item><c>EvidenceSummary</c> — one-paragraph human-readable rationale.</item>
///     <item><c>CertifiedBy</c> — "ci" | "pilot:&lt;pharmacy_id_hash&gt;" | "joshua".</item>
///     <item><c>EvidenceEventIds</c> — pointers to supporting <c>audit_events</c> rows.</item>
///     <item><c>TrippedAt</c> — UTC instant the gate is considered tripped.</item>
///   </list>
/// </summary>
public sealed record WaveGateTrippedPayload(
    string WaveId,
    string EvidenceSummary,
    string CertifiedBy,
    IReadOnlyList<string> EvidenceEventIds,
    DateTimeOffset TrippedAt);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~WaveGateTrippedPayloadTests" 2>&1 | tail -8
```

Expected: 5 individual test cases pass (1 Fact + 3 Theory cases + 1 Fact).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs \
        tests/SuavoAgent.Contracts.Tests/Models/WaveGateTrippedPayloadTests.cs
git commit -m "feat(contracts): add WaveGateTrippedPayload record + tests"
```

---

### Task 3: Add `WaveGateFailedPayload` C# record + test

**Repo:** SuavoAgent
**Files:**
- Create: `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs`
- Test: `tests/SuavoAgent.Contracts.Tests/Models/WaveGateFailedPayloadTests.cs`

- [ ] **Step 1: Write the failing test**

Create `tests/SuavoAgent.Contracts.Tests/Models/WaveGateFailedPayloadTests.cs`:

```csharp
using System;
using SuavoAgent.Contracts.Models;
using Xunit;

namespace SuavoAgent.Contracts.Tests.Models;

public class WaveGateFailedPayloadTests
{
    [Fact]
    public void Construct_AssignsAllFields()
    {
        var committedAt = DateTimeOffset.UtcNow;

        var payload = new WaveGateFailedPayload(
            WaveId: "W3",
            AttemptNumber: 2,
            FailureSummary: "Helper crashed at day 5 of 7-day soak",
            RootCauseClass: "pilot-crash-midsoak",
            RemediationPlanCommittedAt: committedAt,
            NextAttemptEstimated: "after-fix");

        Assert.Equal("W3", payload.WaveId);
        Assert.Equal(2, payload.AttemptNumber);
        Assert.Equal("Helper crashed at day 5 of 7-day soak", payload.FailureSummary);
        Assert.Equal("pilot-crash-midsoak", payload.RootCauseClass);
        Assert.Equal(committedAt, payload.RemediationPlanCommittedAt);
        Assert.Equal("after-fix", payload.NextAttemptEstimated);
    }

    [Theory]
    [InlineData("code-bug")]
    [InlineData("scope-error")]
    [InlineData("blocker-external")]
    [InlineData("architectural-error")]
    [InlineData("pilot-crash-midsoak")]
    public void RootCauseClass_AcceptsAllCanonicalValues(string rootCause)
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "test",
            RootCauseClass: rootCause,
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: "unknown");

        Assert.Equal(rootCause, payload.RootCauseClass);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("when-blocker-clears")]
    [InlineData("after-fix")]
    public void NextAttemptEstimated_AcceptsAllCanonicalValues(string nextAttempt)
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "test",
            RootCauseClass: "code-bug",
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: nextAttempt);

        Assert.Equal(nextAttempt, payload.NextAttemptEstimated);
    }

    [Fact]
    public void RemediationPlanCommittedAt_NullableForUnplannedFailures()
    {
        var payload = new WaveGateFailedPayload(
            WaveId: "W1",
            AttemptNumber: 1,
            FailureSummary: "blocked on Yubikey delivery",
            RootCauseClass: "blocker-external",
            RemediationPlanCommittedAt: null,
            NextAttemptEstimated: "when-blocker-clears");

        Assert.Null(payload.RemediationPlanCommittedAt);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~WaveGateFailedPayloadTests" 2>&1 | tail -15
```

Expected: build fails with `error CS0246: The type or namespace name 'WaveGateFailedPayload' could not be found`.

- [ ] **Step 3: Implement `WaveGateFailedPayload`**

Create `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs`:

```csharp
using System;

namespace SuavoAgent.Contracts.Models;

/// <summary>
/// Audit payload emitted when a meta-roadmap wave gate fails to trip on an
/// attempt. Mirrors <c>wave.gate_failed</c> in
/// <c>docs/self-healing/event-registry.md</c>. Persisted to the
/// <c>roadmap_gates</c> table with <c>status='reset'</c> when this is a
/// counter-resetting failure.
///
/// Field semantics (see meta-roadmap §7 wave-fail recovery):
///   <list type="bullet">
///     <item><c>WaveId</c> — "W0", "W1", ..., "MASTER".</item>
///     <item><c>AttemptNumber</c> — 1, 2, 3, ... per wave.</item>
///     <item><c>FailureSummary</c> — one-paragraph diagnosis.</item>
///     <item><c>RootCauseClass</c> — one of:
///       "code-bug" | "scope-error" | "blocker-external" |
///       "architectural-error" | "pilot-crash-midsoak".</item>
///     <item><c>RemediationPlanCommittedAt</c> — null if no plan yet (e.g.,
///       blocked on external dependency).</item>
///     <item><c>NextAttemptEstimated</c> — "unknown" |
///       "when-blocker-clears" | "after-fix".</item>
///   </list>
/// </summary>
public sealed record WaveGateFailedPayload(
    string WaveId,
    int AttemptNumber,
    string FailureSummary,
    string RootCauseClass,
    DateTimeOffset? RemediationPlanCommittedAt,
    string NextAttemptEstimated);
```

- [ ] **Step 4: Run test to verify it passes**

```bash
dotnet test tests/SuavoAgent.Contracts.Tests/ --filter "FullyQualifiedName~WaveGateFailedPayloadTests" 2>&1 | tail -8
```

Expected: 10 individual test cases pass (1 Fact + 5 Theory cases + 3 Theory cases + 1 Fact).

- [ ] **Step 5: Commit**

```bash
git add src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs \
        tests/SuavoAgent.Contracts.Tests/Models/WaveGateFailedPayloadTests.cs
git commit -m "feat(contracts): add WaveGateFailedPayload record + tests"
```

---

### Task 4: Document PR #35 (UIA3 fallback) hold decision

**Repo:** SuavoAgent
**Files:**
- Create: `docs/hardening/2026-05-01-pr-35-uia3-fallback-hold-decision.md`

- [ ] **Step 1: Create the directory if needed**

```bash
mkdir -p docs/hardening
```

- [ ] **Step 2: Write the decision doc**

Create `docs/hardening/2026-05-01-pr-35-uia3-fallback-hold-decision.md`:

```markdown
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
```

- [ ] **Step 3: Commit**

```bash
git add docs/hardening/2026-05-01-pr-35-uia3-fallback-hold-decision.md
git commit -m "docs(hardening): document PR #35 UIA3 fallback hold decision for Wave 0"
```

---

### Task 5: Push SuavoAgent branch + open PR (operational — may require Joshua's gh-auth)

**Repo:** SuavoAgent
**Files:** none new

- [ ] **Step 1: Run all SuavoAgent tests one last time before pushing**

```bash
cd /Users/joshuahenein/Code/SuavoAgent
dotnet test tests/SuavoAgent.Contracts.Tests/ 2>&1 | tail -8
```

Expected: all Contracts tests pass (existing + new ~15 cases).

- [ ] **Step 2: Push branch**

```bash
git push -u origin feat/wave-0-suavoagent-events 2>&1 | tail -5
```

If push fails (per memory `feedback-gh-multi-account-push.md` — gh-auth is SuavoLLC org only): Joshua handles the auth + push manually. The branch is local-only until then. Proceed to Task 6 in Suavo regardless.

- [ ] **Step 3: Open PR if push succeeded**

```bash
gh pr create --repo MinaH153/SuavoAgent \
  --title "Wave 0 (SuavoAgent side): wave.* event registry + payloads + PR #35 hold doc" \
  --body "$(cat <<'EOF'
Closes Wave 0 SuavoAgent-side deliverables per
`docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md`.

## Changes
- `docs/self-healing/event-registry.md`: register `wave.gate_tripped` + `wave.gate_failed`
- `src/SuavoAgent.Contracts/Models/WaveGateTrippedPayload.cs` + tests
- `src/SuavoAgent.Contracts/Models/WaveGateFailedPayload.cs` + tests
- `docs/hardening/2026-05-01-pr-35-uia3-fallback-hold-decision.md` — hold rationale

## Wave 0 status after this PR
- [x] Meta-roadmap committed (separate PR `docs/v4-roadmap-2026-05-01`)
- [x] Event types registered + C# payloads
- [x] PR #35 hold decision documented
- [ ] PR #33 + PR #34 merged (Joshua operational)
- [ ] PR #36 + PR #37 + Suavo PR #192 synchronized merge (Joshua operational)
- [ ] roadmap_gates migration committed (separate Suavo PR `feat/wave-0-roadmap-gates`)

## Test plan
- [x] `dotnet test` Contracts.Tests suite green
- [ ] Joshua reviews event-registry.md entries for canonical alignment

EOF
)"
```

If gh PR create fails: paste the body via web UI on the open branch.

---

### Task 6: Create `roadmap_gates` Postgres migration

**Repo:** Suavo
**Files:**
- Create: `supabase/migrations/20260501020000_roadmap_gates.sql`

This migration is committed in Wave 0 but **NOT** applied to prod until Wave 1 per meta-roadmap §4.

- [ ] **Step 1: Branch off main**

```bash
cd /Users/joshuahenein/Code/Suavo
git checkout main
git pull --ff-only origin main
git checkout -b feat/wave-0-roadmap-gates
```

- [ ] **Step 2: Write the migration SQL**

Create `supabase/migrations/20260501020000_roadmap_gates.sql`:

```sql
-- Wave 0 deliverable: roadmap_gates operational table.
-- Append-only; mirrors audit_events pattern from
-- docs/self-healing/audit-schema.md. Records meta-roadmap wave gate
-- decisions (trip + fail). Per the v4 meta-roadmap §8 in SuavoAgent.

create table if not exists public.roadmap_gates (
  id uuid primary key default gen_random_uuid(),
  wave_id text not null,
  status text not null check (status in ('open', 'tripped', 'reset')),
  attempt_number integer not null default 1,
  evidence_summary text,
  certified_by text,
  pilot_pharmacy_id_hash text,
  failure_summary text,
  root_cause_class text check (
    root_cause_class is null
    or root_cause_class in (
      'code-bug',
      'scope-error',
      'blocker-external',
      'architectural-error',
      'pilot-crash-midsoak'
    )
  ),
  next_attempt_estimated text check (
    next_attempt_estimated is null
    or next_attempt_estimated in ('unknown', 'when-blocker-clears', 'after-fix')
  ),
  remediation_plan_committed_at timestamptz,
  recorded_at timestamptz not null default now(),
  recorded_by_audit_event_id uuid references public.audit_events(id),

  constraint roadmap_gates_unique_attempt
    unique (wave_id, attempt_number, status)
);

create index if not exists roadmap_gates_wave_idx
  on public.roadmap_gates (wave_id, recorded_at desc);

create index if not exists roadmap_gates_pilot_idx
  on public.roadmap_gates (pilot_pharmacy_id_hash)
  where pilot_pharmacy_id_hash is not null;

-- Append-only enforcement (mirrors audit_events triggers).
create or replace function public.reject_roadmap_gates_mutation()
returns trigger language plpgsql as $$
begin
  raise exception 'roadmap_gates is append-only. Mutation rejected.';
end;
$$;

drop trigger if exists roadmap_gates_no_update on public.roadmap_gates;
create trigger roadmap_gates_no_update
  before update on public.roadmap_gates
  for each row execute function public.reject_roadmap_gates_mutation();

drop trigger if exists roadmap_gates_no_delete on public.roadmap_gates;
create trigger roadmap_gates_no_delete
  before delete on public.roadmap_gates
  for each row execute function public.reject_roadmap_gates_mutation();

-- RLS: deny by default. Service role writes; admin reads.
-- Pilot-row visibility (RLS by pilot_pharmacy_id_hash) deferred to Wave 1
-- when the master gate counter starts ticking.
alter table public.roadmap_gates enable row level security;

comment on table public.roadmap_gates is
  'Wave 0 (2026-05-01): meta-roadmap wave gate audit table. Append-only. Per v4 meta-roadmap (SuavoAgent docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md) §8.';
```

- [ ] **Step 3: Verify migration applies cleanly to a local Supabase instance (if available)**

```bash
# If supabase local is running:
supabase db reset --local 2>&1 | tail -20
# Verify the table exists:
supabase db dump --local --schema public 2>&1 | grep -A 3 "TABLE.*roadmap_gates"
```

Expected: migration runs without error, `roadmap_gates` table exists with declared columns.

If `supabase` local is not running on the dev box: skip this step + verify in PR review using a SQL linter or `psql --dry-run`. Note in the PR description that **prod application is deferred to Wave 1**.

- [ ] **Step 4: Commit**

```bash
git add supabase/migrations/20260501020000_roadmap_gates.sql
git commit -m "feat(migration): roadmap_gates append-only table for v4 meta-roadmap audit"
```

---

### Task 7: Add TS Zod schemas mirroring the C# event payloads

**Repo:** Suavo
**Files:**
- Create: `src/lib/wave-event-payloads.ts`
- Test: `src/lib/__tests__/wave-event-payloads.test.ts`

These Zod schemas guard the cloud-side ingest endpoint (Wave 1+) against malformed wave events. Same shape as the C# records. Field names use snake_case for JSON wire compatibility.

- [ ] **Step 1: Write the failing vitest test**

Create `src/lib/__tests__/wave-event-payloads.test.ts`:

```typescript
import { describe, expect, it } from "vitest";
import {
  WaveGateTrippedPayloadSchema,
  WaveGateFailedPayloadSchema,
  ROOT_CAUSE_CLASSES,
  NEXT_ATTEMPT_ESTIMATES,
} from "@/lib/wave-event-payloads";

describe("WaveGateTrippedPayloadSchema", () => {
  it("parses a valid CI-certified trip", () => {
    const parsed = WaveGateTrippedPayloadSchema.parse({
      wave_id: "W0",
      evidence_summary: "5 open PRs resolved",
      certified_by: "ci",
      evidence_event_ids: ["audit-1", "audit-2"],
      tripped_at: new Date().toISOString(),
    });
    expect(parsed.wave_id).toBe("W0");
    expect(parsed.evidence_event_ids).toHaveLength(2);
  });

  it("parses pilot-certified shape", () => {
    const parsed = WaveGateTrippedPayloadSchema.parse({
      wave_id: "W3",
      evidence_summary: "Nadim 7-day soak passed",
      certified_by: "pilot:abc123hash",
      evidence_event_ids: [],
      tripped_at: new Date().toISOString(),
    });
    expect(parsed.certified_by).toBe("pilot:abc123hash");
  });

  it("rejects missing wave_id", () => {
    expect(() =>
      WaveGateTrippedPayloadSchema.parse({
        evidence_summary: "x",
        certified_by: "ci",
        evidence_event_ids: [],
        tripped_at: new Date().toISOString(),
      }),
    ).toThrow();
  });

  it("rejects non-ISO tripped_at", () => {
    expect(() =>
      WaveGateTrippedPayloadSchema.parse({
        wave_id: "W0",
        evidence_summary: "x",
        certified_by: "ci",
        evidence_event_ids: [],
        tripped_at: "not-a-date",
      }),
    ).toThrow();
  });
});

describe("WaveGateFailedPayloadSchema", () => {
  it("parses a valid pilot-crash-midsoak failure", () => {
    const parsed = WaveGateFailedPayloadSchema.parse({
      wave_id: "W3",
      attempt_number: 2,
      failure_summary: "Helper crashed at day 5 of 7-day soak",
      root_cause_class: "pilot-crash-midsoak",
      remediation_plan_committed_at: new Date().toISOString(),
      next_attempt_estimated: "after-fix",
    });
    expect(parsed.attempt_number).toBe(2);
    expect(parsed.root_cause_class).toBe("pilot-crash-midsoak");
  });

  it("accepts null remediation_plan_committed_at for unplanned blockers", () => {
    const parsed = WaveGateFailedPayloadSchema.parse({
      wave_id: "W6",
      attempt_number: 1,
      failure_summary: "Yubikey not delivered",
      root_cause_class: "blocker-external",
      remediation_plan_committed_at: null,
      next_attempt_estimated: "when-blocker-clears",
    });
    expect(parsed.remediation_plan_committed_at).toBeNull();
  });

  it.each(ROOT_CAUSE_CLASSES)("accepts %s as root_cause_class", (rc) => {
    const parsed = WaveGateFailedPayloadSchema.parse({
      wave_id: "W1",
      attempt_number: 1,
      failure_summary: "test",
      root_cause_class: rc,
      remediation_plan_committed_at: null,
      next_attempt_estimated: "unknown",
    });
    expect(parsed.root_cause_class).toBe(rc);
  });

  it.each(NEXT_ATTEMPT_ESTIMATES)("accepts %s as next_attempt_estimated", (na) => {
    const parsed = WaveGateFailedPayloadSchema.parse({
      wave_id: "W1",
      attempt_number: 1,
      failure_summary: "test",
      root_cause_class: "code-bug",
      remediation_plan_committed_at: null,
      next_attempt_estimated: na,
    });
    expect(parsed.next_attempt_estimated).toBe(na);
  });

  it("rejects unknown root_cause_class", () => {
    expect(() =>
      WaveGateFailedPayloadSchema.parse({
        wave_id: "W1",
        attempt_number: 1,
        failure_summary: "x",
        root_cause_class: "made-up-cause",
        remediation_plan_committed_at: null,
        next_attempt_estimated: "unknown",
      }),
    ).toThrow();
  });

  it("rejects non-positive attempt_number", () => {
    expect(() =>
      WaveGateFailedPayloadSchema.parse({
        wave_id: "W1",
        attempt_number: 0,
        failure_summary: "x",
        root_cause_class: "code-bug",
        remediation_plan_committed_at: null,
        next_attempt_estimated: "unknown",
      }),
    ).toThrow();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

```bash
cd /Users/joshuahenein/Code/Suavo
npx vitest run src/lib/__tests__/wave-event-payloads.test.ts 2>&1 | tail -10
```

Expected: import resolution failure ("@/lib/wave-event-payloads" cannot be found) or all tests fail.

- [ ] **Step 3: Implement the Zod schemas**

Create `src/lib/wave-event-payloads.ts`:

```typescript
import { z } from "zod";

/**
 * Canonical Zod schemas for the meta-roadmap wave gate events.
 * Mirror C# `WaveGateTrippedPayload` + `WaveGateFailedPayload` in
 * SuavoAgent.Contracts.Models. Source of truth for the event shape lives
 * at SuavoAgent's `docs/self-healing/event-registry.md` under `wave.*`.
 *
 * Used by the cloud audit ingest path + the `roadmap_gates` table writer.
 * Per v4 meta-roadmap §8.
 */

export const ROOT_CAUSE_CLASSES = [
  "code-bug",
  "scope-error",
  "blocker-external",
  "architectural-error",
  "pilot-crash-midsoak",
] as const;

export const NEXT_ATTEMPT_ESTIMATES = [
  "unknown",
  "when-blocker-clears",
  "after-fix",
] as const;

export type RootCauseClass = (typeof ROOT_CAUSE_CLASSES)[number];
export type NextAttemptEstimated = (typeof NEXT_ATTEMPT_ESTIMATES)[number];

const isoDateTime = z.string().refine(
  (s) => !Number.isNaN(Date.parse(s)),
  { message: "must be an ISO 8601 datetime" },
);

export const WaveGateTrippedPayloadSchema = z.object({
  wave_id: z.string().min(1),
  evidence_summary: z.string(),
  certified_by: z.string().min(1),
  evidence_event_ids: z.array(z.string()),
  tripped_at: isoDateTime,
});

export type WaveGateTrippedPayload = z.infer<typeof WaveGateTrippedPayloadSchema>;

export const WaveGateFailedPayloadSchema = z.object({
  wave_id: z.string().min(1),
  attempt_number: z.number().int().positive(),
  failure_summary: z.string(),
  root_cause_class: z.enum(ROOT_CAUSE_CLASSES),
  remediation_plan_committed_at: isoDateTime.nullable(),
  next_attempt_estimated: z.enum(NEXT_ATTEMPT_ESTIMATES),
});

export type WaveGateFailedPayload = z.infer<typeof WaveGateFailedPayloadSchema>;
```

- [ ] **Step 4: Run test to verify it passes**

```bash
npx vitest run src/lib/__tests__/wave-event-payloads.test.ts 2>&1 | tail -15
```

Expected: all tests pass (~14 cases including parametrized ones).

- [ ] **Step 5: Commit**

```bash
git add src/lib/wave-event-payloads.ts \
        src/lib/__tests__/wave-event-payloads.test.ts
git commit -m "feat(lib): wave event payload Zod schemas mirroring SuavoAgent contracts"
```

---

### Task 8: Push Suavo branch + open PR (operational)

**Repo:** Suavo
**Files:** none new

- [ ] **Step 1: Push branch**

```bash
cd /Users/joshuahenein/Code/Suavo
git push -u origin feat/wave-0-roadmap-gates 2>&1 | tail -5
```

- [ ] **Step 2: Open PR**

```bash
gh pr create --repo SuavoLLC/MKM \
  --title "Wave 0 (Suavo side): roadmap_gates migration + wave event Zod schemas" \
  --body "$(cat <<'EOF'
Closes Wave 0 Suavo-side deliverables per the meta-roadmap committed in
SuavoAgent (`docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md`).

## Changes
- `supabase/migrations/20260501020000_roadmap_gates.sql` — append-only operational table for meta-roadmap wave gate audit. Mirrors `audit_events` triggers. Per spec §8.
- `src/lib/wave-event-payloads.ts` — Zod schemas for `wave.gate_tripped` + `wave.gate_failed`. Mirror C# `WaveGateTrippedPayload` + `WaveGateFailedPayload` in SuavoAgent.
- `src/lib/__tests__/wave-event-payloads.test.ts` — vitest coverage including all `root_cause_class` + `next_attempt_estimated` enums and reject paths.

## Migration application
NOT applied to prod in this PR. Application is a Wave 1 deliverable per the meta-roadmap.
After merge: `cd ~/Code/Suavo && supabase db push --linked --include-all` (Joshua runs interactively per the existing CLI hook constraint).

## Test plan
- [x] vitest covers parse + reject for both schemas
- [ ] supabase db reset (local) applies migration cleanly
- [ ] paired SuavoAgent PR `feat/wave-0-suavoagent-events` has matching C# records

EOF
)"
```

If gh PR create fails: paste the body via web UI.

---

### Task 9: Self-review + verify Wave 0 gate

**Repo:** both
**Files:** none new

- [ ] **Step 1: Run all tests in both repos**

```bash
cd /Users/joshuahenein/Code/SuavoAgent && dotnet test 2>&1 | tail -10
cd /Users/joshuahenein/Code/Suavo && npx vitest run 2>&1 | tail -10
```

Expected: all green in both repos.

- [ ] **Step 2: Verify Wave 0 mechanical gate per meta-roadmap §4**

```bash
# 1. No stale open PRs in SuavoAgent (PR #33, #34, #36, #37 merged or PR #35 explicitly held)
gh pr list --repo MinaH153/SuavoAgent --state open 2>&1
# Expected after Joshua's operational merges: PR #35 only (held with rationale doc + comment)

# 2. Meta-roadmap on SuavoAgent main
git -C /Users/joshuahenein/Code/SuavoAgent ls-tree origin/main docs/superpowers/specs/2026-05-01-suavoagent-v4-roadmap-design.md 2>&1
# Expected: file shown (after the docs/v4-roadmap-2026-05-01 PR merges)

# 3. roadmap_gates migration committed (NOT applied)
git -C /Users/joshuahenein/Code/Suavo ls-tree origin/main supabase/migrations/20260501020000_roadmap_gates.sql 2>&1
# Expected: file shown after Suavo PR merge
```

- [ ] **Step 3: Joshua-certified gate check**

Joshua reviews:
- The two new event entries in `event-registry.md` for canonical alignment
- The C# payload contracts read true (XML doc comments accurate, field names match `event-registry.md`)
- The Zod schemas match the C# shape (snake_case wire format ↔ PascalCase C#)
- The migration's RLS posture is right (deny default + service-role writes; pilot RLS deferred to W1)
- The PR #35 hold-decision doc reads true

If Joshua-certified passes: emit `wave.gate_tripped` for `W0`. (Manual entry into `roadmap_gates` once Wave 1 applies the migration; until then, log on the meta-roadmap PR description as `Wave 0 closed YYYY-MM-DD`.)

---

## Out-of-scope (operational, Joshua handles)

These items live in the Wave 0 gate but require no implementation planning:

- Merge PR #33 (Codex MEDIUM — audit-archive PHI scrub + helper failure counter)
- Merge PR #34 (last CRITICAL — drop PHI from `SerializeRxBatch` + chained audit on every sync)
- Synchronize PR #36 + PR #37 + paired Suavo PR `SuavoLLC/MKM#192` for unit-merge
- Push `docs/v4-roadmap-2026-05-01` branch + open meta-roadmap PR (or merge directly via web UI given gh-auth constraint)

---

## Self-review

(Engineer: do not skip this section. Run after Task 9.)

1. **Spec coverage** (meta-roadmap §4 Wave 0):
   - Meta-roadmap committed → ✓ separate PR `docs/v4-roadmap-2026-05-01` (out-of-scope merge)
   - `roadmap_gates` migration drafted → ✓ Task 6
   - `wave.gate_tripped` + `wave.gate_failed` registered → ✓ Task 1
   - C# payload contracts → ✓ Tasks 2 + 3
   - TS Zod schemas → ✓ Task 7
   - PR #35 hold-decision doc → ✓ Task 4
   - PR #33/#34/#36/#37 merges → out-of-scope (Joshua operational)

2. **Placeholder scan**: no TBD/TODO/"implement later" in any file produced. All commands and code complete. Date placeholders only in PR-body templates where Joshua fills the actual date.

3. **Type consistency**:
   - C# `WaveGateTrippedPayload(WaveId, EvidenceSummary, CertifiedBy, EvidenceEventIds, TrippedAt)` ↔ Zod `{wave_id, evidence_summary, certified_by, evidence_event_ids, tripped_at}` — same field count, same semantics, snake_case on TS for wire compat.
   - C# `WaveGateFailedPayload(WaveId, AttemptNumber, FailureSummary, RootCauseClass, RemediationPlanCommittedAt, NextAttemptEstimated)` ↔ Zod `{wave_id, attempt_number, failure_summary, root_cause_class, remediation_plan_committed_at, next_attempt_estimated}` — match.
   - C# `DateTimeOffset?` on `RemediationPlanCommittedAt` ↔ Zod `isoDateTime.nullable()` — match.
   - C# `string` enums (`RootCauseClass`, `NextAttemptEstimated`) ↔ Zod `z.enum(ROOT_CAUSE_CLASSES)` / `z.enum(NEXT_ATTEMPT_ESTIMATES)` — match. The C# side relies on docstring enumeration; the Zod side enforces. Acceptable asymmetry for v0.1.
   - SQL `roadmap_gates.root_cause_class` CHECK constraint matches the same 5-value enum — match.
   - SQL `roadmap_gates.next_attempt_estimated` CHECK constraint matches the same 3-value enum — match.

If anything diverges, fix in the relevant file before declaring Wave 0 done.

---

## Change log

- **2026-05-01 v0.1** — Initial plan from brainstorming session.
