# SuavoAgent native Windows installer

This directory contains the WiX Toolset v7 delivery foundation for the x64
SuavoAgent workstation product. It produces:

- `SuavoAgent.msi`: a per-machine Windows Installer package.
- `SuavoAgent-Setup.exe`: an optional Burn bootstrapper that installs the pinned
  Microsoft Visual C++ x64 runtime before the MSI.

The MSI installs the exact runtime cohort into
`C:\Program Files\Suavo\Agent`:

- `SuavoAgent.Core.exe`
- `SuavoAgent.Broker.exe`
- `SuavoAgent.Helper.exe`
- `SuavoAgent.Watchdog.exe`
- `SuavoAgent.Maintenance.exe`

Core, Broker, and Watchdog are registered with native MSI service tables. No
script, shell, `sc.exe`, or deferred custom action creates the services. Core
runs as LocalService; Broker and Watchdog run as LocalSystem because their
current runtime responsibilities require the Windows session/token privileges
documented in the service projects. Recovery uses WiX's reviewed utility
action. Windows Installer's native advanced service-configuration tables are
documented as not working correctly, so narrow rollback/deferred/commit actions
invoke the current package's embedded signed maintenance host with one fixed
switch each around the
`InstallServices`/`StartServices` boundary. That host uses `QueryServiceConfig2` and
`ChangeServiceConfig2` directly to apply delayed automatic start and
unrestricted service SIDs to the exact three-service cohort. It snapshots all
prior values into a bounded non-PHI journal beside the protected maintenance
host, verifies every write, and restores touched services in reverse order
before returning any failure. Paired rollback and commit actions restore that
journal after any later MSI failure or remove it only after successful commit.
Service-object ACLs remain native MSI SDDL.

Program files are read-only to standard users. Runtime state lives under
`C:\ProgramData\SuavoAgent` with separate interactive-helper write boundaries.
The ProgramData directory is intentionally retained on uninstall so audit and
diagnostic evidence is not silently destroyed. Product removal deletes the
installed executables and known residue from Program Files.

The destination is the private MSI directory property `InstallFolder`, rooted
at `ProgramFiles64Folder\Suavo\Agent`; it is not a public command-line property.
The historical public `INSTALLFOLDER=` override is explicitly refused before
mutation. The embedded host independently requires the canonical local
`C:\Program Files\Suavo\Agent` path and rejects redirection or reparse points.

## Connect the installed workstation

The MSI owns binaries and service registration. The installed maintenance app
owns configuration only:

- Burn exposes **Launch SuavoAgent** on its success page and launches the
  protected installed host with the fixed --connect-installed switch.
- A direct MSI install creates the all-users
  **Start → SuavoAgent → Connect SuavoAgent** entry for the same graphical flow.
- The UI performs device-code pairing, local readiness/consent, protected
  configuration writes, pending device-key staging, probation health,
  authority confirmation, and a fresh active-health restart. It never downloads
  or replaces the five MSI-owned executables and never creates, deletes, or
  reconfigures their Windows services.
- The configuration transaction snapshots only its four allowlisted
  non-executable files. A rejected authority result restores the prior files and
  pending key. An ambiguous server result is recover-forward only; the UI must
  reconcile authority before accepting another device code.

No secret, pharmacy identifier, credential, pairing code, or PHI is carried in
an MSI property or process argument.

The MSI deliberately makes no LocalSystem changes to historical user shortcuts,
developer-publish folders, legacy services, or legacy processes. Remove any
legacy graphical installation through Windows **Installed apps** before
starting the new lifecycle. A current-package, read-only immediate action runs
after related-product discovery and before `InstallValidate`: it allows an
actually installed related product or repair, but a direct fresh MSI launch
refuses any SuavoAgent service/Broker process and known product registration,
directory, task, or shortcut state. The Windows rehearsal independently checks
the exact historical `suavo-publish\Broker\SuavoAgent.Broker.exe`
process/shortcut shape. Neither boundary kills, relaunches, rewrites, or deletes
legacy state.

Every transaction action receives the same hidden identity derived from the MSI
product code, Restart Manager session key, original database, and canonical
install directory. The current package host arms a protected active token before
mutation. Marker and service-hardening journals are bound to that identity and
move durably from `pending` to `committed`; rollback requires both the current
token and a matching pending journal. A committed tombstone is cleanup-only and
can never restore old state. Receipt sealing and transaction arm share one
protected proof gate, so they cannot race. Arm is create-new only and refuses
any existing token or pending journal. The last commit/rollback finalizer
disarms only after both journals are absent; any seal, restore, or cleanup
failure deliberately leaves the token in place and blocks Release 1 receipt
creation.

## Build inputs

The installer does not infer release identity from wall-clock time. Every build
must provide:

- `SuavoAgentVersion`: an explicit three-part MSI version, such as `3.79.0`.
- `InstallerMetadataTimestampUtc`: an explicit UTC round-trip timestamp, such as
  `2026-07-12T12:00:00.0000000+00:00`.
- `PayloadRoot`: a directory containing the five reviewed publish outputs. It
  defaults to the repository `publish` directory.
