import Foundation
import XCTest
import MotionClickerCore
@testable import MotionClickerRecording

final class RecordedBundleTests: XCTestCase {
    struct Fixture: Decodable {
        let manifest: RecordingManifest
        let raw: [RawPointerRecord]
        let gaps: [JournalGapRecord]
        let expectedProject: MotionProject
    }
    func fixture() throws -> Fixture {
        let path = URL(fileURLWithPath: #filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().appendingPathComponent("contracts/fixtures/recording/bundle.json")
        return try JSONDecoder().decode(Fixture.self, from: Data(contentsOf: path))
    }
    func sandbox() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("bundle-tests-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: false)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) }; return root
    }
    // Writes only public synthetic evidence; no encoder or capture writer is constructed.
    func published(_ f: Fixture, at root: URL) throws {
        let fm = FileManager.default
        try fm.createDirectory(at: root.appendingPathComponent("takes/take"), withIntermediateDirectories: true)
        try fm.createDirectory(at: root.appendingPathComponent("media/take"), withIntermediateDirectories: true)
        try JSONEncoder().encode(f.manifest).write(to: root.appendingPathComponent("takes/take/manifest.json"))
        var events = Data(), gaps = Data()
        for r in f.raw { events.append(try JSONEncoder().encode(r)); events.append(10) }
        for g in f.gaps { gaps.append(try JSONEncoder().encode(g)); gaps.append(10) }
        try events.write(to: root.appendingPathComponent("takes/take/events.jsonl"))
        try gaps.write(to: root.appendingPathComponent("takes/take/gaps.jsonl"))
        for s in f.manifest.streams where s.writerStatus == .succeeded {
            try Data("synthetic-video-\(s.monitorSlotId)".utf8).write(to: root.appendingPathComponent(s.relativePath))
        }
    }
    func testInitialSaveAndReadOnlyColdOpenPreserveSharedProjectionOffsetsAndTiming() throws {
        let f = try fixture(), parent = try sandbox(), root = parent.appendingPathComponent("recording.motionclicker"), scratch = try sandbox()
        try RecordedBundleStore.create(root: root); try published(f, at: root)
        let result = RecordingResult(manifest: f.manifest, take: f.expectedProject.takes[0], events: f.expectedProject.events)
        let saved = try RecordedBundleStore.saveInitial(root: root, result: result, validationScratch: scratch)
        XCTAssertEqual(saved.project, f.expectedProject)
        let before = try treeBytes(root)
        let opened = try RecordedBundleStore.coldOpen(root: root, validationScratch: scratch)
        XCTAssertEqual(opened, saved)
        XCTAssertEqual(opened.media.map(\.mediaTimeZeroUs), [0,5])
        XCTAssertEqual(opened.manifest.epochHostUs, 9007199254740993)
        XCTAssertEqual(opened.manifest.pointerTimingBasis, .nativeHostObservation)
        XCTAssertEqual(opened.availability, .requiresMonitorMapping)
        _ = try RecordedBundleStore.coldOpen(root: root, validationScratch: scratch)
        XCTAssertEqual(try treeBytes(root), before)
        XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: scratch.path).isEmpty)
    }
    func treeBytes(_ root: URL) throws -> [String: Data] {
        var result: [String: Data] = [:]
        for case let url as URL in FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey])! {
            if try url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true { result[String(url.path.dropFirst(root.path.count))] = try Data(contentsOf: url) }
        }
        return result
    }
}
