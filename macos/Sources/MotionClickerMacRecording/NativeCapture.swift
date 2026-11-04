@preconcurrency import ScreenCaptureKit
@preconcurrency import AppKit
import CoreMedia
import MotionClickerRecording

extension RecordingDependencies {
    static var live: RecordingDependencies {
        RecordingDependencies(inventory: NativeDisplaySnapshot.current, preflight: {
            guard CGPreflightScreenCaptureAccess() else { throw MacRecordingFailure.screenRecordingPermissionMissing }
            guard CGPreflightListenEventAccess() else { throw MacRecordingFailure.inputMonitoringPermissionMissing }
        }, now: { try NativeTime.hostUs() }, writer: { try NativeVideoWriter(url: $0, width: $1, height: $2) },
        driver: { NativeCaptureDriver(targets: $0, displays: $1, sink: $2, generation: $3, ownTag: $4) })
    }
}

/// Immutable delegate identity. SCStream and lifecycle mutations are exclusively
/// on NativeCaptureDriver.queue; callbacks only read identity/sink/admission.
private final class DisplayStream: NSObject, SCStreamOutput, SCStreamDelegate {
    let id: String, generation: UInt64
    let sink: any CaptureSink
    let admission: FrameAdmission
    private(set) var stream: SCStream!
    private let outputQueue: DispatchQueue
    init(id: String, display: SCDisplay, snapshot: NativeDisplaySnapshot, excluded: [SCRunningApplication], sink: any CaptureSink, generation: UInt64, admission: FrameAdmission) throws {
        self.id = id; self.generation = generation; self.sink = sink; self.admission = admission
        outputQueue = DispatchQueue(label: "MotionClicker.screen.\(id)", qos: .userInitiated)
        super.init()
        let config = SCStreamConfiguration()
        config.width = snapshot.pixelWidth; config.height = snapshot.pixelHeight
        config.minimumFrameInterval = CMTime(value: 1, timescale: 30)
        config.queueDepth = 3
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.colorSpaceName = CGColorSpace.itur_709
        config.captureDynamicRange = .SDR
        config.showsCursor = true
        config.capturesAudio = false
        config.captureMicrophone = false
        let filter = SCContentFilter(display: display, excludingApplications: excluded, exceptingWindows: [])
        stream = SCStream(filter: filter, configuration: config, delegate: self)
        try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: outputQueue)
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) {
        sink.failure(id, generation: generation, reason: "SCStream: \(error.localizedDescription)")
    }
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen else { return }
        autoreleasepool {
            do {
                guard let attachment = (CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]])?.first,
                      let statusNumber = attachment[.status] as? NSNumber,
                      let status = SCFrameStatus(rawValue: statusNumber.intValue) else {
                    throw RecordingError(message: "Missing native frame status")
                }
                // Non-image terminal states need no invented display timestamp.
                if status == .blank || status == .suspended || status == .stopped {
                    sink.failure(id, generation: generation, reason: "Screen source became \(status)"); return
                }
                guard let ticks = attachment[.displayTime] as? NSNumber else {
                    if status == .started { return }
                    throw RecordingError(message: "Missing native frame displayTime")
                }
                let time = try NativeTime.hostUs(ticks.uint64Value)
                let mapped: NativeFrameStatus
                switch status {
                case .complete: mapped = .complete
                case .idle: mapped = .idle
                case .started: mapped = .started
                default: throw RecordingError(message: "Unsupported native frame status")
                }
                if mapped == .idle { sink.frame(id, sourceUs: time, status: mapped, pixel: nil, generation: generation); return }
                guard admission.enter() else { sink.dropped(id, sourceUs: time, generation: generation); return }
                defer { admission.leave() }
                // Borrowed sample is alive for the entire synchronous owner handoff.
                sink.frame(id, sourceUs: time, status: mapped, pixel: CMSampleBufferGetImageBuffer(sampleBuffer), generation: generation)
            } catch { sink.failure(id, generation: generation, reason: String(describing: error)) }
        }
    }
}

