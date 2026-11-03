import XCTest
@testable import MotionClickerRuntime
import MotionClickerCore

final class FakeClock: MonotonicClock { var nowUs: Int64 = 0 }
final class FakeMonitors: MonitorProvider {
    var values = [RuntimeDisplay(id: "display", name: "Synthetic", geometry: DisplayGeometry(origin: Point(x: -100, y: 0), size: Size(width: 100, height: 100), scaleFactor: 2, rotationDegrees: 0), nativeBounds: Rect(origin: Point(x: -200, y: 0), size: Size(width: 200, height: 200)))]
    func displays() -> [RuntimeDisplay] { values }
}
final class FakeBackend: InputBackend {
    var ready = true
    var interference: UInt64 = 0
    var calls: [String] = []
    var points: [Point] = []
    var failSend = false
    var failRelease = false
    func position() throws -> Point { Point(x: -150, y: 50) }
    func send(_ step: RuntimeStep, at point: Point) throws {
        calls.append(step.kind.rawValue); points.append(point)
        if failSend { throw RuntimeFailure.unsafe("send failure") }
    }
    func release(_ button: MouseButton, at point: Point) throws {
        calls.append("release"); if failRelease { throw RuntimeFailure.unsafe("release failure") }
    }
}
final class RunnerTests: XCTestCase {
    func fixture() -> (FakeClock, FakeMonitors, FakeBackend, SafeRunner, RunProgram) {
        let c = FakeClock(), m = FakeMonitors(), b = FakeBackend()
        let slot = MonitorSlot(id: "slot", name: "Synthetic", geometry: m.values[0].geometry)
        let p = RunProgram(steps: [RuntimeStep(timeUs: 100, slot: "slot", point: Point(x: 25, y: 25), kind: .buttonDown, button: .left), RuntimeStep(timeUs: 1_000, slot: "slot", point: Point(x: 25, y: 25), kind: .buttonUp, button: .left)], slots: [slot])
        return (c,m,b,SafeRunner(clock:c,monitors:m,backend:b),p)
    }
    func testStopBeforeDeadlineAndIdempotentStop() throws {
        let (c,_,b,r,p) = fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        r.stop(); r.stop(); c.nowUs=2_000; r.pump()
        XCTAssertEqual(r.snapshot.state,.stopped); XCTAssertTrue(b.calls.isEmpty)
    }
    func testStopDuringHoldReleasesExactlyOwnedButton() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=100; r.pump(); r.stop(); r.stop(); c.nowUs=2_000; r.pump()
        XCTAssertEqual(b.calls,["buttonDown","release"])
        XCTAssertEqual(b.points,[Point(x:-150,y:50)])
    }
    func testTopologyChangeStopsBeforeDispatch() throws {
        let (c,m,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        m.values=[]; c.nowUs=100; r.pump()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertTrue(b.calls.isEmpty)
    }
    func testPhysicalInterferenceStopsAndReleases() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=100; r.pump(); b.interference += 1; c.nowUs=200; r.pump()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertEqual(b.calls,["buttonDown","release"])
    }
    func testSleepAndExcessLagNeverCatchUp() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=10_000_000; r.pump(); r.pump()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertTrue(b.calls.isEmpty)
    }
    func testPermissionLossStopsAndReleases() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=100; r.pump(); b.ready=false; c.nowUs=150; r.pump()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertEqual(b.calls,["buttonDown","release"])
    }
    func testBackendFailureStillAttemptsOwnedRelease() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        b.failSend=true; c.nowUs=100; r.pump()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertEqual(b.calls,["buttonDown","release"])
    }
    func testReleaseFailureIsVisibleAndBlocksRestart() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=100; r.pump(); b.failRelease=true; r.stop()
        XCTAssertEqual(r.snapshot.state,.failed); XCTAssertTrue(r.snapshot.reason.contains("release"))
        XCTAssertThrowsError(try r.start(p,mapping:["slot":"display"],hotkeyReady:true))
    }
    func testNoHotkeyOrUnresolvedMappingCannotStart() {
        let (_,_,b,r,p)=fixture()
        XCTAssertThrowsError(try r.start(p,mapping:["slot":"display"],hotkeyReady:false))
        XCTAssertThrowsError(try r.start(p,mapping:[:],hotkeyReady:true)); XCTAssertTrue(b.calls.isEmpty)
    }
    func testPauseRequiresSafeRestartAndNoQueuedUp() throws {
        let (c,_,b,r,p)=fixture(); try r.start(p,mapping:["slot":"display"],hotkeyReady:true)
        c.nowUs=100; r.pump(); r.stop("Paused; restart from beginning",paused:true); c.nowUs=1_000; r.pump()
        XCTAssertEqual(r.snapshot.state,.paused); XCTAssertEqual(b.calls,["buttonDown","release"])
    }
    func testPreviewHasEventsAndZeroInjection() {
        let (_,_,b,_,p)=fixture(); XCTAssertEqual(PreviewRunner.events(p).count,2); XCTAssertTrue(b.calls.isEmpty)
    }
    func testBoundedRepeatedProgramAndInfinitePreviewAreLazy() throws {
        let (c,_,b,r,p)=fixture()
        let repeatProgram=RunProgram(steps:p.steps,slots:p.slots,periodUs:20_000,count:2)
        try r.start(repeatProgram,mapping:["slot":"display"],hotkeyReady:true)
        for time: Int64 in [100,1_000,20_100,21_000,21_001] { c.nowUs=time; r.pump() }
        XCTAssertEqual(b.calls,["buttonDown","buttonUp","buttonDown","buttonUp"])
        XCTAssertEqual(r.snapshot.state,.stopped)
        XCTAssertEqual(PreviewRunner.events(RunProgram(steps:p.steps,slots:p.slots,periodUs:20_000,count:nil),limit:3).count,3)
    }
}
