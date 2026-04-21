# SuavoAgent Self-Healing — Field Registry (v0.1)

> Every field used in any cloud-facing event, verb, audit entry, config, or
> plan MUST be registered here with its data classification tier
> (Public / Operational / PHI-Adjacent / PHI-Direct / Secret) per
> `invariants.md §I.4`. Unregistered fields are rejected at ingest.

**Locked date:** 2026-04-21
**Status:** v0.1 draft

---

## Classification tiers (recap)

| Tier | Model-prompt rules |
|---|---|
| **Public** | Allowed raw |
| **Operational** | Allowed raw |
| **PHI-Adjacent** | Allowed ONLY with per-pharmacy salt |
| **PHI-Direct** | NEVER in model prompts. Redact before any outbound call. |
| **Secret** | NEVER leaves the agent. Encrypted at rest + in transit. |

See `invariants.md §I.4` for authoritative definitions.

---

## Fields by classification tier

### Public

| Field | Type | Source | Where used |
|---|---|---|---|
| `agent.version` | string | agent | heartbeat, events |
| `pms.type` | string (enum) | agent | heartbeat, diagnosis |
| `pms.version` | string | agent | heartbeat, diagnosis |
| `os.version` | string | agent | heartbeat, diagnosis |
| `suavo.version` | string | agent/cloud | events |
| `release.tag` | string | cloud | events |

### Operational

| Field | Type | Source | Where used |
|---|---|---|---|
| `service.name` | string (enum SuavoAgent.Core/Broker/Watchdog) | agent | service events |
| `service.state` | string (enum RUNNING/STOPPED/etc.) | agent | service events |
| `service.uptime_ms` | number | agent | events |
| `process.memory_mb` | number | agent | heartbeat |
| `process.cpu_pct` | number | agent | heartbeat |
| `machine_name` | string | agent | diagnosis |
| `disk.free_bytes` | number | agent | heartbeat |
| `schema.hash` | string (SHA-256 hex) | agent | canary, diagnosis |
| `config.hash` | string (SHA-256 hex) | agent/cloud | config events |
| `binary.hash` | string (SHA-256 hex) | agent/cloud | attestation |
| `verb.invocation_id` | UUID | cloud | verb events |
| `plan.id` | UUID | cloud | plan events |
| `correlation_id` | UUID | cloud | diagnosis, remediation |
| `fence_id` | UUID | cloud | verb signing |
| `signature_hash` | string (SHA-256 hex) | agent | fed mesh |
| `count.*` | number | agent | aggregates (heartbeat.stats, etc.) |
| `duration_ms` | number | agent/cloud | various |
| `event_category` | string (enum) | both | all events |

### PHI-Adjacent (salted before emission)

| Field | Type | Source | Where used |
|---|---|---|---|
| `pharmacy_id` | string (salted hash of UUID) | cloud→agent | all events |
| `pharmacy.npi` | string (salted hash) | cloud | rarely, registry only |
| `pharmacy.dea` | string (salted hash) | cloud | rarely, registry only |
| `pharmacy.address_hash` | string (SHA-256 of address+salt) | cloud | registry only, never outbound |
| `pharmacy.emergency_phone_hash` | string (SHA-256 of phone+salt) | cloud | Twilio alarm routing only |
| `pharmacy_salt_hash` | string | cloud | genesis event only |

### PHI-Direct (NEVER in cloud events)

| Field | Type | Source | Exists in |
|---|---|---|---|
| Patient name | string | on-agent PMS | local state DB only |
| Patient DOB | date | on-agent PMS | local state DB only |
| Rx number | string | on-agent PMS | local state DB only |
| Medication name | string | on-agent PMS | local state DB only |
| NDC code (linked to Rx) | string | on-agent PMS | local state DB only |
| Diagnosis code | string | on-agent PMS | local state DB only |
| Prescriber name | string | on-agent PMS | local state DB only |
| SQL query text | string | on-agent PMS query | agent only |
| SQL query result rows | records | on-agent PMS query | agent only |
| UI screenshot | bytes | on-agent UIA | encrypted on-device only |
| File paths containing identifying strings | string | various | redacted before upload |

### Secret (NEVER leaves agent or cloud KMS)

| Field | Type | Source | Storage |
|---|---|---|---|
| SQL password | string | on-agent config | DPAPI-encrypted on agent |
| API key (agent-side) | string | on-agent config | DPAPI-encrypted on agent |
| API key hash | string (SHA-256) | cloud | Supabase (hash only) |
| Agent signing key | HMAC key | cloud-generated | DPAPI on agent, KMS-envelope in cloud |
| Cloud command key | HMAC key | cloud-generated | DPAPI on agent, KMS-envelope in cloud |
| Release signing key | ECDSA P-256 private | AWS KMS | KMS only (hardware-backed) |
| EV code signing cert | RSA-3072 private | Yubikey FIPS | Hardware token only |

---

## Field path addressing

Events reference fields via dotted paths starting from `payload`:

```
payload.service.name         → Operational
payload.pharmacy_id          → PHI-Adjacent (must be pre-salted)
payload.diagnosis.top_hypothesis.confidence  → Operational
```

Redaction ruleset (`redaction-rulesets/v1.0.0.yaml`) uses these paths in
`exempt_field_paths` entries.

---

## Adding a field

1. Open PR touching this file + any affected event/verb schema
2. Classify the field into one of the 5 tiers above
3. Add to the table under that tier
4. Reviewer checks:
   - Is the classification tight? (PHI-Adjacent when Operational suffices = over-restriction)
   - Is the classification safe? (Operational when actually PHI-Adjacent = under-restriction)
   - Does the field path align with the redaction ruleset?
5. If classification is PHI-Direct → the field must NEVER be added to an outbound schema. It can only exist in agent-local SQLite.
6. CI check: reject PRs that reference a field not in this registry.

---

## Change log

- **2026-04-21 v0.1** — Initial draft with field inventory from existing events + data model. Locks to v1.0 post-Nadim.
