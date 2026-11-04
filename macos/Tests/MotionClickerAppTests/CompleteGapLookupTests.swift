import XCTest
import MotionClickerCore
import MotionClickerRuntime
import MotionClickerRecording
@testable import MotionClickerApp

@MainActor final class CompleteGapLookupTests: XCTestCase {
    func testValidColdBundleJournalOnly65thGapLeadingTrailingAndMonitorSwitchAreMasked() async throws {
        let (root,snapshot)=try fixture()
        let before=try bytes(root)
        let first=try XCTUnwrap(snapshot.media.first), second=try XCTUnwrap(snapshot.media.last)
        XCTAssertEqual(snapshot.manifest.streams.first?.gaps.count,64)
        XCTAssertEqual(snapshot.manifest.streams.first?.gapCount,65)
        let lookup=CompleteGapLookup(snapshot:snapshot)
        let gap=try await lookup.window(monitorSlotId:first.monitorSlotId,sourceUs:65)
        XCTAssertEqual(gap.kind,.backpressure)
        XCTAssertTrue(gap.contains(monitorSlotId:first.monitorSlotId,sourceUs:65))
        let mask=SourceGapInspection(lookup:lookup)
        XCTAssertTrue(mask.unavailable(media:first,sourceUs:65)?.contains("Inspecting") == true)
        mask.request(media:first,sourceUs:65); try await settle(mask)
        XCTAssertTrue(mask.unavailable(media:first,sourceUs:65)?.contains("backpressure") == true)
        // A cached window for one monitor must not grant availability to another.
        XCTAssertTrue(mask.unavailable(media:second,sourceUs:65)?.contains("Inspecting") == true)
        mask.request(media:second,sourceUs:65); try await settle(mask)
        XCTAssertNil(mask.unavailable(media:second,sourceUs:65))
        XCTAssertTrue(mask.unavailable(media:second,sourceUs:2)?.contains("first image") == true)
        XCTAssertTrue(mask.unavailable(media:second,sourceUs:100)?.contains("valid video prefix") == true)
        let clear=try await lookup.window(monitorSlotId:first.monitorSlotId,sourceUs:66)
        XCTAssertNil(clear.kind)
        XCTAssertFalse(clear.contains(monitorSlotId:first.monitorSlotId,sourceUs:65))
        XCTAssertEqual(try bytes(root),before)
        mask.cancel()
    }
    func testIncompleteJournalFailsClosedRatherThanUsingPreview() async throws {
        let (root,snapshot)=try fixture()
        let original=try Data(contentsOf:snapshot.gapsFile)
        defer { try? original.write(to:snapshot.gapsFile) }
        let lines=original.split(separator:10)
        try Data(lines.dropLast().flatMap { Array($0)+[10] }).write(to:snapshot.gapsFile)
        let mask=SourceGapInspection(lookup:CompleteGapLookup(snapshot:snapshot))
        let media=try XCTUnwrap(snapshot.media.first)
        mask.request(media:media,sourceUs:65); try await settle(mask)
        XCTAssertTrue(mask.unavailable(media:media,sourceUs:65)?.contains("unavailable") == true)
        XCTAssertNotNil(mask.error)
        _=root
    }
    private func settle(_ mask:SourceGapInspection) async throws {
        let clock=_Concurrency.ContinuousClock(), deadline=_Concurrency.ContinuousClock().now.advanced(by:.seconds(3))
        while mask.isLoading && clock.now < deadline { try await Task.sleep(for:.milliseconds(1)) }
        XCTAssertFalse(mask.isLoading)
    }
    // Portable valid bundle; synthetic placeholder media is not codec evidence.
    // NativeBundleMediaTests separately combines >=65 gaps with real H.264.
    private func fixture() throws -> (URL,RecordedBundleSnapshot) {
        let parent=FileManager.default.temporaryDirectory.appendingPathComponent("gap-tests-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:parent,withIntermediateDirectories:false)
        addTeardownBlock { try? FileManager.default.removeItem(at:parent) }
        let root=parent.appendingPathComponent("Generated.motionclicker"), scratch=parent.appendingPathComponent("scratch")
        try FileManager.default.createDirectory(at:scratch,withIntermediateDirectories:false)
        try RecordedBundleStore.create(root:root)
        let targets=(0..<2).map { index -> RecordingTarget in
            let g=DisplayGeometry(origin:.init(x:Double(index*100),y:0),size:.init(width:100,height:100),scaleFactor:1,rotationDegrees:0)
            let slot=MonitorSlot(id:"m\(index)",name:"Generated \(index)",geometry:g)
            return RecordingTarget(slot:slot,display:.init(id:slot.id,name:slot.name,geometry:g,nativeBounds:.init(origin:g.origin,size:g.size)))
        }
        let store=try RecordingStore(root:root,takeId:"take"), session=try RecordingSession(takeId:"take",targets:targets,journal:store)
        for target in targets { try session.sourceReady(monitorSlotId:target.slot.id,clock:.init(sourceAnchorTicks:0,hostAnchorUs:0,ticksPerSecond:1_000_000)) }
        try session.eventTapReady(); try session.begin(epochHostUs:1_000)
        for (i,target) in targets.enumerated() {
            let offset:Int64=i == 0 ? 0 : 5
            try Data("generated portable placeholder".utf8).write(to:store.mediaFile(monitorSlotId:target.slot.id))
            try session.acceptedFrame(monitorSlotId:target.slot.id,sourceTicks:1_000+offset,encodedTimeUs:0,durationUs:100-offset)
        }
        for i in 1...65 { try session.reportGap(monitorSlotId:targets[0].slot.id,startTicks:1_000+Int64(i),endTicks:1_001+Int64(i),kind:.backpressure) }
        try session.stop(hostTimeUs:1_100)
        for (i,target) in targets.enumerated() { try session.writerFinished(monitorSlotId:target.slot.id,status:.succeeded,encodedDurationUs:i == 0 ? 100 : 95) }
        let result=try session.project(); _=try store.publish(manifest:result.manifest); try store.close()
        _=try RecordedBundleStore.saveInitial(root:root,result:result,validationScratch:scratch)
        return(root,try RecordedBundleStore.coldOpen(root:root,validationScratch:scratch))
    }
    private func bytes(_ root:URL) throws -> [String:Data] {
        let paths=try XCTUnwrap(FileManager.default.enumerator(at:root,includingPropertiesForKeys:[.isRegularFileKey]))
        var result:[String:Data]=[:]
        for case let file as URL in paths where (try file.resourceValues(forKeys:[.isRegularFileKey])).isRegularFile == true { result[file.path]=try Data(contentsOf:file) }
        return result
    }
}
