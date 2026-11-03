import Foundation
import XCTest
import MotionClickerCore
import MotionClickerRuntime
@testable import MotionClickerRecording

final class MemoryJournal: EventJournal {
    var records: [RawPointerRecord] = []
    var fail = false
    func append(record: RawPointerRecord) throws { if fail { throw RecordingError(message: "disk full") }; records.append(record) }
    func flush() throws { if fail { throw RecordingError(message: "disk full") } }
    func read(visit: (RawPointerRecord) throws -> Void) throws { for r in records { try visit(r) } }
}
func recordingTargets() -> [RecordingTarget] {
    [(-400.0, "left"), (0.0, "right")].map { x, id in
        let geometry = DisplayGeometry(origin: Point(x: x / 2, y: 0), size: Size(width: 200, height: 100), scaleFactor: 2, rotationDegrees: 0)
        return RecordingTarget(slot: MonitorSlot(id: id, name: id, geometry: geometry), display: RuntimeDisplay(id: "native-" + id, name: id, geometry: geometry, nativeBounds: Rect(origin: Point(x: x, y: 0), size: Size(width: 400, height: 200))))
    }
}
func readySession(_ journal: MemoryJournal, epochHostUs: Int64 = 1000000) throws -> RecordingSession {
    let s = try RecordingSession(takeId: "take", targets: recordingTargets(), journal: journal)
    for id in ["left", "right"] { try s.sourceReady(monitorSlotId: id, clock: ClockAnchor(sourceAnchorTicks: 0, hostAnchorUs: 1000000, ticksPerSecond: 1000000)) }
    try s.eventTapReady(); try s.begin(epochHostUs: epochHostUs)
    return s
}
func finishSession(_ s: RecordingSession) throws {
    try s.stop(hostTimeUs: 2000000)
    try s.writerFinished(monitorSlotId: "left", status: .succeeded, encodedDurationUs: 1000)
    XCTAssertEqual(s.state, .finalizing)
    try s.writerFinished(monitorSlotId: "right", status: .succeeded, encodedDurationUs: 1000)
}
func frames(_ s: RecordingSession) throws {
    try s.acceptedFrame(monitorSlotId: "left", sourceTicks: 0, encodedTimeUs: 0, durationUs: 1000)
    try s.acceptedFrame(monitorSlotId: "right", sourceTicks: 250000, encodedTimeUs: 0, durationUs: 1000)
}
final class SessionTests: XCTestCase {
    struct Case: Decodable {
        let name: String; let events: [CapturedPointerEvent]; let state: RecordingState
        let rawCount, projectedCount, incompleteCount: Int; let sourceFailure: Bool
    }
    func testSharedDragInterruptionAndSourceLossFixtures() throws {
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("contracts/fixtures/recording/sessions.json")
        for c in try JSONDecoder().decode([Case].self, from: Data(contentsOf: path)) {
            let j = MemoryJournal(), s = try readySession(j); try frames(s)
            for e in c.events { try s.receive(event: e) }
            if c.sourceFailure { try s.sourceFailed(monitorSlotId: "right", hostTimeUs: 1000030, reason: "source disconnected") }
            try finishSession(s)
            let p = try s.project()
            XCTAssertEqual(s.state, c.state, c.name)
            XCTAssertEqual(j.records.count, c.rawCount, c.name)
            XCTAssertEqual(p.events.count, c.projectedCount, c.name)
            XCTAssertEqual(p.manifest.incompleteGroupIds.count, c.incompleteCount, c.name)
            XCTAssertEqual(p.manifest.streams.map(\.mediaTimeZeroUs), [0, 250000])
            XCTAssertEqual(p.manifest.streams[1].gaps.first?.kind, .leading)
            if !p.events.isEmpty {
                XCTAssertEqual(p.events.map(\.order), [0,1,2]); XCTAssertEqual(p.events.map(\.timeUs), [10,10,20])
                XCTAssertEqual(p.events.map(\.x), [150,50,100]); XCTAssertEqual(Set(p.events.compactMap(\.atomicGroupId)).count, 1)
                XCTAssertGreaterThan(p.take.durationUs, p.events.last!.timeUs)
            }
            try s.stop(hostTimeUs: .max)
            try s.writerFinished(monitorSlotId: "right", status: .succeeded, encodedDurationUs: 1000)
            XCTAssertEqual(try s.project(), p)
        }
    }
    func testReadinessAndPreparingFailure() throws {
        let j = MemoryJournal(), s = try RecordingSession(takeId: "take", targets: recordingTargets(), journal: j)
        try s.receive(event: CapturedPointerEvent(hostTimeUs: 1, nativePosition: Point(x: 1,y: 1), kind: .move))
        XCTAssertThrowsError(try s.begin(epochHostUs: 0)); XCTAssertEqual(s.state, .preparing)
        try s.eventTapReady(); try s.sourceReady(monitorSlotId: "left", clock: ClockAnchor(sourceAnchorTicks: 0, hostAnchorUs: 0, ticksPerSecond: 1))
        XCTAssertThrowsError(try s.begin(epochHostUs: 0))
        s.preparationFailed(reason: "event tap unavailable"); XCTAssertEqual(s.state, .failed); XCTAssertNil(s.result); XCTAssertTrue(j.records.isEmpty)
    }
    func testIgnoresOwnControlAndPreEpochAndIdleDoesNotInventLoss() throws {
        let j = MemoryJournal(), s = try readySession(j)
        for e in [CapturedPointerEvent(hostTimeUs: .max, nativePosition: Point(x: 1,y: 1), kind: .move, isOwnInjected: true), CapturedPointerEvent(hostTimeUs: .max, nativePosition: Point(x: 1,y: 1), kind: .move, isControl: true), CapturedPointerEvent(hostTimeUs: 999999, nativePosition: Point(x: 1,y: 1), kind: .move)] { try s.receive(event: e) }
        try s.acceptedFrame(monitorSlotId: "left", sourceTicks: -1, encodedTimeUs: 0, durationUs: 1000)
        try frames(s); try s.idle(monitorSlotId: "left", sourceTicks: 500000)
        try finishSession(s)
        XCTAssertTrue(j.records.isEmpty); XCTAssertTrue(try s.project().manifest.streams[0].gaps.isEmpty)
        XCTAssertEqual(try s.project().manifest.streams[0].frameCount, 1)
    }
    func testDropZeroFrameAndEncoderFailureAreTruthful() throws {
        let s = try readySession(MemoryJournal())
        try s.acceptedFrame(monitorSlotId: "left", sourceTicks: 0, encodedTimeUs: 0, durationUs: 1000)
        try s.reportGap(monitorSlotId: "left", startTicks: 1000, endTicks: 2000, kind: .backpressure)
        try s.stop(hostTimeUs: 1003000)
        try s.writerFinished(monitorSlotId: "left", status: .succeeded, encodedDurationUs: 1000)
        try s.writerFinished(monitorSlotId: "right", status: .failed, encodedDurationUs: 0, reason: "encoder")
        let p = try s.project(); XCTAssertEqual(p.manifest.state, .interrupted); XCTAssertEqual(p.take.media.count, 1)
        XCTAssertFalse(p.manifest.warnings.isEmpty); XCTAssertEqual(p.manifest.streams[1].writerStatus, .failed)
        let z = try readySession(MemoryJournal()); try z.stop(hostTimeUs: 1000000)
        for id in ["left","right"] { try z.writerFinished(monitorSlotId: id, status: .succeeded, encodedDurationUs: 0) }
        XCTAssertEqual(z.state, .interrupted); XCTAssertTrue(try z.project().take.media.isEmpty)
    }
    func testReversalOverflowAndJournalFailureCannotProjectUnsafeEvents() throws {
        for mode in 0..<3 {
            let j = MemoryJournal(), s = try readySession(j, epochHostUs: mode == 1 ? -1 : 1000000); try frames(s)
            try s.receive(event: CapturedPointerEvent(hostTimeUs: 1000010, nativePosition: Point(x: 1,y: 1), kind: .buttonDown, button: .left))
            if mode == 2 { j.fail = true }
            XCTAssertThrowsError(try s.receive(event: CapturedPointerEvent(hostTimeUs: mode == 0 ? 1000009 : mode == 1 ? .max : 1000020, nativePosition: Point(x: 1,y: 1), kind: .move)))
            j.fail = false; try finishSession(s)
            XCTAssertEqual(s.state, .failed); XCTAssertTrue(try s.project().events.isEmpty)
        }
    }
}

