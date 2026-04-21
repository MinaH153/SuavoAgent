# SuavoAgent Self-Healing — Event Registry (v0.1)

> The canonical registry of every event type the system emits. Every event
> recorded to the audit chain must reference a type defined here. New event
> types require PR + update to this doc.

**Locked date:** 2026-04-21
**Status:** v0.1 draft
**Depends on:** `audit-schema.md` (§Event types)

---

## How to read this registry

Event types use dotted hierarchical notation: `<domain>.<sub>.<action>`.

Each entry documents:
- **Name** — the `type` string in audit_events rows
- **Category** — the taxonomy axis for reporting (install/runtime/diagnosis/remediation/governance/security/compliance/ops)
- **Severity** — info/warn/error/critical
- **Actor type** — who emits it (operator/agent/cloud_dispatcher/system)
- **Payload shape** — TypeScript-style schema of the `payload` column
- **Redaction notes** — any fields needing PHI redaction review

---

## `chain.*` — Chain integrity events

### `chain.genesis`
- Category: `install`
- Severity: `info`
- Actor: `system`
- Payload: `{pharmacy_salt_hash: string, chain_version: string, created_by: string}`
- Notes: First event in every pharmacy chain. `sequence=0`, `prev_hash=GENESIS`.

### `chain.verification_completed`
- Category: `compliance`
- Severity: `info`
- Actor: `system`
- Payload: `{events_verified: number, last_sequence: number, duration_ms: number, status: 'verified'|'tampered'}`
- Notes: Emitted by nightly verify sweep. `tampered` status triggers `invariant.violated`.

### `chain.retention_purge`
- Category: `compliance`
- Severity: `info`
- Actor: `system`
- Payload: `{events_purged: number, purge_range_start: string, purge_range_end: string, authorized_by: string[]}`
- Notes: Cryptographic deletion event — payload replaced with tombstone, hash preserved.

---

## `agent.*` — Agent lifecycle

### `agent.started`
- Category: `runtime`
- Severity: `info`
- Actor: `agent`
- Payload: `{version: string, services: string[], process_id: number, uptime_ms: 0}`

### `agent.stopped`
- Category: `runtime`
- Severity: `info`
- Actor: `agent`
- Payload: `{version: string, uptime_ms: number, reason: 'shutdown'|'update'|'repair'}`

### `agent.crashed`
- Category: `runtime`
- Severity: `error`
- Actor: `agent`
- Payload: `{version: string, process: string, exception_type: string, crash_log_id?: string}`
- Notes: `crash_log_id` references a pre-uploaded crash log in the audit chain.

---

## `service.*` — Windows service events (Watchdog + Setup)

### `service.restarted`
- Category: `remediation`
- Severity: `info`
- Actor: `agent`
- Payload: `{service_name: string, reason: 'watchdog_auto_restart'|'operator_command'|'sc_failure_action', final_state: 'running'|'failed'}`

### `service.failed`
- Category: `runtime`
- Severity: `error`
- Actor: `agent`
- Payload: `{service_name: string, exit_code: number, last_error: string, consecutive_failures: number}`

### `service.healthy`
- Category: `runtime`
- Severity: `info`
- Actor: `agent`
- Payload: `{service_name: string, recovery_time_ms: number}`

---

## `heartbeat.*` — Heartbeat + freshness

### `heartbeat.emitted`
- Category: `runtime`
- Severity: `info`
- Actor: `agent`
- Payload: `{stats: {cpu_pct: number, memory_mb: number, services_running: string[]}}`
- Notes: Emitted every 30s. High-frequency — consider aggregate indexing.

### `heartbeat.silent_alarm`
- Category: `security`
- Severity: `warn` (→ `error` at 30 min → `critical` at 24 h)
- Actor: `cloud_dispatcher`
- Payload: `{silent_duration_seconds: number, last_heartbeat_at: string, escalation_tier: number, notification_channels: string[]}`

---

## `config.*` — Configuration changes

