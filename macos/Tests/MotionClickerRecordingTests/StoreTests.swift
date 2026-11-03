import Foundation
import XCTest
import MotionClickerCore
@testable import MotionClickerRecording

final class StoreTests: XCTestCase {
    func root() throws -> URL {
        let u = FileManager.default.temporaryDirectory.appendingPathComponent("motionclicker-synthetic-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: u, withIntermediateDirectories: false)
        addTeardownBlock { try? FileManager.default.removeItem(at: u) }
        try Data("previous-save".utf8).write(to: u.appendingPathComponent("project.json"))
        return u
    }
    func record() -> RawPointerRecord {
        RawPointerRecord(sequence: 0, event: CapturedPointerEvent(hostTimeUs: 1, nativePosition: Point(x: 1,y: 1), kind: .move), timeUs: 1, action: RecordedAction(id: "event", takeId: "take", monitorSlotId: "left", timeUs: 1, order: 0, kind: .move, x: 1,y: 1))
    }
    func manifest() -> RecordingManifest {
        RecordingManifest(manifestVersion: 1, takeId: "take", state: .completed, epochHostUs: 0, durationUs: 1000, targets: [recordingTargets()[0].slot], streams: [StreamManifest(monitorSlotId: "left", relativePath: "media/take/left.mp4", clock: ClockAnchor(sourceAnchorTicks: 0,hostAnchorUs: 0,ticksPerSecond: 1000000), mediaTimeZeroUs: 0, encodedDurationUs: 1000, frameCount: 1,gaps: [],writerStatus: .succeeded)], rawEventCount: 1,incompleteGroupIds: [],warnings: [])
    }
    func testActualDisposableFilesystemPublishesManifestLastAndStreamsJournal() throws {
        let r = try root(), store = try RecordingStore(root: r, takeId: "take")
        try store.append(record: record()); try Data("synthetic-media".utf8).write(to: store.mediaFile(monitorSlotId: "left"))
        XCTAssertFalse(FileManager.default.fileExists(atPath: r.appendingPathComponent("takes/take/manifest.json").path))
        let path = try store.publish(manifest: manifest())
        XCTAssertEqual(path, try store.publish(manifest: manifest()))
        XCTAssertEqual(try JSONDecoder().decode(RecordingManifest.self,from: Data(contentsOf: path)),manifest())
        var raw: [RawPointerRecord] = []; try store.read { raw.append($0) }; XCTAssertEqual(raw, [record()])
        XCTAssertEqual(try String(contentsOf: r.appendingPathComponent("project.json"), encoding: .utf8), "previous-save")
        XCTAssertTrue(store.recovery.mediaPublished); XCTAssertTrue(store.recovery.manifestPublished)
        XCTAssertThrowsError(try RecordingStore(root: r,takeId: "take")); try store.close()
    }
    func testFaultsPreservePreviousSaveAndExposeStagingOrOrphan() throws {
        for operation in [StoreOperation.append,.flush,.moveMedia,.publishManifest] {
            let r = try root()
            let store = try RecordingStore(root: r, takeId: "take") { if $0 == operation { throw RecordingError(message: "disk full") } }
            try Data("synthetic-media".utf8).write(to: store.mediaFile(monitorSlotId: "left"))
            if operation == .append { XCTAssertThrowsError(try store.append(record: record())) }
            else { try store.append(record: record()) }
            XCTAssertThrowsError(try store.publish(manifest: manifest()))
            XCTAssertEqual(try String(contentsOf: r.appendingPathComponent("project.json"), encoding: .utf8), "previous-save")
            XCTAssertFalse(store.recovery.manifestPublished); XCTAssertEqual(store.recovery.mediaPublished, operation == .publishManifest)
            XCTAssertTrue(FileManager.default.fileExists(atPath: store.recovery.stagingPath)); try store.close()
        }
    }
    func testRejectsTraversalSymlinkAndCaseCollisions() throws {
        let r = try root()
        for id in ["../escape", "bad/name", ".", "..", "", "CON", "aux", "com1", "LPT9", "tail."] { XCTAssertThrowsError(try RecordingStore(root: r,takeId: id)) }
        let outside = try root(); try FileManager.default.createSymbolicLink(at: r.appendingPathComponent("media"),withDestinationURL: outside)
        XCTAssertThrowsError(try RecordingStore(root: r,takeId: "take"))
        let q = try root(); try FileManager.default.createDirectory(at: q.appendingPathComponent("MEDIA"),withIntermediateDirectories: false)
        XCTAssertThrowsError(try RecordingStore(root: q,takeId: "take"))
    }
    func testRejectsRedirectedStagingAndFailedMediaCannotPublishAsSuccessful() throws {
        let r = try root(), store = try RecordingStore(root: r,takeId: "take")
        try store.append(record: record()); _ = try store.mediaFile(monitorSlotId: "left")
        XCTAssertThrowsError(try store.publish(manifest: manifest()))
        let outside = try root(), stagingMedia = URL(fileURLWithPath: store.recovery.stagingPath).appendingPathComponent("media")
        try FileManager.default.removeItem(at: stagingMedia);try FileManager.default.createSymbolicLink(at: stagingMedia,withDestinationURL: outside)
        XCTAssertThrowsError(try store.mediaFile(monitorSlotId: "right")); try store.close()
    }
}

extension StoreTests {
    func testRejectsUnexpectedSymlinkInMediaTreeBeforeAnyPublication() throws {
        let r = try root(), outside = try root(), store = try RecordingStore(root: r, takeId: "take")
        try store.append(record: record()); let file = try store.mediaFile(monitorSlotId: "left")
        XCTAssertFalse(FileManager.default.fileExists(atPath: file.path))
        try Data("synthetic-media".utf8).write(to: file)
        XCTAssertThrowsError(try store.mediaFile(monitorSlotId: "left"))
        try FileManager.default.createSymbolicLink(at: file.deletingLastPathComponent().appendingPathComponent("hidden"), withDestinationURL: outside)
        XCTAssertThrowsError(try store.publish(manifest: manifest()))
        XCTAssertFalse(store.recovery.mediaPublished); try store.close()
    }
    func testPublicationRetryAfterMetadataFailureAndConcurrentTakeCollision() throws {
        let r = try root(); var fail = true
        let store = try RecordingStore(root: r, takeId: "take") { if $0 == .publishManifest && fail { throw RecordingError(message: "disk full") } }
        try store.append(record: record()); try Data("synthetic-media".utf8).write(to: store.mediaFile(monitorSlotId: "left"))
        XCTAssertThrowsError(try store.publish(manifest: manifest())); fail = false
        let path = try store.publish(manifest: manifest()); XCTAssertTrue(FileManager.default.fileExists(atPath: path.path)); try store.close()
        let otherRoot = try root(), first = try RecordingStore(root: otherRoot, takeId: "take"), second = try RecordingStore(root: otherRoot,takeId: "take")
        for s in [first,second] { try s.append(record: record()); try Data("synthetic-media".utf8).write(to: s.mediaFile(monitorSlotId: "left")) }
        let published = try first.publish(manifest: manifest()), before = try Data(contentsOf: published)
        XCTAssertThrowsError(try second.publish(manifest: manifest())); XCTAssertEqual(try Data(contentsOf: published),before)
        try first.close(); try second.close()
    }
}
