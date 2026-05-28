#!/usr/bin/env bash
#
# bundle.sh — assemble SuavoAgent.app, sign, build DMG, notarize, staple.
#
# Expects:
#   - $VERSION       — e.g. v3.15.0-rc1 (with v prefix)
#   - $ARCH          — osx-arm64 or osx-x64
#   - $PUBLISH_DIR   — directory containing the dotnet publish output
#   - $APPLE_TEAM_ID — Apple Developer Team ID (10 chars)
#   - $APPLE_ID      — Apple ID email for notarization
#   - $APPLE_APP_PASSWORD — app-specific password for notarytool
#   - $APPLE_DEVELOPER_ID_APPLICATION_IDENTITY — keychain identity name (string after `security find-identity -v`)
#
# Produces:
#   ./bin/SuavoAgent-${VERSION}-${ARCH}.dmg (signed, notarized, stapled)
#
# Run via the .github/workflows/release.yml macOS matrix job after the
# dotnet publish step.

set -euo pipefail

# --- Inputs --------------------------------------------------------------

: "${VERSION:?VERSION env var is required (e.g. v3.15.0-rc1)}"
: "${ARCH:?ARCH env var is required (osx-arm64 or osx-x64)}"
: "${PUBLISH_DIR:?PUBLISH_DIR env var is required}"
: "${APPLE_TEAM_ID:?APPLE_TEAM_ID env var is required}"
: "${APPLE_ID:?APPLE_ID env var is required}"
: "${APPLE_APP_PASSWORD:?APPLE_APP_PASSWORD env var is required}"
: "${APPLE_DEVELOPER_ID_APPLICATION_IDENTITY:?APPLE_DEVELOPER_ID_APPLICATION_IDENTITY env var is required}"

# Defensively strip ALL whitespace (incl. trailing \n / \r) from the
# notarization credentials. `gh secret set` / `echo` pipelines silently
# append a newline, which notarytool rejects as "401 Invalid credentials"
# (see feedback-vercel-env-add-newline-trap). App-specific passwords,
# emails, and team IDs never contain whitespace, so this is lossless.
APPLE_ID="$(printf '%s' "$APPLE_ID" | tr -d '[:space:]')"
APPLE_APP_PASSWORD="$(printf '%s' "$APPLE_APP_PASSWORD" | tr -d '[:space:]')"
APPLE_TEAM_ID="$(printf '%s' "$APPLE_TEAM_ID" | tr -d '[:space:]')"

# Length diagnostics — values stay masked (registered as GH secrets), but
# lengths reveal newline contamination. Expected: TEAM_ID=10,
# APP_PASSWORD=19 (xxxx-xxxx-xxxx-xxxx). APPLE_ID = email length.
echo "diag: APPLE_ID length          = ${#APPLE_ID}"
echo "diag: APPLE_APP_PASSWORD length = ${#APPLE_APP_PASSWORD} (expect 19: xxxx-xxxx-xxxx-xxxx)"
echo "diag: APPLE_TEAM_ID length      = ${#APPLE_TEAM_ID} (expect 10)"

VERSION_NUMERIC="${VERSION#v}"
VERSION_NUMERIC="${VERSION_NUMERIC%%-*}"  # strip pre-release suffix
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
WORK_DIR="$REPO_ROOT/installer/macos/work"
APP_BUNDLE="$WORK_DIR/SuavoAgent.app"
DMG_OUTPUT="$REPO_ROOT/installer/macos/bin/SuavoAgent-${VERSION}-${ARCH}.dmg"

echo "==> bundle.sh"
echo "    VERSION         = $VERSION"
echo "    VERSION_NUMERIC = $VERSION_NUMERIC"
echo "    ARCH            = $ARCH"
echo "    PUBLISH_DIR     = $PUBLISH_DIR"
echo "    APP_BUNDLE      = $APP_BUNDLE"
echo "    DMG_OUTPUT      = $DMG_OUTPUT"

# --- Workspace setup -----------------------------------------------------

