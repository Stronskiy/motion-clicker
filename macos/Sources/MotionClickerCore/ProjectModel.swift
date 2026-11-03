import Foundation

public struct Point: Codable, Sendable, Equatable {
    public var x: Double
    public var y: Double
    public init(x: Double, y: Double) { self.x = x; self.y = y }
}

public struct Size: Codable, Sendable, Equatable {
    public var width: Double
    public var height: Double
    public init(width: Double, height: Double) { self.width = width; self.height = height }
}

public struct Rect: Codable, Sendable, Equatable {
    public var origin: Point
    public var size: Size
    public init(origin: Point, size: Size) { self.origin = origin; self.size = size }
}

/// Canonical logical units, upper-left origin, x right and y down.
public struct DisplayGeometry: Codable, Sendable, Equatable {
    public var origin: Point
    public var size: Size
    public var scaleFactor: Double
    public var rotationDegrees: Int
    public init(origin: Point, size: Size, scaleFactor: Double, rotationDegrees: Int) {
        self.origin = origin; self.size = size
        self.scaleFactor = scaleFactor; self.rotationDegrees = rotationDegrees
    }
}

public struct MonitorSlot: Codable, Sendable, Equatable {
    public var id: String
    public var name: String
    public var geometry: DisplayGeometry
    public var deviceHint: String?
    public init(id: String, name: String, geometry: DisplayGeometry, deviceHint: String? = nil) {
        self.id = id; self.name = name; self.geometry = geometry; self.deviceHint = deviceHint
    }
}

public struct TakeMedia: Codable, Sendable, Equatable {
    public var monitorSlotId: String
    public var relativePath: String
    public init(monitorSlotId: String, relativePath: String) {
        self.monitorSlotId = monitorSlotId; self.relativePath = relativePath
    }
}

/// Immutable source timeline; editing creates clips rather than changing source timestamps.
public struct Take: Codable, Sendable, Equatable {
    public var id: String
    public var durationUs: Int64
    public var monitorSlotIds: [String]
    public var media: [TakeMedia]
    public init(id: String, durationUs: Int64, monitorSlotIds: [String], media: [TakeMedia] = []) {
        self.id = id; self.durationUs = durationUs; self.monitorSlotIds = monitorSlotIds; self.media = media
    }
}

/// One monitor's linked video/action lanes share the same clips in schema version 1.
public struct Track: Codable, Sendable, Equatable {
    public var id: String
    public var name: String
    public var monitorSlotId: String
    public var order: Int
    public init(id: String, name: String, monitorSlotId: String, order: Int) {
        self.id = id; self.name = name; self.monitorSlotId = monitorSlotId; self.order = order
    }
}

public struct TimelineClip: Codable, Sendable, Equatable {
    public var id: String
    public var trackId: String
    public var takeId: String
    public var sourceInUs: Int64
    public var sourceOutUs: Int64
    public var timelineStartUs: Int64
    public var speed: Double
    public var linkGroupId: String?
    public init(id: String, trackId: String, takeId: String, sourceInUs: Int64,
                sourceOutUs: Int64, timelineStartUs: Int64, speed: Double = 1, linkGroupId: String? = nil) {
        self.id = id; self.trackId = trackId; self.takeId = takeId
        self.sourceInUs = sourceInUs; self.sourceOutUs = sourceOutUs
        self.timelineStartUs = timelineStartUs; self.speed = speed
        self.linkGroupId = linkGroupId
    }
}

public enum ActionKind: String, Codable, Sendable { case move, buttonDown, buttonUp, scroll }
public enum MouseButton: String, Codable, Sendable { case left, right, middle }

public struct RecordedAction: Codable, Sendable, Equatable {
    public var id: String
    public var takeId: String
    public var monitorSlotId: String
    public var timeUs: Int64
    public var order: Int
    public var kind: ActionKind
    public var x: Double
    public var y: Double
    public var button: MouseButton?
    public var atomicGroupId: String?
    public var deltaX: Double?
    public var deltaY: Double?
    public init(id: String, takeId: String, monitorSlotId: String, timeUs: Int64, order: Int,
                kind: ActionKind, x: Double, y: Double, button: MouseButton? = nil,
                atomicGroupId: String? = nil, deltaX: Double? = nil, deltaY: Double? = nil) {
        self.id = id; self.takeId = takeId; self.monitorSlotId = monitorSlotId
        self.timeUs = timeUs; self.order = order; self.kind = kind; self.x = x; self.y = y
        self.button = button; self.atomicGroupId = atomicGroupId
        self.deltaX = deltaX; self.deltaY = deltaY
    }
}

public struct MotionProject: Codable, Sendable, Equatable {
    public var schemaVersion: Int
    public var id: String
    public var revision: Int64
    public var monitorSlots: [MonitorSlot]
    public var takes: [Take]
    public var tracks: [Track]
    public var clips: [TimelineClip]
    public var events: [RecordedAction]
    public init(schemaVersion: Int = 1, id: String, revision: Int64 = 0,
                monitorSlots: [MonitorSlot] = [], takes: [Take] = [], tracks: [Track] = [],
                clips: [TimelineClip] = [], events: [RecordedAction] = []) {
        self.schemaVersion = schemaVersion; self.id = id; self.revision = revision
        self.monitorSlots = monitorSlots; self.takes = takes; self.tracks = tracks
        self.clips = clips; self.events = events
    }
}

/// A value snapshot. IDs identify a clip occurrence, not just a source event.
public struct PlannedAction: Codable, Sendable, Equatable {
    public let id: String
    public let sourceEventId: String
    public let clipId: String
    public let takeId: String
    public let monitorSlotId: String
    public let timeUs: Int64
    public let sourceTimeUs: Int64
    public let trackOrder: Int
    public let eventOrder: Int
    public let kind: ActionKind
    public let x: Double
    public let y: Double
    public let button: MouseButton?
    public let atomicGroupId: String?
    public let deltaX: Double?
    public let deltaY: Double?
}

public struct ProjectError: Error, Codable, Sendable, Equatable {
    public enum Code: String, Codable, Sendable {
        case unsupportedVersion, invalidID, duplicateID, missingReference, invalidRange
        case invalidGeometry, invalidCoordinate, invalidAction, invalidAtomicGroup
        case atomicGroupSplit, mouseConflict, unsafeMediaPath
    }
    public let code: Code
    public let path: String
    public let message: String
    public init(_ code: Code, path: String, message: String) {
        self.code = code; self.path = path; self.message = message
    }
}
