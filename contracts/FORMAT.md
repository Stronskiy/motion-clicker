# Motion Clicker project domain contract

Status: **pre-release domain subset**, schemaVersion **1**. This is the shared
Swift/C# contract for Task 1/2, not a published or complete `.motionclicker`
bundle format. It describes source events, recorded monitor geometry, linked
clips and pure RunPlan compilation. Recording/encoding, media PTS, dropped-frame
metadata, edit history, recovery, bundle I/O, archive import/export and filesystem
containment checks are not implemented here. A future published format must
freeze its complete contract; incompatible changes require a new schema version.
Do not silently reinterpret a published version.

`project.schema.json` uses JSON Schema draft 2020-12 for structural checks.
Compilation also performs the semantic checks below. Passing JSON Schema alone
does not make a project executable. All fixtures are public synthetic data.

## Scalars and encoding

- UTF-8 JSON, exact case-sensitive property and enum names. Required properties
  have no wire defaults, even when convenience constructors have defaults.
- Every time/duration and `revision` is an exact signed **Int64** JSON integer,
  restricted to 0…9223372036854775807 (positive where specified). Never parse these
  through a binary64/JavaScript `Number`; values above 2^53 must remain exact.
- Orders are integers 0…2147483647. Schema version is exactly the integer `1`.
- Source IDs match `[A-Za-z0-9][A-Za-z0-9._-]{0,127}`. Project, monitor, take,
  track, clip and event IDs share one globally unique entity namespace. Reusing
  a source event in a different clip does not duplicate the source entity.
- Atomic group IDs have the same syntax and a separate namespace. Each group is
  one down/up pair plus its optional drag moves. `linkGroupId` has another
  separate namespace, shared by related clips.
- Coordinates, dimensions, deltas, speed and scale are finite binary64 JSON
  numbers. NaN/Infinity and arithmetic overflow are invalid. Names/device hints
  are display strings, not identity or matching authority.
- Optional properties may be absent or JSON `null`. Swift emits absent optionals.
  JSON member order and indentation do not matter. Unknown members are allowed
  as non-executable metadata; current Codable readers ignore and do not preserve
  them. Known-field round trips are supported. New execution semantics require a
  version change, not an unknown property. Unknown enum values fail decoding.
- JSON syntax/type/required-field failures throw the platform decoding error;
  decoded model validation/compilation throws typed `ProjectError`.

## Serialized types

All properties listed here are required unless marked `?`. Array order is not an
implicit scheduler priority. An empty project has empty arrays, not missing keys.

| Type | Exact properties |
| --- | --- |
| `MotionProject` | `schemaVersion`, `id`, `revision`, `monitorSlots: [MonitorSlot]`, `takes: [Take]`, `tracks: [Track]`, `clips: [TimelineClip]`, `events: [RecordedAction]` |
| `Point` | `x: number`, `y: number` |
| `Size` | `width: number`, `height: number` |
| `Rect` | `origin: Point`, `size: Size` |
| `DisplayGeometry` | `origin: Point`, `size: Size`, `scaleFactor: number`, `rotationDegrees: integer` |
| `MonitorSlot` | `id`, `name: string`, `geometry: DisplayGeometry`, `deviceHint?: string` |
| `Take` | `id`, `durationUs: Int64`, `monitorSlotIds: [ID]`, `media: [TakeMedia]` |
| `TakeMedia` | `monitorSlotId: ID`, `relativePath: string` |
| `Track` | `id`, `name: string`, `monitorSlotId: ID`, `order: integer` |
| `TimelineClip` | `id`, `trackId: ID`, `takeId: ID`, `sourceInUs: Int64`, `sourceOutUs: Int64`, `timelineStartUs: Int64`, `speed: number`, `linkGroupId?: ID` |
| `RecordedAction` | `id`, `takeId: ID`, `monitorSlotId: ID`, `timeUs: Int64`, `order: integer`, `kind: ActionKind`, `x: number`, `y: number`, `button?: MouseButton`, `atomicGroupId?: ID`, `deltaX?: number`, `deltaY?: number` |
| `PlannedAction` | `id: occurrence ID`, `sourceEventId: ID`, `clipId: ID`, `takeId: ID`, `monitorSlotId: ID`, `timeUs: Int64`, `sourceTimeUs: Int64`, `trackOrder: integer`, `eventOrder: integer`, `kind: ActionKind`, `x: number`, `y: number`, `button?: MouseButton`, `atomicGroupId?: occurrence group ID`, `deltaX?: number`, `deltaY?: number` |
| `ProjectError` | `code: ErrorCode`, `path: string`, `message: string` |

