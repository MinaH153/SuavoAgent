# SuavoAgent v3.12 — Autonomous Desktop Intelligence

**Milestone:** Spec B observes → WorkflowTemplateExtractor produces PHI-free,
fingerprint-verified, auto-retirable templates → TemplateRuleGenerator writes
its own Tier-1 YAML rules → Spec-D transfers templates cross-pharmacy → Schema
Canary drift adaptations propagate fleet-wide.

**Status:** Post Codex review (2026-04-19, session
`019da827-26b7-7252-8358-af9642d31d01`). Date: 2026-04-19. Author: Claude.
Owner: MKM/Suavo. Depends on v3.11.1 (main at e56b7a6, 837 tests passing).

**Patent positioning hypothesis (not a claim):** Autonomous
Compliance-Bounded Desktop Intelligence with Fingerprint-Verified
Cross-Installation Transfer. Novelty vs NimbusAI / UiPath StudioX /
Automation Anywhere is hypothesised — must be validated by external prior
art search in a separate memo before any filing. Strategy = dual-gate
fingerprint + autonomousOk=false default + structural pre-action
re-verify on writeback.

**Codex review outcome:** 1 BLOCK (Area 2 — structural post-verify
not implementable on current rule surface), 6 WARN (all addressed
inline). See §12.

---

## 1. Architecture decision — locked

The **template fingerprinting + safety gates** architecture is decided before
implementation. Reopening is only permitted for a Precedence-1 finding
(security / HIPAA / data loss / irreversible action).

### 1.1 Template fingerprint (Strategy C)

A WorkflowTemplate matches a live screen when ALL of the following hold:

1. **Process-family gate.** Template declares a `processNameGlob`
   (e.g. `PioneerPharmacy*`). Current foreground process must match.
2. **PMS-version compatibility range.** Template declares a
   `pmsVersionRange` = set of accepted `PmsVersionFingerprints`
   (composite = schema hash + UIA dialect hash). Receiver's own fingerprint
   must be in the set, else fail closed.
3. **Per-step structural fingerprint.** Each step carries an
   `elementSet` of expected `ElementSignature` triples
   `{ControlType, AutomationId, ClassName}`. Match is K-of-M
   where K = `ceil(0.8 * M)` with `M >= 3`. Raw UIA `Name` is never
   used cross-installation; only `AutomationId` (dev-assigned, stable,
   non-PHI) and `ClassName` (framework id, non-PHI).
4. **Relative depth tolerance.** Each `ElementSignature` may declare a
   `depthDelta` window (default `+/-2`). Protects against cosmetic parent
   container rewrites while preventing the template from matching an
   unrelated dialog with coincidentally-named buttons.

A composite `ScreenSignatureV1` hash = SHA-256 over canonically-sorted
triples + depth deltas. Used as a cache key and audit artifact, not as the
only match gate.

### 1.2 Three safety gates — non-negotiable

1. **`autonomousOk=false` always on auto-generated rules.** The extractor
   MUST NOT emit an autonomous rule. Operator flips via a separate
   shadow→approval workflow described in §6. No workaround.
2. **PMS version fingerprint gate.** Template load-time check rejects any
   template whose `pmsVersionRange` does not include the receiver's
   `LocalPmsVersionFingerprint`. Load-time, not run-time: templates in
   the wrong version never enter the RuleEngine catalog at all.
3. **Mandatory structural pre-action re-verify on any writeback step.**
   (Codex Area 2 fix.) The rule-level `VerifyAfter` alone is insufficient
   because the current `RulePredicate.VisibleElements` is a flat
   `IReadOnlyList<string>` that only checks element *names* — a different
   screen that happens to contain the same label passes. For v3.12 we
   therefore:
   - Extend `RulePredicate` and `RuleContext` with
     `ElementFingerprints: IReadOnlyList<ElementSignature>` (see §2.2) so
     structural `{ControlType, AutomationId, ClassName}` triples carry
     through the rule surface.
   - Extend `RuleEngine.PredicateMatches` to require all listed
     `ElementFingerprints` be present in the context's
     `ElementFingerprints` by triple match.
   - Define an executor contract: **any Click/Type/PressKey step whose
     originating `TemplateStep.IsWrite == true` MUST recapture UIA
     structural context and re-run `PredicateMatches(rule.Then[i].VerifyBefore, refreshedCtx)`
     immediately before firing.** TemplateRuleGenerator refuses to emit a
     rule if any writeback step is missing its `VerifyBefore` or
     `VerifyAfter`.
   - v3.12 does **not** wire an autonomous executor; every auto-rule
     hits operator approval via `autonomousOk=false`. The structural
     contract landing now is a prerequisite for the executor to be
     allowed to flip autonomous in v3.12.x.

