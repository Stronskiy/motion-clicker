# Recorded directory bundle — bundleVersion 1 (prerelease)

This initial-save boundary consumes RECORDING.md and preserves project domain
schemaVersion 1 unchanged. It implements one owned directory ending exactly in
`.motionclicker`, one finalized published take, and one initial project commit.
It is not the future editing/autosave/archive format. No capture or input starts
on create/save/open. Native codec/metadata/playback verification is a later gate.

## Layout and commit

`create(root)` exclusively creates a previously nonexistent directory under an
existing parent, and exclusively writes `.bundle-owner` containing exactly
`motionclicker-recorded-bundle-v1\n`. The caller chooses this destination before
constructing RecordingStore. A failed create can leave its own recoverable root;
there is no destructive cleanup or adoption of existing unrelated directories.
RecordingStore publishes immutable `media/<takeId>/` and
`takes/<takeId>/{manifest.json,events.jsonl,gaps.jsonl}` as in RECORDING.md.

`saveInitial` accepts the published RecordingResult, revalidates source files,
and checks that the supplied result equals the projection of that evidence.
It writes a new `.project-<uuid>` file exclusively, durably flushes it, then
atomically renames it without replacement to `project.json` on the same
filesystem. This file is the initial-project commit marker. Its required wire
fields are `bundleVersion: 1`, `manifestPath: "takes/<takeId>/manifest.json"`, and
`project: MotionProject`. Exact integer/required-field/enum rules are inherited
from FORMAT.md and RECORDING.md. Unknown fields are ignored, never execution
semantics. `project.json` is never overwritten, including on repeated save.
Failure leaves staged/source bytes for explicit inspection/retry; source media,
manifest and journals are never opened for writing. No two-directory atomicity,
directory-fsync/power-loss guarantee, or bounded OS flush/rename latency is claimed.

Domain-only prerelease JSON is explicitly imported with the existing
`JSONDecoder().decode(MotionProject.self, from:)` / `ProjectJson.Deserialize<MotionProject>`
and then domain-validated. It is not a recorded bundle and `coldOpen` rejects it;
no default manifest, offset, status, or timing basis is invented.

## Exact public APIs

Swift `public enum RecordedBundleStore` exposes static throwing methods:

- `create(root: URL)`
- `saveInitial(root: URL, result: RecordingResult, validationScratch: URL, fault: (BundleOperation) throws -> Void = { _ in }) -> RecordedBundleSnapshot`
- `coldOpen(root: URL, validationScratch: URL) -> RecordedBundleSnapshot`
- `recoveryEntries(root: URL) -> [URL]`

C# `public static class RecordedBundleStore` exposes `void Create(string root)`,
`RecordedBundleSnapshot SaveInitial(string root, RecordingResult result, string validationScratch, Action<BundleOperation>? fault = null)`,
`RecordedBundleSnapshot ColdOpen(string root, string validationScratch)`, and
`string[] RecoveryEntries(string root)`. C# fields use PascalCase.

`BundleOperation`: `writeProject`, `flushProject`, `publishProject` (C# PascalCase).
`RecordedBundleAvailability`: `requiresMonitorMapping`, `partial` (C# PascalCase).
`RecordedBundleMedia`: `monitorSlotId: String`, `file: URL` (C# string),
`mediaTimeZeroUs: Int64`, `encodedDurationUs: Int64`.
`RecordedBundleSnapshot`: `project: MotionProject`, `manifest: RecordingManifest`,
`manifestFile: URL` (C# string), `eventsFile: URL`, `gapsFile: URL`,
`media: [RecordedBundleMedia]`, `availability: RecordedBundleAvailability`.
Snapshots are detached values; C# array/model properties return defensive copies.
These are runtime values, not additional wire fields. Errors use RecordingError/
RecordingException, domain/codec errors or native I/O causes.

## Projection and availability

Preserve target order and frozen geometry, exact take duration, successful-stream
media order, source event IDs/times/order and pointer timing metadata in the raw
journal. Omit the entire incomplete group from project events as RecordingSession
project() does. Initial project revision is 0. Entity IDs are allocated in order
for project (`bundle-project`), then each target's track (`bundle-track-<index>`)
and clip (`bundle-clip-<index>`): start with that stem and append `-1`, `-2`, ...
until absent from all source entity IDs and previously allocated IDs. Every
track uses target name/order/slot; every clip is linked with `bundle-link`,
source `[0,take.durationUs)`, timeline start 0 and speed 1. ColdOpen verifies
this exact minimal projection rather than accepting arbitrary edited projects.

Only completed, fully successful, balanced raw capture has
`requiresMonitorMapping`; interrupted/failed capture is inspectable `partial`
and cannot run. Even `requiresMonitorMapping` is not execution authorization:
explicit current-monitor binding, native media inspection, permissions/topology
and runtime readiness are required. Stored runtime hints never auto-bind inputs.
Each resolved successful media file is associated with its stream's exact
mediaTimeZeroUs and encodedDurationUs; failed streams remain in manifest with
status/reason/offset evidence but have no successful media resolution.

## Read-only validation and recovery

ColdOpen constructs no RecordingStore/session/native writer. It validates owner,
version, strict references, complete manifest/events/gaps counts/previews, raw
ownership/IDs/scalars/clock/timing, projected domain and exact initial association.
Expected successful media must be ordinary nonempty files. This does not prove
codec decodability. Referenced take/media trees reject symlinks/reparse points,
case-folded aliases, unsupported file types and lexical escapes. Filesystem IDs
use RecordingStore's portable rules. Ancestor aliases are canonicalized; root
itself must be an ordinary directory. The caller must prevent concurrent mutation;
this is not a hostile-process filesystem sandbox.

`validationScratch` is an existing ordinary directory outside the bundle (also
not an ancestor of it); validation owns a unique disposable child there and
removes only that child. Journal identity-sort scratch never touches the bundle.
Project event materialization is explicit here; video remains on disk.

Recovery discovery returns sorted confined paths for `.capture-*`, `.project-*`,
and uncommitted `media/`/`takes/` entries, without deleting, adopting, or traversing
unknown capture contents. It includes unmatched constructor staging. A source
manifest without project.json is recoverable evidence, not a saved scenario.
Use the explicit result and SaveInitial again after correcting the failure;
committed projects are opened, never silently overwritten. Unknown data remains
untouched. A save error is not reported as success merely because source publish
succeeded. Physical/UI/ten-minute/both-OS recording acceptance remains open.
