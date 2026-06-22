# SuavoAgent QA — Wave 2 Backlog (2026-06-21)

Source: 4 parallel adversarial reviewers — learning/moat engine, agentic loop/actuation, Setup/Broker internals, build/CI + test-coverage. Strong verified-safe baseline reconfirmed (harvest PHI gate fail-closed end-to-end, EncryptedScreenStore, signed-command path, actuation type/press fail-closed, presence layer truly cosmetic, agentic loop well-bounded).

## ✅ FIXED in branch `fix/qa-wave1.5-important` (all 3 Criticals + the 3 clean Setup Importants)
- **W2-C1** — `RunCmd` gained `throwOnFailure`; `LockdownDirectoryAcl` now hard-fails the install on an icacls failure (no more world-readable credentials).
- **W2-C2** — install dir is created + ACL-locked BEFORE the binary download, both ConsoleInstaller + InstallOrchestrator (no world-readable binary window).
- **W2-C3** — `ci.yml` build-and-test now runs the PHI analyzer canary (`run-integration.sh`); fails if SUAVO0001 stops firing.
- **I-1** — removed the dead `NetworkService:Modify` grant on the data dir (no PHI/credential exposure to SQL-Server-as-NetworkService).
- **I-2** — `CredentialProtector.SealSecretsFile` writes atomically (temp + Move).
- **I-3** — `ServiceCommand.InvokeRepair` requires a ZERO exit (new `RunForExitCode`), so a failed bootstrap repair no longer reads as success.

**Deferred to wave 2.5** (Important, but involved or contained-today — each deserves its own task): Setup I-4 (Helper crash-relaunch backoff — touches the Broker supervision loop), Agentic click-path TOCTOU + foreground-acquire pause-recheck, Learning dead auto-rule-approval gate + replay terminal-step TOCTOU, and the test-coverage gaps (PhiTextScrubber ReDoS sentinel, OTA per-binary hash, manifest sign round-trip, SQLCipher migration on a Windows runner).

## 🔴 NEW CRITICALS (3)
- ⬜ **W2-C1 [Setup/HIPAA] ACL lockdown silently fails → credentials world-readable** (`ServiceInstaller.cs:349-361,493-522`). `RunCmd("icacls", expectSuccess=true)` only logs (WriteInfo) on a non-zero exit instead of throwing; `LockdownDirectoryAcl`'s catch fires only if `Process.Start` itself fails. A failed icacls (rights/path) → install proceeds with the dir world-readable, then DPAPI seals `appsettings.json` → API key + SQL password written to a world-readable file. **Fix:** `RunCmd` throws on non-zero exit when `expectSuccess`; install hard-fails on ACL failure (same discipline as InstallAndStart).
- ⬜ **W2-C2 [Setup] binaries downloaded into installDir BEFORE ACL lockdown** (`ConsoleInstaller.cs:112` vs `:135`; `InstallOrchestrator.cs:53` vs WriteConfig lockdown). First install: dir created with inherited Program-Files ACL (Users:ReadAndExecute) and all 4 EXEs + checksums sit world-readable for the whole (slow) download. **Fix:** create+lock the dir before download, or download to temp → verify → move after lockdown. (First-install only; upgrade is locked already.)
- ⬜ **W2-C3 [Build/CI/HIPAA] PHI analyzer canary not gated in CI** (`tests/SuavoAgent.Analyzers.IntegrationTest/run-integration.sh` not called by any workflow). The only proof `PhiInOutboundPayloadAnalyzer` is wired + emits SUAVO0001. If a CodeAnalysis bump or a broken Analyzer reference silences it, PHI fields leak to outbound payloads with NO compile-time catch. **Fix:** add the (2-min) `run-integration.sh` step to `ci.yml`'s build-and-test job, fail on missing SUAVO0001.

