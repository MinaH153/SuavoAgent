# Windows release validation

Run this matrix on disposable, fully patched Windows 11 x64 virtual machines.
Capture MSI logs, screenshots, Windows Event Viewer service events, installer
hashes, and artifact signatures for the release evidence package. Do not use a
machine that contains developer credentials or production pharmacy data.

## Release gate

| Scenario | Required proof |
| --- | --- |
| Authoring validation | Release MSI compiles with warnings as errors and passes Windows Installer ICE validation without suppressed errors. |
| Artifact identity | MSI and bootstrapper versions match the release; SHA-256 values are recorded; both Authenticode signatures and timestamp chains validate offline and online. |
| Fresh install | A standard user receives the UAC elevation prompt once; install completes; one Programs and Features entry exists; no terminal or script window appears. |
| Native connection entry | Burn success exposes **Launch SuavoAgent** and direct MSI install exposes **Start → SuavoAgent → Connect SuavoAgent**. Both open the installed graphical maintenance host with only --connect-installed; closing the success page leaves the Start entry available. No terminal appears. |
| Configuration-only ownership | Record service configuration and all five executable hashes immediately after MSI install. Complete pairing and record them again. Executable hashes and service binary path, account, dependencies, start mode, SID type, and recovery configuration are identical; only protected configuration/authority state changes. |
| Pairing recovery | Inject cancellation/failure after snapshot, after each allowlisted write, during probation, immediately before authority confirmation, after server acceptance but before response, after local authority finalization, and before active health. Pre-authority failures restore exact prior configuration. The authority boundary is durably AuthorityUnknown before the request; ambiguous outcomes recover forward and never abort a possibly accepted key. |
| Legacy boundary before pairing | Start with each known legacy graphical generation. Prove both a direct MSI launch and the rehearsal refuse its product registration, services, Broker process, or exact historical shortcut before `InstallValidate`, without mutating them. Prove repair and an actually installed related major-upgrade product are allowed. Remove the legacy product through Windows **Installed apps**, confirm those indicators are absent, then install the RC. The MSI never kills, relaunches, deletes, or rewrites user-owned legacy paths. |
| Service registration | Services UI shows `SuavoAgent.Core`, `SuavoAgent.Broker`, and `SuavoAgent.Watchdog`; all are delayed automatic, running, and use the documented accounts. Broker depends on Core. |
| Service hardening | All three services have unrestricted per-service SIDs. The signed Maintenance host runs after `InstallServices` and before `StartServices`, applies delayed-auto through `ChangeServiceConfig2`, and emits no console window or sensitive arguments. Standard users may query services but cannot change configuration, start, stop, delete, or replace them. Recovery restarts each service after failure. |
| Exact payload | Program Files contains the five named executables, bootstrap `appsettings.json`, and `install-state.json`; ProgramData contains `binaries.manifest`; every manifest SHA-256 matches its installed executable. |
| Filesystem ACLs | Standard users cannot create, replace, rename, or delete Program Files payloads. They cannot write the ProgramData root, general logs, or general diagnostics. Interactive users can write only the helper log, helper diagnostics, and honeytoken subtrees. Administrators, SYSTEM, and the Core service SID retain their documented access. |
| Helper launch | Broker launches exactly one Helper in the signed-in interactive session. Sign-out removes it; sign-in recreates it. A second concurrent session does not cross session boundaries. |
| Runtime startup | Core accepts its installed manifest and starts without fail-open behavior. Watchdog and maintenance host accept the installed state. No PHI, credentials, token material, or full patient/Rx identifiers appear in logs or Event Viewer. |
| VC++ prerequisite | On a VM without the runtime, the Burn package installs the pinned x64 runtime and then the MSI. On a VM with an equal/newer runtime, it skips the prerequisite. Return code 3010 requests a reboot without hiding it. |
| Silent install | `msiexec.exe /i SuavoAgent.msi /qn /norestart /L*v install.log` returns success, produces the same hardened state, and exposes no secret through command-line properties. |
| Fixed install path | `INSTALLFOLDER=C:\attacker-controlled` is refused before `InstallFiles`; a mixed-case `InstallFolder=` command-line value cannot alter the private directory property. No product file, token, or journal is created outside `C:\Program Files\Suavo\Agent`, and a reparse point at any canonical path segment fails closed. |
| Repair | Stop Broker, delete both `SuavoAgent.Helper.exe` and `SuavoAgent.Maintenance.exe`, then run `msiexec.exe /fa SuavoAgent.msi /qn /norestart /L*v repair.log`; both current-MSI payloads and their ACLs return, configuration is preserved, services resume, and repair mints no fresh-install marker. |
| Same-version maintenance | Running the same signed installer offers only supported maintenance behavior and neither duplicates services nor changes configuration. |
| Major upgrade | Install the prior signed release, create non-secret test configuration and evidence, then install the new release. One product remains; services use new binaries; bootstrap configuration, pairing state, logs, and evidence survive; rollback restores the prior working cohort if the upgrade is forced to fail. |
| Downgrade | Installing an older signed MSI over the new version is blocked with the authored message and leaves the new installation untouched. |
| Legacy replacement | Start from every known legacy installer generation. Remove the old product through Windows **Installed apps** first; the RC rehearsal then proves a clean host before installation. Unknown or partially removed layouts fail visibly and direct the operator to support. No duplicate service or mixed executable cohort is permitted. |
| Locked files | Keep Helper and each service executable running or locked during upgrade. Installer handles FilesInUse/reboot behavior without a mixed-version cohort. After reboot all five executable hashes match the new manifest. |
| Interrupted install | Force cancellation, activation failure, service-hardening API failure at each native write, marker failure before/after replacement, service-start failure, full disk, and power interruption at controlled checkpoints. Every vital action restores immediately on its own failure; a later MSI failure executes only journals matching that exact Windows Installer invocation. Cross-run pending or committed journals never replay. Any active token or remaining journal blocks receipt sealing. Final state is the previous healthy version or no product, never a false install marker or partially hardened service cohort. |
| Uninstall | `msiexec.exe /x SuavoAgent.msi /qn /norestart /L*v uninstall.log` stops and removes all three services and the MSI-owned Program Files payload. ProgramData audit and diagnostic evidence remains protected by design. |
| Reinstall after uninstall | A fresh install over retained ProgramData succeeds only when retained configuration is compatible and authentic; otherwise it fails visibly into a recovery path, never silently resets identity. |
| Non-admin boundary | A standard user cannot install, repair, upgrade, or uninstall without elevation and cannot use advertised repair to replace privileged files. |
| OS boundary | Windows 11 x64 succeeds. 32-bit Windows and unsupported OS versions are blocked before payload changes. |
| Accessibility and UX | Setup UI works with keyboard-only navigation, Narrator, 200% scaling, high contrast, and the standard cancellation/reboot/error flows. Branding and support links are production-correct. |
| Security scan | Defender and the release malware scanner report clean; static installer review finds no embedded secret, PHI, unreviewed custom action, shell command, writable privileged executable, or unquoted service path. Every product-authored action accepts only its fixed maintenance switch and bounded hidden MSI data, rejects extra arguments, and exposes no path in verbose logs. Service and marker transaction actions run as LocalSystem and may touch only their protected machine-owned state; no action mutates a legacy user path or process. |

## Configuration handoff gate

The configuration-only handoff is implemented; the exact signed release is not
a production replacement until this clean-Windows proof passes:

1. Install the signed MSI or Burn package.
2. Use Burn's success-page action, then repeat with the direct-MSI Start-menu
   entry. Close each once before pairing and prove it remains discoverable.
3. Paste only the short-lived device pairing code; no pharmacy secret is placed
   in an installer property or process argument.
4. Confirm the dashboard-issued workstation identity is stored using the
   product's protected machine configuration path.
5. Compare the before/after hashes and service configuration. Confirm the
   existing services reload protected configuration and reach probation then
   fresh active health without service re-registration or executable download.
6. Re-run configuration, cancellation, expired-code, offline, revoked-device,
   wrong-tenant, and server-timeout cases. Every failure is visible and
   recoverable, with no partial identity and no PHI in logs.
7. With a known legacy product, service, Broker process, or historical shortcut
   present, prove the rehearsal stops before running either signed installer.
   Remove the graphical legacy product through Windows **Installed apps**,
   confirm a clean host, and repeat successfully. Do not claim in-place process
   or shortcut migration.

Until this matrix passes on signed artifacts, label the build
**engineering validation only**.
