# Nadim PioneerRx Shadow Learning Addendum

Date: 2026-04-25

This addendum authorizes MKM/Suavo to run SuavoAgent observe-only template
learning on Nadim's pharmacy workstation for the PioneerRx pilot. It supplements
the existing BAA. If the existing BAA does not cover this use, this addendum
must be signed before `Agent.LearningMode` or `Agent.TemplateLearning.Enabled`
is enabled.

Reference: HHS sample BAA provisions require written permitted uses,
safeguards, reporting, subcontractor, access, amendment, accounting, return, and
termination terms for PHI handling:
https://www.hhs.gov/hipaa/for-professionals/covered-entities/sample-business-associate-agreement-provisions

## Permitted Use

SuavoAgent may observe UI Automation events from processes matching
`PioneerPharmacy*` to learn structural workflow patterns for future pharmacy
automation review. The pilot is Tier 0 capture only.

## Capture Limits

Allowed:

- UIA structural metadata.
- Element control type, class name, automation id, timing buckets, tree hashes,
  and HMAC-hashed UIA Name values.
- Local encrypted counters and cloud heartbeat counters/templates.

Not allowed:

- Screenshots.
- OCR text.
- Cleartext patient, prescription, NDC, payment, address, phone, or message text.
- Rule generation, auto-approval, execution, or PioneerRx writeback.

## Storage And Safeguards

- Local state is stored in SQLCipher-encrypted `state.db`.
- UIA Name values are HMAC-hashed before persistence.
- The HMAC key/salt is generated per learning session and stored only inside
  the encrypted local state database.
- Cloud heartbeat persists counters and structural template metadata only.
- Vision capture must be absent or disabled.

## Retention

Pilot learning artifacts are retained for 90 days unless Nadim requests earlier
deletion. On request, Suavo will disable Tier 0 capture, restart Core, and either
delete learning artifacts or run signed decommission.

## Rights And Termination

Nadim may request:

- Immediate soft rollback: disable Tier 0 flags and restart Core.
- Hard rollback: signed two-phase decommission, local archive ACK, then uninstall.
- Deletion of pilot learning artifacts.

## Signatures

Pharmacy authorized signer:

Name:

Signature:

Date:

MKM/Suavo authorized signer:

Name:

Signature:

Date:
