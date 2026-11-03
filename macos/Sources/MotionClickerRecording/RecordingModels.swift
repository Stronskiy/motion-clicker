import Foundation
import MotionClickerCore
import MotionClickerRuntime

public enum RecordingState: String, Codable, Sendable { case preparing, recording, finalizing, completed, interrupted, failed }
public enum GapKind: String, Codable, Sendable { case leading, dropped, backpressure, sourceFailure, encoderFailure }
public enum WriterStatus: String, Codable, Sendable { case succeeded, failed }
public enum StoreOperation: String, Codable, Sendable { case append, flush, moveMedia, publishManifest }

public struct RecordingError: Error, Equatable {
    public let message: String
    public init(message: String) { self.message = message }
}

public struct RecordingTarget: Sendable {
    public let slot: MonitorSlot
    public let display: RuntimeDisplay
    public init(slot: MonitorSlot, display: RuntimeDisplay) { self.slot = slot; self.display = display }
}

public struct ClockAnchor: Codable, Sendable, Equatable {
    public let sourceAnchorTicks: Int64
    public let hostAnchorUs: Int64
    public let ticksPerSecond: Int64
    public init(sourceAnchorTicks: Int64, hostAnchorUs: Int64, ticksPerSecond: Int64) {
        self.sourceAnchorTicks = sourceAnchorTicks
        self.hostAnchorUs = hostAnchorUs
        self.ticksPerSecond = ticksPerSecond
    }
}

public struct CapturedPointerEvent: Codable, Sendable, Equatable {
    public let hostTimeUs: Int64
    public let nativePosition: Point
    public let kind: ActionKind
    public let button: MouseButton?
    public let deltaX: Double?
    public let deltaY: Double?
    public let isOwnInjected: Bool
    public let isControl: Bool
    public init(hostTimeUs: Int64, nativePosition: Point, kind: ActionKind, button: MouseButton? = nil, deltaX: Double? = nil, deltaY: Double? = nil, isOwnInjected: Bool = false, isControl: Bool = false) {
        self.hostTimeUs = hostTimeUs
        self.nativePosition = nativePosition
        self.kind = kind
        self.button = button
        self.deltaX = deltaX
        self.deltaY = deltaY
        self.isOwnInjected = isOwnInjected
        self.isControl = isControl
    }
}

public struct RawPointerRecord: Codable, Sendable, Equatable {
    public let sequence: Int32
    public let event: CapturedPointerEvent
    public let timeUs: Int64
    public let action: RecordedAction?
    public let atomicGroupId: String?
    public init(sequence: Int32, event: CapturedPointerEvent, timeUs: Int64, action: RecordedAction? = nil, atomicGroupId: String? = nil) {
        self.sequence = sequence
        self.event = event
        self.timeUs = timeUs
        self.action = action
        self.atomicGroupId = atomicGroupId
    }
}

public struct RecordingGap: Codable, Sendable, Equatable {
    public let startUs: Int64
    public let endUs: Int64
    public let kind: GapKind
    public let reason: String?
    public init(startUs: Int64, endUs: Int64, kind: GapKind, reason: String? = nil) {
        self.startUs = startUs
        self.endUs = endUs
        self.kind = kind
        self.reason = reason
    }
}

public struct StreamManifest: Codable, Sendable, Equatable {
    public let monitorSlotId: String
    public let relativePath: String
    public let clock: ClockAnchor
    public let mediaTimeZeroUs: Int64?
    public let encodedDurationUs: Int64
    public let frameCount: Int64
    public let gaps: [RecordingGap]
    public let writerStatus: WriterStatus
    public let gapCount: Int64
    public let gapJournalPath: String
    public let failureReason: String?
    public init(monitorSlotId: String, relativePath: String, clock: ClockAnchor, mediaTimeZeroUs: Int64? = nil, encodedDurationUs: Int64, frameCount: Int64, gaps: [RecordingGap], writerStatus: WriterStatus, gapCount: Int64, gapJournalPath: String, failureReason: String? = nil) {
        self.monitorSlotId = monitorSlotId
        self.relativePath = relativePath
        self.clock = clock
        self.mediaTimeZeroUs = mediaTimeZeroUs
        self.encodedDurationUs = encodedDurationUs
        self.frameCount = frameCount
        self.gaps = gaps
        self.writerStatus = writerStatus
        self.gapCount = gapCount
        self.gapJournalPath = gapJournalPath
        self.failureReason = failureReason
    }
}

public struct RecordingManifest: Codable, Sendable, Equatable {
    public let manifestVersion: Int32
    public let takeId: String
    public let state: RecordingState
    public let epochHostUs: Int64
    public let durationUs: Int64
    public let targets: [MonitorSlot]
    public let streams: [StreamManifest]
    public let rawEventCount: Int64
    public let incompleteGroupIds: [String]
    public let warnings: [String]
    public let reason: String?
    public init(manifestVersion: Int32, takeId: String, state: RecordingState, epochHostUs: Int64, durationUs: Int64, targets: [MonitorSlot], streams: [StreamManifest], rawEventCount: Int64, incompleteGroupIds: [String], warnings: [String], reason: String? = nil) {
        self.manifestVersion = manifestVersion
        self.takeId = takeId
        self.state = state
        self.epochHostUs = epochHostUs
        self.durationUs = durationUs
        self.targets = targets
        self.streams = streams
        self.rawEventCount = rawEventCount
        self.incompleteGroupIds = incompleteGroupIds
        self.warnings = warnings
        self.reason = reason
    }
}

public struct RecordingResult: Codable, Sendable, Equatable {
    public let manifest: RecordingManifest
    public let take: Take
    public let events: [RecordedAction]
    public init(manifest: RecordingManifest, take: Take, events: [RecordedAction]) {
        self.manifest = manifest
        self.take = take
        self.events = events
    }
}

public struct StoreRecovery: Codable, Sendable, Equatable {
    public let stagingPath: String
    public let mediaPath: String
    public let manifestPath: String
    public let mediaPublished: Bool
    public let manifestPublished: Bool
    public init(stagingPath: String, mediaPath: String, manifestPath: String, mediaPublished: Bool, manifestPublished: Bool) {
        self.stagingPath = stagingPath
        self.mediaPath = mediaPath
        self.manifestPath = manifestPath
        self.mediaPublished = mediaPublished
        self.manifestPublished = manifestPublished
    }
}

public protocol EventJournal: AnyObject {
    func append(record: RawPointerRecord) throws
    func appendGap(record: JournalGapRecord) throws
    func readGaps(visit: (JournalGapRecord) throws -> Void) throws
    func flush() throws
    func read(visit: (RawPointerRecord) throws -> Void) throws
}

public struct JournalGapRecord: Codable, Sendable, Equatable {
    public let monitorSlotId: String
    public let gap: RecordingGap
    public init(monitorSlotId: String, gap: RecordingGap) { self.monitorSlotId = monitorSlotId; self.gap = gap }
}
