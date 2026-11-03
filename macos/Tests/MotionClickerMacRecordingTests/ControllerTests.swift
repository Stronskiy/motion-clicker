import XCTest
import CoreVideo
import MotionClickerRecording
@testable import MotionClickerMacRecording

// Only the callback reference is shared; its state is protected by the lock.
final class FakeCaptureDriver: CaptureDriving, @unchecked Sendable {
    private let lock = NSLock()
    private var sink: (any CaptureSink)?
    private var started = false
    let acknowledgeStop: Bool
    init(acknowledgeStop: Bool = true) { self.acknowledgeStop = acknowledgeStop }
    func connect(_ sink: any CaptureSink) { lock.withLock { self.sink = sink } }
    func start() { lock.withLock { started = true } }
    func stop() { if acknowledgeStop { lock.withLock { sink }?.captureStopped(generation: 1, error: nil) } }
    var isStarted: Bool { lock.withLock { started } }
    func ready() {
        let sink = lock.withLock { self.sink }
        sink?.sourceReady("a", generation: 1); sink?.tapReady(generation: 1); sink?.sourceReady("b", generation: 1)
    }
    func earlyStaticReady(_ pixel: CVPixelBuffer) {
        let sink = lock.withLock { self.sink }
        sink?.sourceReady("a", generation: 1)
        sink?.frame("a", sourceUs: 999_999, status: .complete, pixel: pixel, generation: 1)
        sink?.tapReady(generation: 1); sink?.sourceReady("b", generation: 1)
        sink?.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        sink?.frame("a", sourceUs: 1_000_000, status: .idle, pixel: nil, generation: 1)
    }
    func frames(_ pixel: CVPixelBuffer) {
        let sink = lock.withLock { self.sink }
        sink?.frame("a", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
        sink?.frame("b", sourceUs: 1_000_000, status: .complete, pixel: pixel, generation: 1)
    }
    func fail() { lock.withLock { sink }?.failure("a", generation: 1, reason: "Synthetic source loss") }
}
@MainActor final class ControllerTests: XCTestCase {
    func setup(timeout: Double = 0.1, permissionError: Bool = false, acknowledgeStop: Bool = true, realEncoder: Bool = false, hangingEncoder: Bool = false, verifiedPointerClock: Bool = true) throws -> (MacRecordingController, FakeCaptureDriver, URL) {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: false)
        let driver = FakeCaptureDriver(acknowledgeStop: acknowledgeStop)
        let inventory = engineTargets().map { NativeDisplaySnapshot(id: UInt32($0.display.id)!, geometry: $0.display.geometry, bounds: $0.display.nativeBounds, pixelWidth: 64, pixelHeight: 64) }
        let dependencies = RecordingDependencies(inventory: { inventory }, preflight: { if permissionError { throw MacRecordingFailure.screenRecordingPermissionMissing } }, now: { 1_000_000 }, writer: { url, w, h in if realEncoder { return try NativeVideoWriter(url: url, width: w, height: h) }; return EngineWriter(url: url, hanging: hangingEncoder) }, driver: { _, _, sink, _, _ in driver.connect(sink); return driver }, timeoutSeconds: timeout, pointerClock: verifiedPointerClock ? syntheticPointerClock : nil)
        return (MacRecordingController(options: MacRecordingOptions(ownInjectedTag: 42, isControl: { _ in false }), dependencies: dependencies), driver, dir)
    }
    func waitStarted(_ driver: FakeCaptureDriver) async throws {
        for _ in 0..<200 {
            if driver.isStarted { return }; try await Task.sleep(for: .milliseconds(1))
        }
        XCTFail("Fake start was not invoked")
    }
    func testUnverifiedClockRejectsPreparationBeforeDriverOrStorage() async throws {
        let (c, d, dir) = try setup(verifiedPointerClock: false); defer { try? FileManager.default.removeItem(at: dir) }
        do { _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take"); XCTFail("Unverified pointer source admitted") }
        catch { XCTAssertEqual(error as? MacRecordingFailure, .pointerClockUnverified) }
        XCTAssertFalse(d.isStarted)
        let recovery = await c.recovery(); XCTAssertNil(recovery)
        XCTAssertEqual(try FileManager.default.contentsOfDirectory(atPath: dir.path), [])
    }
    func testEarlyStaticSourceWithOnlyPreEpochImageFailsWithinInitializationDeadline() async throws {
        let (c, d, dir) = try setup(timeout: 0.02); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }; try await waitStarted(d)
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        d.earlyStaticReady(try XCTUnwrap(pixel)); try await start.value
        try await Task.sleep(for: .milliseconds(60))
        let progress = await c.snapshot()
        XCTAssertEqual(progress.state, .interrupted)
        XCTAssertTrue(progress.reason?.contains("post-epoch image") == true)
        XCTAssertTrue(progress.reason?.contains("a") == true)
        let stopped = try await c.stop()
        XCTAssertEqual(stopped?.manifest.streams.first?.frameCount, 0)
        XCTAssertNil(stopped?.manifest.streams.first?.mediaTimeZeroUs)
    }
    func testPostEpochImagesCancelInitializationDeadline() async throws {
        let (c, d, dir) = try setup(timeout: 0.05); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }; try await waitStarted(d); d.ready(); try await start.value
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        d.frames(try XCTUnwrap(pixel))
        try await Task.sleep(for: .milliseconds(120))
        let progress = await c.snapshot(); XCTAssertEqual(progress.state, .recording)
        let stopped = try await c.stop(); XCTAssertEqual(stopped?.manifest.state, .completed)
    }
    func testShutdownTimeoutCannotPublishCompletedEvenWhenWritersFinished() async throws {
        let (c, d, dir) = try setup(timeout: 0.05, acknowledgeStop: false); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }; try await waitStarted(d); d.ready(); try await start.value
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        d.frames(try XCTUnwrap(pixel))
        let stopped = try await c.stop()
        XCTAssertEqual(stopped?.manifest.state, .interrupted)
        XCTAssertTrue(stopped?.manifest.reason?.contains("shutdown") == true)
    }
    func testSyntheticNativeEncodePublishesAndProjectsThroughActualStore() async throws {
        let (c, d, dir) = try setup(timeout: 2, realEncoder: true); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }; try await waitStarted(d); d.ready(); try await start.value
        var pixel: CVPixelBuffer?
        XCTAssertEqual(CVPixelBufferCreate(nil, 64, 64, kCVPixelFormatType_32BGRA, nil, &pixel), kCVReturnSuccess)
        let buffer = try XCTUnwrap(pixel)
        CVPixelBufferLockBaseAddress(buffer, []); memset(CVPixelBufferGetBaseAddress(buffer), 0, CVPixelBufferGetBytesPerRow(buffer) * 64); CVPixelBufferUnlockBaseAddress(buffer, [])
        d.frames(buffer)
        let completion = try await c.stop()
        XCTAssertEqual(completion?.manifest.state, .completed)
        XCTAssertEqual(completion?.manifest.streams.map(\.frameCount), [1, 1])
        let result = try await c.project()
        XCTAssertEqual(result.take.media.count, 2)
        for media in result.take.media {
            let data = try Data(contentsOf: dir.appendingPathComponent(media.relativePath))
            XCTAssertGreaterThan(data.count, 100)
        }
        let manifest = try JSONDecoder().decode(RecordingManifest.self, from: Data(contentsOf: try XCTUnwrap(completion?.manifestURL)))
        XCTAssertEqual(manifest, completion?.manifest)
    }
    func testHungWritersAreCancelledAndReportedFailedOnce() async throws {
        let (c, d, dir) = try setup(timeout: 0.03, hangingEncoder: true); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }; try await waitStarted(d); d.ready(); try await start.value
        do { _ = try await c.stop(); XCTFail("Timed-out encoder published media whose closure is unknown") } catch {}
        XCTAssertFalse(FileManager.default.fileExists(atPath: dir.appendingPathComponent("takes/take/manifest.json").path))
        let recovery = await c.recovery(); XCTAssertNotNil(recovery)
        let progress = await c.snapshot(); XCTAssertTrue(progress.reason?.contains("timed out") == true)
    }
    func testPreparedControllerCanDeallocateWithoutCaptureRetainCycle() async throws {
        var controller: MacRecordingController?
        let driver: FakeCaptureDriver, dir: URL
        (controller, driver, dir) = try setup()
        defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await controller!.prepare(targets: engineTargets(), root: dir, takeId: "take")
        weak let reference = controller
        controller = nil
        for _ in 0..<100 { if reference == nil { break }; try await Task.sleep(for: .milliseconds(1)) }
        XCTAssertNil(reference)
        XCTAssertFalse(driver.isStarted)
    }
    func testAsyncStopDuringStartResolvesBothAndRejectsLateCallbacks() async throws {
        let (c, d, dir) = try setup(); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }
        try await waitStarted(d)
        let stopped = try await c.stop()
        XCTAssertNil(stopped)
        do { try await start.value; XCTFail("Cancelled start succeeded") } catch {}
        d.ready()
        let state = await c.snapshot(); XCTAssertEqual(state.state, .failed)
        let repeated = try await c.stop(); XCTAssertNil(repeated)
    }
    func testAllSourcesReadyThenFailurePublishesHonestZeroFrameManifest() async throws {
        let (c, d, dir) = try setup(timeout: 1); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        let start = Task { try await c.start() }
        try await waitStarted(d); d.ready(); try await start.value
        d.fail()
        let result = try await c.stop()
        let completion = try XCTUnwrap(result)
        XCTAssertEqual(completion.manifest.state, .interrupted)
        XCTAssertEqual(completion.manifest.streams.map(\.writerStatus), [.failed, .failed])
        XCTAssertTrue(FileManager.default.fileExists(atPath: completion.manifestURL.path))
        let again = try await c.stop(); XCTAssertEqual(again?.manifest, completion.manifest)
        let project = try await c.project(); XCTAssertTrue(project.take.media.isEmpty)
    }
    func testPreparationPreflightFailsSpecificallyWithoutStartingDriver() async throws {
        let (c, d, dir) = try setup(permissionError: true); defer { try? FileManager.default.removeItem(at: dir) }
        do { _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take"); XCTFail("Missing permission accepted") }
        catch { XCTAssertEqual(error as? MacRecordingFailure, .screenRecordingPermissionMissing) }
        XCTAssertFalse(d.isStarted)
    }
    func testStartTimeoutResolvesWithoutMainRunLoopCallbacks() async throws {
        let (c, _, dir) = try setup(timeout: 0.02); defer { try? FileManager.default.removeItem(at: dir) }
        _ = try await c.prepare(targets: engineTargets(), root: dir, takeId: "take")
        do { try await c.start(); XCTFail("Start without sources succeeded") } catch {}
        let state = await c.snapshot(); XCTAssertEqual(state.state, .failed)
    }
}
