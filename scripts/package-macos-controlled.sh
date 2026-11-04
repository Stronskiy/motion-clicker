#!/bin/bash
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
source_head="$(git -C "$repo" rev-parse HEAD)"
output="$repo/dist/controlled/$source_head/macos"
if [ -e "$output" ]; then echo "Refusing to replace existing package output: $output" >&2; exit 1; fi
mkdir -p "$output"
staging="$(mktemp -d "$output/.package-stage.XXXXXX")"
trap 'rm -rf "$staging"' EXIT
"$repo/scripts/build-macos-app.sh" "$staging/Motion Clicker.app"
swift build --package-path "$repo/macos" -c release --product MotionClickerFixture
binary_dir="$(swift build --package-path "$repo/macos" -c release --show-bin-path)"
cp "$binary_dir/MotionClickerFixture" "$staging/MotionClickerFixture"
codesign --force --sign - "$staging/MotionClickerFixture"
codesign --verify --strict "$staging/MotionClickerFixture"
file "$staging/Motion Clicker.app/Contents/MacOS/MotionClicker" "$staging/MotionClickerFixture"
codesign -dvv "$staging/Motion Clicker.app" 2>&1
cp "$repo/contracts/FIXTURE.md" "$staging/CONTROLLED-ACCEPTANCE.md"
cp "$repo/LICENSE" "$staging/LICENSE"
swift --version > "$staging/toolchain.txt"
xcrun --show-sdk-version >> "$staging/toolchain.txt"
python3 "$repo/scripts/write-package-identity.py" --root "$staging" --platform macOS --toolchain "$(cat "$staging/toolchain.txt")" --signing 'ad-hoc codesign verified; no Developer ID signing or notarization performed'
archive="$output/MotionClicker-controlled-macos-arm64-$source_head.zip"
# One archive contains the complete native app, separate fixture and identity.
(cd "$staging" && ditto -c -k . "$archive")
cp "$staging/artifact-identity.json" "$output/artifact-identity.json"
(cd "$output" && shasum -a 256 "$(basename "$archive")" artifact-identity.json > SHA256SUMS)
printf '%s\n' "$archive"
