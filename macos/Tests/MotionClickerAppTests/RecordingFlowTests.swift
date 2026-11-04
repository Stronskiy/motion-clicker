import XCTest
@testable import MotionClickerRecording
import MotionClickerCore
import MotionClickerMacRecording
@testable import MotionClickerApp

@MainActor final class RecordingFlowTests: XCTestCase {
    func testConstructionIsInertAndPausedRunExcludesRecording() async {
        let capture = FakeCapture()
        let gate = AppOperationGate()
        let flow = RecordingFlow(gate: gate, makeCapture: { capture }, create: { _ in })
        XCTAssertEqual(capture.calls, [])
        XCTAssertTrue(gate.acquireRun())
        gate.pauseRun()
        await flow.prepare(targets: [], destination: URL(filePath: "/tmp/test.motionclicker"))
        XCTAssertEqual(capture.calls, [])
        XCTAssertEqual(gate.operation, .runPaused)
    }
    func testPreparationDoesNotStartAndCountdownCancelStopsExactlyOnce() async {
        let capture = FakeCapture(), gate = AppOperationGate()
        let flow = RecordingFlow(gate: gate, makeCapture: { capture }, create: { _ in })
        await flow.prepare(targets: [], destination: URL(filePath: "/tmp/test.motionclicker"))
        XCTAssertEqual(flow.phase, .ready)
        XCTAssertEqual(capture.calls, ["prepare"])
        flow.countdown(seconds: 3)
        await flow.stop(reason: "Cancelled")
        await flow.stop(reason: "Duplicate")
        XCTAssertEqual(capture.calls, ["prepare", "stop"])
        XCTAssertFalse(gate.busy)
        XCTAssertNotEqual(flow.phase, .saved)
    }
    func testCloseDuringPreparationWaitsForPreparationThenNativeClosure() async {
        let c=FakeCapture(), gate=AppOperationGate()
        c.holdPreparation=true; c.holdStop=true
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in})
        let preparing=Task { await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker")) }
        await settle { c.prepareWaiter != nil }
        let stop=Task { await flow.stop(reason:"Window closed") }
        await settle { flow.phase == .finalizing }
        XCTAssertTrue(gate.busy); XCTAssertEqual(c.calls,["prepare"])
        c.prepareWaiter?.resume(); c.prepareWaiter=nil
        await settle { c.stopWaiter != nil }
        XCTAssertTrue(gate.busy); XCTAssertFalse(gate.acquireRun())
        c.stopWaiter?.resume(); c.stopWaiter=nil
        await stop.value; await preparing.value
        XCTAssertFalse(gate.busy); XCTAssertEqual(flow.phase,.cancelled)
        XCTAssertFalse(c.calls.contains("start"))
    }
    func testSaveFailureRetainsResultForExplicitRetryAndNeverSaysSaved() async throws {
        let c=FakeCapture(), gate=AppOperationGate(), fixture=try FlowFixture.load()
        c.result=fixture.result; c.published=true
        var attempts=0
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in},save:{_,_ in
            attempts += 1
            if attempts == 1 { throw RecordingError(message:"disk full") }
            return fixture.inspected
        },sleep:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        flow.countdown(seconds:1)
        await settle { flow.phase == .recording }
        await flow.stop()
        XCTAssertEqual(flow.phase,.failed); XCTAssertNil(flow.saved)
        XCTAssertTrue(flow.canRetrySave)
        await flow.retrySave()
        XCTAssertEqual(flow.phase,.saved); XCTAssertNotNil(flow.saved)
        XCTAssertEqual(c.calls.filter{$0 == "stop"}.count,1)
        XCTAssertEqual(attempts,2)
    }
    func testNativeSourceFailureStopsAndSavesPartialWithoutExecuting() async throws {
        let c=FakeCapture(), gate=AppOperationGate(), fixture=try FlowFixture.load()
        c.result=fixture.result; c.published=true
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in},save:{_,_ in fixture.partial},sleep:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        flow.countdown(seconds:1)
        await settle { flow.phase == .recording }
        c.progressState = .interrupted
        await flow.poll()
        XCTAssertEqual(flow.phase,.saved); XCTAssertFalse(try XCTUnwrap(flow.saved).executable)
        XCTAssertTrue(flow.message.contains("partial")); XCTAssertFalse(gate.busy)
    }
    func testPermissionFailureSpecificHelpAndCleanupWithoutStart() async {
        let c=FakeCapture(), gate=AppOperationGate()
        c.prepareError=MotionClickerMacRecording.MacRecordingFailure.screenRecordingPermissionMissing
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        XCTAssertEqual(flow.phase,.failed)
        XCTAssertTrue(flow.message.contains("Screen Recording permission"))
        XCTAssertEqual(c.calls,["prepare","stop"]); XCTAssertFalse(gate.busy)
    }
    private func settle(_ condition: () -> Bool) async {
        for _ in 0..<1000 { if condition() { return }; await Task.yield() }
        XCTFail("Injected state did not settle")
    }
    func testStartFailureAndRepeatedConcurrentStopRetainNativeClosureOwnership() async {
        let c=FakeCapture(), gate=AppOperationGate()
        c.startError=RecordingError(message:"source readiness failed")
        c.holdStop=true
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in},sleep:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        flow.countdown(seconds:1)
        await settle { c.stopWaiter != nil }
        XCTAssertTrue(gate.busy); XCTAssertEqual(flow.phase,.finalizing)
        let duplicate=Task { await flow.stop(reason:"Quit") }
        await Task.yield()
        XCTAssertEqual(c.calls.filter{$0 == "stop"}.count,1)
        c.stopWaiter?.resume(); c.stopWaiter=nil
        await duplicate.value
        XCTAssertEqual(flow.phase,.failed)
        XCTAssertTrue(flow.message.contains("source readiness failed"))
        XCTAssertFalse(gate.busy)
    }
    func testBoundedProgressPollingAllowsOnlyOneInFlightNativeRead() async {
        let c=FakeCapture(), gate=AppOperationGate()
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in},sleep:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        flow.countdown(seconds:1); await settle { flow.phase == .recording }
        c.holdProgress=true
        let first=Task { await flow.poll() }
        await settle { c.progressWaiter != nil }
        await flow.poll(); await flow.poll()
        XCTAssertEqual(c.progressCalls,1)
        c.progressWaiter?.resume(); c.progressWaiter=nil
        await first.value; await flow.stop()
    }
    func testPlaybackTimeClampsLargeValuesAndPreservesLateOffsetWithoutPreviewInput() {
        XCTAssertEqual(SourcePlaybackTime.sourceUs(Double(Int64.max)),Int64.max)
        XCTAssertEqual(SourcePlaybackTime.sourceUs(.nan),0)
        XCTAssertEqual(SourcePlaybackTime.filePTS(sourceUs:1_000_000,mediaOffsetUs:1_500_000,durationUs:2_500_000),0)
        XCTAssertEqual(SourcePlaybackTime.filePTS(sourceUs:2_000_000,mediaOffsetUs:1_500_000,durationUs:2_500_000),500_000)
        XCTAssertEqual(SourcePlaybackTime.filePTS(sourceUs:5_000_000,mediaOffsetUs:1_500_000,durationUs:2_500_000),2_500_000)
    }
    func testRecordedProjectRunAvailabilityRequiresExplicitDistinctMatchingCurrentMappings() throws {
        let fixture=try FlowFixture.load(), model=ClickerModel()
        model.mode="Project"; model.project=fixture.expectedProject; model.bundle=fixture.inspected
        model.displays=fixture.expectedProject.monitorSlots.map { slot in
            .init(id:slot.id,name:slot.name,geometry:slot.geometry,nativeBounds:.init(origin:slot.geometry.origin,size:slot.geometry.size))
        }
        XCTAssertFalse(model.canRun)
        model.mapping=["left":"left"]
        XCTAssertFalse(model.canRun)
        model.mapping=["left":"left","right":"left"]
        XCTAssertFalse(model.canRun)
        model.mapping=["left":"left","right":"right"]
        XCTAssertTrue(model.canRun)
        model.bundle=fixture.partial
        XCTAssertFalse(model.canRun)
    }
    func testQuitDuringSaveRetryWaitsForSaveCommit() async throws {
        let c=FakeCapture(), gate=AppOperationGate(), fixture=try FlowFixture.load()
        c.result=fixture.result; c.published=true
        var calls=0
        var saving: CheckedContinuation<Void,Never>?
        var quitAcknowledged=false
        let flow=RecordingFlow(gate:gate,makeCapture:{c},create:{_ in},save:{_,_ in
            calls += 1
            if calls == 1 { throw RecordingError(message:"disk full") }
            await withCheckedContinuation { saving=$0 }
            return fixture.inspected
        },sleep:{_ in})
        await flow.prepare(targets:[],destination:URL(filePath:"/tmp/test.motionclicker"))
        flow.countdown(seconds:1); await settle { flow.phase == .recording }
        await flow.stop()
        let retry=Task { await flow.retrySave() }
        await settle { saving != nil }
        let quit=Task { await flow.stop(reason:"Application closing"); quitAcknowledged=true }
        for _ in 0..<20 { await Task.yield() }
        XCTAssertFalse(quitAcknowledged)
        XCTAssertTrue(gate.busy); XCTAssertFalse(gate.acquireRun())
        saving?.resume(); saving=nil
        await retry.value; await quit.value
        XCTAssertTrue(quitAcknowledged); XCTAssertEqual(flow.phase,.saved); XCTAssertFalse(gate.busy)
    }
    func testControlRegionsConvertAppKitOriginAndReplaceSnapshot() {
        let regions = ControlRegions()
        regions.replace(appKitRects: [CGRect(x: -100, y: 100, width: 80, height: 40)], primaryHeight: 900)
        XCTAssertTrue(regions.contains(x: -90, y: 770))
        XCTAssertFalse(regions.contains(x: -90, y: 110))
        regions.replace(appKitRects: [], primaryHeight: 900)
        XCTAssertFalse(regions.contains(x: -90, y: 770))
    }
}

