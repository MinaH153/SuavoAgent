# Incident Runbook — Key Compromise

> This runbook is what you execute when any signing key is confirmed or
> suspected compromised. Every key type has different blast radius and
> different response steps. Open this file the moment a
> `key.suspected_compromise` event fires, NOT when you're panicking.

**Locked date:** 2026-04-21
**Status:** v0.1 draft
**Referenced by:** `key-custody.md §Key compromise response`

---

## Severity classification — do this FIRST

Before any rotation, classify the suspected compromise into one of:

| Tier | Definition | Target response time |
|---|---|---|
| **T1 Confirmed breach** | Evidence of the private key material being exfiltrated (e.g., KMS audit log shows unauthorized access, hardware Yubikey is physically lost, private key file observed in attacker infrastructure) | <1 hour |
| **T2 Anomalous use** | Signatures or operations performed with the key that we cannot account for (e.g., cloud shows command signed by key but no dispatcher record) | <4 hours |
| **T3 Theoretical risk** | Vulnerability disclosure affects a primitive this key depends on, but no evidence of exploitation (e.g., OpenSSL CVE announced) | <48 hours |

T1 → hard-stop affected systems, rotate immediately.
T2 → rotate + forensics in parallel.
T3 → scheduled rotation within window, monitoring heightened.

**Classification requires two pairs of eyes.** Joshua + designated Security Officer
both sign off via the emergency Slack channel before executing. If only Joshua is
reachable and incident is clearly T1 (e.g., Yubikey visibly stolen), proceed
unilaterally and post-hoc document the SO notification attempt.

---

## Universal first steps (every tier, every key)

1. **Stop the bleeding.**
   - Soft-stop the kill switch for any pharmacy that might receive a
     fraudulent command signed by the suspected key (T1 only).
   - Pause the scheduled CI release workflows (T1 release-signing-key only).
   - Freeze any in-flight verb invocations (T1 cloud-command-key only).

2. **Open an incident record.**
   - Create a new doc under `docs/self-healing/incidents/<date>-<key-type>-compromise.md`
   - Start a Slack thread in `#suavo-security`
   - Timebox: 15 minutes from detection to incident-record-created. If longer,
     note the delay — it becomes a post-mortem gap.

3. **Capture evidence BEFORE rotating.**
   - KMS audit log export (AWS CloudTrail for KMS key ID)
   - Relevant application logs (cloud API access logs, CI run history)
   - Relevant audit chain entries (`key.*` events, `verb.signed` events signed
     by the suspected key)
   - If the key was on an agent: agent's DPAPI-protected blob + machine name
     + when agent last heartbeated

4. **Notify.**
   - Joshua + Security Officer via SMS + Slack (T1: wake them up).
   - Affected pharmacies within BAA breach-notification window (60 days per
     HIPAA §164.410, but target <24 hours for any PHI-touching compromise).
   - Legal counsel within 72 hours for any T1.

---

## Per-key-type runbooks

### Agent signing key (per-pharmacy)

**Blast radius:** Attacker can forge audit events + heartbeats for ONE
pharmacy. Cannot forge inbound commands (defense in depth — different key).

**Steps:**
1. Cloud service marks the current key as `revoked` in `pharmacy_keys` table
2. Cloud generates a new signing key, envelope-encrypts via KMS
3. Cloud emits signed `key.rotated` command to agent (signed by
   cloud-command-key which is NOT compromised)
4. Agent receives command, verifies signature, stores new key via DPAPI
5. Agent's next heartbeat signs with the new key; cloud accepts ONLY new key
6. **24-hour dual-accept window NOT applied** — T1 means no grace
7. All audit events signed with the compromised key in the compromise window
   are marked `suspicion_flagged=true`. Nightly verify sweep flags any cross-
   reference inconsistencies.
8. Forensic review: compare events between cloud audit chain and S3 Object
   Lock daily digest from BEFORE the compromise window — anything added to
   cloud chain that's not in the digest is suspect.

**Post-mortem questions:**
- How did the attacker get the key? (DPAPI is OS-level; compromise
  usually means machine-level breach.)
- Is PIONEER10 (or whichever machine) compromised at OS level? If yes →
  full reinstall + Windows reimage.
- Are any other pharmacies on the same local network / admin credentials?

### Cloud command key (per-pharmacy)

**Blast radius:** Attacker can forge COMMANDS to ONE agent. Could restart
services, push config overrides, trigger verbs. Can NOT forge audit events.

**Steps:**
1. Cloud generates new key, envelope-encrypts
2. Agent is notified via agent-signing-key signed command (NOT
   compromised — different key)
3. Agent rejects any command signed with old key going forward
4. Cloud marks old key `revoked`
5. Any commands issued during compromise window are audited as suspect —
   cross-reference against dispatcher session records to identify forgeries
6. If forged commands executed any mutation verbs, execute their rollback
   envelopes. If rollback fails → escalate to plan-review with manual
   intervention.

### Release signing key (ECDSA P-256, fleet-wide)

**Blast radius:** Attacker can sign fake OTA update manifests that agents
will install. This is the worst case — a compromised release key effectively
owns every SuavoAgent on the fleet.

**Steps:**
1. Security Officer's offline YubiKey (kept at their residence safe deposit
   box or equivalent) produces an emergency revocation signed by the
   OLD release key + SO's offline key (dual-signature requirement prevents
   attacker who ONLY has compromised release key from faking revocation)