### 1.3 Prior art positioning (hypothesis — requires external validation)

This table is a working hypothesis for a separate prior-art memo; it
is not evidence of novelty on its own. Patent filing decisions must
grind through an external search of USPTO / Google Patents / relevant
arXiv papers first.

| System | Observation | Action | Cross-installation | Healthcare | Safety gates |
|---|---|---|---|---|---|
| NimbusAI (per public docs) | Manual demo | Replay via MCP | No | Blocked | None documented |
| UiPath StudioX | Manual record | Replay script | Package export | Requires services arm | Designer-configured |
| Automation Anywhere | Manual record | Bot | Package export | Services arm | Designer-configured |
| Traditional API integration | n/a | Via vendor API | n/a | BAA per vendor | n/a |
| **SuavoAgent v3.12 (proposed)** | **Autonomous observation** | **Compliance-bounded action** | **Fingerprint-verified transfer** | **HIPAA-native scrubber** | **Three-gate default + structural pre-verify** |

---

## 2. Contracts — `SuavoAgent.Contracts.Learning`

### 2.1 `PmsVersionFingerprint`

```csharp
public sealed record PmsVersionFingerprint(
    string PmsType,              // "PioneerRx" | "Computer-Rx" | ...
    string SchemaHash,           // from ContractFingerprinter.HashObjects
    string UiaDialectHash,       // new — hash of {AutomationId set} per screen family
    string? ProductVersionString // optional human label, never used for matching
);
```

### 2.2 `ElementSignature` — the GREEN-tier per-step atom

All three fields come from the `UiaPropertyScrubber` GREEN tier
(`src/SuavoAgent.Contracts/Behavioral/UiaPropertyScrubber.cs`). Depth
and bounding-box are **not** part of the v3.12 signature because the
Helper does not currently persist those into `behavioral_events`; adding
them is a v3.12.1+ enhancement.

```csharp
public sealed record ElementSignature(
    string ControlType,        // UIA enum string, non-PHI
    string AutomationId,       // required — empty AutomationId rejects the signature
    string? ClassName          // optional disambiguator
);
```

Both `RulePredicate` and `RuleContext` grow a new field:

```csharp
public sealed record RulePredicate
{
    // ... existing fields ...
    public IReadOnlyList<ElementSignature> ElementFingerprints { get; init; }
        = Array.Empty<ElementSignature>();
}

public sealed record RuleContext
{
    // ... existing fields ...
    public IReadOnlyList<ElementSignature> ElementFingerprints { get; init; }
        = Array.Empty<ElementSignature>();
}
```

`RuleEngine.PredicateMatches` is extended to require every listed
fingerprint in the predicate be present (triple match,
`OrdinalIgnoreCase` on `AutomationId`, `ControlType` exact,
`ClassName` null-tolerant) in the context. Existing
`VisibleElements` stays intact for legacy rules — fingerprints are an
AND, not a replacement.

### 2.2.1 `KeyHint` — closed non-PHI type (replaces open-map `KeyHints`)

(Codex Area 1 fix.) An open-ended `Dictionary<string,string>` could
carry UI literals that are PHI-adjacent. v3.12 uses a closed record.

```csharp
public sealed record KeyHint(
    string? KeyName,                       // "Enter", "Escape", "Tab" — UIA KeyName only
    KeyHintPlaceholder? Placeholder);      // optional safe placeholder for Type steps

public enum KeyHintPlaceholder
{
    RxNumberEchoed,        // echoes the Rx number the current skill is processing
    CurrentDateIsoUtc,     // ISO-8601 UTC timestamp
    AgentUserNameFromAdapter // PMS-adapter-provided user identifier (already scrubbed)
}
```

The extractor **MUST NOT** persist operator-entered text or UI
text-derived literals into `KeyHint`. The enum is the entire whitelist.

### 2.3 `TemplateStep`

```csharp
public enum TemplateStepKind { Click, Type, PressKey, WaitForElement, VerifyElement }

public sealed record TemplateStep(
    int Ordinal,
    TemplateStepKind Kind,
    ElementSignature Target,
    IReadOnlyList<ElementSignature> ExpectedVisible,  // N required elements on screen before firing
    int MinElementsRequired,                          // K of ExpectedVisible.Count
    IReadOnlyList<ElementSignature>? ExpectedAfter,   // null unless writeback
    bool IsWrite,                                     // correlated SQL UPDATE/INSERT/DELETE
    string? CorrelatedQueryShapeHash,                 // the SQL shape we expect to fire
    double StepConfidence,
    KeyHint? Hint                                     // closed non-PHI type (see §2.2.1)
);
```

