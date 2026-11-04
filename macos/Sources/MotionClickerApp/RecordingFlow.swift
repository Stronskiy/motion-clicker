import Foundation
import Observation
import MotionClickerRecording
import MotionClickerMacRecording

/// One owner for Run (including paused) and the entire capture/save lifetime.
@MainActor @Observable final class AppOperationGate {
    enum Operation: Equatable { case idle, run, runPaused, recording, opening }
    private(set) var operation: Operation = .idle
    var busy: Bool { operation != .idle }
    func acquireRun() -> Bool {
        guard operation == .idle || operation == .runPaused else { return false }
        operation = .run; return true
    }
    func pauseRun() { if operation == .run { operation = .runPaused } }
    func acquireRecording() -> Bool { guard !busy else { return false }; operation = .recording; return true }
    func acquireOpen() -> Bool { guard !busy else { return false }; operation = .opening; return true }
    func release() { operation = .idle }
}
struct CaptureProgress: Sendable {
    let state: RecordingState
    let accepted: Int64, dropped: Int64
    let reason: String?
}
@MainActor protocol RecordingCapture: AnyObject {
    func prepare(targets: [RecordingTarget], root: URL, takeId: String) async throws
    func start() async throws
    func stop(reason: String?) async throws -> Bool
    func project() async throws -> RecordingResult
    func progress() async -> CaptureProgress
    func recovery() async -> StoreRecovery?
}
@MainActor final class NativeRecordingCapture: RecordingCapture {
    private let recorder: MacRecordingController
    init(regions: ControlRegions) {
        recorder = MacRecordingController(options: MacRecordingOptions(ownInjectedTag: MacInput.tag, isControl: { sample in
            regions.contains(x: sample.position.x, y: sample.position.y)
        }))
    }
    func prepare(targets: [RecordingTarget], root: URL, takeId: String) async throws { _ = try await recorder.prepare(targets: targets, root: root, takeId: takeId) }
    func start() async throws { try await recorder.start() }
    func stop(reason: String?) async throws -> Bool { try await recorder.stop(reason: reason) != nil }
    func project() async throws -> RecordingResult { try await recorder.project() }
    func progress() async -> CaptureProgress {
        let p = await recorder.snapshot()
        return .init(state: p.state, accepted: p.acceptedFrames, dropped: p.droppedFrames, reason: p.reason)
    }
    func recovery() async -> StoreRecovery? { await recorder.recovery() }
}

