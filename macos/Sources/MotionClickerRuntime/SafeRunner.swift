import Foundation
import MotionClickerCore

public struct RuntimeDisplay: Sendable, Equatable {
    public let id: String
    public let name: String
    public let geometry: DisplayGeometry
    public let nativeBounds: Rect
    public init(id: String, name: String, geometry: DisplayGeometry, nativeBounds: Rect) {
        self.id = id; self.name = name; self.geometry = geometry; self.nativeBounds = nativeBounds
    }
}
public protocol MonitorProvider { func displays() -> [RuntimeDisplay] }
public protocol MonotonicClock { var nowUs: Int64 { get } }
public protocol InputBackend {
    var ready: Bool { get }
    var physicalButtonsDown: Bool { get }
    var interference: UInt64 { get }
    func position() throws -> Point
    func send(_ step: RuntimeStep, at: Point) throws
    func release(_ button: MouseButton, at: Point) throws
}
public struct RuntimeStep: Sendable, Equatable {
    public let timeUs: Int64
    public let slot: String
    public let point: Point?
    public let kind: ActionKind
    public let button: MouseButton?
    public let clickCount: Int
    public let deltaX: Double
    public let deltaY: Double
    public init(timeUs: Int64, slot: String, point: Point?, kind: ActionKind, button: MouseButton? = nil, clickCount: Int = 1, deltaX: Double = 0, deltaY: Double = 0) {
        self.timeUs = timeUs; self.slot = slot; self.point = point; self.kind = kind; self.button = button; self.clickCount = clickCount; self.deltaX = deltaX; self.deltaY = deltaY
    }
}
public struct RunProgram: Sendable {
    public let steps: [RuntimeStep]
    public let slots: [MonitorSlot]
    public let periodUs: Int64?
    public let count: Int64?
    public let durationUs: Int64?
    public init(steps: [RuntimeStep], slots: [MonitorSlot], periodUs: Int64? = nil, count: Int64? = 1, durationUs: Int64? = nil) {
        self.steps = steps; self.slots = slots; self.periodUs = periodUs; self.count = count; self.durationUs = durationUs
    }
}
public enum RunnerState: String, Sendable { case idle, armed, running, paused, stopped, failed }
public struct RunnerSnapshot: Sendable {
    public let state: RunnerState
    public let reason: String
    public let dispatched: Int64
    public let maxLagUs: Int64
}
public enum RuntimeFailure: Error { case unsafe(String) }
/// All dispatch, cancellation and hold ownership transitions share this gate.
/// `pump` is driven by the worker, never a drawing timer; it sends at most one event.
public final class SafeRunner: @unchecked Sendable {
    private let gate = NSLock()
    private let clock: any MonotonicClock
    private let monitors: any MonitorProvider
    private let backend: any InputBackend
    private var state: RunnerState = .idle
    private var reason = "Ready"
    private var dispatched: Int64 = 0
    private var maxLag: Int64 = 0
    private var program: RunProgram?
    private var topology: [RuntimeDisplay] = []
    private var mapping: [String: String] = [:]
    private var epoch: Int64 = 0
    private var cycle: Int64 = 0
    private var index = 0
    private var initialInterference: UInt64 = 0
    private var owned: (MouseButton, Point)?
    public init(clock: any MonotonicClock, monitors: any MonitorProvider, backend: any InputBackend) {
        self.clock = clock; self.monitors = monitors; self.backend = backend
    }
    public var snapshot: RunnerSnapshot {
        gate.lock(); defer { gate.unlock() }
        return RunnerSnapshot(state: state, reason: reason, dispatched: dispatched, maxLagUs: maxLag)
    }
    public func start(_ program: RunProgram, mapping: [String: String], delayUs: Int64 = 0, hotkeyReady: Bool) throws {
        gate.lock(); defer { gate.unlock() }
        guard owned == nil, state != .running, state != .armed else { throw RuntimeFailure.unsafe("Stop and resolve owned release before restart.") }
        guard hotkeyReady, backend.ready, !backend.physicalButtonsDown else { throw RuntimeFailure.unsafe("Emergency hotkey and input permissions must be available.") }
        try Self.validate(program)
        let displays = monitors.displays()
        guard !program.steps.isEmpty, delayUs >= 0, clock.nowUs <= Int64.max - delayUs else { throw RuntimeFailure.unsafe("Empty plan or invalid start delay.") }
        for slot in program.slots {
            guard let target = mapping[slot.id], let display = displays.first(where: { $0.id == target }),
                  slot.geometry.size == display.geometry.size,
                  slot.geometry.scaleFactor == display.geometry.scaleFactor,
                  slot.geometry.rotationDegrees == display.geometry.rotationDegrees else {
                throw RuntimeFailure.unsafe("Map every monitor slot explicitly to matching size, scale and rotation.")
            }
        }
        guard Set(mapping.values).count == mapping.count else { throw RuntimeFailure.unsafe("Monitor slots require distinct runtime displays.") }
        for step in program.steps {
            if let local = step.point {
                guard let id=mapping[step.slot], let display=displays.first(where:{$0.id==id}) else { throw RuntimeFailure.unsafe("Unresolved step monitor") }
                _ = try Self.nativePoint(local,display:display)
            }
        }
        self.program = program; self.mapping = mapping; topology = displays
        epoch = clock.nowUs + delayUs; cycle = 0; index = 0; dispatched = 0; maxLag = 0
        initialInterference = backend.interference; state = delayUs > 0 ? .armed : .running
        reason = "Emergency hotkey active; physical input stops execution"
    }
    public func pump() {
        gate.lock(); defer { gate.unlock() }
        guard state == .armed || state == .running, let p = program else { return }
        guard backend.ready else { finish("Input permission or session lost", failed: true); return }
        guard monitors.displays() == topology else { finish("Display topology changed; remap before restart", failed: true); return }
        let now = clock.nowUs
        if state == .armed {
            guard now >= epoch else { return }
            guard !backend.physicalButtonsDown else { finish("Release physical mouse buttons before execution",failed:true); return }
            initialInterference=backend.interference; state = .running
        }
        guard backend.interference == initialInterference else { finish("Physical input detected",failed:true); return }
        guard now >= epoch else { return }
        if let duration = p.durationUs, now - epoch >= duration { finish("Duration complete"); return }
        guard let offset = offsetFor(p, cycle: cycle, index: index), offset <= Int64.max - epoch else {
            finish("Schedule overflow", failed: true); return
        }
        let deadline = epoch + offset
        guard now >= deadline else { return }
        let lag = now - deadline
        maxLag = max(maxLag, lag)
        guard lag <= 100_000 else { finish("Scheduling lag exceeded 100 ms; no catch-up", failed: true); return }
        state = .running
        let step = p.steps[index]
        do {
            let point: Point
            if let local = step.point {
                guard let id = mapping[step.slot], let display = topology.first(where: { $0.id == id }) else { throw RuntimeFailure.unsafe("Unresolved monitor") }
                point = try Self.nativePoint(local, display: display)
            } else {
                // Current-position mode still enforces the explicitly selected display set.
                point = try backend.position()
                guard topology.contains(where: { mapping.values.contains($0.id) && Self.contains(point, in: $0.nativeBounds) }) else { throw RuntimeFailure.unsafe("Pointer is outside selected displays") }
            }
            if step.kind == .buttonDown {
                guard owned == nil, let button = step.button else { throw RuntimeFailure.unsafe("Invalid hold") }
                // Conservatively own before calling: a failing backend may have partially dispatched.
                owned = (button, point)
            }
            try backend.send(step, at: point)
            if step.kind == .buttonUp { owned = nil }
            else if let hold = owned { owned = (hold.0, point) }
            dispatched += 1; index += 1
            if index == p.steps.count {
                index = 0
                if p.periodUs == nil || (p.count != nil && cycle >= p.count! - 1) { finish("Completed"); return }
                guard cycle < Int64.max else { finish("Repeat counter overflow", failed: true); return }
                cycle += 1
            }
        } catch { finish("Input dispatch failed: \(error)", failed: true) }
    }
    public func stop(_ reason: String = "Stopped", paused: Bool = false) {
        gate.lock(); defer { gate.unlock() }
        finish(reason, paused: paused)
    }
    private func finish(_ message: String, failed: Bool = false, paused: Bool = false) {
        state = failed ? .failed : (paused ? .paused : .stopped); reason = message
        if let hold = owned {
            do { try backend.release(hold.0, at: hold.1); owned = nil }
            catch { state = .failed; reason = "Owned release failed; retry Stop before restart: \(error)" }
        }
        program = nil
    }
    private func offsetFor(_ p: RunProgram, cycle: Int64, index: Int) -> Int64? {
        let multiplication = (p.periodUs ?? 0).multipliedReportingOverflow(by: cycle)
        let sum = multiplication.partialValue.addingReportingOverflow(p.steps[index].timeUs)
        return multiplication.overflow || sum.overflow ? nil : sum.partialValue
    }
    public static func contains(_ p: Point, in rect: Rect) -> Bool {
        p.x.isFinite && p.y.isFinite && p.x >= rect.origin.x && p.y >= rect.origin.y
            && p.x < rect.origin.x + rect.size.width && p.y < rect.origin.y + rect.size.height
    }
    /// OS adapter boundary: native units are CG global points on macOS, physical pixels on Windows.
    public static func nativePoint(_ local: Point, display: RuntimeDisplay) throws -> Point {
        guard contains(local, in: Rect(origin: Point(x: 0,y: 0),size: display.geometry.size)) else { throw RuntimeFailure.unsafe("Point outside monitor") }
        let result = Point(x: display.nativeBounds.origin.x + local.x / display.geometry.size.width * display.nativeBounds.size.width,
                           y: display.nativeBounds.origin.y + local.y / display.geometry.size.height * display.nativeBounds.size.height)
        guard contains(result,in:display.nativeBounds) else { throw RuntimeFailure.unsafe("Native point outside display") }
        return result
    }
    private static func validate(_ p: RunProgram) throws {
        guard !p.steps.isEmpty, p.count == nil || p.count! > 0, p.durationUs == nil || p.durationUs! > 0,
              p.periodUs == nil || (p.periodUs! >= 20_000 && p.steps.last!.timeUs < p.periodUs!) else { throw RuntimeFailure.unsafe("Invalid count, duration or repeat interval (minimum 20 ms)") }
        var last: Int64 = -1; var held: MouseButton?
        for step in p.steps {
            guard step.timeUs >= last, p.slots.contains(where: { $0.id == step.slot }) || step.point == nil else { throw RuntimeFailure.unsafe("Invalid order or monitor slot") }
            last = step.timeUs
            switch step.kind {
            case .buttonDown:
                guard held == nil, let button = step.button else { throw RuntimeFailure.unsafe("Overlapping hold") }; held = button
            case .buttonUp:
                guard held != nil, held == step.button else { throw RuntimeFailure.unsafe("Unpaired release") }; held = nil
            case .scroll: guard held == nil else { throw RuntimeFailure.unsafe("Scroll during hold") }
            case .move: break
            }
        }
        guard held == nil else { throw RuntimeFailure.unsafe("Plan ends held") }
    }
}
/// Pure preview deliberately cannot accept an InputBackend; infinite programs are capped.
public enum PreviewRunner {
    public static func events(_ program: RunProgram, limit: Int = 200) -> [RuntimeStep] {
        guard limit > 0, !program.steps.isEmpty else { return [] }
        var result: [RuntimeStep] = []; var cycle: Int64 = 0
        while result.count < limit, program.count == nil || cycle < program.count! {
            let base = (program.periodUs ?? 0).multipliedReportingOverflow(by: cycle)
            if base.overflow { break }
            for step in program.steps {
                let time = base.partialValue.addingReportingOverflow(step.timeUs)
                if time.overflow || (program.durationUs != nil && time.partialValue >= program.durationUs!) { return result }
                result.append(RuntimeStep(timeUs: time.partialValue,slot:step.slot,point:step.point,kind:step.kind,button:step.button,clickCount:step.clickCount,deltaX:step.deltaX,deltaY:step.deltaY))
                if result.count == limit { return result }
            }
            if program.periodUs == nil || cycle == Int64.max { break }; cycle += 1
        }
        return result
    }
}

