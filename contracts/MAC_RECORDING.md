# Native macOS capture adapter

`MotionClickerMacRecording` is a macOS26+ Swift library consuming
[RECORDING.md](RECORDING.md). It implements selected-display ScreenCaptureKit
streams, H.264 MP4 encoding, and a listen-only pointer event tap. No app UI calls
it yet. Native pointer actions use first-hook-entry mach host observation time,
with exact Quartz packet stamps retained as diagnostic data. This enables the
native API without assuming an unverified Quartz-to-mach mapping. This is not
the both-platform Record/Save/Open acceptance milestone.

## API for the future UI

```swift
let recorder = MacRecordingController(options: MacRecordingOptions(
    ownInjectedTag: 0x4d434c49434b, // existing MacInput.tag, supplied by caller
    isControl: classifyControlSample // caller supplies a fast @Sendable classifier
))
// MacRecordingController.pointerClockStatus == .nativeHostObservation.
let prepared = try await recorder.prepare(targets: targets, root: bundleRoot, takeId: takeID)
try await recorder.start() // deliberate user action; real capture starts here
let progress = await recorder.snapshot()
let completion = try await recorder.stop(reason: nil)
let recovery = await recorder.recovery()
let result = try await recorder.project() // optional whole-event projection
```

The initializer is inert. `prepare(targets: [RecordingTarget], root: URL,
takeId: String) async throws -> MacRecordingPreparation` checks permission
preflight, freezes exact selected display IDs/geometry/pixel sizes and creates
owned staging/writers. It neither listens to input nor captures pixels nor
requests permissions. Errors `.screenRecordingPermissionMissing` and
`.inputMonitoringPermissionMissing` are recoverable by the user; no System
Settings window is opened. It may leave diagnostic staging on an encoder or
storage failure; `recovery() async -> StoreRecovery?` exposes it.

`start() async throws` resolves only when every stream and the pointer tap is
ready, then establishes one common epoch. This readiness barrier alone does not
promise a post-epoch image from a static source. The original10-second start
deadline remains active until every selected source supplies a genuine
post-epoch image. An early source that sent its sole image before the barrier
and then supplies only Idle is interrupted with the missing source IDs at that
deadline; the old image is never relabeled. No supported guaranteed post-barrier
refresh protocol has yet been established. Canceling the start task requests
stop. The controller is single-use; another take requires another instance.
`stop(reason: String? = nil) async throws -> MacRecordingCompletion?` is
idempotent; nil means stopped before an epoch. `MacRecordingCompletion` contains
`manifest`, `manifestURL`, and `recovery`. Native shutdown timeout interrupts the
take. Encoder timeout throws and leaves unpublished staging because file closure
is unknown; cancellation continues off the owner queue. No incomplete file is
published as successful media. `project() async throws -> RecordingResult`
requires published completion and explicitly allocates the event projection.

`snapshot() async -> MacRecordingProgress` exposes `state`, `acceptedFrames`,
`droppedFrames`, and `reason`, without reading the whole journal.
`MacRecordingPreparation.displays` contains `monitorSlotId`, `displayId`,
`pixelWidth`, and `pixelHeight`. App UI access is asynchronous and does not require
its main runloop for stop/finalization progress.

The `@Sendable isControl: (MacPointerSample) -> Bool` classifier must be fast,
synchronous, thread-safe and free of UI mutations; capture a value snapshot of
control regions. A down classified as control excludes its entire gesture up
through the matching release, even outside that region. A recorded gesture
crossing a control region retains its real release. Repeated/mismatched ownership
fails visibly. Pre-epoch samples cannot change classification state. The immutable
sample exposes immutable `observationHostUs: Int64` and original diagnostic
`timestampNs: UInt64`, upper-left global logical `position`,
`kind`, `button`, pixel `deltaX`/`deltaY`, and `sourceTag`. Scroll signs match the
project's existing MacInput convention (opposite CG wheel axes). No keys/text are
subscribed; no events are posted. Unsupported extra buttons interrupt capture.

## Ownership, bounded retention and publication

The controller's dedicated serial owner exclusively accesses RecordingSession,
RecordingStore, encoders and native bookkeeping. The portable mutable objects
remain non-Sendable. Native callbacks hand off scalar metadata or synchronously
borrow a buffer on that owner; there are no per-frame Tasks or unbounded frame
closure queues. A weak callback relay prevents retaining a discarded controller.
The tap owns a separate CFRunLoop thread. Native lifecycle callbacks use a separate
serial queue. Neither owner nor lifecycle queue waits for the other's stop
callbacks. Disabled taps are stopped, never silently revived.

SCK `queueDepth` is3 per display. A lock permits at most2 concurrent image
handoffs. The engine retains at most1 pending pixel buffer per selected display.
The next source timestamp or stop determines its exact VFR duration, then an
explicitly timed CMSampleBuffer is appended. Session metadata advances only after
that append succeeds. Idle does not invent loss or synthetic frames. Rejected
encoder/admission work increments dropped count and journals backpressure.
An admission loss ends the preceding valid image at the dropped source time and
retains one unresolved interval per stream. Idle cannot close it. The next actual
image or stop closes it; a subsequent drop closes the previous interval and opens
another, preserving every dropped update's evidence without an unbounded list.
Manifest previews and complete gap journals retain the portable64-entry policy.
AVFoundation's internal buffers are platform-owned; measured ten-minute resident
memory/storage acceptance remains open.

A first accepted frame has filePTS0 and retains its actual positive take offset
in the manifest; rejected frames do not establish an origin. Source spacing is
preserved. Pending static frames receive real durations through stop. Tests check
coded sample durations, not merely movie/track edit-list duration: `endSession`
alone can create empty edit markers rather than extend an image.