2. Revocation rolled out to all live agents via an emergency release:
   - Emergency release is signed BOTH with the old compromised key AND the
     SO offline key; agents accept dual-signed revocations even when
     one signature is the compromised key
3. Every agent embeds the compromised key ID in its revocation list
4. Going forward, any manifest signed ONLY by the compromised key is
     rejected — even if otherwise valid
5. New key generated in AWS KMS (hardware-backed)
6. Next release cycle distributes new public key to agents via signed
     announcement (signed by NEW key + SO offline key — dual-sig for the
     key-rotation announcement, prevents same attack again)
7. All release binaries signed in the compromise window are reviewed:
   - If any reached production → recall + rollback via force-downgrade
   - If any only in staging → delete + rebuild
8. Blocklist the binary hashes of any potentially-compromised releases

**Post-mortem questions:**
- How did the key leave GitHub Actions secrets? (Or AWS KMS? — if KMS, it
  wasn't exfiltrated; use of it was compromised, different root cause.)
- Did we have OIDC federation in place, or was it still long-lived
  `SIGNING_KEY_PEM` secret? (If latter, this is the accelerant to migrate
  to KMS-via-OIDC per `key-custody.md §Migration plan`.)
- Do we need to issue a software supply chain advisory to pharmacies?

### EV code signing cert (SSL.com Yubikey)

**Blast radius:** Attacker can sign binaries that bypass SmartScreen. On a
signed binary they control, Windows shows "MKM Technologies LLC" as
verified publisher. Fleet impact is limited because agents also verify
RELEASE signing key (separate, above) before applying updates.

**Steps:**
1. **Within 24 hours of confirmed loss/theft:** Revoke via SSL.com. Call
   SSL.com (1-877-775-8275 during Houston CT business hours) if the online
   portal is insufficient.
2. Disable `/download` page button to prevent anyone from downloading a
   soon-to-be-fraudulently-signed Setup.exe.
3. Post notice on `/trust`: "EV code signing cert reissuance in progress.
   Agent OTA path is not affected (agents verify release signing key,
   separate from EV cert)."
4. SSL.com issues replacement cert after re-verification (~5 business days).
5. Replacement Yubikey shipped.
6. Future releases signed with new cert.

**Blocklist old cert thumbprint:** agents don't check EV cert at runtime
(only Windows SmartScreen does), so no agent-side blocklist needed.

### Audit digest signing key (Ed25519 in AWS KMS)

**Blast radius:** Attacker can sign fake daily digests and upload to S3,
which would let them claim events that never happened. Limited: S3 Object
Lock prevents overwriting existing digests, so attacker can only FABRICATE
new ones — they can't ERASE real history.

**Steps:**
1. Rotate key via AWS KMS (deprecate current, enable new)
2. Re-sign last 90 days of daily digests with new key (stored in S3 alongside
   existing digests — do not overwrite; add `-resign.json` suffix)
3. Update external verifier's expected public key list
4. Publish advisory on verifier repo
5. Any digests signed during compromise window flagged `suspicion=high` in
   external verifier output

---

## Post-incident requirements

### 7-day post-mortem
For every T1 or T2, a written post-mortem is due within 7 days. Template
lives at `docs/self-healing/post-mortem-template.md` (Phase A deliverable).

Required sections:
- Timeline (detection → classification → containment → resolution)
- Root cause analysis (5 whys minimum)
- Blast radius review (what was actually affected)
- Corrective actions (what we're changing to prevent recurrence)
- Evidence preservation (where forensic artifacts are stored)
- Customer communication timeline

### Corrective actions tracked
Every post-mortem produces one or more corrective actions filed as issues in
GitHub with tag `postmortem-action`. Tracked to closure.

### Invariant update
If the compromise revealed a gap in our invariants, amend
`docs/self-healing/invariants.md` in the same PR as the post-mortem.
Invariant changes also update the violation-handling section (§I.11).

### Policy update
If the compromise revealed a gap in Cedar policy or redaction ruleset,
amend those in the same PR cycle.

---

## Communication templates

### Pharmacy notification (within 24 hours for T1)

```
Subject: Security notice — SuavoAgent <key-type> rotation

<Pharmacy operator>,

On <date>, SuavoAgent detected a potential compromise of the
<key-type-description> used for your pharmacy's agent. Out of an
abundance of caution, we have:

1. Rotated the affected key
2. <specific-mitigations>
3. Preserved all forensic evidence

**Impact on your operations:** <one of: "none", "agent paused for N minutes",
"full incident statement coming">.

**Action required from you:** <one of: "none, agent auto-recovered",
"please confirm agent status in your cockpit", "please call us at ...">.

Full incident report + post-mortem available within 7 business days at
<incident URL>. We will notify you if HIPAA breach notification is triggered.

— Mina Henein, MKM Technologies LLC
```

### Internal Slack first-message

```
@channel SECURITY INCIDENT — <key-type> compromise

Classification: <T1|T2|T3>
Detected at: <timestamp>
Suspected key: <key-id>
Evidence: <1-sentence>
First actions taken: <1 sentence>

Full timeline in incident doc: <link>
```

---

## Change log

- **2026-04-21 v0.1** — Initial draft. Five key-type sub-runbooks, severity
  tiers, universal first steps, communication templates. Locks to v1.0 after
  first simulated tabletop exercise (Phase A deliverable).