public extension RunProgram {
    static func project(_ project: MotionProject) throws -> RunProgram {
        let actions = try RunPlanCompiler.compile(project)
        return RunProgram(steps: actions.map { RuntimeStep(timeUs:$0.timeUs,slot:$0.monitorSlotId,point:Point(x:$0.x,y:$0.y),kind:$0.kind,button:$0.button,deltaX:$0.deltaX ?? 0,deltaY:$0.deltaY ?? 0) },slots:project.monitorSlots)
    }
    /// Repeat count counts passes through the ordered points. Double-click counts use native event semantics.
    static func quick(slots: [MonitorSlot], points: [(String, Point?)], button: MouseButton, doubleClick: Bool,
                      intervalUs: Int64, count: Int64?, durationUs: Int64?) throws -> RunProgram {
        guard intervalUs >= 20_000, intervalUs <= Int64.max / Int64(max(1,points.count)), !points.isEmpty else { throw RuntimeFailure.unsafe("Choose points and an interval of at least 20 ms") }
        var events: [RuntimeStep] = []
        for (i,target) in points.enumerated() {
            let base = Int64(i) * intervalUs
            for click in 1...(doubleClick ? 2 : 1) {
                let time = base + Int64(click - 1) * 5_000
                events.append(RuntimeStep(timeUs:time,slot:target.0,point:target.1,kind:.buttonDown,button:button,clickCount:click))
                events.append(RuntimeStep(timeUs:time + 1_000,slot:target.0,point:target.1,kind:.buttonUp,button:button,clickCount:click))
            }
        }
        return RunProgram(steps:events,slots:slots,periodUs:Int64(points.count) * intervalUs,count:count,durationUs:durationUs)
    }
}