rm -rf "$WORK_DIR"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources/LaunchDaemons"
mkdir -p "$APP_BUNDLE/Contents/Resources/LaunchAgents"
mkdir -p "$(dirname "$DMG_OUTPUT")"

# --- Copy binaries from dotnet publish ---------------------------------

echo "==> Copying binaries from $PUBLISH_DIR"
# Helper is intentionally absent on Mac in Phase 1 (FlaUI is Windows-only).
# All other binaries are best-effort — Phase 1 ships the packaging path;
# real Mac runtime port is v5+.
for bin in SuavoAgent.Core SuavoAgent.Broker SuavoAgent.Helper SuavoAgent.Watchdog SuavoSetup; do
    if [[ -f "$PUBLISH_DIR/$bin" ]]; then
        cp "$PUBLISH_DIR/$bin" "$APP_BUNDLE/Contents/MacOS/"
        echo "    copied: $bin"
    else
        echo "    skipped: $bin (not present in publish output)"
    fi
done

# --- Render Info.plist + plists from templates -------------------------

echo "==> Rendering Info.plist"
sed \
    -e "s/__VERSION__/$VERSION_NUMERIC/g" \
    -e "s/__APPLE_TEAM_ID__/$APPLE_TEAM_ID/g" \
    "$SCRIPT_DIR/Info.plist.template" \
    > "$APP_BUNDLE/Contents/Info.plist"

cp "$SCRIPT_DIR/LaunchDaemon.plist.template" "$APP_BUNDLE/Contents/Resources/LaunchDaemons/com.suavollc.agent.core.plist"
cp "$SCRIPT_DIR/LaunchAgent.plist.template"  "$APP_BUNDLE/Contents/Resources/LaunchAgents/com.suavollc.agent.helper.plist"

# --- Icon ---------------------------------------------------------------

if [[ -f "$REPO_ROOT/installer/shared/SuavoAgent.icns" ]]; then
    cp "$REPO_ROOT/installer/shared/SuavoAgent.icns" "$APP_BUNDLE/Contents/Resources/"
else
    echo "WARN: No SuavoAgent.icns found at installer/shared/ — bundle ships with default icon"
fi

# --- Sign nested binaries first, then bundle ---------------------------
#
# Order matters: nested libraries + helper executables must be signed
# before the parent bundle, otherwise codesign rejects the bundle.

echo "==> Signing nested binaries"
for nested in \
    "$APP_BUNDLE/Contents/MacOS/SuavoAgent.Broker" \
    "$APP_BUNDLE/Contents/MacOS/SuavoAgent.Helper" \
    "$APP_BUNDLE/Contents/MacOS/SuavoAgent.Watchdog" \
    "$APP_BUNDLE/Contents/MacOS/SuavoSetup"
do
    if [[ -f "$nested" ]]; then
        codesign \
            --sign "$APPLE_DEVELOPER_ID_APPLICATION_IDENTITY" \
            --options runtime \
            --entitlements "$SCRIPT_DIR/entitlements.plist" \
            --timestamp \
            --force \
            "$nested"
    else
        echo "    skipped (not present): $nested"
    fi
done

echo "==> Signing main executable + bundle"
# If Core didn't publish, use SuavoSetup as the bundle's main executable
# (the .app needs SOMETHING runnable to satisfy macOS).
if [[ ! -f "$APP_BUNDLE/Contents/MacOS/SuavoAgent.Core" ]]; then
    if [[ -f "$APP_BUNDLE/Contents/MacOS/SuavoSetup" ]]; then
        echo "==> WARN: SuavoAgent.Core missing, using SuavoSetup as bundle main executable"
        sed -i '' 's|<string>SuavoAgent.Core</string>|<string>SuavoSetup</string>|' "$APP_BUNDLE/Contents/Info.plist"
    else
        echo "==> ERROR: neither SuavoAgent.Core nor SuavoSetup present — cannot build bundle"
        exit 1
    fi
fi

