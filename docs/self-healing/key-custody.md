# SuavoAgent Self-Healing — Key Custody (v0.1)

> Every "signed" claim in this architecture is only as strong as the key that
> signs it. This document is the complete lifecycle of every signing key,
> rotation protocol, revocation procedure, and compromise response. Skim this
> and you will make mistakes that cost us customers and patents.

**Locked date:** 2026-04-21
**Status:** v0.1 draft
**Depends on:** `invariants.md` (§I.3 Authentication, authorization, attribution)

---

## Key taxonomy

Six distinct key types. Each has a separate lifecycle, storage, and scope.

| Key | Purpose | Algorithm | Scope | Lifetime |
|---|---|---|---|---|
| **Agent signing key** | Agent-side HMAC signing of audit events + heartbeats. Cloud verifies. | HMAC-SHA256 | Per-pharmacy | 90 days, rotate |
| **Cloud command key** | Cloud signs verb invocations. Agent verifies before executing. | HMAC-SHA256 | Per-pharmacy | 90 days, rotate |
| **Release signing key (ECDSA)** | Signs agent binary releases + update manifests. | ECDSA P-256 | Suavo fleet-wide | 2 years, hardware-token |
| **Code signing cert (EV)** | Signs SuavoSetup.exe + agent binaries for SmartScreen. | RSA-3072 w/ FIPS 140-2 HSM | Suavo fleet-wide | 1 year, SSL.com EV |
| **Audit digest signing key** | Signs daily S3 Object Lock digests. | Ed25519 | Suavo fleet-wide | 2 years, AWS KMS |
| **Daily digest manifest key** | Signs per-day manifest before S3 upload. | Ed25519 | Suavo fleet-wide | Same as above |

**Key insight (Codex review):** "signed" is only as strong as key custody. Every
key below has an explicit lifecycle. Every lifecycle has rotation triggers,
revocation triggers, and compromise response.

---

## Per-pharmacy agent + cloud keys

### Agent signing key

**Purpose:** Every outbound call from agent to cloud (audit events, heartbeats,
diagnosis observations) is HMAC-signed with this key. Cloud verifies before
ingesting.

**Storage on agent:** DPAPI-encrypted blob in `%ProgramData%\SuavoAgent\keys\agent-signing.key.dpapi`. Machine scope (not user scope), so LocalService can read.

**Storage on cloud:** Supabase `pharmacy_keys` table, encrypted with envelope
encryption via AWS KMS. `agent_signing_key_encrypted` column; plaintext never
in Postgres.

**Generation at install:** During bootstrap.ps1, agent generates a 256-bit
key from `RNGCryptoServiceProvider`, immediately DPAPI-encrypts, and sends the
plaintext to cloud over TLS 1.3 inside the `/api/agent/register` payload. Cloud
envelope-encrypts with KMS and stores. Plaintext is zeroed in memory on both
sides after storage.

**Rotation:** Every 90 days OR on any of the following triggers:
- Agent reports `key.suspected_compromise` event
- Cloud detects signature verification anomaly (malformed, replay, time-skew > 5 min)
- Pharmacy operator requests rotation via fleet portal
- Security Officer initiates fleet-wide rotation

**Rotation protocol:**
1. Cloud generates replacement key, envelope-encrypts, stores as `pending`
2. Cloud sends to agent via existing command channel (signed with old key)
3. Agent receives, DPAPI-encrypts, stores as `pending`
4. Agent's next heartbeat signs with BOTH old and new key (dual-sign window)
5. Cloud verifies with old, checks new-sig presence, marks new as `active`,
   old as `deprecated`
6. 24-hour grace period where cloud accepts both signatures
7. After grace: old key zeroed on both sides, `active` is the only accepted key

### Cloud command key

**Purpose:** Every cloud→agent verb invocation is HMAC-signed with this key.
Agent verifies before executing.

Rotation + generation + storage mirror the agent signing key, except direction
is reversed (cloud generates, distributes to agent).

**Why two keys instead of one shared?** Defense in depth. If the agent is
compromised, the attacker has the agent signing key but NOT the cloud command
key, so they cannot forge inbound commands. If cloud is compromised, attacker
has the cloud command key but NOT agent signing key, so they cannot forge
outbound audit entries.

### Fence ID (per-session kill switch)

**Purpose:** Every verb invocation carries a "fence ID" — a UUID that
represents the current kill-switch state. If the fence is invalidated, all
in-flight commands with that ID are rejected by the agent.

**Generation:** Cloud-side, per pharmacy, persisted in `pharmacy_fences` table.
New fence generated on: cloud service restart, operator click of "kill switch"
in fleet portal, Security Officer emergency-stop, or weekly rotation.

**Validation:** Agent checks fence ID in every incoming signed command against
the "current" fence. Mismatch = reject + emit `kill_switch.triggered` event.

---

## Release signing key (ECDSA P-256)

**Purpose:** Signs the `checksums.sha256` file in every GitHub release + signs
OTA update manifests. Agents verify against an embedded public key before
applying updates.