/// Dedicated capture lifecycle queue owns streams/timer/observer state. The
/// SCShareableContent/start/stop callbacks re-enter that queue before mutation.
/// PointerCapture has a separate lock and its own runloop; core is never exposed.
final class NativeCaptureDriver: CaptureDriving, @unchecked Sendable {
    private let queue = DispatchQueue(label: "MotionClicker.capture.lifecycle", qos: .userInitiated)
    private let targets: [RecordingTarget], displays: [NativeDisplaySnapshot]
    private let sink: any CaptureSink
    private let generation: UInt64, ownTag: Int64
    private let admission = FrameAdmission(capacity: 2)
    private var streams: [String: DisplayStream] = [:]
    private var starting = Set<String>(), stopping = Set<String>()
    private var stopped = false, announcedStop = false
    private var tap: PointerCapture?
    private var tapStopped = true
    private var topology: [NativeDisplaySnapshot] = []
    private var suspendClock: SuspendClockGuard?
    private var timer: DispatchSourceTimer?
    private var observations: [(NotificationCenter, NSObjectProtocol)] = []
    private var stopError: String?
    init(targets: [RecordingTarget], displays: [NativeDisplaySnapshot], sink: any CaptureSink, generation: UInt64, ownTag: Int64) {
        self.targets = targets; self.displays = displays; self.sink = sink; self.generation = generation; self.ownTag = ownTag
    }
    func start() {
        queue.async {
            guard !self.stopped else { return }
            do {
                self.topology = try NativeDisplaySnapshot.current()
                try requireNative(try NativeDisplaySnapshot.select(targets: self.targets, inventory: self.topology) == self.displays, "Display changed after preparation")
                try self.observeEnvironment()
            } catch { self.sink.failure(nil, generation: self.generation, reason: String(describing: error)); return }
            // Called only from deliberate public start(), never init/prepare/tests.
            SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { content, error in
                self.queue.async { self.contentReady(content, error: error) }
            }
        }
    }
    private func contentReady(_ content: SCShareableContent?, error: Error?) {
        guard !stopped else { return }
        do {
            if let error { throw error }
            guard let content else { throw RecordingError(message: "ScreenCaptureKit inventory unavailable") }
            let excluded = content.applications.filter { $0.processID == getpid() }
            for (target, snapshot) in zip(targets, displays) {
                guard let display = content.displays.first(where: { $0.displayID == snapshot.id }) else { throw RecordingError(message: "Selected SCDisplay unavailable") }
                let source = try DisplayStream(id: target.slot.id, display: display, snapshot: snapshot, excluded: excluded, sink: sink, generation: generation, admission: admission)
                streams[target.slot.id] = source
            }
            let tap = PointerCapture(sink: sink, generation: generation, ownTag: ownTag) { [weak self] in
                guard let self else { return }
                self.queue.async { self.tapStopped = true; self.checkStopped() }
            }
            self.tap = tap; tapStopped = false; tap.start()
            for (id, source) in streams {
                starting.insert(id)
                source.stream.startCapture { error in
                    self.queue.async {
                        self.starting.remove(id)
                        if self.stopped { self.stopStream(id) }
                        else if let error { self.sink.failure(id, generation: self.generation, reason: error.localizedDescription) }
                        else { self.sink.sourceReady(id, generation: self.generation) }
                        self.checkStopped()
                    }
                }
            }
        } catch { sink.failure(nil, generation: generation, reason: String(describing: error)) }
    }
    func stop() {
        queue.async {
            guard !self.stopped else { self.checkStopped(); return }
            self.stopped = true
            self.timer?.cancel(); self.timer = nil
            for (center, token) in self.observations { center.removeObserver(token) }; self.observations.removeAll()
            self.tap?.stop()
            for id in Array(self.streams.keys) where !self.starting.contains(id) { self.stopStream(id) }
            self.checkStopped()
        }
    }
    private func stopStream(_ id: String) {
        guard let stream = streams[id]?.stream, !stopping.contains(id) else { return }
        stopping.insert(id)
        stream.stopCapture { error in
            self.queue.async {
                if let error { self.stopError = error.localizedDescription }
                self.stopping.remove(id); self.streams.removeValue(forKey: id); self.checkStopped()
            }
        }
    }
    private func checkStopped() {
        if stopped && !announcedStop && starting.isEmpty && streams.isEmpty && tapStopped {
            announcedStop = true; sink.captureStopped(generation: generation, error: stopError)
        }
    }
    private func observeEnvironment() throws {
        suspendClock = try SuspendClockGuard(initialDifferenceUs: SuspendClockGuard.currentDifferenceUs())
        let workspace = NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification, NSWorkspace.didWakeNotification, NSWorkspace.sessionDidResignActiveNotification] {
            let token = workspace.addObserver(forName: name, object: nil, queue: nil) { [sink, generation] _ in
                sink.failure(nil, generation: generation, reason: "System sleep or inactive user session")
            }
            observations.append((workspace, token))
        }
        let token = NotificationCenter.default.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: nil) { [sink, generation] _ in
            sink.failure(nil, generation: generation, reason: "Display configuration changed")
        }
        observations.append((NotificationCenter.default, token))
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.5, repeating: 0.5)
        timer.setEventHandler { [weak self] in
            guard let self, !self.stopped else { return }
            do {
                try self.suspendClock?.check(differenceUs: SuspendClockGuard.currentDifferenceUs())
                guard CGPreflightScreenCaptureAccess() && CGPreflightListenEventAccess() else { throw RecordingError(message: "Capture permission revoked") }
                try requireNative(try NativeDisplaySnapshot.current() == self.topology, "Display topology changed")
            } catch { self.sink.failure(nil, generation: self.generation, reason: String(describing: error)) }
        }
        self.timer = timer; timer.resume()
    }
}