### `config.override_applied`
- Category: `remediation`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{config_path: string, config_value_hash: string, previous_value_hash: string, applied_by_operator: string}`
- Notes: Value itself is NOT in payload (may be sensitive); hash is used.

### `config.rollback_executed`
- Category: `remediation`
- Severity: `info`
- Actor: `agent`
- Payload: `{config_path: string, restored_to_hash: string, trigger: string}`

---

## `attestation.*` — Binary integrity

### `attestation.verified`
- Category: `security`
- Severity: `info`
- Actor: `agent`
- Payload: `{manifest_version: string, file_count: number, verify_duration_ms: number}`

### `attestation.mismatch`
- Category: `security`
- Severity: `critical`
- Actor: `agent`
- Payload: `{expected_manifest: string, mismatched_files: string[], operator_alerted: boolean}`
- Notes: Halts all mutation verbs. See `invariant.violated` escalation.

---

## `diagnosis.*` — L1 Dispatch (Phase C)

### `diagnosis.requested`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{trigger: string, scope: 'single_pharmacy'|'fleet_wide', correlation_id: string}`

### `scout.dispatched`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{scout_type: string, model: string, parent_correlation_id: string}`

### `scout.returned`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{scout_type: string, observations_count: number, duration_ms: number, token_cost: number}`

### `scout.timeout`
- Category: `diagnosis`
- Severity: `warn`
- Actor: `cloud_dispatcher`
- Payload: `{scout_type: string, timeout_ms: number}`

### `diagnosis.synthesized`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{top_hypothesis: string, confidence: number, ranked_hypotheses: string[], evidence_urls: string[]}`

### `hypothesis.rejected_by_charter`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{rejected_hypothesis: string, mission_charter_version: string, constraint_violated: string}`

---

## `verb.*` — L2 Action Grammar (Phase D)

### `verb.proposed`
- Category: `remediation`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{verb_name: string, verb_version: string, schema_hash: string, parameters_hash: string}`

### `verb.policy_evaluated`
- Category: `governance`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{verb_name: string, cedar_decision: 'allow'|'deny', risk_tier: string, policy_version: string}`

### `verb.approved` / `verb.rejected`
- Category: `governance`
- Severity: `info`
- Actor: `operator`
- Payload: `{verb_name: string, operator_id: string, reason?: string, mfa_verified: boolean}`

### `verb.signed`
- Category: `security`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{verb_name: string, key_id: string, fence_id: string, expires_at: string}`

### `verb.dispatched`
- Category: `remediation`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{verb_name: string, agent_id: string, invocation_id: string}`

### `verb.executed`
- Category: `remediation`
- Severity: `info`
- Actor: `agent`
- Payload: `{invocation_id: string, output_hash: string, duration_ms: number, rollback_envelope_id: string}`

### `verb.verified`
- Category: `remediation`
- Severity: `info`
- Actor: `agent`
- Payload: `{invocation_id: string, postconditions_passed: boolean}`

### `verb.failed` / `verb.rolled_back`
- Category: `remediation`
- Severity: `error`
- Actor: `agent`
- Payload: `{invocation_id: string, stage: 'pre'|'execute'|'post', error: string}`

### `verb.rollback_captured`
- Category: `remediation`
- Severity: `info`
- Actor: `agent`
- Payload: `{envelope_id: string, inverse_action_type: string, evidence_hash: string, max_inverse_duration_ms: number}`

### `grammar.version_mismatch`
- Category: `security`
- Severity: `critical`
- Actor: `agent`
- Payload: `{expected_schema_hash: string, got_schema_hash: string, verb_name: string, verb_version: string}`

---

## `plan.*` — L3 Plan-Review (Phase E)

### `plan.drafted` / `plan.reviewed` / `plan.approved` / `plan.rejected`
- Category: `governance`
- Severity: `info`
- Actor: `operator` | `cloud_dispatcher`
- Payload: `{plan_id: string, step_count: number, total_blast_radius_dollars: number, approver_id?: string}`

### `plan.step_executed` / `plan.step_failed` / `plan.compensated` / `plan.completed`
- Category: `remediation`
- Severity: varies
- Actor: `agent`
- Payload: `{plan_id: string, step_index: number, step_verb: string, result: string}`

---

## `autonomy.*` — Phase F

### `autonomy.granted` / `autonomy.revoked`
- Category: `governance`
- Severity: `info`
- Actor: `operator`
- Payload: `{pharmacy_id: string, verb_class: string, grant_expires_at?: string, rate_limit_per_hour?: number}`

