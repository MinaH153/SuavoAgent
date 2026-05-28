# SuavoAgent v4 installer

Cross-platform packaging for the v4 install/distribution overhaul. See
`docs/superpowers/specs/2026-05-28-suavoagent-v4-consumer-install.md` for the
full spec and Codex review history.

## Layout

```
installer/
├── README.md                       # this file
├── windows/                        # WiX 4 MSI project
│   ├── SuavoAgent.wxs              # Product definition + features
│   ├── Services.wxs                # Win32 service registration (Core/Broker/Watchdog)
│   ├── DataDirectories.wxs         # %ProgramData% ACLs
│   ├── ManagedSettings.wxs         # managed.json scaffolding
│   ├── UI.wxs                      # WiX UI extension config
│   └── README.md                   # Windows-specific build notes
├── macos/
│   ├── bundle.sh                   # .app bundle + .dmg + notarize pipeline
│   ├── Info.plist.template         # .app Info.plist with substitution markers
│   ├── LaunchDaemon.plist.template # com.suavollc.agent.core LaunchDaemon
│   ├── LaunchAgent.plist.template  # com.suavollc.agent.helper LaunchAgent (per-user)
│   ├── entitlements.plist          # hardened runtime + sandbox config
│   └── README.md                   # Apple Developer cert setup guide
└── shared/
    └── version.props               # MSBuild props file for version substitution
```

## Phase 1 success criteria

- One release tag (`v3.15.0-rc1`) produces, via a single CI run:
  - `SuavoAgent-v3.15.0-rc1-win-x64.msi` (EV-signed via SSL.com eSigner)
  - `SuavoAgent-v3.15.0-rc1-osx-arm64.dmg` (Developer ID signed + notarized)
  - `SuavoAgent-v3.15.0-rc1-osx-x64.dmg` (Developer ID signed + notarized)
- All artifacts pass platform verification:
  - Windows: `signtool verify /pa /v <msi>` → PASS, EV cert chain visible
  - macOS: `spctl --assess --type install <dmg>` → "accepted, source=Notarized Developer ID"
- Install on clean test machines (Joshua's W11 VM + MacBook):
  - MSI: `msiexec /i` installs services, About box shows "MKM TECHNOLOGIES LLC" verified publisher
  - DMG: drag-to-Applications works, first launch passes Gatekeeper without warnings, LaunchDaemon starts on next login

Phase 1 does NOT include device-code auth (Phase 3), model auto-download
(Phase 4), or auto-update (Phase 5). Phase 1 installs the binaries
correctly and registers services; behavior post-install is identical to
v3.14.6 today.