The raw-H.264 AVAssetWriter path also stretches an earlier image across sparse
PTS gaps even when supplied an explicit short duration. Actual synthetic
readback demonstrated100ms becoming4s. Direct EmptyMedia marker attempts caused
native synthetic test aborts and were removed. Until a supported muxing path is
verified, `videoDiscontinuityUnsupported` rejects a gap greater than1microsecond
before appending the later image; the engine marks that writer failed and the
take interrupted, keeping full loss intervals. A loss lasting to stop preserves
only the known-valid prefix and its full trailing gap. One microsecond allows
integer timestamp rounding, not a claim of missing image continuity. No black
placeholder or duplicated frame is invented. Continuing native video after a
loss remains unsupported; explicit failure is preferable to false footage.

Each generation accepts readiness once and rejects late callbacks after stopping.
Native readiness, shutdown and encoder finalization have10-second callback
deadlines. An encoder timeout requests cancellation on its dedicated cleanup
queue: SDK `cancelWriting` is blocking, so it never runs on the owner. Such a take
is quarantined in staging, with an error/recovery location; it is not published.
These deadlines are not a hard real-time guarantee for synchronous filesystem
operations or a stalled platform append call. OS/process hangs and power loss
remain outside the portable store's guarantees.

Writer terminals are applied only after capture shutdown acknowledgment or its
failure deadline, so a shutdown failure cannot become a completed manifest.
Successful and failed writers are drained once. Raw held input stays incomplete;
no historical release is synthesized. The store publishes only after known writer
closure, then closes journal handles. Previous project files are never rewritten.

## Native configuration and clock evidence

IDs are exactly `String(CGDirectDisplayID)` as in existing MacMonitors. Missing,
mirrored, changed, rotated/rescaled or incompatible pixel geometry fails closed;
there is no primary-display fallback. H.264 requires positive even dimensions.
Each stream targets30fps, whole selected display, BGRA, SDR with ITU-R709 color
space; writer color primaries/transfer/matrix are explicitly709. There is no audio
or microphone stream. The app's own application surfaces are excluded through
SCContentFilter; this is not a claim of real source-image continuity.

Primary installed macOS26.2 SDK evidence:

- ScreenCaptureKit/SCStream.h, `SCStreamFrameInfoDisplayTime`: mach absolute time
  when the window server displayed the frame; Complete/Idle/Blank/Suspended/
  Started/Stopped are distinct states.
- CoreMedia/CMSync.h: CMClockGetHostTimeClock uses `mach_absolute_time`; exact
  system-unit conversion functions are also documented there.
- CoreGraphics/CGEventTypes.h: CGEventTimestamp is only described as roughly
  nanoseconds since startup. It does not establish sleep-domain equivalence.
- AVFoundation/AVAssetWriter.h: status/error reads are thread-safe;
  `cancelWriting` blocks and may delete the partial output file.

Mach ticks are converted with UInt64 full-width rational arithmetic and checked
Int64 results, never Double; the portable anchor is `(0,0,1000000)` over the
converted absolute microseconds. All displays use the same host epoch.
The FIRST native tap callback statement samples `mach_absolute_time()`, before
context/type/tag classification or owner handoff. Checked rational conversion
produces `MacPointerSample.observationHostUs`; the owner and classifier carry it
without resampling. `timestampNs` retains original CGEventTimestamp as UInt64,
including0 and UInt64.max (diagnostic only). Owner entry minus observation time
is recorded as `PointerTiming.ownerDelayUs`, never as OS/hardware latency.

`public enum MacPointerClockStatus: Sendable` has cases `unverified` (reserved
source-mapping capability) and `nativeHostObservation`; the controller's static
read-only `pointerClockStatus` returns `.nativeHostObservation`. There is no public
anchor/Boolean override. The separate internal packet-to-host conversion remains
fail-closed without verified source mapping, including a500ms wrong-domain stamp.
Observation capture does not invoke that conversion. Exact new portable wire and
initializer contracts are in RECORDING.md; native takes declare
`pointerTimingBasis: nativeHostObservation` and event timing metadata with
`machAbsolute / cgEventTimestamp / nanoseconds` plus unsigned decimal packet stamp.

Timestamping before synchronous owner handoff protects this event from its own
owner wait. The next callback may still arrive late because of OS delivery,
coalescing, owner contention, or tap scheduling. No synthetic result establishes
physical latency. Controlled ten-minute/two-physical-monitor normal/stressed-load
measurements on named hardware against an independent reference remain mandatory;
<=50ms is unchanged. Source-created CGEvent-to-mach mapping remains unverified and
is not inferred from observation timing, source-age counters, or constructor probes.

Sleep/wake/session-resign and display-change notifications interrupt capture.
A500ms lifecycle poll checks permission revocation, exact topology and divergence
between continuous and absolute time since start (1ms read-latency tolerance).
Continuous time is used only to detect suspend, never as a media/event anchor.

## Verification boundary

Automated tests use fake sources, scalar metadata, temporary stores and generated
64×64 pixels through the real local H.264 encoder/AVAssetReader. They never call
public native capture start, create event taps, read personal screen pixels, post
input or request/change OS permissions. Compile checks cover real SDK APIs.

Actual selected-monitor capture, mixed-DPI/rotation, HDR→SDR visual fidelity,
listen-only event timestamps, permissions/revocation, sleep/session/topology
interruptions, physical multi-monitor synchronization, long-run memory/storage,
installed-app behavior, UI save/reopen, and Windows capture remain consent/runtime
acceptance work. No unattended capture is authorized by this document or tests.
