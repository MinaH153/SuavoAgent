# SuavoAgent Self-Healing — Invariants (v0.1)

> **Invariants are the things this system MUST guarantee under all conditions.**
> If any code, deployment, or policy change could plausibly violate an invariant,
> it MUST be rejected at review. This document is the contract between Suavo,
> our pharmacy customers, and HIPAA regulators.

**Locked date:** 2026-04-21
**Status:** v0.1 draft — locks to v1.0 after Saturday Nadim pilot post-mortem + Codex review
**Change control:** Breaking changes require PR + Joshua approval + designated Security Officer approval

---

## I.0 Invariants are upstream of everything

Before writing a dashboard, before shipping a verb, before pushing a policy: confirm every user-facing feature maps back to an invariant below. If it doesn't, it's either chrome or an undocumented risk.

**Enforcement mechanism:** every PR that touches `src/` or `docs/self-healing/` requires an "Invariants impacted" section in the description. Reviewers reject PRs missing it.

---

## I.1 PHI minimization

### I.1.1 No raw PHI in model prompts
No field classified as PHI-Direct (see §I.4) shall ever appear in a Claude API
call, MCP tool invocation argument, scout observation payload, diagnosis
evidence string, or LLM-as-judge input.

**Enforcement:** `ComplianceBoundary.Validate()` already runs on every
heartbeat + signals payload. Extend to every outbound cloud call, client-side,
before transmission. Fail closed: if redaction cannot be verified, the request
is dropped and an alarm fires.

### I.1.2 Context Assembler whitelist
The Mission Loop Context Assembler (see `suavoagent-mission-loop-architecture.md`)
can include ONLY these field categories:

- Pharmacy identifier (salted hash, never raw NPI / license)
- Service state (RUNNING/STOPPED/etc.)
- Schema hash (SHA-256 of PMS schema; never column contents)
- Version strings (agent, PMS, Windows)
- Count aggregates (e.g., "3 failed queries in last hour" — NEVER the query text)
- Event category enum (from taxonomy; no free-text)
- Verb invocation IDs (UUID; no parameter values)
- Past incident signatures (anonymized, see Phase G)

**Forbidden in context:** patient names, medication names, Rx numbers, diagnosis
codes, dates of service, any SQL query result contents, any UI screenshot
contents, any file paths containing identifying strings.

### I.1.3 Per-pharmacy salt
Every pharmacy identifier that crosses the agent/cloud boundary is salted with
a per-pharmacy SHA-256 salt. Salts are generated at install, stored in cloud
next to the pharmacy record, never transmitted back to the agent.

**Purpose:** prevents cross-pharmacy correlation in the federated failure
intelligence mesh (Phase G). If a signature leaks from pharmacy A, it cannot be
used to reconstruct pharmacy B's state.

### I.1.4 Redaction audit
Redaction coverage is itself auditable. Every outbound prompt emits an event:

```json
{
  "type": "outbound_llm_call",
  "redaction_ruleset_version": "v1.2.0",
  "input_field_count": 12,
  "redacted_field_count": 4,
  "violations": []
}
```

If `violations` is ever non-empty, the call is dropped AND an alarm fires to the
Security Officer. Zero violations is non-negotiable.

### I.1.5 Model provider BAA
Anthropic has a BAA with us. Only models/endpoints covered by that BAA may
receive any data that could even theoretically contain residual PHI post-redaction.
Non-BAA-covered endpoints (experimental, preview, third-party OpenAI/Google)
are structurally rejected by the dispatcher.

---

## I.2 BAA scope enforcement

### I.2.1 Every verb carries a BAA flag
Every typed verb in the action grammar declares its BAA-coverage requirement:

- `none` — verb touches only infrastructure (service restart, config write). No BAA interaction.
- `agent-baa` — verb operates under the standard SuavoAgent BAA with the pharmacy.
- `agent-baa-amendment:<amendment-id>` — verb requires a specific BAA amendment to be in force.
- `forbidden` — verb may never be executed against this pharmacy (regulatory/contractual block).

### I.2.2 Structural rejection
A verb whose required BAA scope is not currently in force against the target
pharmacy MUST be rejected by the agent-side `IVerb` contract evaluator BEFORE
the operator approval gate sees it.

**Rationale:** if BAA-scope is only checked at approval time, an operator who
doesn't understand the BAA terms could approve a verb that violates our legal
contract. Enforcement must be structural.

### I.2.3 BAA state is queryable by pharmacy
Every pharmacy can fetch its own BAA state (which amendments are in force, which
verbs are therefore enabled) from a public endpoint with their API key. Builds
operator trust AND is a HIPAA §164.314 workforce-transparency artifact.

