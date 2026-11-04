import XCTest
@preconcurrency import AVFoundation
import CoreVideo
import MotionClickerRecording
@testable import MotionClickerMacRecording

@MainActor final class NativeEncoderTests: XCTestCase {
    // Real encoder/readback, generated pixels only. No SCStream, tap or privacy calls.
    func testSyntheticH264NormalizedPTSCountDurationAndNoAudio() async throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false)
        defer { try? FileManager.default.removeItem(at: dir) }
        let url = dir.appendingPathComponent("synthetic.mp4")
        let writer = try NativeVideoWriter(url: url, width: 64, height: 64)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, [kCVPixelBufferIOSurfacePropertiesKey: [:]] as CFDictionary, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, [])
        memset(CVPixelBufferGetBaseAddress(buffer), 127, CVPixelBufferGetBytesPerRow(buffer) * 64)
        CVPixelBufferUnlockBaseAddress(buffer, [])
        for index in 0..<30 {
            let stamp = FrameStamp(sourceUs: 1_500_000 + Int64(index) * 100_000 / 3, ptsUs: Int64(index) * 100_000 / 3, durationUs: 33_333)
            var accepted = false
            for _ in 0..<100 {
                if try writer.append(buffer, stamp: stamp) { accepted = true; break }
                // Synthetic producer yields for encoder readiness; production drops immediately.
                try await Task.sleep(for: .milliseconds(10))
            }
            XCTAssertTrue(accepted)
        }
        let terminal = await withCheckedContinuation { continuation in writer.finish { continuation.resume(returning: $0) } }
        XCTAssertNil(terminal.error)
        XCTAssertEqual(terminal.frameCount, 30)
        XCTAssertEqual(terminal.durationUs, 999_999)
        let asset = AVURLAsset(url: url)
        let tracks = try await asset.load(.tracks)
        XCTAssertEqual(tracks.count, 1)
        let track = try XCTUnwrap(tracks.first)
        XCTAssertEqual(track.mediaType, .video)
        let size = try await track.load(.naturalSize)
        XCTAssertEqual(size.width, 64); XCTAssertEqual(size.height, 64)
        let descriptions = try await track.load(.formatDescriptions)
        XCTAssertEqual(CMFormatDescriptionGetMediaSubType(try XCTUnwrap(descriptions.first)), kCMVideoCodecType_H264)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        reader.add(output); XCTAssertTrue(reader.startReading())
        var pts: [Int64] = []
        while let sample = output.copyNextSampleBuffer() {
            pts.append(CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sample), timescale: 1_000_000, method: .roundHalfAwayFromZero).value)
        }
        XCTAssertEqual(reader.status, .completed)
        XCTAssertEqual(pts.count, 30)
        XCTAssertEqual(pts.first, 0)
        XCTAssertEqual(pts.last, 966_666)
        let duration = try await asset.load(.duration)
        XCTAssertEqual(CMTimeConvertScale(duration, timescale: 1_000_000, method: .roundHalfAwayFromZero).value, 999_999)
        XCTAssertThrowsError(try NativeVideoWriter(url: url, width: 64, height: 64))
    }
    func testStaticVideoExtendsToExplicitStopWithoutInventingFrames() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { if FileManager.default.fileExists(atPath: url.path) { try? FileManager.default.removeItem(at: url) } }
        let writer = try NativeVideoWriter(url: url, width: 64, height: 64)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, []); memset(CVPixelBufferGetBaseAddress(buffer), 0, CVPixelBufferGetBytesPerRow(buffer) * 64); CVPixelBufferUnlockBaseAddress(buffer, [])
        XCTAssertTrue(try writer.append(buffer, stamp: FrameStamp(sourceUs: 1_500_000, ptsUs: 0, durationUs: 1_000_000)))
        let terminal = await withCheckedContinuation { c in writer.finish { c.resume(returning: $0) } }
        XCTAssertNil(terminal.error)
        XCTAssertEqual(terminal.frameCount, 1)
        // Requested stop is 2.5s host time, with first image at1.5s: 1s file.
        XCTAssertEqual(terminal.durationUs, 1_000_000)
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)
        XCTAssertEqual(CMTimeConvertScale(duration, timescale: 1_000_000, method: .roundHalfAwayFromZero).value, 1_000_000)
        let tracks = try await asset.loadTracks(withMediaType: .video)
        let track = try XCTUnwrap(tracks.first)
        let range = try await track.load(.timeRange)
        XCTAssertEqual(CMTimeConvertScale(range.duration, timescale: 1_000_000, method: .roundHalfAwayFromZero).value, 1_000_000)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        reader.add(output); XCTAssertTrue(reader.startReading())
        var count = 0
        while let sample = output.copyNextSampleBuffer() {
            count += CMSampleBufferGetNumSamples(sample)
            if CMSampleBufferGetNumSamples(sample) > 0 {
                XCTAssertEqual(CMTimeConvertScale(CMSampleBufferGetDuration(sample), timescale: 1_000_000, method: .roundHalfAwayFromZero).value, 1_000_000)
            }
        }
        XCTAssertEqual(reader.status, .completed); XCTAssertEqual(count, 1)
    }
    func testNativeSparseFailureRemainsFailedInSessionDespiteSuccessfulPrefixFinish() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { if FileManager.default.fileExists(atPath: url.path) { try? FileManager.default.removeItem(at: url) } }
        let a = try NativeVideoWriter(url: url, width: 64, height: 64), b = EngineWriter(), journal = EngineJournal()
        let e = try RecordingEngine(targets: engineTargets(), takeId: "take", journal: journal, writers: ["a": a, "b": b], options: MacRecordingOptions(ownInjectedTag: 42, isControl: { _ in false }))
        try e.start(); try e.sourceReady("a", generation: 1, nowUs: 1_000_000); try e.sourceReady("b", generation: 1, nowUs: 1_000_000); try e.tapReady(generation: 1, nowUs: 1_000_000)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, []); memset(CVPixelBufferGetBaseAddress(buffer), 0, CVPixelBufferGetBytesPerRow(buffer) * 64); CVPixelBufferUnlockBaseAddress(buffer, [])
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: buffer, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: buffer, generation: 1)
        try e.drop("a", sourceUs: 1_100_000, generation: 1, reason: "admission")
        try e.frame("a", sourceUs: 5_000_000, status: .complete, pixel: buffer, generation: 1)
        try e.stop(nowUs: 6_000_000)
        let aTerminal = await withCheckedContinuation { c in a.finish { c.resume(returning: $0) } }
        let bTerminal = await withCheckedContinuation { c in b.finish { c.resume(returning: $0) } }
        XCTAssertNil(aTerminal.error) // Healthy prefix closure cannot erase rejected continuation.
        try e.writerFinished("a", terminal: aTerminal); try e.writerFinished("b", terminal: bTerminal)
        XCTAssertEqual(e.result?.state, .interrupted)
        XCTAssertEqual(e.result?.streams.first?.writerStatus, .failed)
        XCTAssertEqual(e.result?.streams.first?.encodedDurationUs, 100_000)
        XCTAssertTrue(e.result?.streams.first?.failureReason?.contains("videoDiscontinuityUnsupported") == true)
        XCTAssertEqual(journal.gaps.first?.gap, RecordingGap(startUs: 100_000, endUs: 4_000_000, kind: .backpressure, reason: "admission"))
        XCTAssertEqual(try e.project().take.media.map(\.monitorSlotId), ["b"])
    }
    func testNativeDropLateImageDropCannotPublishSuccessfulPrefix() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { if FileManager.default.fileExists(atPath: url.path) { try? FileManager.default.removeItem(at: url) } }
        let a = try NativeVideoWriter(url: url, width: 64, height: 64), b = EngineWriter(), journal = EngineJournal()
        let e = try RecordingEngine(targets: engineTargets(), takeId: "take", journal: journal, writers: ["a": a, "b": b], options: MacRecordingOptions(ownInjectedTag: 42, isControl: { _ in false }))
        try e.start(); try e.sourceReady("a", generation: 1, nowUs: 1_000_000); try e.sourceReady("b", generation: 1, nowUs: 1_000_000); try e.tapReady(generation: 1, nowUs: 1_000_000)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, []); memset(CVPixelBufferGetBaseAddress(buffer), 0, CVPixelBufferGetBytesPerRow(buffer) * 64); CVPixelBufferUnlockBaseAddress(buffer, [])
        try e.frame("a", sourceUs: 1_000_000, status: .complete, pixel: buffer, generation: 1)
        try e.frame("b", sourceUs: 1_000_000, status: .complete, pixel: buffer, generation: 1)
        try e.drop("a", sourceUs: 1_100_000, generation: 1, reason: "admission")
        try e.frame("a", sourceUs: 5_000_000, status: .complete, pixel: buffer, generation: 1)
        do { try e.drop("a", sourceUs: 5_100_000, generation: 1, reason: "second admission") }
        catch { try e.fail(nowUs: 5_200_000, reason: String(describing: error)) }
        try e.stop(nowUs: 6_000_000)
        let aTerminal = await withCheckedContinuation { c in a.finish { c.resume(returning: $0) } }
        let bTerminal = await withCheckedContinuation { c in b.finish { c.resume(returning: $0) } }
        XCTAssertNil(aTerminal.error) // Healthy prefix closure cannot erase rejected continuation.
        try e.writerFinished("a", terminal: aTerminal); try e.writerFinished("b", terminal: bTerminal)
        XCTAssertEqual(e.droppedFrames, 2)
        XCTAssertTrue(journal.gaps.contains { $0.gap.startUs == 4_100_000 && $0.gap.endUs == 4_200_000 && $0.gap.reason == "second admission" })
        XCTAssertEqual(e.result?.state, .interrupted)
        XCTAssertEqual(e.result?.streams.first?.writerStatus, .failed)
        XCTAssertEqual(e.result?.streams.first?.encodedDurationUs, 100_000)
        XCTAssertTrue(e.result?.streams.first?.failureReason?.contains("videoDiscontinuityUnsupported") == true)
        XCTAssertEqual(journal.gaps.first?.gap, RecordingGap(startUs: 100_000, endUs: 4_000_000, kind: .backpressure, reason: "admission"))
        XCTAssertEqual(try e.project().take.media.map(\.monitorSlotId), ["b"])
    }
    func testSparseVideoFailsClosedBeforeItCanStretchEarlierPixels() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { if FileManager.default.fileExists(atPath: url.path) { try? FileManager.default.removeItem(at: url) } }
        let writer = try NativeVideoWriter(url: url, width: 64, height: 64)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, []); memset(CVPixelBufferGetBaseAddress(buffer), 0, CVPixelBufferGetBytesPerRow(buffer) * 64); CVPixelBufferUnlockBaseAddress(buffer, [])
        XCTAssertTrue(try writer.append(buffer, stamp: FrameStamp(sourceUs: 1_000_000, ptsUs: 0, durationUs: 100_000)))
        XCTAssertThrowsError(try writer.append(buffer, stamp: FrameStamp(sourceUs: 5_000_000, ptsUs: 4_000_000, durationUs: 1_000_000))) {
            XCTAssertEqual($0 as? MacRecordingFailure, .videoDiscontinuityUnsupported)
        }
        let terminal = await withCheckedContinuation { c in writer.finish { c.resume(returning: $0) } }
        XCTAssertNil(terminal.error); XCTAssertEqual(terminal.frameCount, 1); XCTAssertEqual(terminal.durationUs, 100_000)
        let asset = AVURLAsset(url: url)
        let tracks = try await asset.loadTracks(withMediaType: .video)
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: try XCTUnwrap(tracks.first), outputSettings: nil)
        reader.add(output); XCTAssertTrue(reader.startReading())
        var pts: [Int64] = [], durations: [Int64] = []
        while let sample = output.copyNextSampleBuffer() {
            if CMSampleBufferGetNumSamples(sample) > 0 {
                pts.append(CMTimeConvertScale(CMSampleBufferGetPresentationTimeStamp(sample), timescale: 1_000_000, method: .roundHalfAwayFromZero).value)
                durations.append(CMTimeConvertScale(CMSampleBufferGetDuration(sample), timescale: 1_000_000, method: .roundHalfAwayFromZero).value)
            }
        }
        XCTAssertEqual(reader.status, .completed)
        XCTAssertEqual(pts, [0]); XCTAssertEqual(durations, [100_000])
    }
    func testZeroFramesAndCancelledWriterCannotSucceed() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        let writer = try NativeVideoWriter(url: url, width: 64, height: 64)
        let terminal = await withCheckedContinuation { c in writer.finish { c.resume(returning: $0) } }
        XCTAssertNotNil(terminal.error)
        XCTAssertEqual(terminal.frameCount, 0)
    }
}
