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
    func prepared() throws -> (Fixture, URL, URL) {
        let f = try fixture(), root = try sandbox().appendingPathComponent("recording.motionclicker"), scratch = try sandbox()
        try RecordedBundleStore.create(root: root); try published(f, at: root); return (f,root,scratch)
    }
    func save(_ f: Fixture, _ root: URL, _ scratch: URL) throws -> RecordedBundleSnapshot {
        try RecordedBundleStore.saveInitial(root: root, result: RecordingResult(manifest: f.manifest, take: f.expectedProject.takes[0], events: f.expectedProject.events), validationScratch: scratch)
    }
    func rewrite(_ path: URL, _ mutation: (inout [String: Any]) -> Void) throws {
        var json = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(contentsOf: path)) as? [String: Any]); mutation(&json)
        try JSONSerialization.data(withJSONObject: json).write(to: path)
    }
    func testPartialHoldAndSourceFailureRemainInspectableNeverExecutable() throws {
        for hold in [true,false] {
            let (f,root,scratch) = try prepared()
            let manifestPath = root.appendingPathComponent("takes/take/manifest.json")
            try rewrite(manifestPath) { json in
                json["state"] = "interrupted"
                if hold { json["incompleteGroupIds"] = ["hold"]; json["rawEventCount"] = 1 }
                else {
                    var streams = json["streams"] as! [[String: Any]]
                    streams[1]["writerStatus"] = "failed"; streams[1]["failureReason"] = "synthetic source failure"
                    json["streams"] = streams
                }
            }
            if hold {
                var bytes = try JSONEncoder().encode(f.raw[0]); bytes.append(10)
                try bytes.write(to: root.appendingPathComponent("takes/take/events.jsonl"))
            }
            let m = try JSONDecoder().decode(RecordingManifest.self,from: Data(contentsOf: manifestPath))
            let take = Take(id: "take",durationUs: 100,monitorSlotIds: ["left","right"],media: m.streams.filter { $0.writerStatus == .succeeded }.map { TakeMedia(monitorSlotId: $0.monitorSlotId,relativePath: $0.relativePath) })
            let snapshot = try RecordedBundleStore.saveInitial(root: root,result: RecordingResult(manifest: m,take: take,events: hold ? [] : f.expectedProject.events),validationScratch: scratch)
            XCTAssertEqual(snapshot.availability,.partial); XCTAssertEqual(snapshot.project.events.count,hold ? 0 : 4)
            XCTAssertEqual(snapshot.media.count,hold ? 2 : 1)
            XCTAssertEqual(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch),snapshot)
        }
    }
    func testPublicationFaultsAndRealDestinationCollisionPreserveEveryPriorByte() throws {
        for op in [BundleOperation.writeProject,.flushProject,.publishProject] {
            let (f,root,scratch) = try prepared(), before = try treeBytes(root)
            XCTAssertThrowsError(try RecordedBundleStore.saveInitial(root: root,result: RecordingResult(manifest: f.manifest,take: f.expectedProject.takes[0],events: f.expectedProject.events),validationScratch: scratch) { if $0 == op { throw RecordingError(message: "synthetic disk failure") } })
            for (path,bytes) in before { XCTAssertEqual(try Data(contentsOf: root.appendingPathComponent(path)),bytes) }
            XCTAssertFalse(FileManager.default.fileExists(atPath: root.appendingPathComponent("project.json").path))
            XCTAssertFalse(try RecordedBundleStore.recoveryEntries(root: root).isEmpty)
            _ = try save(f,root,scratch)
            let committed = try treeBytes(root); XCTAssertThrowsError(try save(f,root,scratch)); XCTAssertEqual(try treeBytes(root),committed)
        }
        let (f,root,scratch) = try prepared()
        XCTAssertThrowsError(try RecordedBundleStore.saveInitial(root: root,result: RecordingResult(manifest: f.manifest,take: f.expectedProject.takes[0],events: f.expectedProject.events),validationScratch: scratch) { op in
            if op == .publishProject { try Data("prior concurrent save".utf8).write(to: root.appendingPathComponent("project.json")) }
        })
        XCTAssertEqual(try String(contentsOf: root.appendingPathComponent("project.json"),encoding: .utf8),"prior concurrent save")
    }
    func testColdOpenRejectsCorruptSchemaReferencesRawSummaryGapTimingAndLegacyDomainJSON() throws {
        let mutations: [(URL) throws -> Void] = [
            { r in try self.rewrite(r.appendingPathComponent("project.json")) { $0["bundleVersion"] = 2 } },
            { r in try self.rewrite(r.appendingPathComponent("project.json")) { $0["manifestPath"] = "takes/../escape/manifest.json" } },
            { r in try self.rewrite(r.appendingPathComponent("project.json")) { j in var p = j["project"] as! [String: Any]; p["schemaVersion"] = 2; j["project"] = p } },
            { r in try self.rewrite(r.appendingPathComponent("project.json")) { j in var p = j["project"] as! [String: Any]; var e = p["events"] as! [[String: Any]]; e[0]["timeUs"] = 11; p["events"] = e; j["project"] = p } },
            { r in try self.rewrite(r.appendingPathComponent("takes/take/manifest.json")) { $0["rawEventCount"] = 7 } },
            { r in try self.rewrite(r.appendingPathComponent("takes/take/manifest.json")) { $0["manifestVersion"] = 2 } },
            { r in try self.rewrite(r.appendingPathComponent("takes/take/manifest.json")) { j in var streams = j["streams"] as! [[String: Any]]; streams[0]["relativePath"] = "media/../escape.mp4"; j["streams"] = streams } },
            { r in try Data("{}".utf8).write(to: r.appendingPathComponent("takes/take/events.jsonl")) },
            { r in try Data().write(to: r.appendingPathComponent("takes/take/gaps.jsonl")) },
            { r in let p = r.appendingPathComponent("takes/take/events.jsonl"); let s = try String(contentsOf: p,encoding: .utf8).replacingOccurrences(of: "nativeHostObservation",with: "unknownBasis"); try Data(s.utf8).write(to: p) },
            { r in try JSONEncoder().encode(self.fixture().expectedProject).write(to: r.appendingPathComponent("project.json")) }
        ]
        for mutation in mutations {
            let (f,root,scratch) = try prepared(); _ = try save(f,root,scratch); try mutation(root)
            XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch))
            XCTAssertTrue(try FileManager.default.contentsOfDirectory(atPath: scratch.path).isEmpty)
        }
    }
    func testMissingFilesUnsafeLinksCaseAliasesAndScratchConfinementReject() throws {
        for path in ["project.json","takes/take/manifest.json","takes/take/events.jsonl","takes/take/gaps.jsonl","media/take/left.mp4"] {
            let (f,root,scratch) = try prepared(); _ = try save(f,root,scratch)
            try FileManager.default.removeItem(at: root.appendingPathComponent(path))
            XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch))
        }
        for redirect in ["media/take/left.mp4","takes/take/events.jsonl","media/take/hidden"] {
            let (f,root,scratch) = try prepared(); _ = try save(f,root,scratch)
            let target = root.appendingPathComponent(redirect)
            try? FileManager.default.removeItem(at: target)
            try FileManager.default.createSymbolicLink(at: target,withDestinationURL: scratch)
            XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch))
        }
        let (f,root,scratch) = try prepared(); _ = try save(f,root,scratch)
        for invalidScratch in [root,root.appendingPathComponent("media"),root.deletingLastPathComponent()] {
            XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: invalidScratch))
        }
        let link = try sandbox().appendingPathComponent("alias.motionclicker")
        try FileManager.default.createSymbolicLink(at: link,withDestinationURL: root)
        XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: link,validationScratch: scratch))
        XCTAssertThrowsError(try RecordedBundleStore.create(root: root))
        try FileManager.default.moveItem(at: root.appendingPathComponent("media"),to: root.appendingPathComponent("MEDIA"))
        XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch))
    }
    func testResultMismatchAndRecoveryDiscoveryNeverAdoptOrDeleteSource() throws {
        let (f,root,scratch) = try prepared()
        let unknown = root.appendingPathComponent(".capture-constructor-orphan")
        try FileManager.default.createDirectory(at: unknown,withIntermediateDirectories: false)
        let prior = try treeBytes(root)
        let wrong = RecordingResult(manifest: f.manifest,take: f.expectedProject.takes[0],events: [])
        XCTAssertThrowsError(try RecordedBundleStore.saveInitial(root: root,result: wrong,validationScratch: scratch))
        XCTAssertEqual(try treeBytes(root),prior)
        let entries = try RecordedBundleStore.recoveryEntries(root: root)
        XCTAssertTrue(entries.contains(unknown)); XCTAssertTrue(entries.contains(root.appendingPathComponent("media/take")))
        XCTAssertTrue(entries.contains(root.appendingPathComponent("takes/take")))
        XCTAssertThrowsError(try RecordedBundleStore.coldOpen(root: root,validationScratch: scratch))
        _ = try save(f,root,scratch)
        XCTAssertEqual(try RecordedBundleStore.recoveryEntries(root: root),[unknown])
    }
    func treeBytes(_ root: URL) throws -> [String: Data] {
        var result: [String: Data] = [:]
        for case let url as URL in FileManager.default.enumerator(at: root, includingPropertiesForKeys: [.isRegularFileKey])! {
            if try url.resourceValues(forKeys: [.isRegularFileKey]).isRegularFile == true { result[String(url.path.dropFirst(root.path.count))] = try Data(contentsOf: url) }
        }
        return result
    }
}
