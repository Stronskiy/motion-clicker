# Portable capture contract — manifestVersion 1

This is the synthetic-testable recording foundation, not a capture adapter or a
finished recorder. Project schemaVersion 1 and FORMAT.md remain unchanged.
No screen capture, encoding, input injection or UI is implemented here.

## Execution and numeric rules

The caller serializes every session/store call on one executor. These objects
are not thread safe. Adapters must provide verified native source/host anchors;
callback arrival time is never an anchor. All times are exact signed Int64 JSON
integers; all counts/sequence numbers are Int64 except event order (Int32).
JSON uses the existing exact integer codec and case-sensitive camelCase enums;
required fields have no wire defaults. Unknown fields are ignored. Optional
fields may be absent/null. Finite numbers and ASCII IDs follow FORMAT.md.

`MediaClockMapper(sourceAnchorTicks, hostAnchorUs, ticksPerSecond, epochHostUs)`
has `map(sourceTicks) -> Int64`. Scale is 1...2147483647. Mapping is
`hostAnchorUs - epochHostUs + floor((sourceTicks-sourceAnchorTicks)*1000000/scale)`.
Checked quotient/remainder arithmetic avoids multiplying the whole tick delta.
Unrepresentable intermediate subtraction/result is an error, never saturation.
Floor preserves pre-epoch submicrosecond samples as negative. Late media PTS0
keeps its positive take offset. Nothing passes through Double.

## Public values (same wire fields on both platforms)

Swift names below use lowerCamelCase; C# members/methods use PascalCase and
`long`/`int`, `string`, arrays in place of Int64/Int32, String, Swift arrays.
Swift value initializers and C# record constructors take listed fields in order.
No constructor defaults except explicitly optional fields (`= nil` / `= null`).
RuntimeDisplay itself is not Codable; RecordingTarget is a runtime snapshot.

| Type | Fields / values |
|---|---|
| RecordingTarget | `slot: MonitorSlot`, `display: RuntimeDisplay` |
| ClockAnchor | `sourceAnchorTicks: Int64`, `hostAnchorUs: Int64`, `ticksPerSecond: Int64` |
| CapturedPointerEvent | `hostTimeUs: Int64`, `nativePosition: Point`, `kind: ActionKind`, `button?: MouseButton`, `deltaX?: Double`, `deltaY?: Double`, `isOwnInjected: Bool = false`, `isControl: Bool = false` |
| RawPointerRecord | `sequence: Int32`, `event: CapturedPointerEvent`, `timeUs: Int64`, `action?: RecordedAction`, `atomicGroupId?: String` |
| RecordingState | `preparing`, `recording`, `finalizing`, `completed`, `interrupted`, `failed` |
| GapKind | `leading`, `dropped`, `backpressure`, `sourceFailure`, `encoderFailure` |
| WriterStatus | `succeeded`, `failed` |
| RecordingGap | `startUs: Int64`, `endUs: Int64`, `kind: GapKind`, `reason?: String` |
| StreamManifest | `monitorSlotId: String`, `relativePath: String`, `clock: ClockAnchor`, `mediaTimeZeroUs?: Int64`, `encodedDurationUs: Int64`, `frameCount: Int64`, `gaps: [RecordingGap]`, `writerStatus: WriterStatus`, `failureReason?: String` |
| RecordingManifest | `manifestVersion: Int32`, `takeId: String`, `state: RecordingState`, `epochHostUs: Int64`, `durationUs: Int64`, `targets: [MonitorSlot]`, `streams: [StreamManifest]`, `rawEventCount: Int64`, `incompleteGroupIds: [String]`, `warnings: [String]`, `reason?: String` |
| RecordingResult | `manifest: RecordingManifest`, `take: Take`, `events: [RecordedAction]` (on-demand projection, not wire storage) |
| StoreRecovery | `stagingPath: String`, `mediaPath: String`, `manifestPath: String`, `mediaPublished: Bool`, `manifestPublished: Bool` (runtime diagnostic) |
| StoreOperation | `append`, `flush`, `moveMedia`, `publishManifest` (fault-injection boundary) |

Recording failures throw Swift `RecordingError(message: String)` or C#
`RecordingException(string message)`. I/O exceptions retain their native cause.
Messages are diagnostics, not cross-platform equality contracts.

## Session API

