# Controlled recording target fixture — version 1

This separate native executable is a generated target for a deliberately consented
recording session. It never starts the recorder, captures a screen, injects input,
installs a global listener, records keyboard macros or changes privacy settings.
The recorder's reviewed passive observer is separate. The target logs only ordinary
mouse events delivered to its own canvas. User logs and acceptance media stay local.
Original application assets remain Apache-2.0; see assets/README.md.

## Native packages and safe launch

Build from a clean exact source revision:

- macOS 26+, arm64: `scripts/package-macos-controlled.sh`. Swift 6.2+;
  no external Swift dependencies. The archive contains `Motion Clicker.app`,
  standalone `MotionClickerFixture`, artifact-identity.json and license.
  Launch the separate fixture from Terminal with `./MotionClickerFixture`;
  launch the verified app normally only after session consent. Ad-hoc signature
  validation does not mean Developer ID signing, notarization or Gatekeeper approval.
- Windows 11 build 22000+, x64: `scripts/package-windows-controlled.ps1`.
  Exact .NET SDK is windows/global.json; all NuGet graphs are locked. The archive
  contains separate self-contained MotionClicker.Windows and MotionClicker.Fixture
  directories. Launch each matching `.exe` from its own complete directory.
  Actual Authenticode status is recorded in signing-facts.json.

Outputs: ignored `dist/controlled/<full-source-sha>/<platform>/`. Scripts refuse
existing output and never install, register or overwrite a running app bundle.
Every archive has external SHA256SUMS and an internal file/lock hash manifest.
Different source revisions require regenerated packages. Build reproducibility
means pinned source/toolchain/dependencies and commands, not byte-identical SDK
outputs or signing timestamps. Verify archive SHA-256 before extraction and file
hashes against artifact-identity.json. Never substitute an earlier installed app.

## Deliberate session setup

1. Choose a **new local log folder** in the separate fixture. Select a named real
   display and open a windowed canvas. Repeat for the second physical display.
   Each canvas owns a new exclusive `fixture-<uuid>.jsonl`; nothing is overwritten.
2. Choose **Cover selected display** only when ready. This removes the target's
   normal window border and covers that selected display with generated content;
   it never invokes OS fullscreen or changes display settings. Restore/close stays
   visible. macOS Escape also restores. The fixture never starts covered on launch.
   Check the actual target surfaces, taskbar/menu and every intended pixel before
   recording. A windowed canvas alone does not make the surrounding desktop safe.
3. Select those exact monitors and a new owned project destination in Motion
   Clicker. Start the actual Record only under controlled runtime consent.
   Keep independent recorder Stop/emergency control available. Do not use Run.
4. Within generated canvases, physically perform: left click, right click,
   middle click, double-left click, one drag fully inside the canvas, one left
   click on the second display (or target B on the same display for a short
   single-screen test). Expected **seven down/up pairs**. Movement sampling varies.
   Perform two opposite scroll gestures. Scroll **callback counts are variable**;
   use raw direction and visible target outcomes, never one callback per gesture
   or equivalent wheel units across platforms. One physical pointer remains serial.
5. Stop the recorder; wait for native closure **and initial project commit**.
   Hash media/journals/metadata, exit normally, relaunch the same verified artifact,
   use native Open, explicitly map displays, play/seek/switch source inspection.
   Check offsets, complete gap history, counts and target outcomes. Hash again;
   source bytes must stay immutable. Preview must not add unsolicited fixture clicks.
   This observation cannot prove zero input at every other global target.
6. Close each canvas to flush its target log. A visible LOG FAILED state invalidates
   that reference: stop and retain honest partial evidence. No capture, OS input,
   permission, GUI or physical-timing result is inferred from tests or compilation.

## Exact local JSONL

First line `recordType: header`, `schemaVersion: 1`, platform, canvasId, selected
session displayId, hostClock, coordinateSpace and nativeEventUnits. Each following
line has `recordType: sample`, a contiguous exact integer sequence, exact native
integer hostStamp at target callback delivery, kind (`down`, `up`, `move`, `drag`,
`scroll`, `marker`), optional button (0 left/1 right/2 middle), nativeEventStamp,
canvas x/y, nativeX/Y, inside, deltaX/Y and a fresh geometry snapshot.
Markers have no pointer position. Canvas top-left local coordinates have half-open
bounds; outside delivered points remain unchanged and inside=false, never clamped.
Controls outside the target canvas are deliberately absent from this log.

macOS geometry originX/Y is the canvas bottom-left in AppKit global desktop
**points**, width/height in local points, scale is actual window backing scale.
Mapping: native=(originX+x, originY+height-y); backing scale is **not** applied to
desktop points. HostClock is mach_continuous_time raw ticks with exact timebase
numerator/denominator in nanoseconds per tick. nativeEventStamp preserves the
native NSEvent.timestamp Double description in **seconds**. Scroll deltaX/Y are
native NSEvent.scrollingDeltaX/Y device-dependent values, not canonical gestures.

Windows geometry originX/Y is canvas top-left in native virtual-desktop **pixels**
(client origin via ClientToScreen + XAML canvas offset × current XamlRoot scale).
Width/height and local x/y are DIPs; native=(originX+x×scale, originY+y×scale).
HostClock is QueryPerformanceCounter/Stopwatch ticks with ticksPerSecond.
nativeEventStamp preserves PointerPoint.Timestamp UInt64 decimal **microseconds
since boot**. deltaX/Y preserve horizontal/vertical MouseWheelDelta native units.
Negative origins and mixed scales are retained; physical geometry remains a runtime
check. Selected display IDs are session evidence, not automatic project remapping.

The 10 Hz marker logs a **requested UI reference update** and displays its host
stamp/reference number. It is not a source frame timestamp or scanout measurement.
Every target event changes the visible delivery count, outcome and position marker;
matching those visible states to source video is a separate observation. Target
delivery, hook observation, rendered response, capture and hardware contact are
distinct. Clock agreement alone never proves physical alignment or ≤50ms precision.

## Local analysis and uncertainty

`python3 scripts/analyze-fixture-log.py /local/path/fixture-<id>.jsonl`
validates schema/order/exact integer stamps/geometry and reports complete pairs,
unmatched releases, interrupted holds, scroll callbacks/directions and reference
markers. No output is uploaded. Combine counts from the deliberately chosen
canvases; do not count control gestures as fixture events.

Optional `--observations /local/path/observations.json` accepts a JSON array of
operator-matched measurements in the **same original take-relative microsecond
space**: hookTimeUs, targetDeliveryTimeUs, visibleSourceTimeUs, uncertaintyUs.
Use null for any missing/ambiguous observation. Convert raw clock units using exact
integer ratios and the independently verified recorder take epoch/clock relation;
do not compare unanchored raw clocks. The analyzer reports matched/missing counts,
min/median/p95/p99/max hook/target-to-visible deltas and maximum uncertainty.
It does not discard outliers, adjust source media, infer hardware contact, or mark
an acceptance goal passed. Record frame quantization, missed frames, latency and
manual matching uncertainty with the actual hardware/30fps settings.

Short success does not close the original ten-minute, two-physical-monitor,
≤50ms at30fps, emergency-release, drop/resource/topology and failure acceptance.
Those remain explicit runtime gates, including actual native GUI light/dark/focus,
permissions, coldOpen and historical source immutability on **both OSes**.
