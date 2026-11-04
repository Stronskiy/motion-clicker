import Foundation
@preconcurrency import AVFoundation
import MotionClickerRecording

struct NativeMediaEvidence: Sendable, Equatable {
    let slot: String
    let frameCount: Int64
    let durationUs: Int64
    let firstPTSUs: Int64
}
struct InspectedBundle: Sendable {
    let snapshot: RecordedBundleSnapshot
    let evidence: [NativeMediaEvidence]
    let mediaError: String?
    var executable: Bool { snapshot.availability == .requiresMonitorMapping && mediaError == nil && evidence.count == snapshot.media.count }
}

enum BundleIO {
    static func create(_ root: URL) async throws { try await Task.detached { try RecordedBundleStore.create(root: root) }.value }
    private static func scratch() throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("motionclicker-validation-" + UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false)
        return dir
    }
    static func save(_ root: URL, _ result: RecordingResult) async throws -> InspectedBundle {
        try await Task.detached {
            let scratch = try scratch(); defer { try? FileManager.default.removeItem(at: scratch) }
            let snapshot = try RecordedBundleStore.saveInitial(root: root, result: result, validationScratch: scratch)
            return await inspect(snapshot)
        }.value
    }
    static func open(_ root: URL) async throws -> InspectedBundle {
        try await Task.detached {
            let scratch = try scratch(); defer { try? FileManager.default.removeItem(at: scratch) }
            return await inspect(try RecordedBundleStore.coldOpen(root: root, validationScratch: scratch))
        }.value
    }
    static func recovery(_ root: URL) async -> [URL] { await Task.detached { (try? RecordedBundleStore.recoveryEntries(root: root)) ?? [] }.value }
    private static func inspect(_ snapshot: RecordedBundleSnapshot) async -> InspectedBundle {
        do {
            var evidence: [NativeMediaEvidence] = []
            for media in snapshot.media {
                guard let stream = snapshot.manifest.streams.first(where: { $0.monitorSlotId == media.monitorSlotId }) else { throw RecordingError(message: "Missing stream metadata") }
                evidence.append(try await NativeMediaInspector.inspect(media.file, stream: stream))
            }
            return .init(snapshot: snapshot, evidence: evidence, mediaError: nil)
        } catch { return .init(snapshot: snapshot, evidence: [], mediaError: "Native video validation failed: \(error). Inspection only; Run is unavailable.") }
    }
}

/// Full readback on a utility task: coded counts/durations and decoded image count.
/// One microsecond tolerance permits integer timebase rounding only.
enum NativeMediaInspector {
    static func inspect(_ file: URL, stream: StreamManifest) async throws -> NativeMediaEvidence {
        let asset = AVURLAsset(url: file)
        guard try await asset.load(.isPlayable) else { throw RecordingError(message: "Video is not playable") }
        let tracks = try await asset.load(.tracks)
        guard tracks.count == 1, let track = tracks.first, track.mediaType == .video else { throw RecordingError(message: "Expected exactly one video track and no audio") }
        let reader = try AVAssetReader(asset: asset)
        let output = AVAssetReaderTrackOutput(track: track, outputSettings: nil)
        guard reader.canAdd(output) else { throw RecordingError(message: "Cannot read coded video") }
        reader.add(output)
        guard reader.startReading() else { throw reader.error ?? RecordingError(message: "Cannot start coded reader") }
        var count: Int64 = 0, end: Int64 = 0, first: Int64?, previous: Int64 = -1
        while let sample = output.copyNextSampleBuffer() {
            try Task.checkCancellation()
            let n = CMSampleBufferGetNumSamples(sample)
            if n == 0 { continue }
            let pts = try us(CMSampleBufferGetPresentationTimeStamp(sample)), duration = try us(CMSampleBufferGetDuration(sample))
            guard n == 1, duration > 0, pts > previous, pts >= 0 else { throw RecordingError(message: "Invalid coded sample timing") }
            if first == nil { first = pts }
            if count > 0 && abs(pts - end) > 1 { throw RecordingError(message: "Unrepresented native video discontinuity") }
            guard pts <= Int64.max - duration else { throw RecordingError(message: "Native video time overflow") }
            end = pts + duration; previous = pts; count += 1
        }
        guard reader.status == .completed, first == 0, count == stream.frameCount, abs(end - stream.encodedDurationUs) <= 1 else {
            throw RecordingError(message: "Coded origin/count/duration differs from saved manifest")
        }
        let decoder = try AVAssetReader(asset: asset)
        let decoded = AVAssetReaderTrackOutput(track: track, outputSettings: [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA])
        guard decoder.canAdd(decoded) else { throw RecordingError(message: "Cannot decode source video") }
        decoder.add(decoded)
        guard decoder.startReading() else { throw decoder.error ?? RecordingError(message: "Cannot start decoder") }
        var images: Int64 = 0
        while let sample = decoded.copyNextSampleBuffer() {
            try Task.checkCancellation()
            guard CMSampleBufferGetImageBuffer(sample) != nil else { throw RecordingError(message: "Decoded sample has no image") }
            images += 1
        }
        guard decoder.status == .completed, images == count else { throw decoder.error ?? RecordingError(message: "Decoded image count differs from coded video") }
        return .init(slot: stream.monitorSlotId, frameCount: count, durationUs: end, firstPTSUs: first ?? -1)
    }
    private static func us(_ time: CMTime) throws -> Int64 {
        guard time.isNumeric, time.timescale > 0 else { throw RecordingError(message: "Invalid native media time") }
        return CMTimeConvertScale(time, timescale: 1_000_000, method: .roundHalfAwayFromZero).value
    }
}
