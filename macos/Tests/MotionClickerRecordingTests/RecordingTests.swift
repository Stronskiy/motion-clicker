import Foundation
import XCTest
import MotionClickerCore
import MotionClickerRuntime
@testable import MotionClickerRecording

final class RecordingTests: XCTestCase {
    struct Timing: Decodable {
        let sourceAnchorTicks, hostAnchorUs, ticksPerSecond, epochHostUs, sourceTicks, expectedUs: Int64
    }
    let fixtures = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("contracts/fixtures/recording")
    func testNativeAnchorsLargeIntegersAndFloorFromSharedFixtures() throws {
        let cases = try JSONDecoder().decode([Timing].self, from: Data(contentsOf: fixtures.appendingPathComponent("timing.json")))
        for c in cases {
            let mapper = try MediaClockMapper(sourceAnchorTicks: c.sourceAnchorTicks, hostAnchorUs: c.hostAnchorUs, ticksPerSecond: c.ticksPerSecond, epochHostUs: c.epochHostUs)
            XCTAssertEqual(try mapper.map(sourceTicks: c.sourceTicks), c.expectedUs)
        }
    }
    func testInvalidScaleAndOverflowAreVisible() throws {
        for scale: Int64 in [0, -1, 2147483648] {
            XCTAssertThrowsError(try MediaClockMapper(sourceAnchorTicks: 0, hostAnchorUs: 0, ticksPerSecond: scale, epochHostUs: 0))
        }
        let mapper = try MediaClockMapper(sourceAnchorTicks: 0, hostAnchorUs: 0, ticksPerSecond: 1, epochHostUs: 0)
        XCTAssertThrowsError(try mapper.map(sourceTicks: .max))
        let subtraction = try MediaClockMapper(sourceAnchorTicks: .min, hostAnchorUs: 0, ticksPerSecond: 1000000, epochHostUs: 0)
        XCTAssertThrowsError(try subtraction.map(sourceTicks: .max))
    }
}
