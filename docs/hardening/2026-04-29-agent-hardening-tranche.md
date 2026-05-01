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

### 5. Visual-only intent cursor

Problem: agentic assistance needs a "look here" pointer without fighting the pharmacist's real mouse or crossing into hidden automation of PioneerRx.

Fix:
- Added `intent_cursor` as a Core -> Helper IPC command.
- Added `show_intent_cursor` as an ECDSA-signed cloud command that audits locally before dispatch.
- Helper renders a short-lived topmost layered window in the interactive desktop session.
- The payload is numeric only: screen coordinates, duration, diameter, opacity, and tone.
- Core rejects text, label, window title, patient, prescription, medication, NDC, and Rx-bearing payload fields before Helper sees them.
- Tests assert the Windows renderer does not declare `SendInput`, `SetCursorPos`, `mouse_event`, `SetWindowsHookEx`, or UIA invoke APIs.

Compliance effect: this is visible local intent, not stealth control. It does not move the OS cursor, click, type, hook PioneerRx, inject into PioneerRx, or send screen text through the command path.

## Tranche 2 Shipped Here

### 6. Canonical Rx order candidate payload

Problem: PioneerRx ready-order sync had a legacy queue shape that could be confused with telemetry, did not carry field-level provenance, and did not make the "plain Rx stays local" rule explicit.

Fix:
- Added `RxOrderCandidate` as the canonical extraction contract.
- `RxDetectionWorker.SerializeRxBatch` now emits `data.rxOrderCandidates` while keeping the old `rxDeliveryQueue` for backward compatibility.
- Plain Rx numbers never leave the agent in the canonical payload. The cloud-facing key is `sourceExternalKeyHash` with `sourceExternalKeyKind=hmac_sha256_rx_number`.
- Candidate fields include medication display, NDC, quantity, fill number, days supply, ready status GUID, control/schedule flags, priority, temperature requirement, patient/delivery fields, confidence, warnings, provenance, source, schema signature, window signature, and local evidence ID.
- Field provenance classifies operational metadata, PHI direct fields, and HMAC'd PHI keys separately.

Compliance effect: telemetry/model-prompt paths can stay PHI-free while the audited HMAC-authenticated sync path can carry minimum-necessary order fields for pharmacy review.

### 7. PioneerRx metadata enrichment

Problem: the ready-order extraction did not preserve several fields needed by pharmacy/fleet handoff and later exception handling.

Fix:
- `RxMetadata` now carries fill number, days supply, drug schedule, priority, and temperature requirement.
- PioneerRx SQL metadata extraction now includes refill number, days supply, and DEA schedule where available.
- Missing patient identity/address now produces explicit extraction warnings and lower candidate confidence instead of silently looking complete.

Operational effect: the dashboard can distinguish "agent found a ready Rx but still needs pharmacist correction" from "broken inbox" or "complete delivery candidate."

### 8. Cloud sync validation and inbox provenance

Problem: `/api/agent/sync` accepted loose item shapes, legacy hashed Rx fields were ambiguous, and the inbox RPC did not preserve extraction quality/provenance.

Fix:
- `/api/agent/sync` now validates `rxOrderCandidates` against an allowlist, rejects unregistered fields, rejects plain Rx numbers, rejects invalid hashes, and rejects invalid confidence/source/schema/evidence fields before writing snapshots or RPC rows.
- Legacy `rxDeliveryQueue` remains accepted only when its `rxNumber` is already a 64-character HMAC digest.
- Audit metadata intentionally excludes patient, address, medication, and Rx identifiers.
- New Supabase migration adds extraction source, confidence, warnings, provenance, schema/window signatures, local evidence ID, hashed external key columns, NDC, quantity, fill/days supply, status GUID, drug schedule, and patient/counseling flags.
- `upsert_inbox_items` now dedupes by hashed external key, enriches still-pending rows, skips promoted/rejected rows, and normalizes `priority` to canonical text values even across old integer/text schema drift.

