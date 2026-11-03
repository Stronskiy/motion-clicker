public enum RunPlanCompiler {
    /// Pure compilation of one project revision. This function never accesses a display or sends input.
    public static func compile(_ project: MotionProject) throws -> [PlannedAction] {
        let validated = try ValidatedProject(project)
        var result: [PlannedAction] = []
        let occurrences = Dictionary(grouping: project.clips) { clip in
            clip.linkGroupId.map { "link:\($0)" } ?? "clip:\(clip.id)"
        }
        for key in occurrences.keys.sorted() {
            guard let clips = occurrences[key], let first = clips.first else { continue }
            var monitors: Set<String> = []
            for clip in clips {
                // References were checked before reaching this point.
                let monitor = validated.tracks[clip.trackId]!.monitorSlotId
                guard clip.takeId == first.takeId, clip.sourceInUs == first.sourceInUs,
                      clip.sourceOutUs == first.sourceOutUs, clip.timelineStartUs == first.timelineStartUs,
                      monitors.insert(monitor).inserted else {
                    throw ProjectError(.invalidRange, path: "clips.\(clip.id)",
                                       message: "Linked clips need identical take/range/offset and distinct monitor slots.")
                }
            }
            for groupID in validated.groups.keys.sorted() {
                let group = validated.groups[groupID]!
                guard group.down.takeId == first.takeId,
                      group.events.contains(where: { monitors.contains($0.monitorSlotId) }),
                      group.down.timeUs < first.sourceOutUs, group.up.timeUs >= first.sourceInUs else { continue }
                guard group.events.allSatisfy({ monitors.contains($0.monitorSlotId)
                    && $0.timeUs >= first.sourceInUs && $0.timeUs < first.sourceOutUs }) else {
                    throw ProjectError(.atomicGroupSplit, path: "clips.\(first.id)",
                                       message: "Clip selection splits atomic group \(groupID); retain its complete linked range.")
                }
            }
            for clip in clips {
                let track = validated.tracks[clip.trackId]!
                for event in project.events where event.takeId == clip.takeId
                    && event.monitorSlotId == track.monitorSlotId
                    && event.timeUs >= clip.sourceInUs && event.timeUs < clip.sourceOutUs {
                    let group = event.atomicGroupId.map { id in
                        clip.linkGroupId.map { "link:\($0):\(id)" } ?? "\(clip.id):\(id)"
                    }
                    result.append(PlannedAction(
                        id: "\(clip.id):\(event.id)", sourceEventId: event.id, clipId: clip.id,
                        takeId: event.takeId, monitorSlotId: event.monitorSlotId,
                        timeUs: clip.timelineStartUs + (event.timeUs - clip.sourceInUs),
                        sourceTimeUs: event.timeUs, trackOrder: track.order, eventOrder: event.order,
                        kind: event.kind, x: event.x, y: event.y, button: event.button,
                        atomicGroupId: group, deltaX: event.deltaX, deltaY: event.deltaY))
                }
            }
        }
        result.sort {
            if $0.timeUs != $1.timeUs { return $0.timeUs < $1.timeUs }
            if $0.trackOrder != $1.trackOrder { return $0.trackOrder < $1.trackOrder }
            if $0.eventOrder != $1.eventOrder { return $0.eventOrder < $1.eventOrder }
            return $0.id < $1.id
        }
        try validateMouseOwnership(result)
        return result
    }

    private static func validateMouseOwnership(_ actions: [PlannedAction]) throws {
        var activeGroup: String?
        for action in actions {
            var conflict = false
            switch action.kind {
            case .buttonDown:
                conflict = activeGroup != nil
                activeGroup = action.atomicGroupId
            case .buttonUp:
                conflict = activeGroup != action.atomicGroupId
                activeGroup = nil
            case .move:
                conflict = activeGroup != action.atomicGroupId
            case .scroll:
                conflict = activeGroup != nil
            }
            if conflict {
                throw ProjectError(.mouseConflict, path: "plan.\(action.id)",
                                   message: "One desktop mouse cannot interleave unrelated atomic groups or movements.")
            }
        }
        if activeGroup != nil {
            throw ProjectError(.mouseConflict, path: "plan", message: "Plan ends with an owned button still held.")
        }
    }
}

private struct AtomicGroup {
    let down: RecordedAction
    let up: RecordedAction
    let events: [RecordedAction]
}

private struct ValidatedProject {
    let tracks: [String: Track]
    let groups: [String: AtomicGroup]