### 2.4 `WorkflowTemplate` — the transfer currency

(Codex Area 6 fix: `StepsHash` lands alongside `ScreenSignatureV1` so
version bumps + idempotency use the same canonical bytes.)

```csharp
public sealed record WorkflowTemplate(
    string TemplateId,                 // SHA-256(screenSignature + stepsHash)
    string TemplateVersion,             // semver; bumps when StepsHash changes
    string SkillId,                     // e.g. "pricing-lookup", "dispensing"
    string ProcessNameGlob,
    IReadOnlyList<PmsVersionFingerprint> PmsVersionRange,
    string ScreenSignatureV1,           // SHA-256 over canonically-sorted ElementSignatures of Step[0].ExpectedVisible
    string StepsHash,                   // SHA-256 over CanonicalizeSteps(Steps)
    string? RoutineHashOrigin,           // backref to LearnedRoutine
    IReadOnlyList<TemplateStep> Steps,
    double AggregateConfidence,          // min over steps
    int ObservationCount,
    bool HasWriteback,
    string ExtractedAt,
    string ExtractedBy,                  // "local-v3.12" or "seed:<digest>"
    string? RetiredAt,                   // non-null after auto-retirement
    string? RetirementReason
);
```

`CanonicalizeSteps(steps)`:

1. For each step in `Ordinal` order, emit tab-separated lines:
   `Ordinal \t Kind \t Target.CanonicalRepr \t IsWrite \t CorrelatedQueryShapeHash? \t StepConfidence.ToString("F3") \t HintCanonical`
2. `ElementSignature.CanonicalRepr = ControlType + "|" + AutomationId + "|" + (ClassName ?? "")`.
3. `HintCanonical = (KeyName ?? "") + "|" + (Placeholder?.ToString() ?? "")`.
4. UTF-8 SHA-256 the joined string, lower-hex.

Idempotency: same `Steps` → same `StepsHash` → same `TemplateId` →
UPDATE path bumps `ObservationCount` + `AggregateConfidence` without
bumping `TemplateVersion`. Different `StepsHash` → new `TemplateVersion`,
previous version retired with `retirement_reason="superseded"`.

### 2.5 `SchemaAdaptation` — fleet canary propagation payload

(Codex Area 3 fix: own canonical form, not a reuse of `SignedCommand`
bytes.)

```csharp
public sealed record SchemaAdaptation(
    string AdaptationId,                 // GUID or SHA-256(originPharmacyId + fromHash + toHash)
    string CanonicalVersion,             // "SchemaAdaptationCanonicalV1" — future proofing
    string PmsType,
    string FromSchemaHash,               // old fingerprint (hex lower)
    string ToSchemaHash,                 // new fingerprint after adaptation (hex lower)
    IReadOnlyList<SchemaDelta> Deltas,    // column rename / type change / added NOT NULL
    IReadOnlyList<QueryRewrite> Rewrites, // before/after parameterized SQL pairs
    string OriginPharmacyId,              // HMAC(cloud_salt, raw_pharmacy_id) — never plaintext
    string NotBefore,                     // ISO-8601 UTC
    string ExpiresAt,                     // ISO-8601 UTC, max NotBefore + 30 days
    string KeyId,                         // ECDSA key id ("adapt-v1")
    string Signature                      // base64 ECDSA over SchemaAdaptationCanonicalV1
);

// SchemaAdaptationCanonicalV1 byte layout (UTF-8, line-joined by \n, no trailing newline):
//   CanonicalVersion
//   AdaptationId
//   PmsType
//   FromSchemaHash
//   ToSchemaHash
//   DeltasHash     = SHA-256 over SchemaDelta records (see SchemaDelta.CanonicalRepr)
//   RewritesHash   = SHA-256 over QueryRewrite records (see QueryRewrite.CanonicalRepr)
//   OriginPharmacyId
//   NotBefore
//   ExpiresAt
//   KeyId
//
// SchemaDelta.CanonicalRepr =
//   SchemaName|TableName|ColumnName|OldDataType|NewDataType|OldNullable|NewNullable|ChangeKind
// QueryRewrite.CanonicalRepr =
//   OldShapeHash|NewShapeHash|SHA-256(NewParameterizedSql UTF-8)

public sealed record SchemaDelta(
    string SchemaName, string TableName, string ColumnName,
    string OldDataType, string NewDataType,
    bool OldNullable, bool NewNullable,
    string ChangeKind                    // "added" | "removed" | "retyped" | "renamed"
);

public sealed record QueryRewrite(
    string OldShapeHash,
    string NewParameterizedSql,
    string NewShapeHash
);
```

