# Native Windows recording API (Task3 contract)

This contract specifies the Windows11 x64 native adapter source slice. It does not
claim that the app has Record/Stop/Save/Open controls or that real capture/input
and physical timing acceptance have been exercised. Existing RECORDING.md is
binding; no project or recording wire/schema change is introduced.

## Implemented source boundary

The public controller below is implemented, with an injected platform seam for
fake lifecycle faults and generated-only native encoder tests. Production uses
exact frozen HMONITOR WGC sources, owned BGRA copies, a dedicated passive pointer
hook, an independent watchdog, a named serial session/store owner and bounded
same-thread MF encoder workers. No app recording UI is present. Compiling these
paths and exercising generated pixels does not establish live capture, hook,
Windows GUI, permissions, session/sleep/topology or physical timing acceptance.

Direct MF Baseline with processor frame-rate conversion disabled preserves actual
regular/VFR/one-long-static/late input images, coded PTS/duration and no audio.
The writer creates the file exclusively, owns its COM apartment/thread, submits
immutable sample-owned CPU buffers, retains failure, and finalizes once. Native
readback confirms a rejected sparse continuation leaves the valid100ms prefix.
The controller runs each writer on its dedicated bounded worker.
The portable timeline itself retains exactly one image, delays its duration until
the next real source timestamp/loss/stop, and retains full unresolved loss spans.

