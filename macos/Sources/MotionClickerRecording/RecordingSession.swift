import Foundation
import MotionClickerCore
import MotionClickerRuntime

/// All calls must be confined to one serial owner; native callback queues marshal here.
public final class RecordingSession {
    public private(set) var state: RecordingState = .preparing
    public private(set) var result: RecordingManifest?
    public init(takeId: String, targets: [RecordingTarget], journal: EventJournal) throws { }
    public func sourceReady(monitorSlotId: String, clock: ClockAnchor) throws { }
    public func eventTapReady() throws { }
    public func begin(epochHostUs: Int64) throws { }
    public func preparationFailed(reason: String) { }
    public func receive(event: CapturedPointerEvent) throws { }
    public func acceptedFrame(monitorSlotId: String, sourceTicks: Int64, encodedTimeUs: Int64, durationUs: Int64) throws { }
    public func idle(monitorSlotId: String, sourceTicks: Int64) throws { }
    public func reportGap(monitorSlotId: String, startTicks: Int64, endTicks: Int64, kind: GapKind, reason: String? = nil) throws { }
    public func sourceFailed(monitorSlotId: String, hostTimeUs: Int64, reason: String) throws { }
    public func stop(hostTimeUs: Int64, reason: String? = nil) throws { }
    public func writerFinished(monitorSlotId: String, status: WriterStatus, encodedDurationUs: Int64, reason: String? = nil) throws { }
    public func project() throws -> RecordingResult { throw RecordingError(message: "Not implemented") }
}