**PHI boundary:** `OriginPharmacyId` is an HMAC over pharmacy_id with the cloud
salt. Deltas contain schema names only — PioneerRx table/column names are not
PHI (they're vendor public API surface). `QueryRewrite.NewParameterizedSql` is
validated through `SqlTokenizer` before signing AND before applying on
receivers.

### 2.6 `AdaptationRevocation` — fleet recall path (Codex Area 7 fix)

```csharp
public sealed record AdaptationRevocation(
    string RevocationId,
    string TargetAdaptationId,       // the AdaptationId being revoked
    string Reason,                    // free text, capped 512 chars, no PHI
    string RevokedAt,                 // ISO-8601 UTC
    string KeyId,                     // "adapt-v1"
    string Signature                  // base64 over canonical: RevocationId|TargetAdaptationId|RevokedAt|KeyId
);
```

Receivers persist revocations in a `schema_adaptation_denylist` table.
Before applying any adaptation, receiver checks the denylist; a present
`TargetAdaptationId` → skip + audit `schema_adaptation_revoked`. An
already-applied adaptation whose revocation arrives later triggers the
existing rollback path (§5.4).

---

## 3. WorkflowTemplateExtractor — `SuavoAgent.Core.Learning`

### 3.1 Inputs

- `AgentStateDb.GetLearnedRoutines(sessionId)` — the DFG paths produced by
  `RoutineDetector`.
- `AgentStateDb.GetCorrelatedActions(sessionId)` — UI↔SQL mappings with
  confidence.
- `AgentStateDb.GetBehavioralEvents(sessionId, "interaction")` — per-element
  `ControlType`, `AutomationId`, `ClassName` (stored as `element_control_type`,
  `element_id`, `element_class_name`).
- `AgentStateDb.GetObservedProcesses(sessionId)` — process name glob
  derivation.
- Current `PmsVersionFingerprint` from Schema Canary + a new
  `UiaDialectHasher`.

### 3.2 Thresholds — v3.12 defaults

| Threshold | Value | Reason |
|---|---|---|
| `MinObservationCount` | 10 | Matches `correlated_actions.occurrence_count >= 10 → 0.9 confidence` band |
| `MinStepConfidence` | 0.6 | Excludes low-confidence drive-by correlations |
| `MinElementsPerScreen` | 3 | Strategy C requires M >= 3; degenerate screens rejected |
| `MatchRatio` | 0.8 | K = ceil(0.8 * M); 4-of-5, 6-of-7, etc. |
| `MaxTemplateSteps` | 20 | Same as `RoutineDetector.MaxPathLength` |
| `DefaultDepthDelta` | 2 | UIA depth tolerance |

All thresholds exposed via `WorkflowTemplateOptions` bound from
`appsettings.json` `Learning:Template:*` — so pilot pharmacies can tune
without a rebuild.

### 3.3 Algorithm

```
for each LearnedRoutine r with confidence >= MinStepConfidence:
    steps = []
    for each pathNode n in r.pathJson:
        events = db.GetBehavioralEventsByTreeElement(sessionId, n.treeHash, n.elementId)
        if events.count < MinObservationCount: continue
        sig = ElementSignature(n.controlType, events[0].automationId, events[0].className)

        screenEvents = db.GetBehavioralEventsByTreeHash(sessionId, n.treeHash)
        expectedVisible = top-K signatures by occurrence_count (non-PHI only)

        corrAction = findCorrelatedAction(sessionId, n.treeHash, n.elementId)
        isWrite = corrAction?.isWrite ?? false
        expectedAfter = isWrite ? diff(screenEvents_before, screenEvents_after) : null

        steps.add(TemplateStep(...))

    if steps.count < 2: skip   -- degenerate
    if any(step.IsWrite && step.ExpectedAfter is null): skip -- fail closed
    templateId = SHA-256(screenSignature + ordinals)
    template = WorkflowTemplate(..., HasWriteback = steps.any(s => s.IsWrite))
    db.UpsertWorkflowTemplate(template)
```

Extraction is **idempotent** (same inputs → same template id). Extractor
updates `ObservationCount` + `AggregateConfidence` on re-run; retires the
template if the underlying routine drops below `MinStepConfidence` for N
consecutive runs (see §7).

### 3.4 Storage — new `workflow_templates` + versioned migration

(Codex Area 5 fix: formalized migration with schema-version assertion.
Existing ad-hoc `TryAlter` pattern is preserved for backward-compat;
new HIPAA-critical tables use the transactional path below.)

```sql
-- Introduce schema_version tracking once:
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  applied_at TEXT NOT NULL,
  description TEXT NOT NULL
);

-- Migration 001 — workflow templates + auto-rule approvals + adaptation denylist.
-- Applied inside a BEGIN/COMMIT transaction; fails loudly on any error.
CREATE TABLE workflow_templates (
  template_id TEXT PRIMARY KEY,
  template_version TEXT NOT NULL,
  skill_id TEXT NOT NULL,
  process_name_glob TEXT NOT NULL,
  pms_version_range_json TEXT NOT NULL,
  screen_signature TEXT NOT NULL,
  steps_hash TEXT NOT NULL,
  routine_hash_origin TEXT,
  steps_json TEXT NOT NULL,
  aggregate_confidence REAL NOT NULL,
  observation_count INTEGER NOT NULL,
  has_writeback INTEGER NOT NULL,
  extracted_at TEXT NOT NULL,
  extracted_by TEXT NOT NULL,
  retired_at TEXT,
  retirement_reason TEXT,
  consecutive_low_conf_runs INTEGER NOT NULL DEFAULT 0,
  UNIQUE(skill_id, screen_signature)
);
CREATE INDEX idx_wt_skill ON workflow_templates(skill_id) WHERE retired_at IS NULL;
CREATE INDEX idx_wt_writeback ON workflow_templates(has_writeback)
  WHERE retired_at IS NULL AND has_writeback = 1;
```

The `MigrationRunner` (new, lives at `src/SuavoAgent.Core/State/MigrationRunner.cs`)
reads `schema_migrations.version`, applies pending migrations inside a
single transaction each, and records the applied version. Any exception
rolls back and fails startup (fail-closed).

---

## 4. TemplateRuleGenerator — Template → YAML rule

Reads approved WorkflowTemplates; writes YAML files to
`%ProgramData%/SuavoAgent/rules/auto/<skill_id>/<template_id>.yaml`.

### 4.1 Mapping table

| `TemplateStep.Kind` | `RuleActionType` |
|---|---|
| Click | Click |
| Type | Type (parameters: `text` from operator override or `KeyHints.placeholder`) |
| PressKey | PressKey (parameters: `key` from `KeyHints`) |
| WaitForElement | WaitForElement |
| VerifyElement | VerifyElement |

Step 0's `ExpectedVisible` becomes the rule's `when.visibleElements`.
`processNameGlob` becomes `when.processName`.

### 4.2 Invariants the generator enforces

1. `autonomousOk` is **hardcoded false**. Not a parameter, not in options.
2. Any step with `IsWrite == true` and no `ExpectedAfter` → generator
   throws `InvalidOperationException`. TDD covers this (§9.3 test case).
3. Any step with `IsWrite == true` whose predicate carries fewer than 3
   `ElementFingerprints` → generator throws. Writeback rules demand a
   structural fingerprint, not just a name list. (Codex Area 2.)
4. Every `Then` step gets a `VerifyAfter` when `ExpectedAfter` is non-null,
   built from the GREEN-tier signatures — `ElementFingerprints` only,
   empty `VisibleElements`.
5. For any `IsWrite` step, `Then[i]` ALSO gets a `VerifyBefore`
   `RulePredicate` carrying the full `ExpectedVisible` as
   `ElementFingerprints`. The executor contract promises to re-run
   `RuleEngine.PredicateMatches(VerifyBefore, refreshedCtx)` immediately
   before firing; generator refuses emit if `VerifyBefore` would be empty.
6. `Priority` = 200 for non-writeback templates, 300 for writeback
   templates (higher so structural re-verify short-circuits before a
   weaker match pre-empts it).
7. Output round-trips through `YamlRuleLoader.ParseYaml` before being
   written to disk — fail-closed on any serialization mismatch.
8. Rule `id` = `auto.<skill_id>.<template_id[:12]>` to distinguish from
   bundled rules.

### 4.3 Shadow→approval workflow

Generator also upserts an `auto_rule_approvals` row:

```sql
CREATE TABLE auto_rule_approvals (
  rule_id TEXT PRIMARY KEY,
  template_id TEXT NOT NULL,
  yaml_sha256 TEXT NOT NULL,
  status TEXT NOT NULL,            -- 'pending' | 'shadow' | 'approved' | 'rejected'
  shadow_runs INTEGER NOT NULL DEFAULT 0,
  shadow_matches INTEGER NOT NULL DEFAULT 0,
  shadow_mismatches INTEGER NOT NULL DEFAULT 0,
  approved_by TEXT,
  approved_at TEXT,
  rejected_reason TEXT
);
```

- `status='pending'`: generated but never exercised.
- `status='shadow'`: TieredBrain executes in shadow mode; extractor
  increments `shadow_runs` / `shadow_matches`.
- `status='approved'` (`shadow_matches >= 10 AND mismatches == 0` **AND**
  explicit operator flip): rule goes live in `/rules/approved/` and
  `auto_rule_approvals.approved_at` is set. Only then does RuleEngine
  ignore the `autonomousOk=false` default (via a runtime override flag
  that is **only** checked when status='approved'; cannot be forced by
  YAML edit).

---

## 5. Fleet Schema Canary Propagation

### 5.1 Local → Cloud (producer)

1. Schema Canary detects drift, resolves locally via manual or seeded
   rewrite.
2. `SchemaAdaptationPackager` builds a `SchemaAdaptation` from the
   resolved canary incident + latest `correlation_window_overrides`.
3. Validates every `QueryRewrite.NewParameterizedSql` through
   `SqlTokenizer`. Fail closed.
4. Signs with the existing ECDSA key via `SelfUpdater`-style canonical
   string (same bytes as `SignedCommand.ComputeDataHash`).
5. Posts to `/api/agent/schema-adaptation` over HMAC-authed channel
   already used for other agent→cloud calls.

### 5.2 Cloud distribution

Cloud endpoint (not built in v3.12 — contract-only; server impl is a
follow-up):

- Deduplicates by `AdaptationId`.
- Stores with `originPharmacyId` hashed, never plaintext.
- Distributes to agents running the same `PmsType` + `FromSchemaHash` via
  heartbeat response (piggybacking on the existing command channel).

### 5.3 Cloud → Agent (consumer)

1. `SchemaAdaptationWorker` polls heartbeat responses for `adaptations` envelope.
2. Verifies ECDSA signature via `SignedCommandVerifier` (same key
   registry as update manifest; new keyId prefix `adapt-`).
3. Verifies the receiver's own `LocalPmsVersionFingerprint.SchemaHash`
   matches the adaptation's `FromSchemaHash`. If not, rejects as
   "doesn't apply" (not a failure, just skipped).
4. Applies via existing rewrite store — re-uses the per-shape override
   plumbing already used by seeds.
5. Records in `learning_audit` with `operation='schema_adaptation_applied'`.

### 5.4 Failure modes

- Signature invalid → drop, audit.
- Fingerprint mismatch → skip, audit.
- SqlTokenizer rejects a rewrite → drop entire adaptation, audit.
- After apply, `Schema Canary` re-verifies; if still in warning state,
  roll back the rewrite and audit.
- `ExpiresAt` in the past at receipt → drop, audit.
- `AdaptationId` in `schema_adaptation_denylist` → skip, audit.

### 5.4.1 Revocation wiring (Codex Area 7)

`SchemaAdaptationWorker` also pulls signed `AdaptationRevocation`
records on the same heartbeat cadence. On receipt:

1. Verify signature with `adapt-v1` key. Fail → drop.
2. Insert `(target_adaptation_id, revoked_at, reason)` into
   `schema_adaptation_denylist`.
3. If the target adaptation is already applied locally, invoke the
   existing rollback path (the original `fromSchemaHash` queries are
   retained in `correlation_window_overrides` history for ≥ 48 h).
4. Audit `schema_adaptation_revoked`.

Progressive rollout: cloud-side gate limits a new adaptation to 10%
of the eligible fleet for the first 6 h after `SignedAt`; revocation
short-circuits the ramp.

---

## 6. Spec-D Template Transfer — `SeedApplicator` changes

### 6.1 `SeedResponse` additions

```csharp
public sealed record SeedResponse(
    ...
    IReadOnlyList<SeedWorkflowTemplate>? WorkflowTemplates  // NEW
);

public sealed record SeedWorkflowTemplate(
    string TemplateId, string TemplateVersion,
    string SkillId, string ProcessNameGlob,
    IReadOnlyList<PmsVersionFingerprint> PmsVersionRange,
    string ScreenSignatureV1,
    IReadOnlyList<TemplateStep> Steps,
    double AggregateConfidence,
    int ContributorCount,           // how many pharmacies observed this
    int FleetMatchCount,            // fleet-wide successful matches
    int FleetMismatchCount          // fleet-wide mismatches
);
```

### 6.2 `SeedApplicator.ApplyWorkflowTemplates`

1. For each `SeedWorkflowTemplate`:
   - Validate `PmsVersionRange` contains the local `PmsVersionFingerprint`.
     If not — record `RejectSeedItem("template")` and skip.
   - Validate every `CorrelatedQueryShapeHash` is known (already applied
     via `ApplyModelSeeds`). Else reject.
   - Persist as `WorkflowTemplate` with `ExtractedBy = "seed:<seed_digest>"`.
2. Always `autonomousOk=false` on emitted rules (§4.1 invariant re-applies).
3. Increment `seed_items` with `item_type='template'` for confirm round-trip.

### 6.3 Template versioning + auto-retirement

- New column on `workflow_templates`: `consecutive_low_conf_runs`.
- `WorkflowTemplateExtractor` increments this when a re-run produces
  `AggregateConfidence < MinStepConfidence` for an existing template.
- At `consecutive_low_conf_runs >= 5`, extractor sets `retired_at` and
  `retirement_reason = "confidence_drop"`. Retired templates are excluded
  from the RuleEngine catalog at load time.
- When a seeded template reaches retirement, we also emit a feedback
  event (`feedback_events.directive_type='retire_template'`) for the
  cloud to incorporate into future seed decisions.
- `TemplateVersion` bumps when: the same `screenSignature` produces a
  different step list on re-extraction. Old version is retired with
  `retirement_reason = "superseded"`.

### 6.4 Fleet confidence tracking

- Each shadow/live run reports back via existing feedback upload:
  - `directive_type='template_match' | 'template_mismatch'`
  - `target_type='template'`, `target_id=<template_id>`
- Cloud aggregates into `fleetMatchCount` / `fleetMismatchCount` on the
  canonical template record. Re-emitted in future `SeedResponse`s.

---

## 7. Wiring — `LearningWorker` changes

Add two extra calls inside the existing phase loop (after `RoutineDetector.DetectAndPersist` in `pattern` and `model` phases):

```csharp
// Extract templates from latest routines
var extractor = new WorkflowTemplateExtractor(_db, _sessionId,
    pmsVersionFingerprintProvider);
var extracted = extractor.ExtractAndPersist();

// Emit YAML rules for newly-minted templates (always autonomousOk=false)
var generator = new TemplateRuleGenerator(_db,
    rulesRoot: Path.Combine(programData, "SuavoAgent", "rules", "auto"));
generator.EmitPendingRules();
```

New worker `SchemaAdaptationWorker` mirrors `ConfigSyncWorker`: polls
`/api/agent/adaptations` on 15-min cadence; fetches + verifies + applies.
Lives in `src/SuavoAgent.Core/Workers/SchemaAdaptationWorker.cs`.

---

## 8. TieredBrain / RuleEngine integration

RuleEngine load path adds two filters:

1. **PMS fingerprint gate.** At `LoadFromDirectory`, every rule under
   `auto/` must reference an approved `auto_rule_approvals` row whose
   template's `PmsVersionRange` includes the local
   `LocalPmsVersionFingerprint`. If missing/mismatched → rule skipped with
   a WARN log.
2. **Shadow mode routing.** `auto_rule_approvals.status='shadow'` rules
   are loaded only into a shadow catalog. `TieredBrain.DecideAsync` with
   `shadowMode=true` consults the shadow catalog; match outcomes are
   recorded via the existing `feedback_events` path (no user-visible
   action).

No new `DecisionTier` value is required — shadow matches still return
`DecisionTier.Rules` from the shadow catalog; the wire-level effect
(no action executed) comes from the existing `shadowMode` branch.

---

## 9. Test plan (TDD — 80%+ coverage)

### 9.1 Contracts tests (`tests/SuavoAgent.Contracts.Tests/Learning/`)

- `WorkflowTemplateJsonTests` — round-trip camelCase JSON.
- `ElementSignatureEqualityTests` — `AutomationId` case-insensitive;
  `DepthDelta` null vs 0 equivalence documented.
- `PmsVersionFingerprintMatchTests` — `Contains` semantics for
  `PmsVersionRange`.
- `SchemaAdaptationSigningTests` — canonical-string includes all 8
  fields; flipping any bit invalidates signature.

### 9.2 Extractor tests (`tests/SuavoAgent.Core.Tests/Learning/WorkflowTemplateExtractorTests.cs`)

- **Happy path:** 3-step DFG routine with 10+ observations yields a template
  whose step ordinals match the path order.
- **Writeback:** a step with `correlated_actions.query_is_write=1` results in
  `IsWrite=true` and a non-null `ExpectedAfter`. If `ExpectedAfter` cannot be
  derived (no screen-diff events), template is skipped (fail closed).
- **Degenerate:** routine of length 2 → skipped.
- **Low confidence:** routine confidence 0.3 → skipped.
- **Idempotency:** re-running extractor with same inputs updates
  ObservationCount, leaves TemplateId unchanged.
- **Version bump:** mutating one step → new version, old retired
  with reason `superseded`.
- **Retirement:** 5 consecutive low-conf runs → retired with reason
  `confidence_drop`.

### 9.3 TemplateRuleGenerator tests

- **Round-trip:** emitted YAML parses via `YamlRuleLoader` and produces a
  Rule with `AutonomousOk=false`.
- **Writeback invariant:** TemplateStep with `IsWrite=true` and null
  `ExpectedAfter` → generator throws.
- **Priority:** writeback template → Priority 300; non-writeback → 200.
- **Auto-approval row:** generator upserts `auto_rule_approvals` with
  `status='pending'`; never marks as `approved`.

### 9.4 Schema Canary propagation tests

- **Packager happy path:** canary incident + local override → signed
  `SchemaAdaptation` verifies under shared key.
- **Tokenizer reject:** QueryRewrite with bad SQL → packager throws.
- **Consumer fingerprint mismatch:** receiver with different
  `FromSchemaHash` → adaptation skipped, audit written.
- **Consumer signature failure:** flipped bit → adaptation rejected.
- **Apply + revert:** adaptation applied, canary still in warning →
  rewrite rolled back.

### 9.5 Spec-D template transfer tests

- **Apply template seed:** valid seeded template → template row in DB
  with `ExtractedBy = "seed:<digest>"`, rule generated with
  `autonomousOk=false`.
- **Range mismatch reject:** local fingerprint outside `PmsVersionRange` →
  `RejectSeedItem` recorded, no template persisted.
- **Unknown query shape reject:** template references a query shape not
  locally applied → rejected.
- **Fleet confidence feedback:** shadow match/mismatch recorded as
  `feedback_events` with correct `target_type`/`directive_type`.

### 9.6 LearningWorker wiring integration test

- Pattern phase with seeded routines + correlations → WorkflowTemplate
  persisted + YAML emitted + `auto_rule_approvals` pending row.

### 9.7 RuleEngine shadow catalog test

- `status='shadow'` rules do not match in normal evaluation;
  match in shadow evaluation; increment `shadow_runs`.

### 9.8 Coverage target

- New code files: 80%+ line coverage (contract classes excluded from
  coverage gate — pure records). Existing code unchanged.

---

## 10. Rollout

- v3.12.0: contracts + extractor + generator + schema adaptation consumer
  + Spec-D template transfer. `SchemaAdaptationWorker` shipped but disabled
  by default (`FleetFeatures:SchemaAdaptation=false`).
- v3.12.1: flip the worker on in dev / pilot.
- v3.12.2: auto-generated rules actually feed `shadowMode=true` via
  TieredBrain; operator UI for flipping approvals ships in the pharmacy
  portal (separate web PR).

---

## 12. Codex review resolution (session 019da827-26b7-7252-8358-af9642d31d01)

| Area | Verdict | Resolution |
|---|---|---|
| 1. Contract safety | WARN | §2.2.1 — closed `KeyHint` type replaces open `KeyHints` dictionary. |
| 2. Writeback safety gates | **BLOCK** | §1.2 gate 3 rewritten. §2.2 adds `ElementFingerprints` to `RulePredicate` + `RuleContext`. §2.3 adds `VerifyBefore` obligation for writeback steps. §4.2 generator enforces. |
| 3. Schema adaptation signing | WARN | §2.5 defines `SchemaAdaptationCanonicalV1` explicitly; no more reuse claim on `SignedCommand`. Separate `adapt-v1` key confirmed. |
| 4. Spec-D template transfer | PASS | Inherited — `SeedClient` already uses `PostSignedVerifiedAsync` (§6 unchanged). |
| 5. Storage migration risk | WARN | §3.4 introduces `schema_migrations` + transactional `MigrationRunner`. Existing `TryAlter` calls unchanged for backward compat. |
| 6. Extractor idempotency | WARN | §2.4 adds `StepsHash` and `CanonicalizeSteps`; §3.3 updated. |
| 7. Fleet canary recall | WARN | §2.6 introduces `AdaptationRevocation` + denylist. §5.4.1 (new) wires revocation. |
| 8. Patent claim strength | WARN | §1 reframed as "positioning hypothesis". Filing decisions gated on external prior-art memo. |

## 13. Open questions (deferred to future spec iteration)

1. Should the 10-shadow-run approval threshold scale with writeback risk
   (e.g. 10 for read-only, 50 for writeback)? Default: yes, parameterise
   in v3.12.1 after one real-world pilot.
2. Is `workflow_templates` the right storage locus or should templates
   live in the `learned_routines` table with a type discriminator?
   Default: separate table — templates have a genuinely different
   lifecycle (version, retirement, fleet confidence).
3. Do we eventually expose an autonomous executor in v3.12.x? Only after
   (a) `ElementFingerprints` ship + (b) executor contract for pre-action
   re-verify lands + (c) external prior-art memo for patent filing.