@MainActor final class FakeCapture: RecordingCapture {
    var calls: [String] = []
    var holdPreparation=false, holdStop=false, published=false
    var prepareWaiter: CheckedContinuation<Void,Never>?
    var stopWaiter: CheckedContinuation<Void,Never>?
    var prepareError: Error?
    var startError: Error?
    var holdProgress=false
    var progressCalls=0
    var progressWaiter: CheckedContinuation<Void,Never>?
    var result: RecordingResult?
    var progressState: RecordingState = .recording
    func prepare(targets: [RecordingTarget], root: URL, takeId: String) async throws { calls.append("prepare"); if holdPreparation { await withCheckedContinuation { prepareWaiter=$0 } }; if let prepareError { throw prepareError } }
    func start() async throws { calls.append("start"); if let startError { throw startError } }
    func stop(reason: String?) async throws -> Bool { calls.append("stop"); if holdStop { await withCheckedContinuation { stopWaiter=$0 } }; return published }
    func project() async throws -> RecordingResult { guard let result else { throw RecordingError(message:"No result") }; return result }
    func progress() async -> CaptureProgress { progressCalls += 1; if holdProgress { await withCheckedContinuation { progressWaiter=$0 } }; return .init(state: progressState, accepted: 0, dropped: 0, reason: nil) }
    func recovery() async -> StoreRecovery? { nil }
}

struct FlowFixture: Decodable {
    let manifest: RecordingManifest
    let expectedProject: MotionProject
    var result: RecordingResult { .init(manifest:manifest,take:expectedProject.takes[0],events:expectedProject.events) }
    var inspected: InspectedBundle { make(.requiresMonitorMapping) }
    var partial: InspectedBundle { make(.partial) }
    private func make(_ availability: RecordedBundleAvailability) -> InspectedBundle {
        let url=URL(filePath:"/tmp/generated")
        let snapshot=RecordedBundleSnapshot(project:expectedProject,manifest:manifest,manifestFile:url,eventsFile:url,gapsFile:url,media:[],availability:availability)
        return .init(snapshot:snapshot,evidence:[],mediaError:nil)
    }
    static func load() throws -> Self {
        let root=URL(filePath:#filePath).deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent().deletingLastPathComponent()
        return try JSONDecoder().decode(Self.self,from:Data(contentsOf:root.appendingPathComponent("contracts/fixtures/recording/bundle.json")))
    }
}
