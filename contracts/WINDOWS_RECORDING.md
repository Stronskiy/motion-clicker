# Native Windows recording API (Task3 contract)

This contract specifies the Windows11 x64 native adapter source slice. It does not
claim that the app has Record/Stop/Save/Open controls or that real capture/input
and physical timing acceptance have been exercised. Existing RECORDING.md is
binding; no project or recording wire/schema change is introduced.

## Public surface

The planned library namespace is `MotionClicker.Windows.Recording`, targeting
`net10.0-windows10.0.26100.0`, minimum Windows11 22000, x64.

- `WindowsRecordingOptions(nuint OwnInjectedTag, Func<WindowsPointerSample,bool> IsControl)`.
  Both arguments required. The classifier must be fast, thread-safe, synchronous
  and free of UI calls; the later recording UI supplies a snapshot of control
  regions. Existing Windows runtime tag is `0x4d434c49434b`.
- `WindowsPointerSample(long ObservationHostUs, uint PacketTimeMs,
  Point NativePosition, ActionKind Kind, MouseButton? Button,
  double? DeltaX, double? DeltaY, nuint SourceTag)` is immutable. NativePosition
  is global physical desktop pixels. Original packet DWORD is exact diagnostics.
- `WindowsRecordingController(WindowsRecordingOptions options)` is inert,
  single-use, and implements `IAsyncDisposable`.
- `Task<WindowsRecordingPreparation> PrepareAsync(RecordingTarget[] targets,
  string root, string takeId, CancellationToken cancellation = default)` freezes
  targets, validates the exact current monitor identity/native rectangle/scale/
  rotation, reserves new staging paths and initializes writers. It does not
  capture a pixel, install a hook, inject input, request permission or change
  settings. Partial failures expose recoverable staging.
- `Task StartAsync(CancellationToken cancellation = default)` explicitly starts
  native sources and the passive mouse hook. One common epoch is established
  after all readiness acknowledgments. The ten-second initialization deadline
  remains until every selected source supplies a genuine post-epoch image;
  pre-epoch images are never relabeled to initialize a static display.
- `Task<WindowsRecordingCompletion?> StopAsync(string? reason = null)` requests
  idempotent stop and returns the same completion. Null means no epoch was
  established. A non-null reason interrupts rather than completing the take.
- `Task<WindowsRecordingProgress> SnapshotAsync()` returns bounded state/counts.
- `Task<StoreRecovery?> RecoveryAsync()` exposes staging even on failure.
- `Task<RecordingResult> ProjectAsync()` requires published completion and is
  the explicit optional allocation of full executable events.
- `WindowsRecordingPreparation(WindowsRecordingDisplay[] Displays)`;
  `WindowsRecordingDisplay(string MonitorSlotId, string DisplayId,
  int PixelWidth, int PixelHeight)`;
  `WindowsRecordingProgress(RecordingState State, long AcceptedFrames,
  long DroppedFrames, string? Reason)`;
  `WindowsRecordingCompletion(RecordingManifest Manifest, string ManifestPath,
  StoreRecovery Recovery)`.

Only a named serial owner accesses RecordingSession/RecordingStore, including
projection/publication. API calls do not depend on the WinUI dispatcher. Native
resources have separate bounded work transport. Exact resource bounds and
shutdown behavior must be recorded here alongside the implementation before
source-gate completion; no unspecified unbounded callback/task queue is allowed.

## Clock and pointer rules

QPC host microseconds use signed Int128 rational arithmetic and checked Int64
results, flooring negative fractions. QueryPerformanceFrequency must be positive.
No Stopwatch floating-point conversion or wallclock anchor is used. WGC
SystemRelativeTime is documented compositor QPC time in TimeSpan100ns units;
portable video anchor is `(0,0,10000000)`. Epoch uses the same QPC host domain;
encoded sample timestamps subtract only the first accepted source timestamp.
The manifest retains its true positive take offset. Callback arrival never
rebases video. No public arbitrary clock anchor override exists.

The FIRST executable low-level mouse hook statement samples QueryPerformanceCounter,
before packet decoding/classifier/owner queue/I/O. New raw events declare
nativeHostObservation and queryPerformanceCounter/windowsLowLevelMouseTime/
milliseconds metadata. PacketTimeMs uses invariant unsigned decimal string,
including UInt32.max; wrap/origin has no effect on canonical time. OwnerDelayUs
is nonnegative hook-entry-to-owner elapsed time, never OS delivery latency.

Move/drag, left/right/middle down/up and wheel axes match existing WinInput.
Unsupported buttons, conflicting ownership, selected/unselected held crossing,
queue overflow, hook/session/source loss interrupt honestly. Control-owned
presses suppress their whole real gesture through matching up; recorded holds
retain their real up even over control regions. Own-tag events never alter
ownership. Stop never manufactures historical releases. Equal observations
retain queue sequence. Frozen NativeBounds map to local logical coordinates
through existing portable session mapping; divided monitor origins are not used
as a shared desktop coordinate system.

## Encoder capability selection

A real Windows synthetic probe confirmed Vortice3.8.3/CsWinRT2.2 device and surface
ABI wrapping, generated WARP pixels and completed SoftwareBitmap deep copy.
The MediaTranscoder Baseline path preserved regular30fps PTS/count/duration but
created extra images for VFR and a single long static sample, so it is rejected.
The controller approved probing direct MF IMFSinkWriter through the already
pinned Vortice.MediaFoundation3.8.3; this is still a capability experiment until
its static/VFR/late/count/duration/noaudio native readback passes.

No screen/hook/personal-content probe is authorized here. No failed or zero-frame
writer becomes successful; no prior file may be overwritten. If a muxer cannot
represent an actual discontinuity, the adapter must fail that continuation and
preserve the valid prefix and entire loss interval rather than stretching stale
pixels or inserting placeholders. Static source spacing alone is not a loss.

## Primary source evidence

- [WGC compositor QPC source time](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.capture.direct3d11captureframe.systemrelativetime?view=winrt-26100)
- [QPC domain and conversion guidance](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps)
- [Completed surface deep copy](https://learn.microsoft.com/en-us/uwp/api/windows.graphics.imaging.softwarebitmap.createcopyfromsurfaceasync?view=winrt-26100)
- [MF sink writer per-sample timing](https://learn.microsoft.com/en-us/windows/win32/medfound/tutorial--using-the-sink-writer-to-encode-video)
- [Exclusive byte stream plus informational extension URL](https://learn.microsoft.com/en-us/windows/win32/api/mfreadwrite/nf-mfreadwrite-mfcreatesinkwriterfromurl)

Common clock identity is not physical timing proof. Live monitor capture,
permissions/revocation, actual Windows GUI, mixed DPI/topology/session/sleep,
long-run memory/storage and ten-minute/two-monitor<=50ms remain UNVERIFIED until
controlled runtime evidence. The native capability probe is not the next user
build or the requested finished recorder.
