# macOS installer build

Builds a signed + notarized `SuavoAgent-vX.Y.Z-osx-{arm64,x64}.dmg` containing
`SuavoAgent.app` plus the LaunchDaemon (Core) and LaunchAgent (Helper) plists.

## Required GitHub Actions secrets (Joshua must add — manual one-time setup)

These are gated; Mac CI step is skipped if any are missing. Once added, CI
produces signed+notarized DMGs on every `v*` tag push.

| Secret | Description | How to obtain |
|---|---|---|
| `APPLE_TEAM_ID` | 10-char team identifier | https://developer.apple.com/account → Membership → Team ID |
| `APPLE_ID` | Apple ID email used for the org enrollment | (your suavollc / MKM-org Apple ID) |
| `APPLE_APP_PASSWORD` | App-specific password for `notarytool` | https://appleid.apple.com → Sign-In and Security → App-Specific Passwords → Generate one labeled `SuavoAgent-notarytool` |
| `APPLE_DEVELOPER_ID_APPLICATION_P12_BASE64` | Developer ID Application cert + private key | Apple Developer portal → Certificates → +; pick "Developer ID Application"; download `.cer`; in Keychain Access, find cert + private key together, export as `.p12` with a password; then: `base64 -i ./SuavoAgent-DevID-App.p12 \| pbcopy` |
| `APPLE_DEVELOPER_ID_APPLICATION_P12_PASSWORD` | Password chosen during the `.p12` export | (whatever you set during export) |
| `APPLE_DEVELOPER_ID_INSTALLER_P12_BASE64` | Developer ID Installer cert + private key | Same flow as Application, but pick "Developer ID Installer" cert type in the portal |
| `APPLE_DEVELOPER_ID_INSTALLER_P12_PASSWORD` | Password chosen during the Installer `.p12` export | (whatever you set during export) |

## Joshua's manual setup checklist

Tick these and the Mac CI path goes live:

- [ ] Sign into https://developer.apple.com/account with the MKM-org Apple ID — confirm team status is "Active" and team type is "Organization" (not Individual — Individual can sign but the publisher line won't show "MKM TECHNOLOGIES LLC" the way Org will)
- [ ] Go to Certificates, Identifiers & Profiles → Certificates → +
- [ ] Generate a Developer ID Application certificate:
  - Pick "Developer ID Application" under "Software"
  - You'll need to generate a CSR via Keychain Access (Keychain Access → Certificate Assistant → Request a Certificate from a Certificate Authority → save to disk)
  - Upload CSR, download the resulting `.cer`, double-click to import to Keychain
- [ ] Generate a Developer ID Installer certificate the same way (separate cert)
- [ ] In Keychain Access, find both certs. Each cert should have an associated private key (revealed by clicking the disclosure triangle). Select cert + private key together, right-click → Export 2 items as `.p12` with a strong password.
- [ ] Base64 each `.p12`: `base64 -i SuavoAgent-DevID-App.p12 | pbcopy` then paste into GitHub secret. Repeat for the Installer `.p12`.
- [ ] Generate an app-specific password at https://appleid.apple.com → Sign-In and Security → App-Specific Passwords. Label it `SuavoAgent-notarytool`. Save to 1Password too — it's only shown once.
- [ ] Add all 7 secrets to `MinaH153/SuavoAgent` repo settings → Secrets and variables → Actions

Expected one-time setup time: ~30 minutes if you're at your Mac with Keychain Access open.

## What the bundle script does

1. Copies the published `osx-arm64` (or `osx-x64`) .NET binaries into `SuavoAgent.app/Contents/MacOS/`
2. Stamps `Info.plist` with version + bundle ID `com.suavollc.SuavoAgent`
3. Copies LaunchDaemon plist into `SuavoAgent.app/Contents/Resources/LaunchDaemons/`
4. Codesigns nested binaries first (libllama.dylib, libggml.dylib, helpers), then the bundle itself, with the Developer ID Application cert + hardened runtime entitlements
5. Builds the DMG via `create-dmg` (npm package — installed via `brew install create-dmg`)
6. Signs the DMG with the Developer ID Installer cert (`productsign` for .pkg, `codesign` for .dmg)
7. Submits the DMG to Apple notarization via `xcrun notarytool submit --wait`
8. On notarization success, staples the ticket: `xcrun stapler staple SuavoAgent-vX.Y.Z-osx-arm64.dmg`
9. Final artifact: stapled, notarized, signed DMG ready for distribution

Each step has retry logic for notarization (Apple's service occasionally
flakes with "no such record" — `notarytool` retries with exponential backoff
up to 3 attempts).

## Threats addressed by Apple notarization

- macOS Gatekeeper trusts notarized apps without the "unidentified developer" warning that today's unsigned `SuavoSetup.exe` Windows equivalent shows
- A user double-clicking `SuavoAgent.app` on first launch sees the standard "downloaded from the internet" confirmation, then the app launches with no warnings
- The bundle's Designated Requirement string is what the System Keychain entry in Phase 3 will bind credential access to (per Codex 2026-05-28 review §7)