- `VCRedistPath`: the reviewed Microsoft VC++ x64 redistributable. The bundle
  build rejects any file whose SHA-256 is not
  `cc0ff0eb1dc3f5188ae6300faef32bf5beeba4bdd6e8e445a9184072096b713b`.
  Its reviewed version is `14.44.35211.0`; Burn installs it when the registered
  x64 runtime is absent or older and skips only an equal or newer runtime.

The metadata tool creates deterministic, UTF-8, no-BOM `binaries.manifest` and
`install-state.json` files from those inputs. The Broker and maintenance host
use them to verify the installed binary cohort.

## Build

Before legal acceptance or compilation, restore and evaluate both WiX projects:

```text
dotnet msbuild installer/SuavoAgent.Installer.Preflight.proj -t:RestoreAndEvaluate
```

This probe never sets `AcceptEula`; it catches malformed MSBuild conditions and
SDK/project-reference evaluation failures before WiX compilation.

Run from the repository root with the .NET 8 SDK. The direct MSI build is one
command (shown on multiple lines only for readability):

```text
dotnet build installer/SuavoAgent.Msi/SuavoAgent.Msi.wixproj -c Release -p:AcceptEula=wix7 -p:SuavoAgentVersion=3.79.0 -p:InstallerMetadataTimestampUtc=2026-07-12T12:00:00.0000000+00:00 -p:PayloadRoot=C:\reviewed\publish
```

The bootstrapper build additionally needs the pinned VC++ runtime:

```text
dotnet build installer/SuavoAgent.Bundle/SuavoAgent.Bundle.wixproj -c Release -p:AcceptEula=wix7 -p:SuavoAgentVersion=3.79.0 -p:InstallerMetadataTimestampUtc=2026-07-12T12:00:00.0000000+00:00 -p:PayloadRoot=C:\reviewed\publish -p:VCRedistPath=C:\reviewed\vc_redist.x64.exe
```

WiX v7 stops before compilation until its OSMF EULA has been accepted. The
`AcceptEula=wix7` property is deliberately not embedded in either project: the
legal owner must review and explicitly authorize acceptance for each build
environment. WiX documents additional sponsorship terms for organizations over
its revenue threshold. See the official [WiX OSMF terms](https://docs.firegiant.com/wix/osmf/)
before enabling a release build.

Run the platform-independent authoring and metadata tests with:

```text
dotnet test installer/tests/SuavoAgent.Installer.Tests/SuavoAgent.Installer.Tests.csproj -c Release
```

Run the service-hardening transaction tests with:

```text
dotnet test tests/SuavoAgent.Setup.Tests/SuavoAgent.Setup.Tests.csproj -c Release --filter FullyQualifiedName~MsiServiceHardeningTests
```

MSI ICE validation and install/repair/upgrade/uninstall proof must run on a clean
x64 Windows VM. Follow [WINDOWS-VALIDATION.md](WINDOWS-VALIDATION.md).

For the repeatable signed-artifact slice, run the repository-owned rehearsal
from an elevated PowerShell 7 session on a disposable Windows 11 x64 machine:

```text
.\scripts\Invoke-SuavoAgentInstallerRehearsal.ps1 -InstallerKind Bundle -InstallerPath C:\reviewed\SuavoAgent-Setup.exe -MsiPath C:\reviewed\SuavoAgent-v3.93.0-win-x64.msi -ExpectedReleaseTag v3.93.0 -AllowedSignerSha256 <approved-certificate-sha256> -EvidenceDirectory C:\SuavoAgent-Rehearsal
```

The rehearsal refuses any pre-existing SuavoAgent service, Program Files
cohort, installer transaction journal, exact legacy Broker process/shortcut, or
known legacy product registration, directory, and task. It validates both
signed installer inputs, exact installed hashes, service accounts and hardening, the MSI-bound
Release 1 marker, real file-loss repair, the rule that repair cannot mint
fresh-install proof, rollback-journal cleanup, uninstall, and exact
ProgramData-sentinel preservation. Its JSON summary is PHI-negative; MSI and
Burn logs remain separate evidence. This is one automated slice of the larger
matrix, not a substitute for reboot, upgrade rollback, Defender,
accessibility, or graphical pairing proof.

## Deployment rules

- Sign both the MSI and bootstrapper with the MKM Authenticode release identity
  backed by the approved certificate/HSM. Unsigned artifacts are test-only.
- Never pass pairing codes, access tokens, pharmacy identifiers, credentials,
  or PHI through public MSI properties or process arguments. Windows Installer
  logs command-line properties.
- Pairing and workstation configuration remain the installed authenticated
  device-code UI flow. Do not reintroduce the legacy full installer,
  binary-download, or service-registration path behind `--connect-installed`.
- Release only after the exact payload cohort, installer artifacts, and
  SBOM/provenance are archived under the same version.

Primary WiX references: [SDK-style projects](https://docs.firegiant.com/wix/tools/msbuild/),
[service installation](https://docs.firegiant.com/wix/schema/wxs/serviceinstall/),
[service control](https://docs.firegiant.com/wix/schema/wxs/servicecontrol/),
[advanced service configuration warning](https://docs.firegiant.com/wix/schema/wxs/serviceconfig/),
[major upgrades](https://docs.firegiant.com/wix/schema/wxs/majorupgrade/), and
[Burn bundles](https://docs.firegiant.com/wix/tools/burn/).
