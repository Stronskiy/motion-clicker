import Foundation
import XCTest
import Darwin
import MotionClickerCore
@testable import MotionClickerRecording

final class ValidationTests: XCTestCase {
    func manifest(_ count: Int) -> RecordingManifest {
        RecordingManifest(manifestVersion: 1,takeId: "take",state: .interrupted,epochHostUs: 0,durationUs: Int64(count)+1,targets: [recordingTargets()[0].slot],streams: [],rawEventCount: Int64(count),incompleteGroupIds: [],warnings: [])
    }
    func raw(_ i: Int, id: String? = nil) -> RawPointerRecord {
        RawPointerRecord(sequence: Int32(i),event: CapturedPointerEvent(hostTimeUs: Int64(i),nativePosition: Point(x: 1,y: 1),kind: .move),timeUs: Int64(i),action: RecordedAction(id: id ?? "event-\(i)",takeId: "take",monitorSlotId: "left",timeUs: Int64(i),order: i,kind: .move,x: 1,y: 1))
    }
    func scratch() throws -> URL {
        let root = FileManager.default.temporaryDirectory.appendingPathComponent("recording-validation-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: root,withIntermediateDirectories: false)
        addTeardownBlock { try? FileManager.default.removeItem(at: root) };return root
    }
    func testLargeJournalUsesBoundedValidationBuffersAndFindsDistantDuplicate() throws {
        let root = try scratch(), total = 30000
        let stats = try RecordingJournalValidation.validate(manifest: manifest(total),scratchRoot: root) { visit in
            for i in 0..<total { try visit(raw(i)) }
        }
        XCTAssertEqual(stats.records,Int64(total));XCTAssertLessThanOrEqual(stats.maximumBufferedIDs,4096)
        XCTAssertThrowsError(try RecordingJournalValidation.validate(manifest: manifest(total),scratchRoot: root) { visit in
            for i in 0..<total { try visit(raw(i,id: i == total-1 ? "event-0" : nil)) }
        })
    }
    func testStreamingValidatorReleasesConsumedActionsBeforeReadingWholeJournal() throws {
        var start = malloc_statistics_t();malloc_zone_statistics(nil,&start)
        let root = try scratch(), total = 60000
        _ = try RecordingJournalValidation.validate(manifest: manifest(total),scratchRoot: root) { visit in
            for i in 0..<total {
                try visit(raw(i))
                if i == total-1 {
                    var current = malloc_statistics_t();malloc_zone_statistics(nil,&current)
                    XCTAssertLessThan(Int64(current.size_in_use)-Int64(start.size_in_use),8*1024*1024)
                }
            }
        }
    }
}
