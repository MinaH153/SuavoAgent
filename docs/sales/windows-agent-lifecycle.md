# SuavoAgent for Windows — install, update, repair, and removal

**Audience:** pharmacy staff, fleet operators, and Suavo support working with a
customer workstation.

This is the only approved customer lifecycle. A customer must never be asked to
open a terminal, run a script, paste a command, replace program files, or edit
SuavoAgent configuration by hand.

> **Release status:** this document describes the native product contract in the
> current codebase. It is not evidence that a particular build has passed clean
> Windows hardware validation. Support may provide a build only after the release
> gate in `docs/hardening/release-gate.md` is complete for that exact signed
> artifact.

## Before installation

- Use a supported Windows 10 or Windows 11 x64 workstation.
- Sign in to the pharmacy or fleet dashboard with the authorized account.
- Complete the required business agreement, BAA, employee notice, and consent
  steps before observation is enabled.
- Download the installer only from the authenticated Suavo dashboard. Do not use
  an attachment, raw repository link, shared drive, or third-party mirror.
- The file name begins `SuavoAgent-Setup-` and ends `-win-x64.exe`. In the
  Windows security prompt, the verified publisher must be **MKM Technologies
  LLC**. Cancel if the publisher is missing, unknown, or different.

SmartScreen reputation and a valid code signature are separate signals. A
low-reputation warning can still appear for a newly signed release. Never bypass
a missing or invalid publisher signature.

## Install and pair

1. Open the downloaded `SuavoAgent-Setup-…-win-x64.exe` and approve the Windows
   administrator prompt.
2. Follow the setup wizard. Do not choose an undocumented or console mode.
3. Confirm the pharmacy or fleet shown in the pairing screen.
4. Approve the short-lived pairing code in the authenticated Suavo dashboard.
5. Setup signs the exact device, readiness, and observed SQL Server certificate
   identity with the workstation private key. The cloud promotes that same
   identity only after the probation health proof is fresh and complete.
6. Review the observation disclosure and consent summary.
7. Let setup finish its system checks, signed-component verification, service
   registration, and first start.
8. Keep setup open until it reports completion. A partial service start is not a
   successful installation.

Old installations are handled by the native installer and maintenance system.
After required services are safely stopped, Setup removes only the exact known
retired installer files, obsolete repair tasks, and old command registrations.
It writes a PHI-free migration receipt and refuses to restart the agent if a
privileged retired repair path remains. Do not manually delete old folders,
tasks, services, or registry entries before reinstalling.

## Confirm the agent is ready

Use the dashboard, not local log files:

1. Confirm the workstation appears **Online**.
2. Run **Diagnostics**. This invokes the built-in `fetch_diagnostics` command and
   returns a PHI-safe health summary.
3. Confirm Core, Broker, and Watchdog are healthy and the interactive Helper is
   attached when a user is signed in.
4. Confirm the reported installed version matches the intended release.
5. Confirm the observation indicator is visible before observation is enabled.
6. For PioneerRx access, confirm the dashboard's device identity and process
   approval are bound to the same SQL Server certificate digest. A certificate
   change invalidates the prior PioneerRx authority and requires review.

If any check is red or missing, leave Autopilot off and use **Repair**. Do not ask
the customer to send raw logs, screenshots, patient information, prescription
numbers, credentials, or configuration files.

## Updates

Updates are delivered by the dashboard-driven native updater. The signed update
manifest binds the exact component hashes, and the installed agent stages and
verifies the declared cohort before activation.

- The customer does not download replacement binaries.
- The customer does not stop or restart Windows services.
- The customer does not edit files under Program Files or ProgramData.
- The dashboard must show the requested version, progress or failure, and the
  post-update health result.
- A failed update must remain visible and recover to the last known-good cohort;
  it must never be described as successful merely because the request was sent.

## Repair

Use one of these native paths:

### From the Suavo dashboard

1. Open the workstation in the Agent dashboard.
2. Choose **Repair** and confirm the action.
3. Wait for the signed command acknowledgement and the post-repair diagnostics.

### From Windows Settings

1. Open **Settings → Apps → Installed apps**.
2. Find **SuavoAgent** and open its actions menu.
3. Choose **Modify/Repair** when Windows offers it.
4. Let the installed `SuavoAgent.Maintenance.exe` repair the registered services
   and verify the installed component cohort.

If neither path completes, stop there and escalate using the dashboard's
non-PHI diagnostic receipt. Repeated manual retries can erase useful failure
ordering and are not an approved support procedure.

## Pause or stop Autopilot

Use the visible pharmacist-panda control or the dashboard control. **Pause
Autopilot** immediately prevents clicks and typing while observation continues;
**Stop Autopilot** trips the local kill switch and requires a deliberate restart.
Neither action requires stopping services or removing the application. Do not
describe an Autopilot pause as an observation/privacy pause.

## Uninstall

1. Open **Settings → Apps → Installed apps**.
2. Find **SuavoAgent** and choose **Uninstall**.
3. Confirm the native maintenance prompt.
4. Wait for Windows to report completion, then confirm the workstation is no
   longer active in the dashboard.

Removal must preserve any retention or audit evidence required by the active
business and legal policy. Do not promise "zero residue" or delete retained
evidence manually. A remote decommission request is complete only after its
matching signed archive and completion receipts are recorded.

## What this product language does and does not mean

SuavoAgent is designed to support HIPAA-aligned safeguards such as minimum
necessary access, consent, access controls, PHI-safe diagnostics, and auditable
actions. That does not make a build "HIPAA certified," and SuavoAgent is not
"FDA approved" unless a future, applicable regulatory determination is obtained
and documented. Sales, setup, and support must not make either claim.