## 🟠 IMPORTANT
- ⬜ **[Setup] NetworkService:Modify on dataDir** (`Core/Program.cs:367-374`) — a dead grant (Broker is LocalSystem, not NetworkService) that gives any NetworkService process (e.g. SQL Server) Modify on `credentials.dat`/`state.key`/`state.db` (PHI). **Fix:** remove the rule.
- ⬜ **[Setup] CredentialProtector.SealSecretsFile non-atomic write** (`CredentialProtector.cs:89`) — a crash mid-write leaves `appsettings.json` torn → agent starts blind. **Fix:** temp+move (like DpapiCredentialStore).
- ⬜ **[Setup] InvokeBootstrapRepair false-positive** (`ScWatchdogServiceProbe.cs:147-151`) — returns true if the repair process merely launched (ignores exit code) → Broker stops escalating while Watchdog never re-registers. **Fix:** check exit code.
- ⬜ **[Setup] Helper relaunch loop no crash backoff** (`SessionWatcher.cs:107-141,419-437`) — a Helper that crashes on launch is relaunched every 5s forever, double-SHA256-ing the apphost each time (pegs a constrained box). **Fix:** crash-relaunch backoff (5s→30s→60s→5min), reset on attach.
- ⬜ **[Agentic] click path no foreground/rect re-assert (TOCTOU)** (`SendInputDriver.cs:264` via `ActuationCommandHandler.cs:125/165`) — click_by_label/signature click stale absolute coords with no foreground/HWND re-check (type/press get the guard; clicks don't). Contained TODAY by the sandbox allowlist; becomes a wrong-target click into PHI the moment a PMS process is click-allowlisted. **Fix:** re-assert foreground + point-in-target-rect before MoveAndClick.
- ⬜ **[Agentic] foreground-acquire loop fights the user** (`SendInputDriver.cs:423-428`) — the up-to-6s acquire loop force-foregrounds the target without re-checking the gate pause, so a pharmacist grabbing focus mid-acquire gets it yanked back. **Fix:** `CheckOrReject()` at the top of each acquire iteration.
- ⬜ **[Learning] auto_rule_approvals.status is a dead gate** (`Program.cs:657`, `YamlRuleLoader`, `RuleEngine.cs:106`) — Pending/Rejected auto-rules load identically to approved; cloud approve/reject is cosmetic. Saved TODAY only by hardcoded `AutonomousOk=false` (bad rule can't auto-actuate, only prompt). **Becomes Critical if `AutoApproveOnFingerprintMatch` is ever honored.** **Fix:** filter by approval status on load (or stage to a non-loaded dir until approved).
- ⬜ **[Learning] replay perceive→click TOCTOU** (`VerifiedSkillReplayer.cs:90-92`) — single/last-step skills have no next-step to catch a mis-landing; postcondition asserts change, not correct-change → a wrong click on a shifted UI records Completed and re-thickens the skill. **Fix:** assert the expected post-state fingerprint on the terminal step.
- ⬜ **[Build/CI] manifest-signature test is a false-negative** (`PackageUpdateTests.cs:34` asserts a test key does NOT verify) — no round-trip test of the DER→P1363 verify path → a key rotation / encoding bug could silently brick all OTAs. **Fix:** add a generate-key→sign→verify round-trip test.
- ⬜ **[Build/CI] SQLCipher migration tests skipped in CI** (`CriticalPathTests.cs:79,100`) — `MigrateToEncrypted` runs every startup; a regression could wipe state DB with no CI catch. **Fix:** run on the windows-uia-smoke runner.
- ⬜ **[Build/CI] windows-uia-smoke is continue-on-error + not a release gate** (`ci.yml:144`) — the core actuation smoke (perceive→type→read, ETW honeytoken) can merge/ship broken. **Fix:** promote the Notepad perceive+type + ETW steps to a gating job for Helper/Actuation+Vision changes.
- ⬜ **[Build/CI] OTA per-binary hash verify untested** (`BinaryDownloader.DownloadAndVerifyAsync`) — only the checksum-signature is tested, not the per-file hash compare (the fresh-install tamper gate). **Fix:** add a mismatched-hash rejection test.
- ⬜ **[Build/CI] PhiTextScrubber ReDoS sentinel untested** (`PhiTextScrubber.cs` + tests) — the fail-closed `[SCRUB_TIMEOUT]` path (the only thing stopping a crafted input from hanging the scrubber + leaking PHI) has no test. **Fix:** inject a nanosecond-timeout regex, assert the sentinel + ContainsPhi=true.

## 🟡 MINOR
DPAPI LocalMachine + no entropy in `CredentialProtector` (mitigated by ACL — but see W2-C1) · per-file config grants drop on atomic rewrite (`ServiceInstaller.cs:449`) → Helper reads stale policy · `SelfUninstall.RunSchtasks` no exit-code check · EncryptedScreenStore tests CI-skipped · pricing argmin no multi-supplier test (`SqlPricingJobRunner`) · presence glow race (cosmetic) · navigate `Deadline=null` loses wall-clock cap · behavioral-event pruning dual-owned (7d vs 30d) · ShadowDenylist fails-open on regex timeout in the certifier (defense-in-depth holds).

## Top recommended next fixes (value × cleanliness)
1. **W2-C1** (RunCmd throws on icacls failure) — HIPAA credential exposure, mostly a logic change.
2. **Setup I-1** (remove dead NetworkService grant) — PHI/credential exposure, 1-line removal.
3. **W2-C3** (analyzer canary in CI) — restores the compile-time PHI gate, one CI step.
4. **Setup I-2** (atomic appsettings write) + **I-3** (bootstrap-repair exit code) — clean logic fixes.
5. **W2-C2** (lockdown before download) — reorder; Windows-pathed, slightly more involved.
6. The test-coverage gaps (PhiTextScrubber sentinel, OTA hash, SQLCipher migration, manifest round-trip) — add the missing tests.