[Microsoft's hook documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelmouseproc)
states that timeout removal of WH_MOUSE_LL can be silent and cannot be queried.
The controller ruling selects a dedicated fast passive hook thread with independent
pump/callback watchdog. This detects known/suspected loss, not arbitrary silent
removal: a healthy pump is not proof that the hook remains installed. That remains
an explicit diagnostic and physical-acceptance limit. No input sentinel, settings
mutation, silent reinstall or unsupported hook-installed query is used.

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
resources have separate bounded work transport. RecoveryAsync reads the owner's
cached immutable StoreRecovery value without entering its queue, including when
the owner is stalled or disposed. No unspecified unbounded callback/task queue is
used; no per-frame or per-pointer Task fanout occurs.

### Resume lifecycle and transport contract

The serial owner runs on a named background thread, independent of WinUI. Its API
command mailbox holds at most16 commands; a full mailbox rejects the request.
Pointer ingress holds1024 immutable scalar packets; overflow interrupts the take.
Each source holds one pending owned image handoff plus the timeline's one image.
Image and earliest loss are a single atomically drained immutable batch, so a
loss cannot be consumed by an earlier batch while its real image stays queued.
Admission loss retains its earliest source timestamp until owner processing;
it is never confused with a quiet/static display. Encoder workers hold3 input
images, one executing image and3 successful metadata-only acknowledgments each.
The WGC frame pool has2 OS buffers; the source admits only one completed copy at
a time, then returns the original frame/surface. An owned-copy operation, source
mailbox, timeline,3 encoder inputs and executing encoder retain at most7 managed
pixel arrays per source (acknowledgments retain no pixels). OS/MF internal buffer
usage and long-run memory/storage still require runtime measurement. Only native Append success
produces a metadata acknowledgment; only the owner mutates session/store.

Preparation freezes monitor mapping and initializes same-thread encoder workers;
constructors/preparation never start capture or install hooks. Explicit Start
starts native sources; readiness acknowledgments precede one QPC epoch. Start
completes only after all selected sources provide genuine post-epoch images,
with a10s start/image deadline. There is no pre-epoch static-frame relabeling.

Independent watchdog probes every100ms, requires an owned pump acknowledgment
within250ms and interrupts a callback running100ms or longer. Its stop request
does not wait for the owner, UI, encoder or GPU thread. Known installation,
uninstallation, pump exit, desktop/session/power/topology and source errors
interrupt; stale callbacks cannot revive a stopped generation.

Stop is idempotent. Its first QPC boundary admits already-observed pre-stop
metadata/images through native closure; post-stop and closed-generation callbacks
are rejected. A failed QPC stop observation still signals every native producer
and quarantines, without inventing a timestamp. Native producers first acknowledge closure, admissible ingress
is drained, every timeline closes at the stop QPC boundary, and every dedicated
encoder drains/finalizes once. Owner consumes successful acknowledgments before
writer terminals and publication. Encoder terminals explicitly acknowledge native
closure separately from successful Append/counts. Native preparation/start/stop
waits are bounded at10s; an independent watchdog can fail the stop wait even if
the owner is stalled. On unknown native closure, staging is quarantined and Stop fails with recoverable
paths available through RecoveryAsync. No manifest/media publication, deletion,
resource disposal under live native work or prior-save overwrite occurs. A blocked
OS call cannot safely be killed: its background worker retains its resources and
performs closure itself if the call returns. Quarantined disposal allows100ms
for a responsive owner then returns while a blocked owner retains its resources.
Once all native closures are acknowledged and serial store publication begins,
portable synchronous filesystem flush/move calls are not forcibly cancellable;
their latency is an explicit remaining OS-I/O bound, not a10s publication promise.
No prior save is overwritten and failed publication retains store recovery paths.

## Clock and pointer rules

### Fix round1 source admission, pixel layout and terminal ordering

Each native capture generation admits the earliest genuine frame in each exact
source-QPC bucket `floor(sourceTicks * 30 / 10000000)` using Int128. Bucket zero is
the fixed native source-domain zero, never first arrival or a per-display origin.
The common epoch is signaled to each capture source after portable Begin; preepoch
frames do not occupy a bucket. Selection occurs before GPU/CPU copy and bounded
handoff, preserves original selected PTS verbatim, and creates no frame, gap or
drop counter for intentionally unselected updates. This policy covers Windows11
22000 without MinUpdateInterval; the implementation does not depend on that
26100-only optimization. VFR/static durations and actual admitted-frame loss
remain timeline behavior, including sparse continuation rejection.

RGB32 input is contiguous top-down BGRA with explicit positive
MF_MT_DEFAULT_STRIDE = width * 4, verified before SetInputMediaType. Generated
asymmetric encoded video must be decoded and spatial/channel placement checked,
with lossy tolerance, independently of coded PTS/count tests.

After observing every encoder Closed task complete, owner drains acknowledgments
again before any WriterFinished. Each source's accepted acknowledgment count must
equal its native terminal FrameCount; mismatch quarantines before publication.
The internal worker boundary is injectable for deterministic closure/admission
tests. No new public controller API or recording wire field is introduced.

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
The direct MF IMFSinkWriter through pinned Vortice.MediaFoundation3.8.3 passed
static/VFR/late/count/duration/noaudio native readback after disabling the Video
Processor MFT frame-rate conversion with MF_XVP_DISABLE_FRC before BeginWriting.
Sparse continuation remains rejected before native submission. Finalize releases
the sink and its byte stream once; tests reopen the file exclusively before
readback to verify closure. Extra Close after sink release returned E_INVALIDARG
on the actual Windows runner and is deliberately not performed.

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

## Task6 UI integration boundary (in progress)

WinUI controls live in `RecordingUI/`, outside the native adapter's `Recording/`
compile glob. `RecordingWorkspace` is the native-free, injected orchestration
boundary. `IRecordingWorkspaceServices` owns exclusive destination creation,
controller construction and cancellable countdown; `IRecordingTake` owns inert
Prepare, deliberate Start, idempotent Stop, initial project commit, progress and
recovery. Construction and display selection never call a native producer.
`WorkspacePhase` distinguishes native finalization from filesystem Saving; Saved
requires SaveInitial's project commit. The app operation gate must include paused
Run and unresolved owned-input release, and all recording/open work.

Native playback will be selected only after the synthetic generated H264 probe
verifies source readiness, offsets and a native endpoint independent of UI polling.
Candidate is MediaPlayer/MediaPlaybackItem with native DurationLimit, hosted by
WinUI MediaPlayerElement. Source files remain read-only. Complete gapsFile lookup
is authoritative; the first64 manifest gaps are only a validation preview.

### Implemented orchestration decisions

`RecordingTakeStatus?` distinguishes no epoch (null) from a published completed
or partial result. `RecordingWorkspaceSnapshot` is immutable; the app polls it
without UI/native-owner locks. One `AppOperationGate` lease spans countdown,
source readiness, native finalization and project commit. An unacknowledged
native Stop retains `CleanupBlocked` and its lease, with Retry Stop available;
no disposal under live native work is attempted. `ControlRegions` copies physical
rectangles and exposes a fast, allocation-free hook read. `RunnerSnapshot`
includes nullable `OwnedButton`, acquired conservatively before dispatch and
cleared only after successful owned release. Failed state alone is not idle.

The generated probe on Windows26100 confirmed that frame-server MediaPlayer
reaches native DurationLimit107ms after a seek to100ms, ending at exactly107ms
without a UI timer. Default headless rendering opened but did not advance; that
mode is not used as evidence. WinUI hosting will use MediaPlayerElement's native
surface; the same MediaPlaybackItem DurationLimit constrains each inspected clear
interval. System transport commands are disabled, so they cannot bypass the
inspection controller. No known-gap auto-skip is permitted.
