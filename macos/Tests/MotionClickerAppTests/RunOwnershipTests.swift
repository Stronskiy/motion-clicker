import XCTest
import MotionClickerCore
import MotionClickerRuntime
@testable import MotionClickerApp

@MainActor final class RunOwnershipTests: XCTestCase {
    func testFailedOwnedReleaseBlocksRecordUntilEmergencyRetryAcknowledgesRelease() async throws {
        let (runner,backend)=try heldFailure()
        let model=ClickerModel(worker:RunWorker(runner))
        let capture=FakeCapture()
        model.recording=RecordingFlow(gate:model.operation,makeCapture:{capture},create:{_ in})
        XCTAssertTrue(model.operation.acquireRun())
        model.poll()
        XCTAssertTrue(model.active)
        XCTAssertTrue(model.runNeedsRelease)
        XCTAssertTrue(model.snapshot.hasUnreleasedInput)
        XCTAssertFalse(model.canRun)
        let opened=model.operation.acquireOpen(); XCTAssertFalse(opened)
        if opened { model.operation.release() }
        let originalDisplays=model.displays
        model.displays=[]; model.refreshDisplays()
        XCTAssertTrue(model.displays.isEmpty)
        model.displays=originalDisplays
        await model.recording.prepare(targets:[],destination:URL(filePath:"/tmp/blocked.motionclicker"))
        XCTAssertEqual(capture.calls,[])
        // Cleanup the old behavior's wrongly admitted fake take before retry.
        if model.recording.ownsOperation { await model.recording.stop() }
        backend.failRelease=false
        model.stop("Emergency hotkey")
        XCTAssertFalse(model.active)
        XCTAssertEqual(backend.calls,["buttonDown","release","release"])
        await model.recording.prepare(targets:[],destination:URL(filePath:"/tmp/allowed.motionclicker"))
        XCTAssertEqual(capture.calls,["prepare"])
        await model.recording.stop()
    }
    func testStartCatchCannotReleaseAnExistingUnresolvedRunHold() throws {
        let (runner,_)=try heldFailure()
        let model=ClickerModel(worker:RunWorker(runner),runPreparation:{throw RuntimeFailure.unsafe("Injected preparation failure")})
        XCTAssertTrue(model.operation.acquireRun()); model.operation.pauseRun()
        model.start()
        XCTAssertTrue(model.active)
        XCTAssertFalse(model.operation.acquireRecording())
    }
    func testFailedRunWithAcknowledgedReleaseDoesNotHoldTheGate() throws {
        let (runner,_)=try heldFailure(releaseFails:false)
        let model=ClickerModel(worker:RunWorker(runner))
        XCTAssertTrue(model.operation.acquireRun()); model.poll()
        XCTAssertFalse(model.active)
        XCTAssertTrue(model.operation.acquireRecording())
    }
    private func heldFailure(releaseFails:Bool = true) throws -> (SafeRunner,OwnershipBackend) {
        let clock=OwnershipClock(), monitors=OwnershipMonitors(), backend=OwnershipBackend()
        let slot=MonitorSlot(id:"slot",name:"Generated",geometry:monitors.value.geometry)
        let program=RunProgram(steps:[.init(timeUs:0,slot:slot.id,point:.init(x:10,y:10),kind:.buttonDown,button:.left),.init(timeUs:10_000,slot:slot.id,point:.init(x:10,y:10),kind:.buttonUp,button:.left)],slots:[slot])
        let runner=SafeRunner(clock:clock,monitors:monitors,backend:backend)
        try runner.start(program,mapping:[slot.id:monitors.value.id],hotkeyReady:true)
        backend.failSend = !releaseFails; backend.failRelease=releaseFails
        runner.pump(); if releaseFails { runner.stop() }
        XCTAssertEqual(runner.snapshot.state,.failed)
        return(runner,backend)
    }
}
private final class OwnershipClock: MonotonicClock { var nowUs:Int64=0 }
private final class OwnershipMonitors: MonitorProvider {
    let value=RuntimeDisplay(id:"generated",name:"Generated",geometry:.init(origin:.init(x:0,y:0),size:.init(width:100,height:100),scaleFactor:1,rotationDegrees:0),nativeBounds:.init(origin:.init(x:0,y:0),size:.init(width:100,height:100)))
    func displays()->[RuntimeDisplay] { [value] }
}
private final class OwnershipBackend: InputBackend {
    var ready=true,physicalButtonsDown=false,failRelease=false,failSend=false
    var interference:UInt64=0
    var calls:[String]=[]
    func position() throws -> Point { .init(x:10,y:10) }
    func send(_ step:RuntimeStep,at point:Point) throws { calls.append(step.kind.rawValue); if failSend { throw RuntimeFailure.unsafe("Injected send failure") } }
    func release(_ button:MouseButton,at point:Point) throws { calls.append("release"); if failRelease { throw RuntimeFailure.unsafe("Injected owned release failure") } }
}
