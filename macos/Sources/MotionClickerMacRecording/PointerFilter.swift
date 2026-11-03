import CoreGraphics
import MotionClickerCore
import MotionClickerRecording

public struct MacRecordingOptions: Sendable {
    public let ownInjectedTag: Int64
    public let isControl: @Sendable (MacPointerSample) -> Bool
    public init(ownInjectedTag: Int64, isControl: @escaping @Sendable (MacPointerSample) -> Bool) { self.ownInjectedTag = ownInjectedTag; self.isControl = isControl }
}
public struct MacPointerSample: Sendable {
    public let timestampNs: UInt64
    public let position: Point
    public let kind: ActionKind
    public let button: MouseButton?
    public let deltaX: Double?, deltaY: Double?
    public let sourceTag: Int64
    static func decode(type: CGEventType, timestampNs: UInt64, x: Double, y: Double, buttonNumber: Int64, scrollX: Double, scrollY: Double, sourceTag: Int64) throws -> MacPointerSample? {
        let kind: ActionKind, button: MouseButton?
        switch type {
        case .mouseMoved, .leftMouseDragged, .rightMouseDragged, .otherMouseDragged:
            if type == .otherMouseDragged { try requireNative(buttonNumber == 2, "Unsupported pointer button") }
            kind = .move; button = nil
        case .leftMouseDown, .leftMouseUp: kind = type == .leftMouseDown ? .buttonDown : .buttonUp; button = .left
        case .rightMouseDown, .rightMouseUp: kind = type == .rightMouseDown ? .buttonDown : .buttonUp; button = .right
        case .otherMouseDown, .otherMouseUp:
            try requireNative(buttonNumber == 2, "Unsupported pointer button")
            kind = type == .otherMouseDown ? .buttonDown : .buttonUp; button = .middle
        case .scrollWheel: kind = .scroll; button = nil
        default: return nil
        }
        try requireNative(x.isFinite && y.isFinite && scrollX.isFinite && scrollY.isFinite, "Invalid pointer coordinates")
        // CG point deltas are pixels; the project's scroll axes are opposite CG's
        // wheel axes (the existing MacInput backend negates them on dispatch).
        return MacPointerSample(timestampNs: timestampNs, position: Point(x: x, y: y), kind: kind, button: button,
                                deltaX: kind == .scroll ? -scrollX : nil, deltaY: kind == .scroll ? -scrollY : nil, sourceTag: sourceTag)
    }
}
struct PointerFilter {
    let options: MacRecordingOptions
    private let clock: VerifiedPointerClock?
    private var controlButton: MouseButton?
    private var recordedButton: MouseButton?
    init(options: MacRecordingOptions, clock: VerifiedPointerClock? = nil) { self.options = options; self.clock = clock }
    mutating func receive(_ sample: MacPointerSample, nowUs: Int64) throws -> CapturedPointerEvent? {
        if sample.sourceTag == options.ownInjectedTag { return nil }
        if let controlButton {
            try requireNative(sample.kind != .buttonDown, "Repeated down during control gesture")
            if sample.kind == .buttonUp {
                try requireNative(sample.button == controlButton, "Mismatched control release")
                self.controlButton = nil
            }
            return nil
        }
        if recordedButton == nil && options.isControl(sample) {
            if sample.kind == .buttonDown { controlButton = sample.button }
            return nil
        }
        let time = try NativeTime.pointerUs(timestampNs: sample.timestampNs, nowUs: nowUs, clock: clock)
        // Core remains the authority for repeated/mismatched physical ownership.
        if sample.kind == .buttonDown { recordedButton = sample.button }
        if sample.kind == .buttonUp { recordedButton = nil }
        return CapturedPointerEvent(hostTimeUs: time, nativePosition: sample.position, kind: sample.kind,
                                    button: sample.button, deltaX: sample.deltaX, deltaY: sample.deltaY)
    }
}