Security effect: the cloud rejects malformed agent payloads before PHI ingestion and stores provenance without leaking PHI into logs or audit metadata.

### 9. Pharmacy/fleet handoff visibility

Problem: inbox rows could look like real Rx numbers, loaded-empty/broken states were hard to distinguish, and fleet delivery APIs did not receive extraction-quality hints.

Fix:
- Pharmacy inbox now labels agent rows as `Agent candidate {hashSuffix}` instead of `Rx {hash}`.
- Inbox cards show address readiness, missing-address extraction warning, confidence, and extraction source.
- Inbox APIs select real `temperature_req` and `priority` columns plus extraction fields, avoiding removed ghost columns.
- Fleet deliveries API includes non-PHI extraction source/confidence/warnings for operational triage.

UX effect: pharmacists see what the agent knows, what it is missing, and what still needs correction before promotion to a delivery order.

## Tranche 3 Shipped Here

### 10. Install false-green and cloud-auth recovery

Problem: a Windows machine could show Core, Broker, Helper, and Watchdog as running while cloud auth was dead. Two root causes were observed in the field: LocalService could not rewrite `appsettings.json` to DPAPI-seal/rotate credentials, and a reinstall with a fresh token could accidentally reuse a stale local API key.

Fix:
- Bootstrap now grants LocalService `Modify` on `appsettings.json` while keeping NetworkService read-only.
- A provided install token always replaces stale local registration instead of skipping `/api/agent/register`.
- Bootstrap now refuses local-only fallback IDs; a production install must have `ApiKey`, cloud UUID `AgentId`, and `PharmacyId` before writing the final config.
- The older `SuavoSetup.exe` console/GUI path no longer generates friendly local `agent-*` IDs; it requires a cloud UUID if that legacy path is ever invoked.
- Bootstrap Phase 6 now signs a redacted GET `/api/agent/config` and fails the install before the success banner if cloud HMAC auth is rejected.
- The release probe checks appsettings ACLs and performs a redacted live HMAC GET to `/api/agent/config`.
- Core can recover from a 401 `Agent not found` by calling `/api/agent/recover-key` with the cloud UUID + machine fingerprint, writing the rotated key locally, and stopping so Watchdog restarts Core with a rebuilt HMAC signer.
- Config sync now invokes the same one-shot recovery coordinator when `/api/agent/config` returns 401 `Agent not found`, so recovery is not delayed until the next heartbeat.
- Config sync now persists the sanitized client failure kind into `config-sync-health.json`; the pharmacy dashboard treats `http_401_Agent_not_found` as critical "Cloud auth rejected agent identity" while the agent is still heartbeating.
- Credential recovery now writes `cloud-auth-health.json` with sanitized status, last error kind, recovery outcome, and restart request state; heartbeat accepts only those allowlisted fields, rejects free-form auth error text, and dashboard health marks failed recovery such as `http_404_Not_Found` as critical without storing raw cloud bodies.
- Signed `collect_health_probe` results now include the same `cloudAuth` evidence, and both agent command ack storage plus pharmacy command detail views sanitize it down to code-style status/outcome fields.
- Bootstrap's failed-verification copy now distinguishes service failures from cloud-auth failures so operators do not chase Windows SCM when the real issue is registration/HMAC identity.
- Core, Broker, Helper, Watchdog, and startup/crash logs are written as UTF-8 with BOM so Windows PowerShell 5.1 `Get-Content` displays runtime logs cleanly instead of mojibake like `â€”`.
- Operator-facing PowerShell helper scripts now keep status output ASCII-safe, so precheck/validate/install helper output does not depend on a UTF-8 PowerShell host.
- Cloud error reasons are now whitelist/code-style sanitized: compact operational codes and known auth reasons survive, while free-form English that could carry names, addresses, Rx numbers, or medication context is redacted before entering logs or exception messages.