`PlannedAction` is an immutable value snapshot; the plan is a JSON array of these
values in dispatch order. Its coordinate values remain local to a monitor slot;
they are not silently mapped to a current runtime display.

### Action variants

`ActionKind`: exactly `move`, `buttonDown`, `buttonUp`, `scroll`.
`MouseButton`: exactly `left`, `right`, `middle`.

| kind | Variant constraints in addition to common properties |
| --- | --- |
| `move` | `button`, `deltaX`, `deltaY` absent/null; optional `atomicGroupId` only for a drag move |
| `buttonDown` | non-null `button` and `atomicGroupId` required; `deltaX`, `deltaY` absent/null |
| `buttonUp` | non-null `button` and `atomicGroupId` required; `deltaX`, `deltaY` absent/null |
| `scroll` | non-null finite `deltaX`, `deltaY` required; `button`, `atomicGroupId` absent/null |

`x/y` always identify the pointer's local monitor position. Scroll deltas are
separate canonical logical scroll units, positive x right and positive y down;
adapters must convert OS-specific line/pixel units before recording. There is no
high-level click/drag variant or `durationUs` on raw events: duration is the
difference between paired down/up source times. Even an instantaneous click has
two ordered raw events.

## Geometry and monitor references

Canonical geometry has an upper-left origin, x increasing right, y increasing
down, in logical desktop units. Negative desktop origins are valid. `size`
describes the displayed logical rectangle **after rotation**; allowed rotations
are 0, 90, 180, 270 degrees. Width, height and scaleFactor must be positive and
finite; origin plus size must remain finite. Scale is physical pixels per logical
unit, stored as evidence, not an instruction to scale silently at run time.

Every event satisfies `0 <= x < width` and `0 <= y < height` for its slot. No
clamping or spill into another display is allowed. A slot stores the recorded
geometry used by its takes. Different recorded geometries require separate
slots and a later explicit runtime mapping; mutating a used slot's geometry is
not a substitute for remapping. Device hints and runtime IDs are not permanent
monitor identity.

`desktopPoint(local:displayOrigin:)` is finite checked translation:
`desktop = origin + local`. It has no size parameter and cannot validate display
bounds; compilation and callers with geometry must do that. Physical pixel,
rotation and platform coordinate-system adaptation belong to later OS adapters.

Preview aspect fit uses `s = min(viewport.width/source.width,
viewport.height/source.height)`, centers the image in the viewport and preserves
the limiting viewport dimension exactly to avoid a rounding overflow. The other
dimension is multiplied by `s`. Local-to-preview uses the normalized local
fraction of each dimension; the inverse removes the fit origin and uses the
normalized image fraction. Right/bottom edges and letterbox bars are excluded.
Invalid/degenerate/nonfinite geometry throws. Transforms are pure and never send
input. Non-integral round trips should be compared with a numeric tolerance.

## Takes, tracks and linked clips

A take has positive duration and at least one distinct existing monitor slot.
Every source event is in `[0, take.durationUs)` and references a monitor in that
take. Every track references an existing slot. A track represents that monitor's
linked video/action lanes; its clip uses the same source range for both. Detached
video/action editing is outside this domain subset.

Each clip references an existing track and take, and its track monitor must
belong to the take. The source range is **half-open**
`[sourceInUs, sourceOutUs)`, with `0 <= sourceInUs < sourceOutUs <= take.durationUs`.
`timelineStartUs >= 0`, `speed == 1`. Both the complete timeline end and every
mapped timestamp must fit Int64. There is no scenario retiming.

Events selected by a clip match its take and track monitor, and lie in its source
range. Mapping is exact integer arithmetic:

```
planned.timeUs = clip.timelineStartUs + (event.timeUs - clip.sourceInUs)
planned.sourceTimeUs = event.timeUs
planned.id = clip.id + ":" + event.id
```

