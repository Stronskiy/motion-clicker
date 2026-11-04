#!/bin/bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
source_head="$(git -C "$repo" rev-parse HEAD)"
app="${1:-$repo/dist/staged/$source_head/Motion Clicker.app}"
case "$app" in "$repo"/dist/*/Motion\ Clicker.app) ;; *) echo "Stage bundles under repository dist; installed replacement is a separate operation." >&2; exit 1 ;; esac
if [ -e "$app" ]; then echo "Refusing to overwrite an existing bundle: $app" >&2; exit 1; fi
swift build --package-path "$repo/macos" -c release --product MotionClicker
binary_dir="$(swift build --package-path "$repo/macos" -c release --show-bin-path)"
mkdir -p "$(dirname "$app")"
staging="$(mktemp -d "$(dirname "$app")/.app-stage.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
bundle="$staging/Motion Clicker.app"
mkdir -p "$bundle/Contents/MacOS" "$bundle/Contents/Resources"
cp "$binary_dir/MotionClicker" "$bundle/Contents/MacOS/MotionClicker"
cp "$repo/macos/Resources/Info.plist" "$bundle/Contents/Info.plist"
cp "$repo/macos/Resources/MotionClicker.icns" "$bundle/Contents/Resources/MotionClicker.icns"
plutil -lint "$bundle/Contents/Info.plist"
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$bundle/Contents/Info.plist")" = MotionClicker
test "$(/usr/libexec/PlistBuddy -c 'Print :CFBundleIconFile' "$bundle/Contents/Info.plist")" = MotionClicker
cmp "$repo/macos/Resources/MotionClicker.icns" "$bundle/Contents/Resources/MotionClicker.icns"
codesign --force --sign - "$bundle"
codesign --verify --deep --strict "$bundle"
# Reserve a previously absent name, then atomically replace only that empty reservation.
# A nonempty/running bundle can never be the destination of this rename.
mkdir "$app"
if ! python3 - "$bundle" "$app" <<'PYRENAME'
import os
import sys
os.rename(sys.argv[1], sys.argv[2])
PYRENAME
then
    rmdir "$app" 2>/dev/null || true
    exit 1
fi
printf '%s\n' "$app"