@MainActor @Observable final class RecordingFlow {
    enum Phase: String { case idle, preparing, ready, countdown, starting, recording, finalizing, saving, saved, cancelled, failed }
    private(set) var phase: Phase = .idle
    private(set) var message = "Choose displays and a new local recording destination."
    private(set) var destination: URL?
    private(set) var recovery: StoreRecovery?
    private(set) var saved: InspectedBundle?
    private(set) var accepted: Int64 = 0
    private(set) var dropped: Int64 = 0
    private(set) var remaining = 0
    private(set) var availableBytes: Int64?
    private let gate: AppOperationGate
    private let makeCapture: () -> any RecordingCapture
    private let create: (URL) async throws -> Void
    private let save: (URL, RecordingResult) async throws -> InspectedBundle
    private let sleep: (Duration) async throws -> Void
    private var capture: (any RecordingCapture)?
    private var countdownTask: Task<Void, Never>?
    private var prepareTask: Task<Void, Never>?
    private var startTask: Task<Void, Error>?
    private var stopTask: Task<Void, Never>?
    private var pendingResult: RecordingResult?
    var canRetrySave: Bool { phase == .failed && pendingResult != nil && !gate.busy }
    private var polling = false
    private var stopRequested = false
    var ownsOperation: Bool { gate.operation == .recording }
    var canStop: Bool { ownsOperation && phase != .saving && phase != .finalizing }
    init(gate: AppOperationGate, makeCapture: @escaping () -> any RecordingCapture,
         create: @escaping (URL) async throws -> Void = BundleIO.create,
         save: @escaping (URL, RecordingResult) async throws -> InspectedBundle = BundleIO.save,
         sleep: @escaping (Duration) async throws -> Void = { try await Task.sleep(for: $0) }) {
        self.gate = gate; self.makeCapture = makeCapture; self.create = create; self.save = save; self.sleep = sleep
    }
    func prepare(targets: [RecordingTarget], destination: URL) async {
        guard gate.acquireRecording() else { return }
        phase = .preparing; message = "Preparing selected displays; capture has not started."
        self.destination = destination; availableBytes = nil; saved = nil; recovery = nil; stopRequested = false; stopTask = nil
        accepted = 0; dropped = 0; pendingResult = nil
        let c = makeCapture(); capture = c
        // Retain the capture until preparation and asynchronous cleanup acknowledge closure.
        let task = Task { @MainActor in
            do {
                availableBytes = await Task.detached {
                    try? destination.deletingLastPathComponent().resourceValues(forKeys:[.volumeAvailableCapacityForImportantUsageKey]).volumeAvailableCapacityForImportantUsage
                }.value
                if let availableBytes, availableBytes <= 0 { self.availableBytes = nil } // Capacity can be unavailable; actual writes remain authoritative.
                try await create(destination)
                if !stopRequested { try await c.prepare(targets: targets, root: destination, takeId: UUID().uuidString.lowercased()) }
                if !stopRequested { phase = .ready; message = "Ready. Start countdown deliberately to begin recording." }
            } catch {
                if !stopRequested { phase = .failed; message = Self.help(error) }
            }
        }
        prepareTask = task
        await task.value
        prepareTask = nil
        if phase == .failed { await stop(reason: "Preparation failed", preserveFailure: true) }
    }
    func countdown(seconds: Int = 3) {
        guard phase == .ready, !stopRequested else { return }
        phase = .countdown; remaining = max(1, seconds)
        countdownTask = Task { @MainActor in
            do {
                while remaining > 0 {
                    message = "Recording starts in \(remaining)… Cancel to keep capture off."
                    try await sleep(.seconds(1)); try Task.checkCancellation(); remaining -= 1
                }
                guard !stopRequested, let capture else { return }
                phase = .starting; message = "Waiting for native video and pointer readiness…"
                let task = Task { @MainActor in try await capture.start() }
                startTask = task
                try await task.value; startTask = nil
                guard !stopRequested else { return }
                phase = .recording; message = "Recording selected displays and pointer actions."
            } catch {
                startTask = nil
                guard !stopRequested else { return }
                message = Self.help(error); phase = .failed
                await stop(reason: "Native start failed", preserveFailure: true)
            }
        }
    }
    func poll() async {
        guard !polling, ownsOperation, phase == .recording || phase == .starting, let capture else { return }
        polling = true; defer { polling = false }
        let p = await capture.progress()
        accepted = p.accepted; dropped = p.dropped
        if p.state == .failed || p.state == .interrupted || p.state == .finalizing || p.state == .completed {
            if let reason = p.reason { message = reason }
            await stop(reason: p.reason)
        }
    }
    func stop(reason: String? = nil, preserveFailure: Bool = false) async {
        if let stopTask { await stopTask.value; return }
        guard ownsOperation, let c = capture else { return }
        stopRequested = true; countdownTask?.cancel(); startTask?.cancel()
        let previousMessage = message
        phase = .finalizing; message = "Stopping native capture and closing video files…"
        let task = Task { @MainActor in
            await prepareTask?.value
            do {
                let published = try await c.stop(reason: reason)
                recovery = await c.recovery()
                if published, let destination {
                    let result = try await c.project()
                    pendingResult = result
                    phase = .saving; message = "Saving initial project; source video is already closed."
                    saved = try await save(destination, result)
                    pendingResult = nil
                    phase = .saved
                    message = saved?.snapshot.availability == .partial ? "Saved partial recording — inspection only." : "Recording saved. Open Project to inspect and map monitors."
                } else {
                    phase = preserveFailure ? .failed : .cancelled
                    message = preserveFailure ? previousMessage : "Cancelled before a take was published. Any staging remains recoverable."
                }
            } catch {
                recovery = await c.recovery(); phase = .failed; message = Self.help(error) + " Source/staging retained; the project is not saved."
            }
            capture = nil; gate.release()
        }
        stopTask = task
        await task.value
    }
    func retrySave() async {
        guard canRetrySave, let destination, let result=pendingResult, gate.acquireRecording() else { return }
        phase = .saving; message = "Retrying initial project save…"
        defer { gate.release() }
        do {
            saved = try await save(destination,result)
            pendingResult = nil; phase = .saved
            message = saved?.snapshot.availability == .partial ? "Saved partial recording — inspection only." : "Recording saved. Map monitors explicitly before Run."
        } catch { phase = .failed; message = Self.help(error) + " Source/staging retained; the project is not saved." }
    }
    private static func help(_ error: Error) -> String {
        switch error as? MacRecordingFailure {
        case .screenRecordingPermissionMissing: "Screen Recording permission is missing. Enable Motion Clicker in System Settings → Privacy & Security → Screen & System Audio Recording, then restart if requested."
        case .inputMonitoringPermissionMissing: "Input Monitoring permission is missing. Enable Motion Clicker in System Settings → Privacy & Security → Input Monitoring, then restart if requested."
        default: String(describing: error)
        }
    }
}
