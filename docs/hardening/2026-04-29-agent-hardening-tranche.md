# SuavoAgent Hardening Tranche — 2026-04-29

## Goal

Move SuavoAgent toward field-grade operation before any V4 branding: safe remote control surfaces, truthful runtime gates, and audit-complete pipelines.

## Tranche 1 Shipped Here

### 1. Signed-command nonce safety

Problem: signed-command replay protection recorded nonces before cryptographic verification. A forged command envelope could burn a future valid nonce and make the real cloud command look replayed.

Fix:
- `SignedCommandVerifier` now rejects malformed commands before signature work.
- Invalid signatures no longer consume in-memory nonces.
- `HeartbeatWorker` records persistent command nonces only after ECDSA verification succeeds.
- Regression tests cover both verifier-level and worker-level nonce poisoning.

Security effect: unauthenticated traffic can no longer deny a future valid command by preusing its nonce.

### 2. Vision capture runtime gates

Problem: `VisionCaptureWorker` snapshotted vision config at startup. Cloud/runtime config flips could not reliably stop or start periodic capture behavior, and `TickAsync` could capture even when vision or periodic capture was disabled.

Fix:
- Worker now uses `IOptionsMonitor<VisionOptions>`.
- Each tick checks `Vision.Enabled` and `Vision.PeriodicCapture.Enabled` before any audit row or IPC call.
- Disabled gates return explicit reasons: `vision_disabled` and `periodic_capture_disabled`.

HIPAA effect: screen capture obeys the current runtime gate, not stale startup state.

### 3. Vision audit completion

Problem: failed/rejected capture attempts wrote a pre-send audit row but no terminal outcome, leaving forensic review with dangling `request` entries.

Fix:
- Every attempted capture now writes a terminal audit entry: `complete` or `failed`.
- Failure reasons are sanitized before entering `CaptureReason`.
- IPC disconnected, timeout, helper rejection, and success paths are covered by tests.

Audit effect: every capture attempt has a closed chain of custody.

### 4. Protected config override paths

Problem: cloud config sync would write any override path to `config-overrides.json`, including agent identity, cloud endpoint, cert pinning, SQL credentials, and HMAC salt.

Fix:
- `ConfigOverrideStore` blocks protected identity, secret, cloud trust, and SQL connection paths.
- Multi-pharmacy config array overrides are blocked because they can carry per-pharmacy SQL credentials and identities.
- Atomic writes now use an explicit write-through file stream and disk flush before rename.

Security effect: the generic config pipeline can still flip safe operating modes, but it cannot rewrite trust anchors or local credentials.

## Still Not Done

These are the next tranches, in order:

1. Phase A cloud substrate: silent-agent alarms, version drift dashboard, crash-log aggregation, probe ingest, audit digest verification.
2. Typed `apply_config_override` verb: replace generic config polling authority with signed command, risk tier, BAA scope, rollback, and audit.
3. Remote support v1: polled capture viewer, log pull, Helper restart, PIAG trigger, session expiry, break-glass audit.
4. PioneerRx writeback hardening: official API first, SQL writeback only behind explicit typed verb, schema canary, pre/post verification, human exception queue.
5. Full field acceptance pipeline: install probe, runtime heartbeat proof, release tag check, signed binary evidence, decommission smoke, and 7-day soak.

## Acceptance Standard

This tranche is not a V4 release. It is a foundation patch set. It is ready to continue only after targeted tests and the full solution test suite pass.