Multiple clips may reuse a source event. Occurrence IDs remain unambiguous
because source IDs cannot contain `:`. The compiler never changes the take or
rewrites historical video.

Clips sharing non-null `linkGroupId` represent one synchronized occurrence across
monitor tracks. They must use identical take, source range and timelineStartUs,
and distinct monitor slots. A singleton linked group is allowed. Unlinked clips
are independent occurrences. These rules make a cross-display drag explicit:
down, moves and up can reference different slots while sharing one atomic group.

For each source atomic group, source order is `(timeUs, order, id)`. There must be
exactly one down and one up, the same button and take, with down first and up
last; all drag moves lie between them. The monitor can change. For each selected
occurrence whose monitor set intersects the group and whose range intersects its
held interval, **every** group event must be retained across that occurrence's
linked clips. Trimming away either edge, omitting a required monitor clip, or
retaining a middle slice of a held interval throws `atomicGroupSplit`, even if
that slice contains no raw event. A completely disjoint trim omits the group.

Planned group identity is `clip.id + ":" + sourceGroupId` for an unlinked clip,
or `"link:" + linkGroupId + ":" + sourceGroupId` for linked clips. These forms
cannot collide; linked clones need new link IDs. Splitting/remapping/undo commands
are future tasks; this compiler validates the resulting values only.

## Determinism and safe ownership

Sort planned actions ascending by **`(timeUs, trackOrder, eventOrder, id)`** with
ordinal ASCII ID comparison. All IDs are restricted to ASCII. Never use input
array position or locale-sensitive comparison. Track and event orders need not
be unique; occurrence ID is the final tie-breaker.

There is one sequential desktop mouse stream. In the sorted plan, a down acquires
its atomic group, grouped moves and the matching up retain/release it. No second
down, ungrouped move, other-group move, or scroll may interleave while a button
is owned. A grouped move/up before its down is a conflict. Simultaneous holds
and multi-button chords are conservatively rejected in this subset. Consecutive
clicks and cross-display drag within one group are valid. A source-valid group
whose track order would place up before down at the same time is rejected by
plan ownership validation. This is a safety rule, not parallel physical cursors.

All entities, geometry, source actions and references are validated before a
plan is returned, including currently unused events. No partial plan is returned
on failure. Runtime topology/permissions/hotkey/readiness checks are not performed
by this pure compiler and remain mandatory in later execution layers.

## Media scope and failures

`Take.media` may be empty for the current synthetic/domain-only subset. Entries
reference distinct monitor slots belonging to the take. `relativePath` must
start with `media/`, use only ASCII letters, digits, `.`, `_`, `-`, `/`, and have
no empty, `.` or `..` segment. Absolute paths, backslashes, percent escapes, URLs
and drive prefixes are rejected. This is only lexical validation: symlinks,
filesystem existence, codec validity, complete capture coverage, path length,
case collisions, atomic save and archive containment belong to bundle I/O. A
successful compile is not evidence that any video exists or can be opened.

`ErrorCode` values are exactly: `unsupportedVersion`, `invalidID`, `duplicateID`,
`missingReference`, `invalidRange`, `invalidGeometry`, `invalidCoordinate`,
`invalidAction`, `invalidAtomicGroup`, `atomicGroupSplit`, `mouseConflict`,
`unsafeMediaPath`. Paths identify a property/index (e.g.
`tracks[0].monitorSlotId`), or a validated entity/group (`clips.<id>`,
`atomicGroups.<id>`, `plan.<occurrenceId>`). Messages are human-readable and are
not a cross-platform equality contract. When an input has multiple errors, the
choice of first error is not a portability guarantee.

## Conformance fixtures

`fixtures/cases.json` enumerates project inputs and corresponding expected
plans or error expectations. Error expectations contain only `code` and `path`.
Geometry cases contain input and hand-derived expected points/fit rectangles.
JSON array equality is semantic (object member order is irrelevant); absent and
null optional fields are equivalent. Fixtures cover the empty project, two-track
ties with ID/order tie-breaks, linked half-open boundaries, raw cross-display drag
and independent scroll deltas, missing monitor and negative-origin letterboxing.
Tests additionally cover invalid values and arithmetic overflow. These are pure
domain checks, not GUI/capture/input or complete bundle conformance evidence.
