import XCTest
@preconcurrency import AVFoundation
import CoreVideo
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
    func testZeroFramesAndCancelledWriterCannotSucceed() async throws {
        let url = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString + ".mp4")
        defer { try? FileManager.default.removeItem(at: url) }
        let writer = try NativeVideoWriter(url: url, width: 64, height: 64)
        let terminal = await withCheckedContinuation { c in writer.finish { c.resume(returning: $0) } }
        XCTAssertNotNil(terminal.error)
        XCTAssertEqual(terminal.frameCount, 0)
    }
}