    init(_ project: MotionProject) throws {
        guard project.schemaVersion == 1 else {
            throw ProjectError(.unsupportedVersion, path: "schemaVersion", message: "Only schemaVersion 1 is supported.")
        }
        var ids: Set<String> = []
        func register(_ id: String, _ path: String) throws {
            try Self.validateID(id, path)
            guard ids.insert(id).inserted else {
                throw ProjectError(.duplicateID, path: path, message: "Entity IDs must be unique across the project.")
            }
        }
        try register(project.id, "id")
        try Self.require(project.revision >= 0, .invalidRange, "revision", "Revision cannot be negative.")
        var monitors: [String: MonitorSlot] = [:]
        for (i, monitor) in project.monitorSlots.enumerated() {
            let path = "monitorSlots[\(i)]"
            try register(monitor.id, path + ".id")
            let g = monitor.geometry
            try Self.require(CoordinateMapper.valid(Rect(origin: g.origin, size: g.size))
                             && g.scaleFactor.isFinite && g.scaleFactor > 0
                             && [0, 90, 180, 270].contains(g.rotationDegrees),
                             .invalidGeometry, path + ".geometry", "Invalid recorded monitor geometry.")
            monitors[monitor.id] = monitor
        }
        var takes: [String: Take] = [:]
        for (i, take) in project.takes.enumerated() {
            let path = "takes[\(i)]"
            try register(take.id, path + ".id")
            try Self.require(take.durationUs > 0 && !take.monitorSlotIds.isEmpty,
                             .invalidRange, path, "A take needs positive duration and at least one monitor.")
            var seenMonitors: Set<String> = []
            for (j, monitor) in take.monitorSlotIds.enumerated() {
                let referencePath = path + ".monitorSlotIds[\(j)]"
                try Self.require(monitors[monitor] != nil, .missingReference, referencePath, "Unknown monitor slot.")
                try Self.require(seenMonitors.insert(monitor).inserted, .duplicateID, referencePath, "Repeated take monitor.")
            }
            var mediaMonitors: Set<String> = []
            for (j, media) in take.media.enumerated() {
                let mediaPath = path + ".media[\(j)]"
                try Self.require(seenMonitors.contains(media.monitorSlotId), .missingReference,
                                 mediaPath + ".monitorSlotId", "Media monitor must belong to its take.")
                try Self.require(mediaMonitors.insert(media.monitorSlotId).inserted, .duplicateID,
                                 mediaPath + ".monitorSlotId", "One media reference per take monitor is allowed.")
                try Self.require(Self.safeMediaPath(media.relativePath), .unsafeMediaPath,
                                 mediaPath + ".relativePath", "Expected a plain relative path inside media/.")
            }
            takes[take.id] = take
        }
        var tracks: [String: Track] = [:]
        for (i, track) in project.tracks.enumerated() {
            let path = "tracks[\(i)]"
            try register(track.id, path + ".id")
            try Self.require(monitors[track.monitorSlotId] != nil, .missingReference,
                             path + ".monitorSlotId", "Unknown monitor slot.")
            try Self.validateOrder(track.order, path + ".order")
            tracks[track.id] = track
        }
        for (i, clip) in project.clips.enumerated() {
            let path = "clips[\(i)]"
            try register(clip.id, path + ".id")
            if let group = clip.linkGroupId { try Self.validateID(group, path + ".linkGroupId") }
            guard let track = tracks[clip.trackId] else {
                throw ProjectError(.missingReference, path: path + ".trackId", message: "Unknown track.")
            }
            guard let take = takes[clip.takeId] else {
                throw ProjectError(.missingReference, path: path + ".takeId", message: "Unknown source take.")
            }
            try Self.require(take.monitorSlotIds.contains(track.monitorSlotId), .missingReference,
                             path + ".takeId", "Track monitor is absent from the source take.")
            try Self.require(clip.sourceInUs >= 0 && clip.sourceOutUs > clip.sourceInUs
                             && clip.sourceOutUs <= take.durationUs && clip.timelineStartUs >= 0
                             && clip.speed == 1, .invalidRange, path,
                             "Clip needs a positive contained source range, nonnegative offset, and 1x speed.")
            let (_, overflow) = clip.timelineStartUs.addingReportingOverflow(clip.sourceOutUs - clip.sourceInUs)
            try Self.require(!overflow, .invalidRange, path + ".timelineStartUs", "Timeline end exceeds Int64.")
        }
        var rawGroups: [String: [RecordedAction]] = [:]
        for (i, event) in project.events.enumerated() {
            let path = "events[\(i)]"
            try register(event.id, path + ".id")
            guard let take = takes[event.takeId] else {
                throw ProjectError(.missingReference, path: path + ".takeId", message: "Unknown source take.")
            }
            guard let monitor = monitors[event.monitorSlotId], take.monitorSlotIds.contains(event.monitorSlotId) else {
                throw ProjectError(.missingReference, path: path + ".monitorSlotId", message: "Event monitor is absent from the take.")
            }
            try Self.require(event.timeUs >= 0 && event.timeUs < take.durationUs, .invalidRange,
                             path + ".timeUs", "Source time must be within the take's half-open range.")
            try Self.validateOrder(event.order, path + ".order")
            try Self.require(CoordinateMapper.contains(Point(x: event.x, y: event.y), size: monitor.geometry.size),
                             .invalidCoordinate, path, "Point must be finite and inside its monitor's half-open bounds.")
            if let group = event.atomicGroupId {
                try Self.validateID(group, path + ".atomicGroupId")
                rawGroups[group, default: []].append(event)
            }
            switch event.kind {
            case .buttonDown, .buttonUp:
                try Self.require(event.button != nil && event.atomicGroupId != nil
                                 && event.deltaX == nil && event.deltaY == nil,
                                 .invalidAction, path, "Button events require button/group and no scroll deltas.")
            case .move:
                try Self.require(event.button == nil && event.deltaX == nil && event.deltaY == nil,
                                 .invalidAction, path, "Moves cannot carry a button or scroll deltas.")
            case .scroll:
                try Self.require(event.button == nil && event.atomicGroupId == nil
                                 && event.deltaX?.isFinite == true && event.deltaY?.isFinite == true,
                                 .invalidAction, path, "Scroll requires finite deltas and no button/group.")
            }
        }
        var groups: [String: AtomicGroup] = [:]
        for id in rawGroups.keys.sorted() {
            let events = rawGroups[id]!.sorted(by: Self.sourceOrder)
            let downs = events.filter { $0.kind == .buttonDown }
            let ups = events.filter { $0.kind == .buttonUp }
            try Self.require(downs.count == 1 && ups.count == 1, .invalidAtomicGroup,
                             "atomicGroups.\(id)", "An atomic group needs exactly one down and one up.")
            let down = downs[0], up = ups[0]
            try Self.require(down.button == up.button && events.first?.id == down.id && events.last?.id == up.id
                             && events.allSatisfy({ $0.takeId == down.takeId }),
                             .invalidAtomicGroup, "atomicGroups.\(id)",
                             "Group must stay in one take, with matching button, down first and up last.")
            groups[id] = AtomicGroup(down: down, up: up, events: events)
        }
        self.tracks = tracks; self.groups = groups
    }

