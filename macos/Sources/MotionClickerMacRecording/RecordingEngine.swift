import Foundation
import CoreVideo
import MotionClickerRecording

enum NativeFrameStatus { case complete, idle, blank, suspended, started, stopped }
/// The only shared mutable admission state is protected by this lock.
final class FrameAdmission: @unchecked Sendable {
    private let lock = NSLock()
    private let capacity: Int
    private var active = 0
    init(capacity: Int) { precondition(capacity > 0); self.capacity = capacity }
    func enter() -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard active < capacity else { return false }; active += 1; return true
    }
    func leave() { lock.lock(); defer { lock.unlock() }; precondition(active > 0); active -= 1 }
}
/// NOT Sendable. Every method, including journal/writer calls, belongs to one
/// serial owner. No retained frame payload or whole event/gap array lives here.
final class RecordingEngine {
    private let session: RecordingSession
    let writers: [String: any VideoWriting]
    private let ids: [String]
    private var ready = Set<String>()
    private var tapIsReady = false, requested = false, stopped = false
    private(set) var generation: UInt64 = 1
    private var epochHostUs: Int64?
    private var timelines: [String: FrameTimeline] = [:]
    // Exactly one owned CVPixelBuffer per selected display, until the next source
    // timestamp or stop establishes its real VFR duration. No frame history.
    private struct PendingFrame { let sourceUs: Int64; let pixel: CVPixelBuffer }
    private var pending: [String: PendingFrame] = [:]
    private var frameCounts: [String: Int64] = [:]
    private var encoderFailures: [String: String] = [:]
    private var terminals = Set<String>()
    private var filter: PointerFilter
    private(set) var droppedFrames: Int64 = 0, acceptedFrames: Int64 = 0
    private(set) var reason: String?
    var state: RecordingState { session.state }
    var result: RecordingManifest? { session.result }