### I.2.4 BAA changes propagate via canary
A BAA amendment that enables new verbs is NEVER applied fleet-wide at once.
Rollout: 1 pilot pharmacy → 5% → 25% → 100% with 48-hour soak at each tier.
Amendment reversion is a one-button operation and bypasses the soak (safety
unwinds faster than it locks).

---

## I.3 Authentication, authorization, attribution

### I.3.1 Every action is attributable
Every verb invocation, every cloud API call, every config change, every
heartbeat MUST trace to:

- A pharmacy ID (salted)
- An agent key ID (per-pharmacy)
- An operator identity (for operator-triggered actions)
- A cloud-side dispatcher session ID (for autonomous actions)
- A Mission Charter version (what objectives/constraints were in force)

Events missing any field are rejected at ingest. No "unknown actor" allowed.

### I.3.2 Signed commands are cryptographically verified
All cloud→agent verb invocations MUST be HMAC-SHA256 signed by a per-pharmacy
shared secret. Agent verifies signature before executing. Mismatch = reject +
alarm.

See `key-custody.md` for key lifecycle.

### I.3.3 Operator consent is time-bound and action-scoped
When an operator approves a verb invocation or autonomy grant, consent is
recorded with:

- Operator identity (Supabase auth UUID + session ID)
- Approved verb + scope (pharmacy, verb name, parameter hash)
- Timestamp + expiry (expiry defaults to 30 minutes)
- MFA challenge response hash (future: MFA required for HIGH-risk verbs)

After expiry, consent does not transfer to replay attempts. New invocation =
new consent.

### I.3.4 Anti-spoofing
Consent UI emits a time-bound challenge per action. Operator's approval click
signs the challenge with their session key. Replaying a stale challenge is
structurally rejected. This closes the "operator disputed the approval"
repudiation risk.

---

## I.4 Data classification

Every field in the Suavo system is classified into one of these tiers:

| Tier | Definition | Examples | Model-prompt rules |
|---|---|---|---|
| **Public** | Safe to share publicly | Pharmacy name (if opted in), app version | Allowed raw |
| **Operational** | Non-identifying ops data | Service state, process count, free disk bytes | Allowed raw |
| **PHI-Adjacent** | Could identify a pharmacy but not a patient | NPI, pharmacy address, DEA number | Allowed only with per-pharmacy salt |
| **PHI-Direct** | Identifies a patient or medical event | Patient names, Rx numbers, medication names, diagnoses, dates of service | NEVER in model prompts. Redact before any outbound call. |
| **Secret** | Credentials, keys | SQL passwords, API keys, signing keys | NEVER leaves the agent. Encrypted at rest (DPAPI). Encrypted in transit (TLS 1.3). |

**Field registry:** every field added to any schema (event, verb, config, audit) MUST
be classified in `docs/self-healing/field-registry.md` (to be created during
Phase A). Unclassified fields fail CI.

---

## I.5 Audit trail integrity

### I.5.1 Tamper-evident
See `audit-schema.md`. The audit chain uses SHA-256 hash linking per-pharmacy. Any modification to any historical entry breaks the chain and is detected by nightly verification sweeps.

### I.5.2 Retention
6 years minimum from the later of (date of creation) and (date of last
relevant activity). HIPAA §164.312(b) floor. Suavo policy: 7 years (one year
above floor to avoid edge-case disputes).

### I.5.3 Offsite copy
Nightly digest written to S3 Object Lock in compliance mode (AWS account
owned by MKM Technologies LLC, not any third-party vendor). WORM-protected.
Deletion requires both Joshua and designated Security Officer.

### I.5.4 Per-pharmacy isolation
Each pharmacy's audit chain is independent. A compromise of one pharmacy's
signing key cannot forge entries in another pharmacy's chain.

### I.5.5 External verifiability
Every pharmacy can fetch its own audit chain (HMAC-authenticated download) and
verify the hash chain independently against an open-source verifier we publish
at `github.com/MinaH153/SuavoAgent-AuditVerifier`. Pharmacies don't have to
trust us — they can prove the chain to themselves.

---

## I.6 Kill switch + safety

### I.6.1 Per-pharmacy kill switch
Every pharmacy has a **big-red-button** in the fleet portal that:

- Disables all autonomous remediation for that pharmacy in <5 seconds
- Drains in-flight verb invocations (does not just cancel; waits for safe
  rollback point)
- Requires operator affirmative re-enable to resume

Implementation: cloud-side kill fence ID. Every verb invocation carries the
fence ID it was signed against. If fence ID is invalidated, agent refuses to
execute (even if HMAC signature is valid).

### I.6.2 Fleet-wide emergency stop
Joshua + designated Security Officer both hold an emergency-stop credential that
halts ALL autonomous verbs across the entire fleet with a single command. Used
exactly once per incident. Logged + post-mortem required.

