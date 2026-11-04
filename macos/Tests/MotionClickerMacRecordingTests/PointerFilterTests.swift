import XCTest
import MotionClickerCore
@testable import MotionClickerMacRecording

final class PointerFilterTests: XCTestCase {
    func sample(_ kind: ActionKind, _ button: MouseButton? = nil, x: Double = 1, tag: Int64 = 0) -> MacPointerSample {
        MacPointerSample(observationHostUs: 1_000_000, timestampNs: 1_000_000_000, position: Point(x: x, y: 1), kind: kind, button: button, deltaX: nil, deltaY: nil, sourceTag: tag)
    }
    func testObservationSurvivesOwnerAndClassifierDelayAndIgnoresPacketOrigin() throws {
        var filter = PointerFilter(options: MacRecordingOptions(ownInjectedTag: 42, isControl: { sample in
            // Deliberately later classification; it cannot resample canonical time.
            Thread.sleep(forTimeInterval: 0.02)
            return false
        }))
        for stamp: UInt64 in [0, 500_000_000, 9_007_199_254_740_993, .max] {
            let sample = MacPointerSample(observationHostUs: 1_000_010, timestampNs: stamp, position: Point(x: 1, y: 1), kind: .move, button: nil, deltaX: nil, deltaY: nil, sourceTag: 0)
            let event = try XCTUnwrap(filter.receive(sample, nowUs: 9_000_010))
            XCTAssertEqual(event.hostTimeUs, 1_000_010)
            XCTAssertEqual(event.timing?.packetTimestamp, String(stamp))
            XCTAssertEqual(event.timing?.ownerDelayUs, 8_000_000)
        }
    }
    func testInjectedAndWholeControlGesturesAreExcluded() throws {
        var filter = PointerFilter(options: MacRecordingOptions(ownInjectedTag: 42, isControl: { $0.position.x < 0 }))
        XCTAssertNil(try filter.receive(sample(.buttonDown, .left, tag: 42), nowUs: 1_000_001))
        XCTAssertNil(try filter.receive(sample(.buttonDown, .left, x: -1), nowUs: 1_000_001))
        XCTAssertNil(try filter.receive(sample(.move, x: 50), nowUs: 1_000_001))
        XCTAssertNil(try filter.receive(sample(.buttonUp, .left, x: 50), nowUs: 1_000_001))
        XCTAssertNotNil(try filter.receive(sample(.buttonDown, .left), nowUs: 1_000_001))
        // Real gesture crossing the control region must retain its real release.
        XCTAssertNotNil(try filter.receive(sample(.buttonUp, .left, x: -1), nowUs: 1_000_001))
    }
    func testControlRepeatedDownDoesNotInventBalance() throws {
        var filter = PointerFilter(options: MacRecordingOptions(ownInjectedTag: 42, isControl: { _ in true }))
        XCTAssertNil(try filter.receive(sample(.buttonDown, .left), nowUs: 1_000_001))
        XCTAssertThrowsError(try filter.receive(sample(.buttonDown, .right), nowUs: 1_000_001))
    }
    func testNativeMetadataRejectsUnsupportedButtonAndKeepsScrollSigns() throws {
        XCTAssertThrowsError(try MacPointerSample.decode(type: .otherMouseDown, observationHostUs: 1_000_000, timestampNs: 1, x: 1, y: 2, buttonNumber: 3, scrollX: 0, scrollY: 0, sourceTag: 0))
        let scroll = try XCTUnwrap(MacPointerSample.decode(type: .scrollWheel, observationHostUs: 1_000_000, timestampNs: 9_007_199_254_740_993, x: -100, y: 2, buttonNumber: 0, scrollX: -3, scrollY: 7, sourceTag: 0))
        XCTAssertEqual(scroll.timestampNs, 9_007_199_254_740_993)
        XCTAssertEqual(scroll.position.x, -100)
        XCTAssertEqual(scroll.deltaX, 3)
        XCTAssertEqual(scroll.deltaY, -7)
        XCTAssertNil(try MacPointerSample.decode(type: .keyDown, observationHostUs: 1_000_000, timestampNs: 1, x: 1, y: 2, buttonNumber: 0, scrollX: 0, scrollY: 0, sourceTag: 0))
    }
}