/// The thread exclusively owns tap/CFRunLoopSource. Lock protects only stop flag
/// and runloop publication. stop() never joins/waits on the recording owner.
private final class PointerCapture: @unchecked Sendable {
    private let sink: any CaptureSink
    private let generation: UInt64, ownTag: Int64
    private let finished: @Sendable () -> Void
    private let lock = NSLock()
    private var stopped = false
    private var runLoop: CFRunLoop?
    init(sink: any CaptureSink, generation: UInt64, ownTag: Int64, finished: @escaping @Sendable () -> Void) {
        self.sink = sink; self.generation = generation; self.ownTag = ownTag; self.finished = finished
    }
    func start() { Thread.detachNewThread { self.run() } }
    func stop() {
        let loop = lock.withLock { stopped = true; return runLoop }
        if let loop { CFRunLoopStop(loop); CFRunLoopWakeUp(loop) }
    }
    private func run() {
        defer { lock.withLock { runLoop = nil }; finished() }
        guard !lock.withLock({ stopped }) else { return }
        guard CGPreflightListenEventAccess() else { sink.failure(nil, generation: generation, reason: "Input Monitoring permission missing"); return }
        guard ![CGMouseButton.left, .right, .center].contains(where: { CGEventSource.buttonState(.combinedSessionState, button: $0) }) else {
            sink.failure(nil, generation: generation, reason: "Release physical mouse buttons before recording"); return
        }
        let types: [CGEventType] = [.mouseMoved, .leftMouseDown, .leftMouseUp, .rightMouseDown, .rightMouseUp, .otherMouseDown, .otherMouseUp, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged, .scrollWheel]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap, options: .listenOnly, eventsOfInterest: mask, callback: { _, type, event, context in
            let observationTicks = mach_absolute_time() // FIRST entry operation, before classification or owner wait.
            guard let context else { return Unmanaged.passUnretained(event) }
            let owner = Unmanaged<PointerCapture>.fromOpaque(context).takeUnretainedValue()
            owner.receive(type: type, event: event, observationTicks: observationTicks)
            return Unmanaged.passUnretained(event)
        }, userInfo: Unmanaged.passUnretained(self).toOpaque()) else { sink.failure(nil, generation: generation, reason: "Cannot create listen-only pointer event tap"); return }
        defer { CFMachPortInvalidate(tap) }
        guard let source = CFMachPortCreateRunLoopSource(nil, tap, 0) else { sink.failure(nil, generation: generation, reason: "Cannot create pointer runloop source"); return }
        let loop = CFRunLoopGetCurrent()!
        CFRunLoopAddSource(loop, source, .commonModes)
        defer { CFRunLoopRemoveSource(loop, source, .commonModes) }
        CGEvent.tapEnable(tap: tap, enable: true)
        guard CGEvent.tapIsEnabled(tap: tap) else { sink.failure(nil, generation: generation, reason: "Pointer event tap disabled"); return }
        let shouldRun = lock.withLock { runLoop = loop; return !stopped }
        guard shouldRun else { return }
        sink.tapReady(generation: generation)
        // A stop before CFRunLoopRun is preserved by stopped; bounded intervals
        // also cover CFRunLoopStop's race with entry, without waiting on main.
        while !lock.withLock({ stopped }) { CFRunLoopRunInMode(.defaultMode, 0.1, false) }
    }
    private func receive(type: CGEventType, event: CGEvent, observationTicks: UInt64) {
        if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
            sink.failure(nil, generation: generation, reason: "Pointer tap disabled; continuity lost"); stop(); return
        }
        let tag = event.getIntegerValueField(.eventSourceUserData)
        if tag == ownTag { return }
        do {
            let point = event.location
            if let sample = try MacPointerSample.decode(type: type, observationHostUs: NativeTime.hostUs(observationTicks), timestampNs: event.timestamp, x: point.x, y: point.y,
                buttonNumber: event.getIntegerValueField(.mouseEventButtonNumber),
                scrollX: event.getDoubleValueField(.scrollWheelEventPointDeltaAxis2), scrollY: event.getDoubleValueField(.scrollWheelEventPointDeltaAxis1), sourceTag: tag) {
                sink.pointer(sample, generation: generation)
            }
        } catch { sink.failure(nil, generation: generation, reason: String(describing: error)) }
    }
}
