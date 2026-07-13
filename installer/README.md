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
invoke the signed maintenance host with one fixed switch each around the
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

Successful MSI commit also invokes one fixed native cleanup mode before pairing
is optional. It inspects only the exact historical Suavo.lnk candidates and
stops only the exact
...\suavo-publish\Broker\SuavoAgent.Broker.exe process. Unclassified same-name
shortcuts/processes and the developer-publish directory are preserved. Because
cleanup is commit-only, an MSI rollback never touches the old interactive
launch.

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