### I.6.3 Soft-stop vs hard-stop
- **Soft-stop**: no new verbs dispatched; in-flight verbs complete normally. Use for known non-urgent issues.
- **Hard-stop**: in-flight verbs roll back at next safe point; agent enters observation-only mode. Use for suspected breach, data leak, or compliance incident.

Both require audit trail entry with reason + authorizing operator.

### I.6.4 Dead-man switch
If agent loses contact with cloud for >30 minutes, agent enters read-only mode
automatically. Resumes only on successful cloud handshake + fresh fence ID.
Prevents the "cloud is compromised, agent keeps executing commands" scenario.

---

## I.7 Cross-pharmacy isolation

### I.7.1 No cross-tenant data flow
A signature, diagnosis, plan, or verb invocation generated in the context of
pharmacy A MUST NOT ever appear in pharmacy B's context, audit chain, or
visible surface. The federated failure intelligence mesh (Phase G) is an
EXCEPTION with explicit anonymization guarantees — see Phase G design for
boundary conditions.

### I.7.2 Key namespace isolation
Every signing key is scoped to a (pharmacy, environment, purpose) triple.
No key is ever valid across two pharmacies.

### I.7.3 Cloud-side RLS
Every Supabase table with multi-tenant data has an RLS policy. Policies are
code-reviewed by Joshua. Bypassing RLS via service role requires an audit
entry with justification.

### I.7.4 Data export boundary
Any cross-pharmacy aggregation (e.g., "fleet-wide schema drift report") must
output only counts + percentages, never per-pharmacy identifiers, and must be
explicitly marked as cross-tenant in the audit trail.

---

## I.8 Mission Charter enforcement

### I.8.1 Charter is machine-verifiable
See `suavoagent-mission-loop-architecture.md`. The Mission Charter compiles
into typed verb preconditions. LLM reasoning CANNOT bypass a machine-verifiable
constraint via clever framing.

### I.8.2 Charter changes require operator signoff
A pharmacy's Charter can only be modified by the authorized pharmacy operator
(not by Suavo staff, not by the agent, not by Claude). Changes go through a
PR-like flow with audit trail entry.

### I.8.3 Charter drift detection
The Mission Drift Detector continuously monitors action patterns against the
Charter. Drift > threshold → soft-stop + operator alert.

---

## I.9 Code-level hard invariants

### I.9.1 No raw shell from cloud to agent
The agent-side code MUST NOT expose a "run arbitrary command" verb. Every
executable action is a typed verb with pre/post-condition contracts and
rollback envelopes.

### I.9.2 No direct SQL writeback without verb
The agent MUST NOT issue any SQL `INSERT`/`UPDATE`/`DELETE` statements outside
of a typed `pioneerrx_writeback_*` verb. All writes go through the verb
contract for audit + approval.

### I.9.3 No config mutation without signed command
The agent config file (`appsettings.json`) is read-only at runtime. Mutations
arrive via signed `apply_config_override` verb with audit + rollback envelope.

### I.9.4 No PMS binary modification
The agent NEVER modifies any PMS binaries, DLLs, or config files. This is a
hard violation of the pharmacy's contract with PioneerRx and is structurally
prevented by the action grammar (no verb exists for it).

### I.9.5 No privilege escalation
The agent does not use `runas`, `psexec`, `Invoke-Command -Credential`, or any
other privilege-escalation primitive at runtime. Runtime privilege level is set
at install (LocalService for Core, NetworkService for Broker, LocalSystem for
Watchdog) and never changes.

---

## I.10 Self-improvement constraints

### I.10.1 Never autonomous
All rule changes proposed by the Retrospective Learner MUST be surfaced as
operator-approvable PRs. Auto-merging rule changes = NEVER.

### I.10.2 Cache entries have validity envelopes
Every Pattern Cache entry carries a (PMS version range, agent version range,
Windows version range) envelope. Expires on drift. Periodic re-validation
sweeps verify cached playbooks still work.

### I.10.3 Training on failure data is scoped
Failure signatures used for federated learning are bounded by differential
privacy budget. Budget is tracked per pharmacy and per fleet-week. Exceeded
budget → federated learning pauses; operator alert.

---

## I.11 Violation handling

If any invariant in this document is detected to have been violated (during
operation, during review, during audit):

1. **Immediate soft-stop** of all autonomous verbs on affected pharmacies
2. **Audit trail entry** classifying the violation with severity
3. **Notification** to Joshua + designated Security Officer within 5 minutes
4. **Post-mortem** required within 7 days; root cause + corrective action
5. **Sanctions policy** integration (per HIPAA §164.308): agent-caused violation
   triggers the same workflow as a workforce member violation
6. **Customer notification** per BAA terms if PHI is potentially involved

No violation is "minor." No violation is "just this once."

---

## Change log

- **2026-04-21 v0.1** — Initial draft. Locks to v1.0 after Nadim pilot + Codex review.
