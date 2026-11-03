import Foundation
import CoreVideo
import MotionClickerRecording

public enum MacRecordingFailure: Error, Sendable, Equatable {
    case screenRecordingPermissionMissing, inputMonitoringPermissionMissing, invalidState(String)
}
public struct MacRecordingDisplay: Sendable {
    public let monitorSlotId: String, displayId: UInt32
    public let pixelWidth: Int, pixelHeight: Int
}
public struct MacRecordingPreparation: Sendable { public let displays: [MacRecordingDisplay] }
public struct MacRecordingProgress: Sendable {
    public let state: RecordingState
    public let acceptedFrames: Int64, droppedFrames: Int64
    public let reason: String?
}
public struct MacRecordingCompletion: Sendable {
    public let manifest: RecordingManifest
    public let manifestURL: URL
    public let recovery: StoreRecovery
}
protocol CaptureSink: AnyObject, Sendable {
    func captureStopped(generation: UInt64, error: String?)
    func sourceReady(_ id: String, generation: UInt64)
    func tapReady(generation: UInt64)
    func failure(_ id: String?, generation: UInt64, reason: String)
    func frame(_ id: String, sourceUs: Int64, status: NativeFrameStatus, pixel: CVPixelBuffer?, generation: UInt64)
    func dropped(_ id: String, sourceUs: Int64, generation: UInt64)
    func pointer(_ sample: MacPointerSample, generation: UInt64)
}
/// Immutable Sendable closure uses Swift's atomic weak reference storage. Native
/// callbacks retain the controller only for the duration of a single delivery.
private final class CaptureRelay: CaptureSink, Sendable {
    private let target: @Sendable () -> (any CaptureSink)?
    init(_ target: any CaptureSink) { self.target = { [weak target] in target } }
    func captureStopped(generation: UInt64, error: String?) { target()?.captureStopped(generation: generation, error: error) }
    func sourceReady(_ id: String, generation: UInt64) { target()?.sourceReady(id, generation: generation) }
    func tapReady(generation: UInt64) { target()?.tapReady(generation: generation) }
    func failure(_ id: String?, generation: UInt64, reason: String) { target()?.failure(id, generation: generation, reason: reason) }
    func frame(_ id: String, sourceUs: Int64, status: NativeFrameStatus, pixel: CVPixelBuffer?, generation: UInt64) { target()?.frame(id, sourceUs: sourceUs, status: status, pixel: pixel, generation: generation) }
    func dropped(_ id: String, sourceUs: Int64, generation: UInt64) { target()?.dropped(id, sourceUs: sourceUs, generation: generation) }
    func pointer(_ sample: MacPointerSample, generation: UInt64) { target()?.pointer(sample, generation: generation) }
}
protocol CaptureDriving: AnyObject { func start(); func stop() }
struct RecordingDependencies {
    let inventory: () throws -> [NativeDisplaySnapshot]
    let preflight: () throws -> Void
    let now: () throws -> Int64
    let writer: (URL, Int, Int) throws -> any VideoWriting
    let driver: ([RecordingTarget], [NativeDisplaySnapshot], any CaptureSink, UInt64, Int64) -> any CaptureDriving
    var timeoutSeconds: Double = 10
}
/// Audited queue-confinement wrapper: mutable engine/store/native handles and all
/// continuations are accessed ONLY on owner. Callbacks transfer scalar values or
/// synchronously borrow a frame; no Sendable conformance is added to the core.
public final class MacRecordingController: @unchecked Sendable, CaptureSink {
    private let owner = DispatchQueue(label: "MotionClicker.recording.owner", qos: .userInitiated)
    private let ownerKey = DispatchSpecificKey<Bool>()
    private let options: MacRecordingOptions
    private let dependencies: RecordingDependencies
    private var engine: RecordingEngine?
    private var store: RecordingStore?
    private var driver: (any CaptureDriving)?
    private var prepared: MacRecordingPreparation?
    private var startWaiter: CheckedContinuation<Void, Error>?
    private var stopWaiters: [CheckedContinuation<MacRecordingCompletion?, Error>] = []
    private var completion: MacRecordingCompletion?
    private var finalError: Error?
    private var ending = false, ended = false, captureEnded = false
    private var publicationUnsafe = false
    private var finishedWriters = Set<String>()
    private var pendingTerminals: [String: VideoTerminal] = [:]
    private var deadline: DispatchWorkItem?

