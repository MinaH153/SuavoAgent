# SuavoAgent release gate

No SuavoAgent build is customer-ready because it compiles or because a service
starts. The exact signed artifact must pass every evidence group below.

## 1. Release identity and provenance

- [ ] Immutable `vX.Y.Z` tag and source commit recorded.
- [ ] Release URL and artifact SHA-256 recorded.
- [ ] `checksums.sha256` and its detached signature published and verified.
- [ ] OTA manifest and detached signature published and verified.
- [ ] The manifest format is accepted by the oldest cohort allowed to receive
      the update; a full-cohort manifest is not enabled until the compatibility
      rollout proves support.
- [ ] Release and hotfix signing preflight failed closed unless the approved
      eSigner Cloud signing key credentials were present.
- [ ] Every shipped Windows executable has a valid, timestamped Authenticode
      signature from **MKM Technologies LLC**.
- [ ] The signed binary hashes match the published checksum and OTA evidence.
- [ ] No binary was modified after signing.

Unsigned passthrough artifacts are never field, pharmacy, or Queen releases.
The release workflow uses `sslcom/actions-codesigner` for the approved cloud
signing operation; unsigned passthrough is not a Queen/field release.

Required release evidence includes the customer-facing **Native installer URL**,
`checksums.sha256.sig`, `update-manifest-vX.Y.Z.sig`, and any applicable
**Production migration evidence** (project, migration name, operator, and UTC
deployment time).

## 2. Build, test, and security evidence

- [ ] Full solution build passed with zero errors.
- [ ] Required unit, integration, installer, updater, watchdog, IPC, security,
      and PHI-scrubbing tests passed.
- [ ] Deterministically merged authored-code coverage is at least 80% line and
      80% branch, includes every authored production source on Linux and
      Windows, and permits only hash-pinned, reviewed declaration-only files
      with no executable sequence points outside the denominator.
- [ ] The exact official Qwen LFS object and repository-signed LLamaSharp
      backend package were re-proved against their immutable publisher
      metadata, size, digest, retained license, and repository signature.
- [ ] Production shell-boundary check passed: runtime, setup, customer docs, and
      release notes do not invoke or instruct a script-based lifecycle.
- [ ] Release artifact probe verified the declared Core, Broker, Helper,
      Watchdog, Setup, and native maintenance cohort.
- [ ] Signature, checksum, cohort-mismatch, missing-file, stale request, replay,
      timeout, partial swap, and rollback failure paths were exercised.
- [ ] Production migration names, operator, timestamp, and Supabase project ID
      are recorded when the release depends on database changes.
- [ ] Suavo web post-deploy smoke passed for the agent registration, config,
      key recovery, sync, heartbeat, diagnostics, repair, update, and install
      telemetry routes used by this release.

Build and artifact probes may use internal CI tooling. They are not customer
instructions and are never copied into a pharmacy support procedure.

## 3. Clean-Windows native experience

Run this section on a supported, clean Windows x64 machine using the exact final
signed artifact. Record screen evidence and non-PHI receipts for every step.

### Install and pair

- [ ] Download began from the authenticated Suavo dashboard.
- [ ] Windows showed the expected verified publisher before elevation.
- [ ] The signed `SuavoAgent-Setup.exe` bundle installed the MSI
      and completed its graphical pairing flow with no terminal.
- [ ] An older supported installation was discovered and handled without manual
      folder or service deletion.
- [ ] Pairing selected the correct tenant and required the expected privileged
      account/MFA gate.
- [ ] Setup reported success only after Core, Broker, and Watchdog were running.
- [ ] Helper attached to the interactive desktop when a user was signed in.
- [ ] SuavoAgent appeared in **Windows Settings → Apps → Installed apps** with
      native modify/repair and uninstall actions.

### Health and diagnostics

- [ ] Dashboard showed the intended installed version and a fresh heartbeat.
- [ ] Built-in **Diagnostics** returned a PHI-safe snapshot with service state,
      Helper attachment, cloud/config health, and maintenance-host presence.
- [ ] A missing required health artifact produced a visible failure, never an
      inferred green state.
- [ ] Failed key recovery or cloud authentication surfaced a sanitized status
      without response bodies, secrets, or patient data.
- [ ] The observation indicator was visible whenever observation was active,
      and **Pause Autopilot** immediately blocked clicks/typing without falsely
      claiming that observation stopped.

### Repair

- [ ] Automatic Watchdog escalation repaired a controlled service failure.
- [ ] Dashboard **Repair** produced a matching command acknowledgement, native
      maintenance result, and refreshed diagnostics.
- [ ] Windows Settings **Modify/Repair** restored the same complete cohort.
- [ ] Each privileged launch rejected a renamed, relocated, substituted,
      unsigned, or cohort-mismatched maintenance host.
- [ ] Repair could not be triggered by a bare, malformed, stale, or replayed
      local marker.
- [ ] Repair did not deadlock when Broker or Core had to be stopped.
- [ ] Repair preserved pairing, consent, configuration, and retained audit
      evidence.

### Update and recovery

- [ ] Dashboard-driven native OTA staged and verified the complete declared
      cohort before activation.
- [ ] Dashboard reported request, progress/failure, installed version, and
      post-update diagnostics.
- [ ] A controlled bad activation recovered to the last known-good compatible
      cohort without local intervention.
- [ ] Interrupted file moves and manifest regeneration failures recovered or
      failed visibly without mixed-version execution.
- [ ] Probation covered the full swapped cohort and required a strong health
      milestone, not merely one process start.

### Uninstall and reinstall

- [ ] Windows Settings **Uninstall** launched native maintenance without a
      terminal.
- [ ] Remote decommission required the matching signed archive and completion
      receipts before destructive removal.
- [ ] Retention-governed audit evidence was preserved or quarantined according
      to policy; the test did not assume "zero residue."
- [ ] A fresh reinstall completed without manually deleting old files, services,
      or registry entries.

Any terminal, script, manual service restart, registry edit, or Program Files
replacement needed in this section is a release failure.

## 4. HIPAA-first evidence boundary

- [ ] No PHI, credentials, raw screenshots, raw log bodies, or prescription
      identifiers appeared in setup, diagnostics, repair, update, release notes,
      or test evidence.
- [ ] Minimum-necessary access and role/tenant boundaries were verified.
- [ ] Consent and employee-disclosure evidence was recorded before observation.
- [ ] Audit events and decommission receipts were tamper-evident and retained
      under the documented policy.
- [ ] Vendors that can touch PHI have the required agreements and approved
      configuration for the deployed environment.
- [ ] Optional native features such as Tesseract OCR remained disabled unless
      their exact independently reviewed cohort was allow-listed in the signed
      release; an HTTPS URL plus a hash is not sufficient authorization.

Passing this gate is engineering and operational evidence. It is not a claim of
"HIPAA certification" or "FDA approval."

## 5. Field exit

Queen/PioneerRx validation uses only the signed build that passed sections 1–4.
The field gate remains closed until:

- release tag, source commit, artifact digest, checksum-signature digest,
  install receipt, and rollback receipt are recorded;
- replay has zero forbidden-token hits;
- candidate hashes are stable and the schema canary is passing;
- candidate rows and correction receipts are visible in the dashboard; and
- the supervised real-PMS workflow passes without weakening fail-closed safety.

Historical pilot or smoke documents are evidence only. Their old shell-based
commands are not approved release procedures.