    private static func sourceOrder(_ lhs: RecordedAction, _ rhs: RecordedAction) -> Bool {
        if lhs.timeUs != rhs.timeUs { return lhs.timeUs < rhs.timeUs }
        if lhs.order != rhs.order { return lhs.order < rhs.order }
        return lhs.id < rhs.id
    }

    private static func require(_ condition: Bool, _ code: ProjectError.Code, _ path: String, _ message: String) throws {
        if !condition { throw ProjectError(code, path: path, message: message) }
    }

    private static func validateOrder(_ order: Int, _ path: String) throws {
        try require(order >= 0 && order <= 2_147_483_647, .invalidRange, path, "Order must fit a nonnegative Int32.")
    }

    private static func validateID(_ id: String, _ path: String) throws {
        let bytes = Array(id.utf8)
        func alphanumeric(_ b: UInt8) -> Bool { (48...57).contains(b) || (65...90).contains(b) || (97...122).contains(b) }
        try require(!bytes.isEmpty && bytes.count <= 128 && alphanumeric(bytes[0])
                    && bytes.allSatisfy({ alphanumeric($0) || $0 == 45 || $0 == 46 || $0 == 95 }),
                    .invalidID, path, "ID must match [A-Za-z0-9][A-Za-z0-9._-]{0,127}.")
    }

    private static func safeMediaPath(_ path: String) -> Bool {
        guard path.hasPrefix("media/"), path.utf8.allSatisfy({ byte in
            (48...57).contains(byte) || (65...90).contains(byte) || (97...122).contains(byte)
                || [UInt8(45), 46, 47, 95].contains(byte)
        }) else { return false }
        let parts = path.split(separator: "/", omittingEmptySubsequences: false)
        return parts.count >= 2 && parts.allSatisfy { !$0.isEmpty && $0 != "." && $0 != ".." }
    }
}