extension SessionTests {
    func testFramesRejectReversalAndStopWaitsForLateWriterDrain() throws {
        let s = try readySession(MemoryJournal()); try frames(s)
        XCTAssertThrowsError(try s.acceptedFrame(monitorSlotId: "left",sourceTicks: 0,encodedTimeUs: 1000,durationUs: 1000))
        try finishSession(s); XCTAssertEqual(s.state,.failed)
        let drain = try readySession(MemoryJournal()); try frames(drain);try drain.stop(hostTimeUs: 1500000)
        try drain.acceptedFrame(monitorSlotId: "left",sourceTicks: 700000,encodedTimeUs: 700000,durationUs: 1000)
        try drain.writerFinished(monitorSlotId: "left",status: .succeeded,encodedDurationUs: 701000)
        XCTAssertNil(drain.result);try drain.writerFinished(monitorSlotId: "right",status: .succeeded,encodedDurationUs: 1000)
        XCTAssertEqual(try drain.project().take.durationUs,701000)
        XCTAssertThrowsError(try drain.writerFinished(monitorSlotId: "right",status: .failed,encodedDurationUs: 1000))
    }
    func testKnownDropsCompleteWithWarningsAndScrollRemainsLogical() throws {
        let s = try readySession(MemoryJournal());try frames(s)
        try s.reportGap(monitorSlotId: "left",startTicks: 1000,endTicks: 2000,kind: .dropped)
        try s.receive(event: CapturedPointerEvent(hostTimeUs: 1000010,nativePosition: Point(x: 100,y: 50),kind: .scroll,deltaX: -2,deltaY: 3))
        try finishSession(s);let p = try s.project();XCTAssertEqual(p.manifest.state,.completed)
        XCTAssertFalse(p.manifest.warnings.isEmpty);XCTAssertEqual(p.events[0].deltaX,-2);XCTAssertEqual(p.events[0].y,25)
    }
    func testUnknownRecordingEnumsAndLargeWireIntegerRemainStrict() throws {
        let event = CapturedPointerEvent(hostTimeUs: 9007199254740993,nativePosition: Point(x: 1,y: 1),kind: .move)
        XCTAssertEqual(try JSONDecoder().decode(CapturedPointerEvent.self,from: JSONEncoder().encode(event)),event)
        for invalid in ["0","\"Completed\"","\"unknown\""] { XCTAssertThrowsError(try JSONDecoder().decode(RecordingState.self,from: Data(invalid.utf8))) }
    }
    func testFrozenTargetsAndInvalidMappings() throws {
        var targets = recordingTargets();let s = try RecordingSession(takeId: "take",targets: targets,journal: MemoryJournal())
        targets.removeAll();try s.sourceReady(monitorSlotId: "left",clock: ClockAnchor(sourceAnchorTicks: 0,hostAnchorUs: 0,ticksPerSecond: 1))
        XCTAssertThrowsError(try RecordingSession(takeId: "take",targets: [recordingTargets()[0],recordingTargets()[0]],journal: MemoryJournal()))
        XCTAssertThrowsError(try RecordingSession(takeId: "left",targets: recordingTargets(),journal: MemoryJournal()))
    }
}

extension SessionTests {
    func testOverflowingWriterCannotAppearAsSuccessfulMedia() throws {
        let s = try readySession(MemoryJournal());try frames(s);try s.stop(hostTimeUs: 2000000)
        try s.writerFinished(monitorSlotId: "left",status: .succeeded,encodedDurationUs: 1000)
        try s.writerFinished(monitorSlotId: "right",status: .succeeded,encodedDurationUs: .max)
        let p = try s.project();XCTAssertEqual(p.manifest.state,.failed)
        XCTAssertEqual(p.manifest.streams[1].writerStatus,.failed);XCTAssertEqual(p.take.media.count,1)
    }
}