MAIN_EXE_NAME=$(plutil -extract CFBundleExecutable raw "$APP_BUNDLE/Contents/Info.plist")
codesign \
    --sign "$APPLE_DEVELOPER_ID_APPLICATION_IDENTITY" \
    --options runtime \
    --entitlements "$SCRIPT_DIR/entitlements.plist" \
    --timestamp \
    --force \
    "$APP_BUNDLE/Contents/MacOS/$MAIN_EXE_NAME"

codesign \
    --sign "$APPLE_DEVELOPER_ID_APPLICATION_IDENTITY" \
    --options runtime \
    --entitlements "$SCRIPT_DIR/entitlements.plist" \
    --timestamp \
    --force \
    "$APP_BUNDLE"

# --- Verify signature ---------------------------------------------------

echo "==> Verifying bundle signature"
codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"
spctl --assess --type execute --verbose "$APP_BUNDLE" || {
    echo "WARN: spctl assess failed — common before notarization completes; continuing"
}

# --- Build DMG ----------------------------------------------------------

echo "==> Building DMG"
if ! command -v create-dmg >/dev/null 2>&1; then
    echo "ERROR: create-dmg not installed. Run: brew install create-dmg" >&2
    exit 1
fi

create-dmg \
    --volname "SuavoAgent ${VERSION_NUMERIC}" \
    --window-pos 200 120 \
    --window-size 600 400 \
    --icon-size 100 \
    --icon "SuavoAgent.app" 175 200 \
    --hide-extension "SuavoAgent.app" \
    --app-drop-link 425 200 \
    --skip-jenkins \
    "$DMG_OUTPUT" \
    "$APP_BUNDLE"

# --- Sign DMG -----------------------------------------------------------

echo "==> Signing DMG"
codesign \
    --sign "$APPLE_DEVELOPER_ID_APPLICATION_IDENTITY" \
    --timestamp \
    --force \
    "$DMG_OUTPUT"

# --- Submit to notarization with retries -------------------------------

echo "==> Submitting DMG to Apple notarization (this can take 5-30 min)"
NOTARIZE_LOG="$WORK_DIR/notarize.log"
ATTEMPT=0
MAX_ATTEMPTS=3
while (( ATTEMPT < MAX_ATTEMPTS )); do
    ATTEMPT=$((ATTEMPT + 1))
    echo "==> notarytool attempt $ATTEMPT of $MAX_ATTEMPTS"
    if xcrun notarytool submit "$DMG_OUTPUT" \
        --apple-id "$APPLE_ID" \
        --team-id "$APPLE_TEAM_ID" \
        --password "$APPLE_APP_PASSWORD" \
        --wait \
        --timeout 30m \
        2>&1 | tee "$NOTARIZE_LOG"; then

        # Check for "Accepted" status in the log; notarytool exits 0
        # even on rejection if the submission was accepted (just
        # rejected later). We re-parse to be sure.
        if grep -q "status: Accepted" "$NOTARIZE_LOG"; then
            echo "==> Notarization succeeded on attempt $ATTEMPT"
            break
        fi
    fi
    if (( ATTEMPT == MAX_ATTEMPTS )); then
        echo "ERROR: Notarization failed after $MAX_ATTEMPTS attempts" >&2
        exit 1
    fi
    echo "==> Notarization attempt $ATTEMPT failed, retrying in 60s"
    sleep 60
done

# --- Staple the notarization ticket ------------------------------------

echo "==> Stapling notarization ticket"
xcrun stapler staple "$DMG_OUTPUT"
xcrun stapler validate "$DMG_OUTPUT"

# --- Final verification ------------------------------------------------

echo "==> Final spctl assessment"
spctl --assess --type install --verbose "$DMG_OUTPUT" \
    && echo "==> DMG accepted by Gatekeeper" \
    || { echo "ERROR: Final spctl assessment failed" >&2; exit 1; }

echo ""
echo "==> Success: $DMG_OUTPUT"
ls -lh "$DMG_OUTPUT"
