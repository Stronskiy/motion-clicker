#!/bin/bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
swift build --package-path "$repo/macos" -c release --product MotionClicker
binary_dir="$(swift build --package-path "$repo/macos" -c release --show-bin-path)"
app="$repo/dist/Motion Clicker.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$binary_dir/MotionClicker" "$app/Contents/MacOS/MotionClicker"
cp "$repo/macos/Resources/Info.plist" "$app/Contents/Info.plist"
cp "$repo/macos/Resources/MotionClicker.icns" "$app/Contents/Resources/MotionClicker.icns"
codesign --force --sign - "$app"
printf '%s\n' "$app"
