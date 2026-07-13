# Running SuavoAgent on a personal Windows laptop

This is a product-experience smoke test for a normal Windows 10/11 x64 laptop.
The same no-terminal lifecycle used by a pharmacy applies here. The canonical
customer guide is `docs/sales/windows-agent-lifecycle.md`.

> **Validation boundary:** the current codebase contains the signed-installer,
> native maintenance, dashboard command, and PHI-safe diagnostics paths described
> below. A particular release is not approved for customer use until that exact
> signed artifact passes `docs/hardening/release-gate.md` on clean Windows.

## What this test proves

A personal laptop without PioneerRx can prove that:

- the signed setup experience installs and pairs without a terminal;
- Core, Broker, and Watchdog start, and Helper attaches to the signed-in desktop;
- the dashboard receives a heartbeat and a built-in diagnostic result;
- the visible companion can pause and resume Autopilot without hiding its state;
- native repair and uninstall work through the dashboard and Windows Settings.

It cannot prove PioneerRx discovery, pharmacy SQL connectivity, a real Rx
workflow, or safe live-PMS actuation. Those require a separately authorized,
supervised pharmacy test.

## Prepare the laptop

1. Use Windows 11 24H2 or newer on an x64 machine.
2. Sign in to Windows with an account that can approve an administrator prompt.
3. Sign in to the Suavo pharmacy or fleet dashboard with an authorized sandbox
   account and MFA.
4. Close patient, prescription, banking, and other sensitive applications before
   the smoke test.

Do not create production identities or edit production database rows merely to
make this test pass. Use an approved sandbox tenant.

## Install and pair

1. Download the signed `SuavoAgent-Setup.exe` bundle from the authenticated
   Suavo dashboard.
2. Open **Properties → Digital Signatures** and confirm the signature is valid and
   names **MKM Technologies LLC**. Stop if it does not.
3. Open the installer and approve the Windows administrator prompt.
4. When installation finishes, choose **Launch SuavoAgent**. If that page was
   closed, open **Start → SuavoAgent → Connect SuavoAgent**.
5. Complete the graphical device-code pairing, readiness, and consent flow.
6. Leave the connection window open until it reports that the workstation is
   connected and the complete required service cohort is healthy.

Never download a bootstrap script, use a raw repository URL, invoke a console
installer, delete an older install folder, or copy component files by hand.

## Prove it is running

1. Open the workstation in the Suavo dashboard.
2. Wait for **Online** and the intended version to appear.
3. Choose **Diagnostics**. The built-in `fetch_diagnostics` command should move
   from requested to acknowledged and return a PHI-safe health summary.
4. Confirm Core, Broker, and Watchdog are healthy.
5. Confirm Helper is attached to the interactive desktop.
6. Confirm the pharmacist-panda indicator appears and that **Pause Autopilot**
   immediately prevents clicks and typing while the indicator truthfully shows
   that observation continues.

An online badge by itself is insufficient. Missing service, Helper, version, or
diagnostic evidence fails the test.

## Prove native repair

Run both supported user experiences, one at a time:

1. In the dashboard, choose **Repair**, confirm it, and wait for the matching
   acknowledgement and refreshed diagnostics.
2. In **Windows Settings → Apps → Installed apps → SuavoAgent**, choose
   **Modify/Repair** and let the installed native maintenance host finish.
3. Confirm the agent returns online with the same tenant binding and no new
   pairing request.

If repair cannot complete without opening a terminal, the build fails this
product test.

## Prove native update

Only test with a signed canary release authorized for the sandbox:

1. Request the update from the dashboard.
2. Watch the dashboard show the requested target and acknowledgement.
3. Confirm the new installed version and a fresh diagnostic result.
4. Confirm the presence indicator and pause control still work.

Do not replace binaries, restart services, or edit update markers manually. A
release that requires those steps has failed the native experience gate.

## Remove the test installation

1. Open **Windows Settings → Apps → Installed apps**.
2. Choose **SuavoAgent → Uninstall**.
3. Let native maintenance complete.
4. Confirm the workstation is no longer active in the sandbox dashboard.

Retained audit or decommission evidence follows the configured retention policy;
do not manually delete ProgramData and do not describe removal as "zero residue."

## Stop conditions

Stop and record the non-PHI dashboard receipt if:

- Windows does not show the expected verified publisher;
- setup reports success with any required service unhealthy;
- pairing selects the wrong tenant;
- diagnostics exposes or requests PHI, secrets, raw screenshots, or raw logs;
- repair, update, pause, or uninstall requires a terminal;
- an update cannot recover from a failed activation;
- the observation indicator is missing while observation is active.
