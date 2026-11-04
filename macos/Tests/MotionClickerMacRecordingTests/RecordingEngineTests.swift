import XCTest
import CoreVideo
import MotionClickerCore
import MotionClickerRuntime
import MotionClickerRecording
@testable import MotionClickerMacRecording

final class EngineJournal: EventJournal {
    var events: [RawPointerRecord] = [], gaps: [JournalGapRecord] = []
    func append(record: RawPointerRecord) throws { events.append(record) }
    func appendGap(record: JournalGapRecord) throws { gaps.append(record) }
    func flush() throws {}
    func read(visit: (RawPointerRecord) throws -> Void) throws { for event in events { try visit(event) } }
    func readGaps(visit: (JournalGapRecord) throws -> Void) throws { for gap in gaps { try visit(gap) } }
}
final class EngineWriter: VideoWriting {
    var accepts = true, fails = false
    let url: URL?
    let hanging: Bool
    init(url: URL? = nil, hanging: Bool = false) { self.url = url; self.hanging = hanging }
    var stamps: [FrameStamp] = []
    func append(_ pixel: CVPixelBuffer, stamp: FrameStamp) throws -> Bool {
        if fails { throw RecordingError(message: "synthetic append failure") }
        if accepts { if let url, stamps.isEmpty { try Data([1]).write(to: url) }; stamps.append(stamp) }; return accepts
    }
    func finish(_ completion: @escaping @Sendable (VideoTerminal) -> Void) { if hanging { return }; completion(VideoTerminal(frameCount: Int64(stamps.count), durationUs: stamps.last.map { $0.ptsUs + $0.durationUs } ?? 0, error: nil)) }
    func cancel() {}
}
func engineTargets() -> [RecordingTarget] {
    ["a", "b"].enumerated().map { index, id in
        let g = DisplayGeometry(origin: Point(x: Double(index * 64), y: 0), size: Size(width: 64, height: 64), scaleFactor: 1, rotationDegrees: 0)
        return RecordingTarget(slot: MonitorSlot(id: id, name: id, geometry: g), display: RuntimeDisplay(id: String(index + 1), name: id, geometry: g, nativeBounds: Rect(origin: g.origin, size: g.size)))
    }
}
final class RecordingEngineTests: XCTestCase {
    func fixture() throws -> (RecordingEngine, EngineJournal, EngineWriter, EngineWriter, CVPixelBuffer) {
        let journal = EngineJournal(), a = EngineWriter(), b = EngineWriter()
        let engine = try RecordingEngine(targets: engineTargets(), takeId: "take", journal: journal, writers: ["a": a, "b": b], options: MacRecordingOptions(ownInjectedTag: 42, isControl: { _ in false }))
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        return (engine, journal, a, b, try XCTUnwrap(pixel))
    }
    func ready(_ e: RecordingEngine) throws {
        try e.start(); try e.sourceReady("a", generation: 1, nowUs: 1_000_000)
        try e.tapReady(generation: 1, nowUs: 1_000_000)
        XCTAssertEqual(e.state, .preparing)
        try e.sourceReady("b", generation: 1, nowUs: 1_000_000)
        XCTAssertEqual(e.state, .recording)
    }
    func finish(_ e: RecordingEngine, a: VideoTerminal = VideoTerminal(frameCount: 1, durationUs: 1_000_000, error: nil), b: VideoTerminal = VideoTerminal(frameCount: 1, durationUs: 1_000_000, error: nil)) throws {
        try e.writerFinished("a", terminal: a)
        try e.writerFinished("b", terminal: b)
    }
    func testDropLateImageDropRetainsFailureAndEveryAdmissionDiagnostic() throws {
        let (e, j, a, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.drop("a", sourceUs: 1_100_000, generation: 1, reason: "first drop")
        try e.frame("a", sourceUs: 5_000_000, status: .complete, pixel: pixel, generation: 1)
        a.fails = true
        do { try e.drop("a", sourceUs: 5_100_000, generation: 1, reason: "second drop") }
        catch { try e.fail(nowUs: 5_200_000, reason: String(describing: error)) }
        try e.stop(nowUs: 6_000_000)
        try finish(e, a: VideoTerminal(frameCount: 1, durationUs: 100_000, error: nil), b: VideoTerminal(frameCount: 1, durationUs: 4_200_000, error: nil))
        XCTAssertEqual(e.result?.state, .interrupted)
        XCTAssertEqual(e.result?.streams.first?.writerStatus, .failed)
        XCTAssertTrue(e.result?.streams.first?.failureReason?.contains("synthetic append failure") == true)
        XCTAssertEqual(try e.project().take.media.map(\.monitorSlotId), ["b"])
        XCTAssertEqual(e.droppedFrames, 2)
        XCTAssertTrue(j.gaps.contains { $0.gap.startUs == 100_000 && $0.gap.endUs == 4_000_000 })
        XCTAssertTrue(j.gaps.contains { $0.gap.startUs == 4_100_000 && $0.gap.endUs == 4_200_000 && $0.gap.reason == "second drop" })
        XCTAssertTrue(j.gaps.contains { $0.gap.kind == .encoderFailure && $0.gap.startUs <= 4_200_000 && $0.gap.endUs >= 4_200_000 })
    }
    func testAdmissionDropThenIdleKeepsEntireUnknownIntervalAndTruncatesOldPixels() throws {
        let (e, j, a, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.drop("a", sourceUs: 1_100_000, generation: 1, reason: "admission")
        try e.frame("a", sourceUs: 4_000_000, status: .idle, pixel: nil, generation: 1)
        try e.stop(nowUs: 6_000_000)
        XCTAssertEqual(a.stamps.count, 1)
        XCTAssertEqual(a.stamps.first?.durationUs, 100_000)
        XCTAssertEqual(j.gaps.filter { $0.monitorSlotId == "a" }.map(\.gap), [RecordingGap(startUs: 100_000, endUs: 5_000_000, kind: .backpressure, reason: "admission")])
        XCTAssertEqual(e.droppedFrames, 1)
    }
    func testAdmissionDropThenLateImageDoesNotExtendEarlierValidFrameAcrossGap() throws {
        let (e, j, a, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.drop("a", sourceUs: 1_100_000, generation: 1, reason: "first")
        try e.drop("a", sourceUs: 1_500_000, generation: 1, reason: "second")
        try e.frame("a", sourceUs: 5_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.stop(nowUs: 6_000_000)
        XCTAssertEqual(a.stamps.map(\.ptsUs), [0, 4_000_000])
        XCTAssertEqual(a.stamps.map(\.durationUs), [100_000, 1_000_000])
        XCTAssertEqual(j.gaps.filter { $0.monitorSlotId == "a" }.map(\.gap), [
            RecordingGap(startUs: 100_000, endUs: 500_000, kind: .backpressure, reason: "first"),
            RecordingGap(startUs: 500_000, endUs: 4_000_000, kind: .backpressure, reason: "second")])
        XCTAssertEqual(e.droppedFrames, 2)
    }
    func testObservationTimesPersistAcrossOwnerDelayPacketOriginsTiesAndStaleCallbacks() throws {
        let (e, j, _, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        func sample(_ time: Int64, _ stamp: UInt64) -> MacPointerSample {
            MacPointerSample(observationHostUs: time, timestampNs: stamp, position: Point(x: 20, y: 20), kind: .move, button: nil, deltaX: nil, deltaY: nil, sourceTag: 0)
        }
        try e.pointer(sample(999_999, .max), nowUs: 5_000_000, generation: 1)
        try e.pointer(sample(1_000_010, .max), nowUs: 5_000_000, generation: 1)
        try e.pointer(sample(1_000_010, 0), nowUs: 5_500_000, generation: 1)
        try e.stop(nowUs: 6_000_000)
        try e.pointer(sample(1_000_020, 5), nowUs: 6_000_000, generation: 1)
        try finish(e, a: VideoTerminal(frameCount: 1, durationUs: 5_000_000, error: nil), b: VideoTerminal(frameCount: 1, durationUs: 5_000_000, error: nil))
        XCTAssertEqual(j.events.map(\.timeUs), [10, 10])
        XCTAssertEqual(j.events.map(\.sequence), [0, 1])
        XCTAssertEqual(j.events.map { $0.event.timing?.packetTimestamp }, ["18446744073709551615", "0"])
        XCTAssertEqual(e.result?.pointerTimingBasis, .nativeHostObservation)
        XCTAssertEqual(try e.project().events.map(\.timeUs), [10, 10])
    }
    func testPreEpochPointerCannotPoisonControlGestureClassification() throws {
        let journal = EngineJournal(), a = EngineWriter(), b = EngineWriter()
        let e = try RecordingEngine(targets: engineTargets(), takeId: "take", journal: journal, writers: ["a": a, "b": b], options: MacRecordingOptions(ownInjectedTag: 42, isControl: { $0.position.x < 10 }))
        try ready(e)
        try e.pointer(MacPointerSample(observationHostUs: 999_999, timestampNs: 999_999_000, position: Point(x: 20, y: 20), kind: .buttonDown, button: .left, deltaX: nil, deltaY: nil, sourceTag: 0), nowUs: 1_000_000, generation: 1)
        try e.pointer(MacPointerSample(observationHostUs: 1_000_000, timestampNs: 1_000_000_000, position: Point(x: 1, y: 20), kind: .buttonDown, button: .left, deltaX: nil, deltaY: nil, sourceTag: 0), nowUs: 1_000_000, generation: 1)
        try e.pointer(MacPointerSample(observationHostUs: 1_000_001, timestampNs: 1_000_001_000, position: Point(x: 20, y: 20), kind: .buttonUp, button: .left, deltaX: nil, deltaY: nil, sourceTag: 0), nowUs: 1_000_002, generation: 1)
        XCTAssertTrue(journal.events.isEmpty)
        XCTAssertEqual(e.state, .recording)
    }
    func testPendingFrameIsEncodedOnlyWhenItsActualDurationIsKnown() throws {
        let (e, _, a, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        XCTAssertTrue(a.stamps.isEmpty)
        try e.frame("a", sourceUs: 1_100_000, status: .complete, pixel: pixel, generation: 1)
        XCTAssertEqual(a.stamps.count, 1); XCTAssertEqual(a.stamps.first?.durationUs, 100_000)
        try e.frame("a", sourceUs: 1_900_000, status: .idle, pixel: nil, generation: 1)
        try e.stop(nowUs: 2_000_000)
        XCTAssertEqual(a.stamps.count, 2); XCTAssertEqual(a.stamps.last?.durationUs, 900_000)
    }
    func testBarrierLateOriginsIdleAndGenerationRejectStaleFrames() throws {
        let (e, j, a, b, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 999_999, status: .complete, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_100_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("b", sourceUs: 1_500_000, status: .started, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_900_000, status: .idle, pixel: nil, generation: 1)
        try e.stop(nowUs: 2_000_000)
        try e.frame("a", sourceUs: 2_000_000, status: .complete, pixel: pixel, generation: 1)
        try finish(e)
        XCTAssertEqual(a.stamps.count, 1); XCTAssertEqual(b.stamps.count, 1)
        XCTAssertEqual(e.result?.streams.map(\.mediaTimeZeroUs), [100_000, 500_000])
        XCTAssertEqual(j.gaps.map(\.gap.kind), [.leading, .leading])
        XCTAssertEqual(e.state, .completed)
    }
    func testCancelledStartAndLateReadinessCannotResurrect() throws {
        let (e, _, _, _, _) = try fixture(); try e.start()
        try e.sourceReady("a", generation: 1, nowUs: 10)
        try e.stop(nowUs: 20)
        try e.tapReady(generation: 1, nowUs: 30)
        try e.sourceReady("b", generation: 1, nowUs: 30)
        try e.stop(nowUs: 40)
        XCTAssertEqual(e.state, .failed); XCTAssertNil(e.result)
        XCTAssertThrowsError(try e.start())
    }
    func testBackpressureDoesNotCommitRejectedOriginAndIdleIsNotLoss() throws {
        let (e, j, a, _, pixel) = try fixture(); try ready(e)
        a.accepts = false
        try e.frame("a", sourceUs: 1_100_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_200_000, status: .complete, pixel: pixel, generation: 1)
        a.accepts = true
        try e.frame("b", sourceUs: 1_300_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_400_000, status: .idle, pixel: nil, generation: 1)
        try e.stop(nowUs: 2_000_000); try finish(e)
        XCTAssertEqual(e.result?.streams.first?.mediaTimeZeroUs, 200_000)
        XCTAssertEqual(e.droppedFrames, 1); XCTAssertEqual(e.acceptedFrames, 2)
        XCTAssertEqual(j.gaps.filter { $0.gap.kind == .backpressure }.count, 1)
        XCTAssertEqual(e.state, .completed)
    }
    func testSourceLossStopsAllAndTerminalDuplicatesDoNotRefinalize() throws {
        let (e, _, _, _, pixel) = try fixture(); try ready(e)
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_100_000, status: .blank, pixel: nil, generation: 1)
        XCTAssertEqual(e.state, .finalizing)
        try finish(e)
        try e.writerFinished("a", terminal: VideoTerminal(frameCount: 999, durationUs: 999, error: "stale"))
        XCTAssertEqual(e.state, .interrupted)
        XCTAssertEqual(e.result?.streams.first?.frameCount, 1)
    }
    func testEncoderErrorAndZeroFramesAreNotSuccessfulMedia() throws {
        let (e, _, a, _, pixel) = try fixture(); try ready(e); a.fails = true
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        try e.frame("a", sourceUs: 1_100_000, status: .complete, pixel: pixel, generation: 1)
        XCTAssertEqual(e.state, .finalizing)
        try finish(e, a: VideoTerminal(frameCount: 0, durationUs: 0, error: "append failure"), b: VideoTerminal(frameCount: 0, durationUs: 0, error: nil))
        XCTAssertEqual(e.state, .interrupted)
        XCTAssertEqual(e.result?.streams.map(\.writerStatus), [.failed, .failed])
    }
    func testBoundedFrameAdmissionRejectsExcessWorkAndRecovers() {
        let gate = FrameAdmission(capacity: 2)
        XCTAssertTrue(gate.enter()); XCTAssertTrue(gate.enter())
        for _ in 0..<10000 { XCTAssertFalse(gate.enter()) }
        gate.leave(); XCTAssertTrue(gate.enter()); XCTAssertFalse(gate.enter())
    }
}