    public convenience init(options: MacRecordingOptions) { self.init(options: options, dependencies: .live) }
    init(options: MacRecordingOptions, dependencies: RecordingDependencies) { self.options = options; self.dependencies = dependencies; owner.setSpecific(key: ownerKey, value: true) }
    deinit {
        let cleanup = { [engine, store, driver] in
            driver?.stop()
            for writer in engine?.writers.values ?? Dictionary<String, any VideoWriting>().values { writer.cancel() }
            try? store?.close()
        }
        if DispatchQueue.getSpecific(key: ownerKey) == true { cleanup() } else { owner.sync(execute: cleanup) }
    }
    private func perform<T: Sendable>(_ body: @escaping @Sendable (MacRecordingController) throws -> T) async throws -> T {
        try await withCheckedThrowingContinuation { continuation in
            owner.async { do { continuation.resume(returning: try body(self)) } catch { continuation.resume(throwing: error) } }
        }
    }
    public func prepare(targets: [RecordingTarget], root: URL, takeId: String) async throws -> MacRecordingPreparation {
        try await perform { c in
            try requireNative(c.engine == nil && c.store == nil && !c.ended, "Recorder already prepared")
            try c.dependencies.preflight()
            let selected = try NativeDisplaySnapshot.select(targets: targets, inventory: c.dependencies.inventory())
            let store = try RecordingStore(root: root, takeId: takeId)
            c.store = store
            var writers: [String: any VideoWriting] = [:]
            do {
                for (target, display) in zip(targets, selected) {
                    writers[target.slot.id] = try c.dependencies.writer(store.mediaFile(monitorSlotId: target.slot.id), display.pixelWidth, display.pixelHeight)
                }
                c.engine = try RecordingEngine(targets: targets, takeId: takeId, journal: store, writers: writers, options: c.options)
                c.driver = c.dependencies.driver(targets, selected, CaptureRelay(c), 1, c.options.ownInjectedTag)
                let preparation = MacRecordingPreparation(displays: zip(targets, selected).map { MacRecordingDisplay(monitorSlotId: $0.slot.id, displayId: $1.id, pixelWidth: $1.pixelWidth, pixelHeight: $1.pixelHeight) })
                c.prepared = preparation; return preparation
            } catch {
                for writer in writers.values { writer.cancel() }; try? store.close(); c.ended = true; c.finalError = error; throw error
            }
        }
    }
    public func start() async throws {
        try Task.checkCancellation()
        try await withTaskCancellationHandler {
            try await withCheckedThrowingContinuation { continuation in
                owner.async {
                    do {
                        guard let engine = self.engine, let driver = self.driver else { throw MacRecordingFailure.invalidState("Prepare before start") }
                        try self.dependencies.preflight()
                        try engine.start()
                        self.startWaiter = continuation
                        self.scheduleDeadline()
                        driver.start()
                    } catch { continuation.resume(throwing: error) }
                }
            }
        } onCancel: { self.owner.async { self.requestStop(reason: "Start cancelled") } }
    }
    public func stop(reason: String? = nil) async throws -> MacRecordingCompletion? {
        try await withCheckedThrowingContinuation { continuation in
            owner.async {
                self.stopWaiters.append(continuation)
                self.requestStop(reason: reason)
            }
        }
    }
    public func snapshot() async -> MacRecordingProgress {
        // This closure cannot throw; only the generic transport uses throws.
        try! await perform { c in MacRecordingProgress(state: c.engine?.state ?? (c.ended ? .failed : .preparing), acceptedFrames: c.engine?.acceptedFrames ?? 0, droppedFrames: c.engine?.droppedFrames ?? 0, reason: c.finalError.map(String.init(describing:)) ?? c.engine?.reason) }
    }
    public func recovery() async -> StoreRecovery? { try! await perform { $0.store?.recovery } }
    public func project() async throws -> RecordingResult {
        try await perform { c in
            guard c.ended, c.completion != nil, let engine = c.engine else { throw MacRecordingFailure.invalidState("No published recording") }
            return try engine.project()
        }
    }
    private func requestStop(reason: String?) {
        if ended { resolveStops(); return }
        guard let engine else { ended = true; resolveStops(); return }
        do { try engine.stop(nowUs: dependencies.now(), reason: reason) }
        catch { finalError = error }
        advance()
    }
    private func scheduleDeadline() {
        deadline?.cancel()
        let item = DispatchWorkItem { [weak self] in self?.timedOut() }
        deadline = item; owner.asyncAfter(deadline: .now() + dependencies.timeoutSeconds, execute: item)
    }
    private func timedOut() {
        guard !ended, let engine else { return }
        if !ending {
            do { try engine.fail(nowUs: dependencies.now(), reason: "Capture readiness timed out") } catch { finalError = error }
            advance(); return
        }
        if !captureEnded {
            captureEnded = true
            try? engine.shutdownFailed(reason: "Native capture shutdown timed out", nowUs: dependencies.now())
        }
        for (id, writer) in engine.writers where !finishedWriters.contains(id) {
            publicationUnsafe = true
            finalError = RecordingError(message: "Encoder finalization timed out; staging retained because native file closure is unknown")
            writer.cancel()
            receiveTerminal(id, terminal: VideoTerminal(frameCount: 0, durationUs: 0, error: "Encoder finalization timed out"))
        }
        finishIfReady()
    }
    private func advance() {
        guard let engine, !ended else { return }
        if engine.state == .recording {
            deadline?.cancel(); startWaiter?.resume(); startWaiter = nil; return
        }
        guard engine.state != .preparing else { return }
        if !ending {
            ending = true
            startWaiter?.resume(throwing: MacRecordingFailure.invalidState(engine.reason ?? "Capture stopped before epoch")); startWaiter = nil
            scheduleDeadline()
            driver?.stop()
            if engine.state == .finalizing {
                for (id, writer) in engine.writers {
                    writer.finish { terminal in self.owner.async { self.receiveTerminal(id, terminal: terminal) } }
                }
            } else {
                for writer in engine.writers.values { writer.cancel() }
            }
        }
        finishIfReady()
    }
    private func receiveTerminal(_ id: String, terminal: VideoTerminal) {
        guard !ended, engine != nil, !finishedWriters.contains(id) else { return }
        finishedWriters.insert(id)
        pendingTerminals[id] = terminal
        finishIfReady()
    }
    private func finishIfReady() {
        guard ending && captureEnded && !ended, let engine else { return }
        if !pendingTerminals.isEmpty {
            let pending = pendingTerminals; pendingTerminals.removeAll()
            for (id, terminal) in pending {
                do { try engine.writerFinished(id, terminal: terminal) } catch { finalError = error }
            }
        }
        if engine.state == .finalizing { return }
        if engine.result != nil && finishedWriters.count != engine.writers.count { return }
        ended = true; deadline?.cancel(); driver = nil
        if !publicationUnsafe, let manifest = engine.result, let store {
            do { completion = MacRecordingCompletion(manifest: manifest, manifestURL: try store.publish(manifest: manifest), recovery: store.recovery) }
            catch { finalError = error }
        }
        // The store closes append handles, while read/project uses the published paths.
        do { try store?.close() } catch { finalError = error }
        resolveStops()
    }
    private func resolveStops() {
        let waiters = stopWaiters; stopWaiters.removeAll()
        for waiter in waiters {
            if let finalError { waiter.resume(throwing: finalError) } else { waiter.resume(returning: completion) }
        }
    }
    func captureStopped(generation: UInt64, error: String?) {
        owner.async {
            guard generation == 1 && self.ending && !self.captureEnded else { return }
            self.captureEnded = true
            if let error { try? self.engine?.shutdownFailed(reason: error, nowUs: self.dependencies.now()) }
            self.finishIfReady()
        }
    }
    private func callback(_ body: (RecordingEngine) throws -> Void) {
        guard let engine, !ended else { return }
        do { try body(engine) }
        catch {
            finalError = error
            try? engine.fail(nowUs: dependencies.now(), reason: String(describing: error))
        }
        advance()
    }
    func sourceReady(_ id: String, generation: UInt64) {
        owner.async { self.callback { try $0.sourceReady(id, generation: generation, nowUs: self.dependencies.now()) } }
    }
    func tapReady(generation: UInt64) {
        owner.async { self.callback { try $0.tapReady(generation: generation, nowUs: self.dependencies.now()) } }
    }
    func failure(_ id: String?, generation: UInt64, reason: String) {
        owner.sync {
            guard generation == self.engine?.generation else { return }
            self.callback { try $0.fail(id, nowUs: self.dependencies.now(), reason: reason) }
        }
    }
    // Native output/tap callbacks run on dedicated queues, never owner or main.
    // sync borrows the payload for exactly this callback; no per-frame Task/backlog.
    func frame(_ id: String, sourceUs: Int64, status: NativeFrameStatus, pixel: CVPixelBuffer?, generation: UInt64) {
        owner.sync { callback { try $0.frame(id, sourceUs: sourceUs, status: status, pixel: pixel, generation: generation) } }
    }
    func dropped(_ id: String, sourceUs: Int64, generation: UInt64) {
        owner.sync { callback { try $0.drop(id, sourceUs: sourceUs, generation: generation, reason: "Frame admission capacity exceeded") } }
    }
    func pointer(_ sample: MacPointerSample, generation: UInt64) {
        owner.sync { callback { try $0.pointer(sample, nowUs: dependencies.now(), generation: generation) } }
    }
}
