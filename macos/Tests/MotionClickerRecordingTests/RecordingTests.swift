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
    func testNewAndLegacyPointerMetadataRoundTripExactlyFromSharedFixtures() throws {
        let data = try Data(contentsOf: fixtures.appendingPathComponent("pointer-timing.json"))
        let cases = try XCTUnwrap(JSONSerialization.jsonObject(with: data) as? [[String: Any]])
        for c in cases {
            let expected = try XCTUnwrap(c["event"] as? NSDictionary)
            let event = try JSONDecoder().decode(CapturedPointerEvent.self, from: JSONSerialization.data(withJSONObject: expected))
            let actual = try XCTUnwrap(JSONSerialization.jsonObject(with: JSONEncoder().encode(event)) as? NSDictionary)
            XCTAssertEqual(actual, expected, "\(c["name"]!)")
        }
    }
    func testMalformedPointerMetadataRejectedFromSharedFixtures() throws {
        let cases = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixtures.appendingPathComponent("pointer-timing-invalid.json"))) as? [[String: Any]])
        for c in cases {
            XCTAssertThrowsError(try JSONDecoder().decode(CapturedPointerEvent.self, from: JSONSerialization.data(withJSONObject: c)), "\(c)")
        }
    }
    func testNewAndLegacyTakeJournalFixturesPreserveCanonicalTimesAndValidate() throws {
        let cases = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: fixtures.appendingPathComponent("pointer-timing-takes.json"))) as? [[String: Any]])
        let root = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: root) }
        for c in cases {
            let m = try JSONDecoder().decode(RecordingManifest.self, from: JSONSerialization.data(withJSONObject: c["manifest"]!))
            let records = try JSONDecoder().decode([RawPointerRecord].self, from: JSONSerialization.data(withJSONObject: c["records"]!))
            XCTAssertEqual(try JSONSerialization.jsonObject(with: JSONEncoder().encode(m)) as? NSDictionary, c["manifest"] as? NSDictionary)
            XCTAssertEqual(try JSONSerialization.jsonObject(with: JSONEncoder().encode(records)) as? NSArray, c["records"] as? NSArray)
            XCTAssertEqual(records.map(\.timeUs), [10]); XCTAssertEqual(records.map { $0.action?.timeUs }, [10])
            XCTAssertEqual(try RecordingJournalValidation.validate(manifest: m, scratchRoot: root) { visit in for r in records { try visit(r) } }.records, 1)
            // New basis cannot be stripped or added to reinterpret legacy history.
            var wrong = c["manifest"] as! [String: Any]
            if m.pointerTimingBasis == nil { wrong["pointerTimingBasis"] = "nativeHostObservation" } else { wrong.removeValue(forKey: "pointerTimingBasis") }
            let wrongManifest = try JSONDecoder().decode(RecordingManifest.self, from: JSONSerialization.data(withJSONObject: wrong))
            XCTAssertThrowsError(try RecordingJournalValidation.validate(manifest: wrongManifest, scratchRoot: root) { visit in for r in records { try visit(r) } })
        }
    }
    func testSessionRejectsMissingMetadataAndMixedObservationClocks() throws {
        let timing = try PointerTiming(basis: .nativeHostObservation, hostClock: .machAbsolute, packetClock: .cgEventTimestamp, packetUnits: .nanoseconds, packetTimestamp: "18446744073709551615", ownerDelayUs: 0)
        let windows = try PointerTiming(basis: .nativeHostObservation, hostClock: .queryPerformanceCounter, packetClock: .windowsLowLevelMouseTime, packetUnits: .milliseconds, packetTimestamp: "0", ownerDelayUs: 0)
        let missing = try readySession(MemoryJournal(), pointerTimingBasis: .nativeHostObservation)
        XCTAssertThrowsError(try missing.receive(event: CapturedPointerEvent(hostTimeUs: 1_000_010, nativePosition: Point(x: 1,y: 1), kind: .move)))
        let mixed = try readySession(MemoryJournal(), pointerTimingBasis: .nativeHostObservation)
        try mixed.receive(event: CapturedPointerEvent(hostTimeUs: 1_000_010, nativePosition: Point(x: 1,y: 1), kind: .move, timing: timing))
        XCTAssertThrowsError(try mixed.receive(event: CapturedPointerEvent(hostTimeUs: 1_000_020, nativePosition: Point(x: 1,y: 1), kind: .move, timing: windows)))
    }
    func testUnknownManifestTimingBasisRejected() throws {
        let json = #"{"manifestVersion":1,"takeId":"take","state":"completed","epochHostUs":0,"durationUs":1,"targets":[],"streams":[],"rawEventCount":0,"incompleteGroupIds":[],"warnings":[],"pointerTimingBasis":"futureUnknown"}"#
        XCTAssertThrowsError(try JSONDecoder().decode(RecordingManifest.self, from: Data(json.utf8)))
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