    init(targets: [RecordingTarget], takeId: String, journal: EventJournal, writers: [String: any VideoWriting], options: MacRecordingOptions) throws {
        session = try RecordingSession(takeId: takeId, targets: targets, journal: journal)
        ids = targets.map(\.slot.id)
        try requireNative(Set(writers.keys) == Set(ids), "Missing selected encoder")
        self.writers = writers; filter = PointerFilter(options: options)
    }
    func start() throws {
        try requireNative(!requested && !stopped && state == .preparing, "Recorder is single-use")
        requested = true
    }
    func sourceReady(_ id: String, generation: UInt64, nowUs: Int64) throws {
        guard generation == self.generation && requested && !stopped && state == .preparing else { return }
        if ready.contains(id) { return }
        // Integer source microseconds and host microseconds share the mach absolute epoch.
        try session.sourceReady(monitorSlotId: id, clock: ClockAnchor(sourceAnchorTicks: 0, hostAnchorUs: 0, ticksPerSecond: 1_000_000))
        ready.insert(id); try beginIfReady(nowUs: nowUs)
    }
    func tapReady(generation: UInt64, nowUs: Int64) throws {
        guard generation == self.generation && requested && !stopped && state == .preparing else { return }
        try session.eventTapReady(); tapIsReady = true; try beginIfReady(nowUs: nowUs)
    }
    private func beginIfReady(nowUs: Int64) throws {
        if tapIsReady && ready.count == ids.count {
            try session.begin(epochHostUs: nowUs)
            epochHostUs = nowUs
            for id in ids { timelines[id] = FrameTimeline(epochUs: nowUs) }
        }
    }
    func frame(_ id: String, sourceUs: Int64, status: NativeFrameStatus, pixel: CVPixelBuffer?, generation: UInt64) throws {
        guard generation == self.generation && state == .recording && !stopped else { return }
        switch status {
        case .idle: try session.idle(monitorSlotId: id, sourceTicks: sourceUs); return
        case .blank, .suspended, .stopped:
            try fail(id, nowUs: sourceUs, reason: "Screen source became \(status)"); return
        case .started where pixel == nil: return
        case .complete, .started: break
        }
        guard let pixel, var timeline = timelines[id] else {
            try fail(id, nowUs: sourceUs, reason: "Missing complete frame pixels"); return
        }
        do {
            guard try timeline.propose(sourceUs: sourceUs) != nil else { return }
            if let previous = pending[id] {
                try requireNative(sourceUs > previous.sourceUs, "Reversed or duplicate source frame")
                try encodePending(id, endingUs: sourceUs)
            }
            pending[id] = PendingFrame(sourceUs: sourceUs, pixel: pixel)
        } catch {
            encoderFailures[id] = String(describing: error)
            try fail(id, nowUs: sourceUs, reason: String(describing: error))
        }
    }
    private func encodePending(_ id: String, endingUs: Int64) throws {
        guard let frame = pending.removeValue(forKey: id), var timeline = timelines[id], let writer = writers[id] else { return }
        let duration = max(1, try subtractNative(endingUs, frame.sourceUs))
        guard let stamp = try timeline.propose(sourceUs: frame.sourceUs, durationUs: duration) else { return }
        if try writer.append(frame.pixel, stamp: stamp) {
            try session.acceptedFrame(monitorSlotId: id, sourceTicks: frame.sourceUs, encodedTimeUs: stamp.ptsUs, durationUs: stamp.durationUs)
            try timeline.accept(stamp); timelines[id] = timeline
            frameCounts[id] = try addNative(frameCounts[id] ?? 0, 1)
            acceptedFrames = try addNative(acceptedFrames, 1)
        } else {
            try session.reportGap(monitorSlotId: id, startTicks: frame.sourceUs, endTicks: addNative(frame.sourceUs, duration), kind: .backpressure, reason: "Encoder backpressure")
            droppedFrames = try addNative(droppedFrames, 1)
        }
    }
    private func drain(nowUs: Int64) {
        for id in ids {
            do { try encodePending(id, endingUs: nowUs) }
            catch { encoderFailures[id] = String(describing: error) }
        }
    }
    func drop(_ id: String, sourceUs: Int64, generation: UInt64, reason: String) throws {
        guard generation == self.generation && state == .recording && !stopped else { return }
        try session.reportGap(monitorSlotId: id, startTicks: sourceUs, endTicks: addNative(sourceUs, 33_333), kind: .backpressure, reason: reason)
        droppedFrames = try addNative(droppedFrames, 1)
    }
    func pointer(_ sample: MacPointerSample, nowUs: Int64, generation: UInt64) throws {
        guard generation == self.generation && state == .recording && !stopped else { return }
        if let epochHostUs, sample.timestampNs > 0, sample.timestampNs / 1_000 < UInt64(epochHostUs) { return }
        do {
            if let event = try filter.receive(sample, nowUs: nowUs) { try session.receive(event: event) }
        } catch {
            try fail(ids[0], nowUs: nowUs, reason: String(describing: error))
        }
        if state != .recording { drain(nowUs: nowUs); invalidate() }
    }
    func fail(_ id: String? = nil, nowUs: Int64, reason: String) throws {
        guard !stopped else { return }
        self.reason = reason
        defer { invalidate() }
        if state == .recording || state == .finalizing { drain(nowUs: nowUs) }
        try session.sourceFailed(monitorSlotId: id ?? ids[0], hostTimeUs: nowUs, reason: reason)
    }
    func stop(nowUs: Int64, reason: String? = nil) throws {
        guard !stopped else { return }
        self.reason = reason
        defer { invalidate() }
        if state == .recording || state == .finalizing { drain(nowUs: nowUs) }
        try session.stop(hostTimeUs: nowUs, reason: reason)
    }
    private func invalidate() { if !stopped { stopped = true; generation += 1 } }
    func writerFinished(_ id: String, terminal: VideoTerminal) throws {
        guard state == .finalizing && !terminals.contains(id) else { return }
        let mismatch = terminal.frameCount != frameCounts[id, default: 0]
        let error = encoderFailures[id] ?? terminal.error ?? (mismatch ? "Encoder frame count mismatch" : nil)
        try session.writerFinished(monitorSlotId: id, status: error == nil ? .succeeded : .failed, encodedDurationUs: terminal.durationUs, reason: error)
        terminals.insert(id)
    }
    func shutdownFailed(reason: String, nowUs: Int64) throws {
        self.reason = reason
        if state == .finalizing { try session.sourceFailed(monitorSlotId: ids[0], hostTimeUs: nowUs, reason: reason) }
    }
    func project() throws -> RecordingResult { try session.project() }
}