**Current location:** Stored as `SIGNING_KEY_PEM` GitHub Actions secret.
Plaintext PEM, generated once at repo setup, never rotated since.

**Problem with current state:** Plaintext PEM in GitHub secrets is the weakest
link in our chain. If GitHub is compromised OR a privileged repo admin is
compromised OR a supply-chain attack reaches Actions runners, attacker can
sign fake updates.

**Migration plan (Phase A):**
1. Generate new ECDSA P-256 key pair in AWS KMS (hardware-backed)
2. Store public key in agent binary (embedded) and rotation PR distributes
   update to all live agents
3. GitHub Actions calls KMS sign API via OIDC federation (no long-lived secret)
4. Old key: deprecate, keep public key accepted for 90 days, then revoke
5. Old key entry in agent binary removed at v3.15.x

**Rotation:** Every 2 years. Emergency rotation on suspected compromise.

### Embedded public key rotation

The agent binary embeds a TRUSTED public key for verifying release signatures.
Rotating the release signing key requires a signed release signed by the OLD
key that announces the NEW key. Agents apply the announcement, embed the new
key, and future updates are verified against the new key.

**Race condition:** if an attacker compromises the old key and signs a malicious
rotation announcement, they can install their own malicious key. Mitigation:
rotation announcements require DUAL signatures (old key + Security Officer's
offline key stored on a YubiKey at Joshua's residence).

### Emergency rotation (key compromise)
1. Suavo emits a "revocation" message signed by the Security Officer's offline
   YubiKey
2. Agents add the compromised key to an embedded revocation list
3. Future updates signed with the compromised key are rejected
4. New key is rolled out via the dual-signature rotation protocol

### Code signing cert (EV) — SSL.com / FIPS 140-2 Yubikey

**Purpose:** SmartScreen trust. UAC publisher string. Prevents Windows
Defender + user warnings. Different from release signing — this signs the EXE
itself, not the checksums file.

**Current state (2026-04-21):** Ordered from SSL.com. Order ref
`co-861kueeu2a3`. $628 paid. Validation queue. Yubikey ships 2-3 business days
after validation completes. See `mkm-legal-entity.md` + session-2026-04-21
memories for full context.

**Storage:** Exclusively on the Yubikey FIPS 140-2 hardware token. Key
material never exists on any disk.

**Use:** Self-hosted Windows runner (Joshua's Bakersfield PC or dedicated
Parallels VM) with Yubikey plugged in. `signtool.exe sign /fd SHA256 /tr ...
/sha1 $CODESIGN_CERT_THUMBPRINT ...`. GitHub Actions trigger via
`self-hosted` runner label.

**Rotation:** 1-year renewal via SSL.com (or vendor rotation if we migrate).

**Loss recovery:** If Yubikey is lost or damaged, SSL.com issues a replacement
after re-verification. Expect 3-5 business days downtime. During that window,
agent releases are unsigned (beta-flagged on /download).

**Thumbprint embedded in release.yml** (not the key; the thumbprint is public)
lets CI pick the correct cert from the runner's certificate store.

---

## Audit digest keys

### Audit digest signing key (Ed25519, in AWS KMS)

**Purpose:** Signs the daily digest blob before upload to S3 Object Lock.
Lets auditors verify the digest is authentically from Suavo.

**Location:** AWS KMS, asymmetric customer-managed key, `SIGN_VERIFY` usage,
Ed25519 curve. IAM policy: only the `audit-digest-writer` role can sign; only
the `audit-digest-reader` role can verify-via-public-key.

**Rotation:** Every 2 years via KMS key rotation. Previous signatures remain
verifiable against the archived public key.

### Daily manifest key

Same as above, separate role assignment so a compromise of the digest key
doesn't cascade. Redundant, but cheap (KMS keys are $1/month each).

---

## Key compromise response

Every key type has a documented compromise protocol. One sheet here; full
incident runbook in `docs/self-healing/incident-runbooks/key-compromise.md`
(Phase A deliverable).

### Agent signing key compromise
1. Operator or auto-detection fires `key.suspected_compromise` event
2. Cloud immediately rotates the key (new generated, old invalidated)
3. All audit events signed with old key are marked with a suspicion flag
4. Security Officer + Joshua notified within 5 minutes
5. Forensic review: which events might be forged? Cross-reference against
   independent sources (S3 Object Lock digests from before compromise window).
6. Audit trail entry + post-mortem

### Cloud command key compromise
1. Cloud rotates the key; all old-key-signed commands in-flight are aborted
2. Agent rejects any commands signed with old key going forward
3. Same notification + forensic + post-mortem flow

### Release signing key compromise
1. Security Officer emergency-signs a revocation with their offline YubiKey
2. Revocation rolled out to all live agents via an emergency signed release
3. New key generated, distributed
4. All releases between suspected compromise time and revocation re-signed
   with new key
5. If attacker shipped malicious binaries, agents verify against embedded
   revocation list AND blocklist those binary hashes
6. Customer notification per BAA

### Code signing cert (EV) compromise
1. Yubikey lost/stolen → revoke cert via SSL.com within 24 hours
2. If YubiKey was never found and may be compromised: disable all agents from
   downloading new builds until replacement cert arrives
3. Replacement cert issued after re-verification
4. Resume signed releases

### Audit digest key compromise
1. Rotate via KMS
2. Re-sign last 90 days of digests with new key (digests are cheap)
3. Update external verifier's expected public key list
4. Post-mortem

---

## Key storage matrix

| Key | Hot storage | Cold backup | Who has access |
|---|---|---|---|
| Agent signing key (per pharmacy) | DPAPI blob on agent, KMS-encrypted in cloud | S3 Object Lock encrypted export (read-only) | Agent LocalService, cloud service role |
| Cloud command key (per pharmacy) | KMS-encrypted in cloud, DPAPI blob on agent | Same | Cloud dispatcher, agent runtime |
| Release signing key (ECDSA) | AWS KMS (hardware-backed) | AWS KMS region replication | GitHub Actions OIDC role only |
| EV code signing cert | FIPS 140-2 Yubikey (on-person or in safe) | None — replacement via vendor | Joshua, physical possession |
| Security Officer's offline YubiKey | Offline, in safe deposit box | Secondary YubiKey in different location | Security Officer only |
| Audit digest signing key | AWS KMS | KMS region replication | Audit cron service role |
| Daily manifest key | AWS KMS | KMS region replication | Audit cron service role |

**No human ever touches raw private key material for any key in cloud.** Every
cloud key lives in KMS and is used via API signing calls.

**Joshua DOES touch private material for the EV cert via Yubikey** but the key
never leaves the Yubikey.

---

## Environment isolation

Every key is scoped to (environment, purpose, pharmacy). Dev/staging/prod keys
NEVER cross.

- Dev signing keys: random per developer, never leave local machines
- Staging signing keys: in `staging` KMS key ring, separate AWS account
- Prod signing keys: in `prod` KMS key ring, separate AWS account

Cross-environment deployments are blocked at the CI level: the release workflow
refuses to sign with the prod key if `GITHUB_REF` doesn't match
`refs/tags/v*`.

---

## Key ceremony process

### Initial key generation (at service first install or compromise rotation)

**Required participants:** Joshua + Security Officer, both present (video call
acceptable). Two-person rule prevents single-actor compromise.

**Recording:** Every ceremony is recorded (screen + audio) with the recording
itself encrypted + signed + archived in Glacier Deep Archive for 10 years.

**Checklist:**
1. Verify ceremony date, time, participants
2. Confirm environment (dev/staging/prod)
3. Generate key using documented entropy source
4. Split key via Shamir's Secret Sharing if offline (not needed for KMS-resident keys)
5. Verify generation success via test signature
6. Record key identifier + creation time in key registry
7. Rotate any referring configurations
8. Archive ceremony recording

### Rotation (scheduled 90-day / 1-year / 2-year)

Can be done by a single authorized principal (no two-person rule needed for
scheduled rotations). But MUST emit audit event + be verifiable post-hoc.

### Emergency rotation (compromise)

Two-person rule applies. Ceremony recorded.

---

## Key registry

Lives at `docs/self-healing/key-registry.md` (Phase A deliverable). Every key
has an entry:

```markdown
## Key: cloud-command-<pharmacy-hash>

- Purpose: Cloud→agent command signing for pharmacy <hash>
- Algorithm: HMAC-SHA256
- Created: 2026-04-21T12:00:00Z
- Created by: [ceremony-id]
- Current state: active
- Last rotated: 2026-04-21
- Next rotation due: 2026-07-21
- Compromise status: none
- Rotation history: (initial, 2026-04-21, active)
```

Updates are append-only. Rotations append a new state entry. The registry
itself is version-controlled and part of the audit chain.

---

## What we deliberately do NOT build in v1

- On-device key generation via TPM 2.0 attestation (v2, pharmacy-specific)
- Threshold signatures (e.g., 2-of-3 signing for high-risk verbs) — maybe v2
- HSM-backed agent signing keys — migration path after fleet >50 pharmacies
  justifies Azure Key Vault Premium HSM cost
- Key escrow for pharmacy offboarding (pharmacy gets their own chain export
  encrypted with their own key; we don't hold it for them)

---

## Immediate action items (from this doc)

Before Phase A code ships:
1. Design the `pharmacy_keys` table schema + RLS policies
2. Implement `/api/agent/register` to generate + envelope-encrypt the initial keys
3. Implement rotation endpoint + dual-sign window logic
4. Set up AWS KMS key rings for dev/staging/prod
5. Write the key ceremony checklist doc
6. Migrate release signing key from GitHub secrets to KMS-via-OIDC

Post-Phase A:
7. Implement compromise detection heuristics (replay, time skew, malformed sig)
8. Automated 90-day rotation cron
9. External audit of key custody by third-party security firm (Phase B or C)

---

## Change log

- **2026-04-21 v0.1** — Initial draft. Locks to v1.0 after Nadim pilot + Codex review.
