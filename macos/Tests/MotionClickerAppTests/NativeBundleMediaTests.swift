import XCTest
@preconcurrency import AVFoundation
import CoreVideo
import MotionClickerCore
import MotionClickerRuntime
import MotionClickerRecording
@testable import MotionClickerMacRecording
@testable import MotionClickerApp

@MainActor final class NativeBundleMediaTests: XCTestCase {
    func testGeneratedEncodeSaveFreshOpenDecodeAndPlaybackPreserveLateOffsetAndBytes() async throws {
        let root=try await generatedBundle()
        defer { if ProcessInfo.processInfo.environment["MOTIONCLICKER_GENERATED_FIXTURE_OUT"] == nil { try? FileManager.default.removeItem(at:root.deletingLastPathComponent()) } }
        let before=try bytes(root)
        let opened=try await BundleIO.open(root)
        XCTAssertTrue(opened.executable,opened.mediaError ?? "Not executable")
        XCTAssertEqual(opened.snapshot.media.map(\.mediaTimeZeroUs),[0,1_500_000])
        XCTAssertEqual(opened.evidence.map(\.firstPTSUs),[0,0])
        XCTAssertEqual(opened.evidence.map(\.frameCount),[1,1])
        XCTAssertEqual(opened.evidence.map(\.durationUs),[4_000_000,2_500_000])
        let app=ClickerModel()
        app.mapping=["stale":"runtime"]
        app.openBundle(root)
        for _ in 0..<200 { if !app.operation.busy { break }; try await Task.sleep(for:.milliseconds(10)) }
        XCTAssertEqual(app.mode,"Project"); XCTAssertTrue(app.mapping.isEmpty)
        XCTAssertTrue(app.bundle?.executable == true)
        app.requestPreview()
        XCTAssertEqual(app.preview.count,1)
        XCTAssertEqual(app.operation.operation,.idle)
        let late=try XCTUnwrap(opened.snapshot.media.last)
        let player=AVPlayer(url:late.file)
        let sought=await player.seek(to:CMTime(value:500_000,timescale:1_000_000),toleranceBefore:.zero,toleranceAfter:.zero)
        XCTAssertTrue(sought)
        let readyClock=_Concurrency.ContinuousClock()
        let readyDeadline=readyClock.now.advanced(by:.seconds(10))
        while player.currentItem?.status == .unknown && readyClock.now < readyDeadline {
            try await Task.sleep(for:.milliseconds(10))
        }
        XCTAssertEqual(player.currentItem?.status,.readyToPlay,"Native player status=\(String(describing:player.currentItem?.status.rawValue)) error=\(String(describing:player.currentItem?.error))")
        let fileUs=CMTimeConvertScale(player.currentTime(),timescale:1_000_000,method:.roundHalfAwayFromZero).value
        XCTAssertEqual(fileUs+late.mediaTimeZeroUs,2_000_000)
        player.play(); XCTAssertEqual(player.rate,1); player.pause(); player.replaceCurrentItem(with:nil)
        XCTAssertEqual(try bytes(root),before)
        print("GENERATED_NATIVE_BUNDLE \(root.path) offsets=[0,1500000] codedPTS=[0,0] counts=[1,1] durations=[4000000,2500000] noAudio=true nativePlayerReady=true bytePreserved=true")
    }
    func testNonVideoBytesRemainInspectableButCannotRun() async throws {
        let root=try await generatedBundle()
        defer { if ProcessInfo.processInfo.environment["MOTIONCLICKER_GENERATED_FIXTURE_OUT"] == nil { try? FileManager.default.removeItem(at:root.deletingLastPathComponent()) } }
        let cold=try await BundleIO.open(root)
        let file=try XCTUnwrap(cold.snapshot.media.first).file
        let original=try Data(contentsOf:file)
        defer { try? original.write(to:file) }
        try Data("synthetic-not-a-video".utf8).write(to:file)
        let opened=try await BundleIO.open(root)
        XCTAssertFalse(opened.executable); XCTAssertNotNil(opened.mediaError)
        XCTAssertEqual(opened.snapshot.project,cold.snapshot.project)
    }
    func testNativeInspectorRejectsManifestCountAndDurationMismatch() async throws {
        let root=try await generatedBundle()
        defer { if ProcessInfo.processInfo.environment["MOTIONCLICKER_GENERATED_FIXTURE_OUT"] == nil { try? FileManager.default.removeItem(at:root.deletingLastPathComponent()) } }
        let bundle=try await BundleIO.open(root)
        let media=try XCTUnwrap(bundle.snapshot.media.first), s=try XCTUnwrap(bundle.snapshot.manifest.streams.first)
        for (count,duration) in [(s.frameCount+1,s.encodedDurationUs),(s.frameCount,s.encodedDurationUs+100)] {
            let bad=StreamManifest(monitorSlotId:s.monitorSlotId,relativePath:s.relativePath,clock:s.clock,mediaTimeZeroUs:s.mediaTimeZeroUs,encodedDurationUs:duration,frameCount:count,gaps:s.gaps,writerStatus:s.writerStatus,gapCount:s.gapCount,gapJournalPath:s.gapJournalPath,failureReason:s.failureReason)
            do { _=try await NativeMediaInspector.inspect(media.file,stream:bad); XCTFail("Inconsistent manifest accepted") }
            catch { XCTAssertTrue(String(describing:error).contains("origin/count/duration")) }
        }
    }
    private func generatedBundle() async throws -> URL {
        let requested=ProcessInfo.processInfo.environment["MOTIONCLICKER_GENERATED_FIXTURE_OUT"]
        let parent=FileManager.default.temporaryDirectory.appendingPathComponent("motion-media-"+UUID().uuidString)
        try FileManager.default.createDirectory(at:parent,withIntermediateDirectories:false)
        let root=requested.map { URL(filePath:$0+"-"+UUID().uuidString+".motionclicker") } ?? parent.appendingPathComponent("Generated.motionclicker")
        try RecordedBundleStore.create(root:root)
        let geometry=DisplayGeometry(origin:.init(x:0,y:0),size:.init(width:320,height:180),scaleFactor:1,rotationDegrees:0)
        let targets=(0..<2).map { index -> RecordingTarget in
            var g=geometry; g.origin.x=Double(index*320)
            let slot=MonitorSlot(id:"generated-\(index)",name:"Generated monitor \(index+1)",geometry:g)
            return RecordingTarget(slot:slot,display:RuntimeDisplay(id:slot.id,name:slot.name,geometry:g,nativeBounds:.init(origin:g.origin,size:g.size)))
        }
        let store=try RecordingStore(root:root,takeId:"generated-take")
        let session=try RecordingSession(takeId:"generated-take",targets:targets,journal:store)
        for t in targets { try session.sourceReady(monitorSlotId:t.slot.id,clock:.init(sourceAnchorTicks:0,hostAnchorUs:0,ticksPerSecond:1_000_000)) }
        try session.eventTapReady(); try session.begin(epochHostUs:1_000_000)
        for (index,target) in targets.enumerated() {
            let writer=try NativeVideoWriter(url:store.mediaFile(monitorSlotId:target.slot.id),width:320,height:180)
            var pixel: CVPixelBuffer?
            XCTAssertEqual(CVPixelBufferCreate(nil,320,180,kCVPixelFormatType_32BGRA,nil,&pixel),kCVReturnSuccess)
            let buffer=try XCTUnwrap(pixel)
            CVPixelBufferLockBaseAddress(buffer,[])
            let base=try XCTUnwrap(CVPixelBufferGetBaseAddress(buffer)).assumingMemoryBound(to:UInt8.self)
            let stride=CVPixelBufferGetBytesPerRow(buffer)
            for y in 0..<180 { for x in 0..<320 {
                let p=y*stride+x*4
                base[p]=UInt8((x+index*90)%255); base[p+1]=UInt8(y); base[p+2]=index == 0 ? 40 : 220; base[p+3]=255
            } }
            CVPixelBufferUnlockBaseAddress(buffer,[])
            let offset:Int64=index == 0 ? 0 : 1_500_000, duration=4_000_000-offset
            var accepted=false
            for _ in 0..<100 { if try writer.append(buffer,stamp:.init(sourceUs:1_000_000+offset,ptsUs:0,durationUs:duration)) { accepted=true; break }; try await Task.sleep(for:.milliseconds(10)) }
            XCTAssertTrue(accepted)
            let terminal=await withCheckedContinuation { c in writer.finish { c.resume(returning:$0) } }
            XCTAssertNil(terminal.error)
            try session.acceptedFrame(monitorSlotId:target.slot.id,sourceTicks:1_000_000+offset,encodedTimeUs:0,durationUs:duration)
        }
        try session.receive(event:.init(hostTimeUs:3_000_000,nativePosition:.init(x:40,y:50),kind:.move))
        try session.stop(hostTimeUs:5_000_000)
        for (index,target) in targets.enumerated() { try session.writerFinished(monitorSlotId:target.slot.id,status:.succeeded,encodedDurationUs:index == 0 ? 4_000_000 : 2_500_000) }
        let result=try session.project()
        _=try store.publish(manifest:result.manifest); try store.close()
        let saved=try await BundleIO.save(root,result)
        XCTAssertTrue(saved.executable,saved.mediaError ?? "No metadata evidence")
        return root
    }
    private func bytes(_ root:URL) throws -> [String:Data] {
        let enumerator=try XCTUnwrap(FileManager.default.enumerator(at:root,includingPropertiesForKeys:[.isRegularFileKey]))
        var result:[String:Data]=[:]
        for case let file as URL in enumerator where (try file.resourceValues(forKeys:[.isRegularFileKey])).isRegularFile == true { result[file.path]=try Data(contentsOf:file) }
        return result
    }
}
