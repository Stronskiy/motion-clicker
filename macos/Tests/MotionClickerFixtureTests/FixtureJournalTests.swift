import XCTest
@testable import MotionClickerFixtureSupport

final class FixtureJournalTests: XCTestCase {
    func testFreshFileHasOrderedExactNativeStampsAndGeometryWithoutOverwriting() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("synthetic.jsonl")
        let journal = try FixtureJournal(url: url, canvasId: "test", displayId: "synthetic", hostClock: ["name":"synthetic", "numerator":"1", "denominator":"1"])
        let geometry = FixtureGeometry(originX: -100, originY: 30, width: 800, height: 600, scale: 2)
        try journal.append(hostStamp: 9007199254740993, kind: "down", geometry: geometry, button: 0, x: 40, y: 80)
        try journal.append(hostStamp: 9007199254740994, kind: "up", geometry: geometry, button: 0, x: 900, y: 80)
        try journal.close()
        XCTAssertTrue(FileManager.default.fileExists(atPath: url.path))
        guard FileManager.default.fileExists(atPath: url.path) else { return }
        let bytes = try Data(contentsOf: url)
        let lines = String(decoding: bytes, as: UTF8.self).split(separator: "\n")
        XCTAssertEqual(lines.count, 3)
        XCTAssertTrue(lines[1].contains("9007199254740993"))
        let last = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(lines[2].utf8)) as? [String: Any])
        XCTAssertEqual(last["sequence"] as? Int, 2)
        XCTAssertEqual(last["inside"] as? Bool, false)
        XCTAssertEqual(last["nativeX"] as? Double, 800)
        XCTAssertEqual(last["nativeY"] as? Double, 550)
        XCTAssertThrowsError(try FixtureJournal(url: url, canvasId: "again", displayId: "synthetic", hostClock: [:]))
        XCTAssertEqual(try Data(contentsOf: url), bytes)
        XCTAssertThrowsError(try journal.append(hostStamp: 9007199254740995, kind: "marker", geometry: geometry))
    }
    func testReversedHostStampCannotEnterReferenceLog() throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: url) }
        let journal = try FixtureJournal(url: url, canvasId: "test", displayId: "synthetic", hostClock: [:])
        let g = FixtureGeometry(originX: 0, originY: 0, width: 8, height: 6, scale: 1)
        try journal.append(hostStamp: 12, kind: "marker", geometry: g)
        XCTAssertThrowsError(try journal.append(hostStamp: 11, kind: "marker", geometry: g))
        try journal.close()
    }
}