Operational effect: "services running" is no longer accepted as proof of success. The install is only healthy when local services, Helper attestation, config ACL, and cloud HMAC auth all verify.

### 11. Atomic token registration

Problem: `/api/agent/register` burned the one-time install token before upserting `agent_instances`, then attempted app-level rollback if the upsert failed. A server crash or network cut in that window could leave a pharmacy with `used=true` on the token and no registered agent row.

Fix:
- Registration now calls the database function `register_agent_with_install_token`.
- The function validates the token, burns it, and inserts/updates the `agent_instances` row in one PostgreSQL transaction.
- The route sends only the SHA-256 API key hash to SQL; the raw API key is returned to the installer only.
- The function preserves existing `config_json` stats while replacing `api_key_hash` on reinstall.
- Execute permission is revoked from `PUBLIC`, `anon`, and `authenticated`, then granted only to `service_role`.

Operational effect: a failed cloud registration no longer consumes the one-time install token without creating the agent identity needed for heartbeat/config HMAC auth.

### 12. Agent post-deploy smoke coverage

Problem: the cloud smoke gate covered pharmacy pages and APIs, but not the agent registration/auth/recovery boundary that decides whether a Windows install can be trusted.

Fix:
- The Suavo web post-deploy smoke runner now sends non-mutating synthetic requests to `/api/agent/heartbeat`, `/api/agent/config`, `/api/agent/sync`, `/api/agent/register`, `/api/agent/recover-key`, and `/api/agent/install-telemetry`.
- Invalid registration uses a real-shaped `sai_` token plus the legacy `0000000000` NPI placeholder so old and new register paths both exercise the intended token validation branch.
- The key-recovery smoke tolerates rate-limit `429` on repeated manual runs but still fails on `404` or `5xx`.
- CI contract tests now assert the smoke workflow stays wired to both pharmacy and agent routes.
- Dashboard runtime health now marks a heartbeating agent as degraded when config-sync evidence is missing and critical when config-sync evidence is unreadable.

Operational effect: production can no longer pass a post-deploy smoke while the agent recovery endpoint is missing, the register RPC route is broken, or HMAC-gated routes are returning unexpected server errors.

## Still Not Done

These are the next tranches, in order:

1. Phase A cloud substrate: silent-agent alarms, version drift dashboard, crash-log aggregation, probe ingest, audit digest verification.
2. Typed `apply_config_override` verb: replace generic config polling authority with signed command, risk tier, BAA scope, rollback, and audit.
3. Remote support v1: polled capture viewer, log pull, Helper restart, PIAG trigger, session expiry, break-glass audit.
4. PioneerRx writeback hardening: official API first, SQL writeback only behind explicit typed verb, schema canary, pre/post verification, human exception queue.
5. Full field acceptance pipeline: install probe, runtime heartbeat proof, release tag check, signed binary evidence, decommission smoke, and 7-day soak.
6. Windows visual smoke for `intent_cursor` on a real installed agent: verify overlay visibility, no focus steal, no mouse movement, no PioneerRx input events, and clean teardown across repeated commands.
7. Live PioneerRx shadow validation: compare extracted candidates against expected Rx/order fields on a real pharmacy workstation with no mutation.
8. Production migration execution evidence: record who applied the inbox provenance/atomic-registration migrations, when, and which post-deploy smoke proved them.

## Acceptance Standard

This tranche is not a V4 release. It is a foundation patch set. It is ready to continue only after targeted tests and the full solution test suite pass.

Current verification on 2026-04-29:
- `dotnet test SuavoAgent.sln --no-restore -p:UseAppHost=false` passed 1,477 tests with 9 expected skips.
- `/Users/joshuahenein/Code/Suavo`: `npm run typecheck` passed.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm run typecheck` passed.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- src/__tests__/api/pharmacy/inbox-schema-contract.test.ts src/__tests__/api/agent/sync-rx-order-candidates.test.ts` passed 7 tests.
- `git diff --check` passed for `/Users/joshuahenein/Code/SuavoAgent`, `/Users/joshuahenein/Code/Suavo`, and the touched mirrored `web/web` files. Full `web/web` diff-check is currently polluted by unrelated generated/old whitespace files outside this tranche.