### `autonomy.threshold_reached`
- Category: `governance`
- Severity: `warn`
- Actor: `cloud_dispatcher`
- Payload: `{pharmacy_id: string, verb_class: string, rate_this_hour: number, limit: number}`

### `retrospective.proposed_rule`
- Category: `governance`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{rule_type: string, rationale_summary: string, supporting_incidents: string[]}`

### `retrospective.proposal_approved` / `retrospective.proposal_rejected`
- Category: `governance`
- Severity: `info`
- Actor: `operator`
- Payload: `{proposal_id: string, operator_id: string, reason?: string}`

---

## `consent.*` — HIPAA consent tracking

### `consent.requested` / `consent.granted` / `consent.expired` / `consent.revoked`
- Category: `compliance`
- Severity: `info`
- Actor: `operator` | `system`
- Payload: `{action_id: string, operator_id: string, scope: string, expires_at?: string}`

### `baa.amendment_applied` / `baa.amendment_reverted`
- Category: `compliance`
- Severity: `info`
- Actor: `operator`
- Payload: `{amendment_id: string, effective_at: string, enables_verbs: string[]}`

---

## `key.*` — Key lifecycle events

### `key.rotated`
- Category: `security`
- Severity: `info`
- Actor: `system`
- Payload: `{key_type: string, key_id_old: string, key_id_new: string, rotation_trigger: 'scheduled'|'operator'|'compromise'}`

### `key.revoked`
- Category: `security`
- Severity: `warn`
- Actor: `system`
- Payload: `{key_type: string, key_id: string, revocation_reason: string}`

### `key.suspected_compromise`
- Category: `security`
- Severity: `critical`
- Actor: `agent` | `cloud_dispatcher`
- Payload: `{key_type: string, key_id: string, detection_signal: string}`

---

## `kill_switch.*`

### `kill_switch.triggered`
- Category: `security`
- Severity: `critical`
- Actor: `operator` | `system`
- Payload: `{scope: 'pharmacy'|'fleet', pharmacy_id?: string, trigger_reason: string, triggered_by: string}`

### `kill_switch.cleared`
- Category: `security`
- Severity: `info`
- Actor: `operator`
- Payload: `{scope: string, cleared_by: string, justification: string}`

---

## `invariant.*`

### `invariant.violated`
- Category: `security`
- Severity: `critical`
- Actor: `system`
- Payload: `{invariant_id: string, detection_source: string, evidence_event_ids: string[], operator_alerted: boolean}`
- Notes: Always triggers immediate Security Officer notification. Post-mortem required.

### `invariant.violation_resolved`
- Category: `security`
- Severity: `info`
- Actor: `operator`
- Payload: `{invariant_id: string, resolution_summary: string, postmortem_url: string}`

---

## `fed_mesh.*` — Federated Failure Intelligence (Phase G)

### `signature.emitted`
- Category: `diagnosis`
- Severity: `info`
- Actor: `agent`
- Payload: `{signature_hash: string, signature_version: string, dp_budget_consumed: number}`

### `signature.pattern_match`
- Category: `diagnosis`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{match_count: number, cached_playbook_id: string, confidence: number}`

### `fed_mesh.privacy_budget_consumed`
- Category: `compliance`
- Severity: `info`
- Actor: `cloud_dispatcher`
- Payload: `{pharmacy_id: string, period: string, consumed_epsilon: number, remaining_epsilon: number}`

### `fed_mesh.budget_exhausted`
- Category: `compliance`
- Severity: `warn`
- Actor: `cloud_dispatcher`
- Payload: `{pharmacy_id: string, period: string, federation_paused_until: string}`

---

## Adding a new event type

1. Open PR touching this file
2. Declare name, category, severity, actor_type, payload schema, redaction notes
3. Register PHI redaction requirements in `redaction-rulesets/vX.Y.Z.yaml` if payload has free-text
4. Update `src/SuavoAgent.Contracts/Events/` with TypeScript-typed event class
5. Add unit tests for emission + redaction pass
6. Add RLS test if event is pharmacy-scoped
7. Reviewer checks: is this event needed for some invariant? If not → reject

## Change log

- **2026-04-21 v0.1** — Initial draft. 40+ event types across 11 domains. Locks to v1.0 post-Nadim.
