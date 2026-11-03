import AppKit
import SwiftUI
import CoreGraphics
import Carbon
import MotionClickerCore
import MotionClickerRuntime

final class MacMonitors: MonitorProvider {
    func displays() -> [RuntimeDisplay] {
        var count: UInt32 = 0
        guard CGGetActiveDisplayList(0,nil,&count) == .success else { return [] }
        var ids = [CGDirectDisplayID](repeating:0,count:Int(count))
        guard CGGetActiveDisplayList(count,&ids,&count) == .success else { return [] }
        return ids.prefix(Int(count)).filter { CGDisplayMirrorsDisplay($0) == kCGNullDirectDisplay }.map { id in
            let b = CGDisplayBounds(id)
            let geometry = DisplayGeometry(origin:Point(x:b.minX,y:b.minY),size:Size(width:b.width,height:b.height),
                scaleFactor:Double(CGDisplayPixelsWide(id))/b.width,rotationDegrees:Int(CGDisplayRotation(id)))
            return RuntimeDisplay(id:String(id),name:"Display \(id) · \(Int(b.width)) × \(Int(b.height))",geometry:geometry,
                nativeBounds:Rect(origin:geometry.origin,size:geometry.size))
        }.sorted { $0.id < $1.id }
    }
}
final class ContinuousClock: MonotonicClock {
    private let numerator: UInt64
    private let denominator: UInt64
    init() { var info = mach_timebase_info_data_t(); mach_timebase_info(&info); numerator=UInt64(info.numer); denominator=UInt64(info.denom) }
    var nowUs: Int64 { Int64(mach_continuous_time() / denominator * numerator / 1_000) }
}
/// CGEvent locations are global upper-left logical points, NOT Retina backing pixels.
final class MacInput: InputBackend, @unchecked Sendable {
    static let tag: Int64 = 0x4d434c49434b
    private let lock = NSLock()
    private var generation: UInt64 = 0
    private var activeSession = true
    private var tapHealthy = false
    private var heldButton:MouseButton?
    private var tap: CFMachPort?
    private var source: CFRunLoopSource?
    var physicalButtonsDown: Bool { [.left,.right,.center].contains { CGEventSource.buttonState(.hidSystemState,button:$0) } }
    var interference: UInt64 { lock.lock(); defer { lock.unlock() }; return generation }
    var ready: Bool {
        lock.lock(); let healthy = tapHealthy && activeSession; lock.unlock()
        return healthy && CGPreflightPostEventAccess() && CGPreflightListenEventAccess()
    }
    func setSession(_ active: Bool) { lock.lock(); activeSession = active; lock.unlock() }
    func beginMonitoring() throws {
        guard CGPreflightPostEventAccess(), CGPreflightListenEventAccess() else { throw RuntimeFailure.unsafe("Allow Accessibility and Input Monitoring using the permission button, then restart the app if macOS requests it.") }
        if tap != nil { return }
        let types: [CGEventType] = [.mouseMoved,.leftMouseDown,.leftMouseUp,.rightMouseDown,.rightMouseUp,.otherMouseDown,.otherMouseUp,.leftMouseDragged,.rightMouseDragged,.otherMouseDragged,.scrollWheel,.keyDown,.keyUp,.flagsChanged]
        let mask = types.reduce(CGEventMask(0)) { $0 | (CGEventMask(1) << $1.rawValue) }
        guard let eventTap = CGEvent.tapCreate(tap:.cgSessionEventTap,place:.headInsertEventTap,options:.listenOnly,eventsOfInterest:mask,
            callback:{ _,type,event,context in
                guard let context else { return Unmanaged.passUnretained(event) }
                let owner = Unmanaged<MacInput>.fromOpaque(context).takeUnretainedValue()
                owner.lock.lock()
                if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput { owner.tapHealthy=false }
                else if event.getIntegerValueField(.eventSourceUserData) != MacInput.tag { owner.generation &+= 1 }
                owner.lock.unlock()
                return Unmanaged.passUnretained(event)
            },userInfo:Unmanaged.passUnretained(self).toOpaque()) else { throw RuntimeFailure.unsafe("Input monitor registration failed") }
        tap=eventTap; source=CFMachPortCreateRunLoopSource(kCFAllocatorDefault,eventTap,0)
        CFRunLoopAddSource(CFRunLoopGetMain(),source,.commonModes)
        CGEvent.tapEnable(tap:eventTap,enable:true)
        lock.lock(); tapHealthy=CGEvent.tapIsEnabled(tap:eventTap); lock.unlock()
        guard ready else { throw RuntimeFailure.unsafe("Input monitor is unavailable") }
    }
    func position() throws -> Point {
        guard let e=CGEvent(source:nil) else { throw RuntimeFailure.unsafe("Pointer unavailable") }
        return Point(x:e.location.x,y:e.location.y)
    }
    func send(_ step:RuntimeStep,at point:Point) throws {
        let effectiveButton=step.button ?? heldButton
        let button: CGMouseButton = effectiveButton == .right ? .right : (effectiveButton == .middle ? .center : .left)
        let type: CGEventType
        switch step.kind {
        case .move: type = heldButton == .right ? .rightMouseDragged : (heldButton == .middle ? .otherMouseDragged : (heldButton == .left ? .leftMouseDragged : .mouseMoved))
        case .buttonDown: type = button == .left ? .leftMouseDown : (button == .right ? .rightMouseDown : .otherMouseDown)
        case .buttonUp: type = button == .left ? .leftMouseUp : (button == .right ? .rightMouseUp : .otherMouseUp)
        case .scroll:
            guard let event = CGEvent(scrollWheelEvent2Source:nil,units:.pixel,wheelCount:2,wheel1:Int32(max(Double(Int32.min),min(Double(Int32.max),-step.deltaY.rounded()))),wheel2:Int32(max(Double(Int32.min),min(Double(Int32.max),-step.deltaX.rounded()))),wheel3:0) else { throw RuntimeFailure.unsafe("Cannot create scroll event") }
            event.location=CGPoint(x:point.x,y:point.y); post(event); return
        }
        guard let event=CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:CGPoint(x:point.x,y:point.y),mouseButton:button) else { throw RuntimeFailure.unsafe("Cannot allocate mouse event") }
        event.setIntegerValueField(.mouseEventClickState,value:Int64(step.clickCount)); post(event)
        if step.kind == .buttonDown { heldButton=step.button }; if step.kind == .buttonUp { heldButton=nil }
    }
    private func post(_ event:CGEvent) {
        event.setIntegerValueField(.eventSourceUserData,value:Self.tag)
        event.post(tap:.cghidEventTap) // API has no acknowledgment; this reports dispatch attempts only.
    }
    func release(_ button:MouseButton,at point:Point) throws {
        let current=(try? position()) ?? point
        try send(RuntimeStep(timeUs:0,slot:"release",point:current,kind:.buttonUp,button:button),at:current)
    }
    deinit { if let tap { CFMachPortInvalidate(tap) } }
}
@MainActor final class EmergencyHotkey {
    private var reference: EventHotKeyRef?
    private var handler: EventHandlerRef?
    var stop: (() -> Void)?
    func register(keyCode: UInt32) throws {
        if let reference { UnregisterEventHotKey(reference); self.reference=nil }
        if handler == nil {
            var type=EventTypeSpec(eventClass:OSType(kEventClassKeyboard),eventKind:UInt32(kEventHotKeyPressed))
            let status=InstallEventHandler(GetApplicationEventTarget(),{ _,_,context in
                guard let context else { return OSStatus(eventNotHandledErr) }
                MainActor.assumeIsolated { Unmanaged<EmergencyHotkey>.fromOpaque(context).takeUnretainedValue().stop?() }
                return noErr
            },1,&type,Unmanaged.passUnretained(self).toOpaque(),&handler)
            guard status == noErr else { throw RuntimeFailure.unsafe("Emergency hotkey handler failed: \(status)") }
        }
        let status=RegisterEventHotKey(keyCode,UInt32(controlKey | optionKey | cmdKey),EventHotKeyID(signature:0x4d434c4b,id:1),GetApplicationEventTarget(),0,&reference)
        guard status == noErr, reference != nil else { throw RuntimeFailure.unsafe("Emergency hotkey is already in use. Choose the other key. Status \(status)") }
    }
    func unregister() { if let reference { UnregisterEventHotKey(reference); self.reference=nil } }
}
final class RunWorker: @unchecked Sendable {
    private let queue=DispatchQueue(label:"MotionClicker.runner",qos:.userInteractive)
    private let wake=DispatchSemaphore(value:0)
    private let lock=NSLock()
    private var cancelled=false
    private var isCancelled:Bool { lock.lock(); defer { lock.unlock() }; return cancelled }
    let runner: SafeRunner
    init(_ runner:SafeRunner) { self.runner=runner }
    func launch() {
        queue.async { [self] in
            while !isCancelled && [.armed,.running].contains(runner.snapshot.state) {
                runner.pump()
                // Cancellable wait, independent of SwiftUI rendering. Never catch up after a stall.
                _ = wake.wait(timeout:.now() + .microseconds(1_000))
            }
        }
    }
    func stop(_ reason:String="Stopped",paused:Bool=false) { lock.lock(); cancelled=true; lock.unlock(); runner.stop(reason,paused:paused); wake.signal() }
}
