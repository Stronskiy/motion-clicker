@preconcurrency import AVFoundation
import MotionClickerRecording

struct VideoTerminal: Sendable { let frameCount: Int64; let durationUs: Int64; let error: String? }
protocol VideoWriting: AnyObject {
    func append(_ pixel: CVPixelBuffer, stamp: FrameStamp) throws -> Bool
    func finish(_ completion: @escaping @Sendable (VideoTerminal) -> Void)
    func cancel()
}
/// All methods run on the recording owner. The finish callback captures only
/// immutable counters and AVFoundation's writer, never mutable session state.
final class NativeVideoWriter: VideoWriting {
    private let writer: AVAssetWriter
    private let input: AVAssetWriterInput
    private let width: Int, height: Int
    private var count: Int64 = 0
    private var endUs: Int64 = 0
    private var finishing = false
    private var cancelRequested = false
    private let cancellationQueue = DispatchQueue(label: "MotionClicker.encoder.cancel", qos: .utility)

    init(url: URL, width: Int, height: Int) throws {
        try requireNative(width > 0 && height > 0 && width % 2 == 0 && height % 2 == 0, "H.264 requires positive even pixel dimensions")
        try requireNative(!FileManager.default.fileExists(atPath: url.path), "Refusing to overwrite existing media")
        self.width = width; self.height = height
        writer = try AVAssetWriter(outputURL: url, fileType: .mp4)
        writer.movieTimeScale = 1_000_000
        input = AVAssetWriterInput(mediaType: .video, outputSettings: [
            AVVideoCodecKey: AVVideoCodecType.h264,
            AVVideoWidthKey: width, AVVideoHeightKey: height,
            AVVideoColorPropertiesKey: [AVVideoColorPrimariesKey: AVVideoColorPrimaries_ITU_R_709_2,
                                       AVVideoTransferFunctionKey: AVVideoTransferFunction_ITU_R_709_2,
                                       AVVideoYCbCrMatrixKey: AVVideoYCbCrMatrix_ITU_R_709_2],
            AVVideoCompressionPropertiesKey: [AVVideoExpectedSourceFrameRateKey: 30,
                                              AVVideoAllowFrameReorderingKey: false,
                                              AVVideoMaxKeyFrameIntervalKey: 60]])
        input.expectsMediaDataInRealTime = true
        input.mediaTimeScale = 1_000_000
        try requireNative(writer.canAdd(input), "H.264 input unavailable")
        writer.add(input)
        try requireNative(writer.startWriting(), writer.error?.localizedDescription ?? "H.264 writer start failed")
        writer.startSession(atSourceTime: .zero)
    }
    func append(_ pixel: CVPixelBuffer, stamp: FrameStamp) throws -> Bool {
        try requireNative(!finishing && writer.status == .writing, writer.error?.localizedDescription ?? "Encoder is not writing")
        try requireNative(CVPixelBufferGetWidth(pixel) == width && CVPixelBufferGetHeight(pixel) == height && CVPixelBufferGetPixelFormatType(pixel) == kCVPixelFormatType_32BGRA, "Unexpected captured pixel format or size")
        try requireNative(stamp.ptsUs >= endUs && (count > 0 || stamp.ptsUs == 0), "Invalid encoded frame PTS")
        let end = try addNative(stamp.ptsUs, stamp.durationUs)
        if !input.isReadyForMoreMediaData { return false }
        var format: CMVideoFormatDescription?
        try requireNative(CMVideoFormatDescriptionCreateForImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixel, formatDescriptionOut: &format) == noErr, "Cannot describe frame pixels")
        var timing = CMSampleTimingInfo(duration: CMTime(value: stamp.durationUs, timescale: 1_000_000), presentationTimeStamp: CMTime(value: stamp.ptsUs, timescale: 1_000_000), decodeTimeStamp: .invalid)
        var sample: CMSampleBuffer?
        try requireNative(CMSampleBufferCreateReadyWithImageBuffer(allocator: kCFAllocatorDefault, imageBuffer: pixel, formatDescription: format!, sampleTiming: &timing, sampleBufferOut: &sample) == noErr, "Cannot build timed frame sample")
        try requireNative(input.append(sample!), writer.error?.localizedDescription ?? "Encoder append failed")
        count = try addNative(count, 1); endUs = end
        return true
    }
    func finish(_ completion: @escaping @Sendable (VideoTerminal) -> Void) {
        guard !finishing else { completion(VideoTerminal(frameCount: count, durationUs: endUs, error: "Writer already finishing")); return }
        finishing = true
        guard count > 0, writer.status == .writing else {
            let error = writer.error?.localizedDescription ?? "No accepted video frames"
            let terminal = VideoTerminal(frameCount: count, durationUs: endUs, error: error)
            cancelNative { completion(terminal) }; return
        }
        writer.endSession(atSourceTime: CMTime(value: endUs, timescale: 1_000_000))
        input.markAsFinished()
        let status = WriterCompletionStatus(writer), count = count, end = endUs
        writer.finishWriting {
            completion(VideoTerminal(frameCount: count, durationUs: end, error: status.failure))
        }
    }
    func cancel() { finishing = true; cancelNative {} }
    private func cancelNative(_ completion: @escaping @Sendable () -> Void) {
        guard !cancelRequested else { return }
        cancelRequested = true
        let status = WriterCompletionStatus(writer)
        // SDK cancelWriting is blocking. Never execute it on the recording owner.
        cancellationQueue.async { status.cancel(); completion() }
    }
}

/// AVAssetWriter.h documents status/error as thread safe. Only these reads and
/// post-admission cancelWriting cross queues; append/configuration stay on owner.
private final class WriterCompletionStatus: @unchecked Sendable {
    private let writer: AVAssetWriter
    init(_ writer: AVAssetWriter) { self.writer = writer }
    var failure: String? { writer.status == .completed ? nil : writer.error?.localizedDescription ?? "Encoder finish failed" }
    func cancel() { writer.cancelWriting() }
}