Swift throws on fallible APIs; C# uses exceptions. Swift reference class
`RecordingSession(takeId: String, targets: [RecordingTarget], journal: EventJournal)`
(C#: `IEventJournal`) exposes read-only `state`, `result: RecordingManifest?`.
Session freezes targets by value; C# copies the input array. Source order follows
this snapshot. Each target must have distinct slot/display IDs, valid geometry,
and nonoverlapping NativeBounds; slot geometry must equal display geometry.
Take ID must differ from all slot IDs. Generated event/group IDs are UUID-based.

- `sourceReady(monitorSlotId: String, clock: ClockAnchor)`; once per source, preparing only.
- `eventTapReady()`; preparing only, idempotent.
- `begin(epochHostUs: Int64)`; fails without changing state unless ALL sources and tap ready.
- `preparationFailed(reason: String)`; preparing -> failed, idempotent terminal; no manifest/take.
- `receive(event: CapturedPointerEvent)`; ignores preparing/finalized, own/control, pre-epoch.
- `acceptedFrame(monitorSlotId: String, sourceTicks: Int64, encodedTimeUs: Int64, durationUs: Int64)`.
- `idle(monitorSlotId: String, sourceTicks: Int64)`; explicit idle, never creates gaps.
- `reportGap(monitorSlotId: String, startTicks: Int64, endTicks: Int64, kind: GapKind, reason: String? = nil)`; dropped/backpressure only.
- `sourceFailed(monitorSlotId: String, hostTimeUs: Int64, reason: String)`; marks source loss and stops all.
- `stop(hostTimeUs: Int64, reason: String? = nil)`; preparing -> failed; recording -> finalizing; repeated stop is a no-op.
- `writerFinished(monitorSlotId: String, status: WriterStatus, encodedDurationUs: Int64, reason: String? = nil)`; finalizing only; identical repeated terminal calls ignored, conflicting duplicates throw.
- `project() -> RecordingResult`; final result only; streams journal on demand and excludes every event belonging to an incomplete group.

Accepted frames require strictly increasing source times and nondecreasing
encoded times with no overlap. The first encoded timestamp must be zero;
`mediaTimeZeroUs` is its mapped source timestamp. Each frame's source time must
equal mediaTimeZeroUs + encodedTimeUs (within one microsecond for native clock
rounding); adapters must preserve source timing in encoded PTS. Positive frame
durations/count and writer duration are checked. Native frame arrival order does
not define event order. Pre-epoch frames are discarded before encoder metadata.
Final writer duration must cover accepted frame ends. No frame payloads or frame
arrays are retained. Gaps are explicit metadata; static/VFR spacing is not loss.

Pointer coordinates map proportionally from selected NativeBounds to frozen
local logical size; right/bottom edges are excluded, no clamping/fallback.
One button ownership group may be active. Down/moves/up crossing selected
monitors share that group. Own/control events do not affect order or ownership.
A held group crossing an unselected display is journaled with null action and
interrupts recording. Malformed actions, second down, unmatched up, timestamp
reversal/overflow and journal failure fail visibly and stop the session. Every
held group at stop is incomplete; no release is synthesized. Projection retains
raw history but omits the entire incomplete group. Duration is positive and
exceeds every emitted event. Equal timestamps retain capture sequence.

Finalization waits for every selected writer terminal, including failed writers.
Zero frames or failed writer interrupts the take. Known gaps complete with
warnings. Session failure stays failed even if all writers succeed. Result take
includes only successful nonempty media; manifest retains failures. Final result
is immutable by value (C# consumers receive defensive copies). Raw journal
failure suppresses executable projection entirely. Projection is deliberately
on-demand and may allocate event arrays; recording itself does not.

## Journal/store API and publication

`EventJournal` / `IEventJournal`: `append(record: RawPointerRecord)`, `flush()`,
`read(visit: (RawPointerRecord) throws -> Void)` (C#: Action visitor).
`RecordingStore(root: URL, takeId: String, fault: (StoreOperation) throws -> Void = no-op)`
(C#: string root, Action<StoreOperation> optional) implements this interface.
Read-only `recovery: StoreRecovery` exposes recoverable locations even after
failure. `mediaFile(monitorSlotId: String) -> URL` (C#: string) reserves an
exclusive `<slot>.mp4` file in staging for the future encoder. The adapter opens
this existing file without replacing its inode; closes before publish.
`publish(manifest: RecordingManifest) -> URL` returns published manifest path;
retry after failure is allowed, repeated successful identical publish is a no-op,
conflicting manifest is rejected. `close()` (C#: Dispose) closes journal handles.

Bundle root must already be an ordinary directory; resolve ancestor aliases to
one canonical root, reject a symlink root, reject links/reparse points within
all used paths, case-folded collisions, invalid IDs, existing target take/media.
Do not allow concurrent mutation of the bundle by external processes while a
store owns staging; containment is revalidated before each operation, but this
portable layer is not a hostile-process filesystem sandbox. No other writer may
edit this store's files. No prior project.json or published file is opened for
writing. All staging is on the destination filesystem.

Store owns only `.capture-<uuid>/media`, `.capture-<uuid>/metadata/events.jsonl`
and `.capture-<uuid>/metadata/manifest.json`. Raw UTF-8 JSONL is streamed and
flushed durably. `publish` validates terminal manifest, ID, exact stream paths,
nonempty successful media files, journal count, and duration. It flushes, writes
and flushes the manifest in staging, then atomically moves the uniquely named
media directory to `media/<takeId>`, then moves metadata directory to
`takes/<takeId>` without replacing any destination. Manifest visibility is the
commit marker, last. No claim of a two-directory transaction or full power-loss
recovery: directory durability depends on the OS/filesystem. Readers enumerate
only `takes/<takeId>/manifest.json`. Failed media/publication leaves explicit
staging/orphan information in recovery; previous save bytes remain unchanged.
An append/flush error poisons the store (publication forbidden), since a partial
journal write cannot safely be retried. No automatic deletion of recoverable data.

Synthetic fixture cases and disposable filesystem tests exercise both platforms.
Actual capture/encoding, Windows GUI, permissions, sleep/topology, ten-minute
physical two-monitor sync (goal <=50 ms at30fps), memory/storage acceptance and
Record/Open UI remain OPEN for subsequent slices.
