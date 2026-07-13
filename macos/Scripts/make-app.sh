#!/bin/zsh
# Build QuickMail.app from the SwiftPM package.
#   ./make-app.sh          -> release build to macos/build/QuickMail.app
#   ./make-app.sh debug    -> debug build
#   ./make-app.sh run      -> release build + launch
set -euo pipefail

SCRIPT_DIR="${0:a:h}"
PKG_DIR="$SCRIPT_DIR/../QuickMailMac"
OUT_DIR="$SCRIPT_DIR/../build"
APP="$OUT_DIR/QuickMail.app"

CONFIG=release
[[ "${1:-}" == "debug" ]] && CONFIG=debug

echo "Building ($CONFIG)…"
swift build --package-path "$PKG_DIR" -c "$CONFIG" --product QuickMailMac

BIN="$PKG_DIR/.build/$CONFIG/QuickMailMac"
VERSION="0.1.0"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/QuickMail"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>QuickMail</string>
    <key>CFBundleIdentifier</key><string>net.theideaplace.quickmail</string>
    <key>CFBundleName</key><string>QuickMail</string>
    <key>CFBundleDisplayName</key><string>QuickMail</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>14.0</string>
    <key>NSPrincipalClass</key><string>NSApplication</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>LSApplicationCategoryType</key><string>public.app-category.productivity</string>
</dict>
</plist>
PLIST

# Sign with a stable identity so Keychain access survives rebuilds.
# Override with QM_SIGN_IDENTITY; falls back to ad-hoc if none found.
IDENTITY="${QM_SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" ]]; then
    IDENTITY=$(security find-identity -v -p codesigning \
        | awk -F'"' '/Developer ID Application/ {print $2; exit}')
fi
if [[ -n "$IDENTITY" ]]; then
    echo "Signing with: $IDENTITY"
    codesign --force --options runtime --timestamp --sign "$IDENTITY" "$APP"
else
    echo "No Developer ID identity found; ad-hoc signing (Keychain will re-prompt after each rebuild)"
    codesign --force --sign - "$APP"
fi
codesign --verify --deep --strict "$APP" && echo "Signature verified"
echo "Built $APP"

if [[ "${1:-}" == "run" ]]; then
    open "$APP"
fi