Additional verification on 2026-05-01:
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj --no-restore` passed 58 tests.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet test tests/SuavoAgent.Core.Tests/SuavoAgent.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~CloudErrorSanitizerTests|FullyQualifiedName~AgentConfigClientTests|FullyQualifiedName~SuavoCloudClientTests|FullyQualifiedName~AgentCredentialRecoveryTests"` passed 15 tests.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet test tests/SuavoAgent.Core.Tests/SuavoAgent.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~ConfigSyncWorkerTests|FullyQualifiedName~AgentConfigClientTests"` passed 11 tests.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet test tests/SuavoAgent.Core.Tests/SuavoAgent.Core.Tests.csproj --no-restore --filter "FullyQualifiedName~AgentCredentialRecoveryTests|FullyQualifiedName~HealthSnapshotTests|FullyQualifiedName~AgentConfigClientTests|FullyQualifiedName~ConfigSyncWorkerTests"` passed 17 tests.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet build SuavoAgent.sln --no-restore` passed with 23 existing Windows-only analyzer warnings in Helper/Helper.Tests and 0 errors.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet build SuavoAgent.sln --no-restore` passed with 0 warnings and 0 errors after the cloud-auth health/probe pass.
- `/Users/joshuahenein/Code/SuavoAgent`: `dotnet test SuavoAgent.sln --no-build` passed 1,552 tests with 9 expected skips.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- scripts/ci/__tests__/post-deploy-smoke.test.ts src/__tests__/config/ci-workflow-contract.test.ts` passed 7 tests.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- src/__tests__/lib/agent-page-contract.test.ts` passed 9 tests.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm test -- src/__tests__/lib/agent-page-contract.test.ts` passed 9 tests.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- src/__tests__/lib/agent-runtime-health.test.ts src/__tests__/lib/agent-dashboard-model.test.ts` passed 17 tests.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm test -- src/__tests__/lib/agent-runtime-health.test.ts src/__tests__/lib/agent-dashboard-model.test.ts` passed 17 tests.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- src/__tests__/api/agent/heartbeat-runtime-health.test.ts src/__tests__/lib/agent-runtime-health.test.ts src/__tests__/lib/agent-dashboard-model.test.ts` passed 22 tests.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm test -- src/__tests__/api/agent/heartbeat-runtime-health.test.ts src/__tests__/lib/agent-runtime-health.test.ts src/__tests__/lib/agent-dashboard-model.test.ts` passed 22 tests.
- `/Users/joshuahenein/Code/Suavo`: `npm test -- src/__tests__/api/agent/commands-ack.test.ts src/__tests__/api/pharmacy/agent-command-detail.test.ts` passed 6 tests.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm test -- src/__tests__/api/agent/commands-ack.test.ts src/__tests__/api/pharmacy/agent-command-detail.test.ts` passed 6 tests.
- `/Users/joshuahenein/Code/Suavo`: combined agent health/command verification passed 28 tests.
- `/Users/joshuahenein/Code/Suavo/web/web`: combined agent health/command verification passed 28 tests.
- `/Users/joshuahenein/Code/Suavo`: `npm run typecheck` passed.
- `/Users/joshuahenein/Code/Suavo/web/web`: `npm run typecheck` passed.
- `/Users/joshuahenein/Code/Suavo`: `npx --yes tsx scripts/ci/check-schema-drift.ts` passed with no new drift; 11 baseline entries are now removable.
- Live unauthenticated smoke against `https://suavollc.com` still fails until the local web changes are deployed: `/api/agent/recover-key` returns `404`, which is exactly the missing-route condition the new smoke gate is meant to catch.
