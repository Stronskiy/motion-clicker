import Foundation
import XCTest
@testable import MotionClickerCore

final class ProjectCoreTests: XCTestCase {
    private let fixtures = URL(fileURLWithPath: #filePath)
        .deletingLastPathComponent().deletingLastPathComponent()
        .deletingLastPathComponent().deletingLastPathComponent()
        .appendingPathComponent("contracts/fixtures")

    private func load<T: Decodable>(_ name: String, as type: T.Type = T.self) throws -> T {
        try JSONDecoder().decode(type, from: Data(contentsOf: fixtures.appendingPathComponent(name)))
    }

    private func sample() -> MotionProject {
        MotionProject(id: "project-one", monitorSlots: [
            MonitorSlot(id: "main", name: "Main", geometry: DisplayGeometry(
                origin: Point(x: -1920, y: -200), size: Size(width: 1920, height: 1080),
                scaleFactor: 2, rotationDegrees: 0))
        ], takes: [Take(id: "take-one", durationUs: 1000, monitorSlotIds: ["main"])],
        tracks: [Track(id: "track-one", name: "Main", monitorSlotId: "main", order: 0)],
        clips: [TimelineClip(id: "clip-one", trackId: "track-one", takeId: "take-one",
                             sourceInUs: 0, sourceOutUs: 1000, timelineStartUs: 0)],
        events: [move("move-one", time: 100)])
    }

    private func move(_ id: String, time: Int64, order: Int = 0) -> RecordedAction {
        RecordedAction(id: id, takeId: "take-one", monitorSlotId: "main", timeUs: time,
                       order: order, kind: .move, x: 100, y: 150)
    }

    private func clickGroup() -> [RecordedAction] {
        [RecordedAction(id: "down", takeId: "take-one", monitorSlotId: "main", timeUs: 100,
                        order: 0, kind: .buttonDown, x: 100, y: 150, button: .left, atomicGroupId: "click"),
         RecordedAction(id: "up", takeId: "take-one", monitorSlotId: "main", timeUs: 200,
                        order: 1, kind: .buttonUp, x: 120, y: 170, button: .left, atomicGroupId: "click")]
    }

    private func expectError(_ project: MotionProject, _ code: ProjectError.Code,
                             path: String? = nil, file: StaticString = #filePath, line: UInt = #line) {
        XCTAssertThrowsError(try RunPlanCompiler.compile(project), file: file, line: line) { error in
            guard let error = error as? ProjectError else {
                return XCTFail("Expected ProjectError, got \(error)", file: file, line: line)
            }
            XCTAssertEqual(error.code, code, file: file, line: line)
            if let path { XCTAssertEqual(error.path, path, file: file, line: line) }
        }
    }

    // Break caught: relying on source array order instead of the full stable sort tuple.
    func testEqualTimeOrderingMatchesGoldenDespiteShuffledInputs() throws {
        var project: MotionProject = try load("two-track-tie.project.json")
        let expected: [PlannedAction] = try load("two-track-tie.plan.json")
        XCTAssertEqual(try RunPlanCompiler.compile(project), expected)
        project.events.reverse(); project.tracks.reverse(); project.clips.reverse()
        XCTAssertEqual(try RunPlanCompiler.compile(project), expected)
    }

    // Break caught: inclusive sourceOut, missing offset, or changing source timestamps.
    func testLinkedHalfOpenClipRangesMatchGolden() throws {
        let project: MotionProject = try load("linked-clip-boundary.project.json")
        let expected: [PlannedAction] = try load("linked-clip-boundary.plan.json")
        XCTAssertEqual(try RunPlanCompiler.compile(project), expected)
    }

    func testEmptyProjectGoldenProducesEmptyPlan() throws {
        let project: MotionProject = try load("empty.project.json")
        let expected: [PlannedAction] = try load("empty.plan.json")
        XCTAssertEqual(try RunPlanCompiler.compile(project), expected)
    }

    func testSharedConformanceManifestLoadsEveryProjectCase() throws {
        struct Case: Decodable { let input: String; let expectedPlan: String?; let expectedError: String? }
        struct Manifest: Decodable { let projectCases: [Case] }
        struct Failure: Decodable { let code: ProjectError.Code; let path: String }
        let manifest: Manifest = try load("cases.json")
        for entry in manifest.projectCases {
            let project: MotionProject = try load(entry.input)
            if let file = entry.expectedPlan {
                let expected: [PlannedAction] = try load(file)
                XCTAssertEqual(try RunPlanCompiler.compile(project), expected, entry.input)
            } else {
                let file = try XCTUnwrap(entry.expectedError)
                let expected: Failure = try load(file)
                expectError(project, expected.code, path: expected.path)
            }
        }
    }

    func testMissingMonitorGoldenFailsBeforePartialPlan() throws {
        struct Failure: Decodable { let code: ProjectError.Code; let path: String }
        let project: MotionProject = try load("missing-monitor.project.json")
        let failure: Failure = try load("missing-monitor.error.json")
        expectError(project, failure.code, path: failure.path)
    }

    func testCodableRoundTripRetainsExecutableMeaning() throws {
        let original: MotionProject = try load("two-track-tie.project.json")
        let decoded = try JSONDecoder().decode(MotionProject.self, from: JSONEncoder().encode(original))
        XCTAssertEqual(decoded, original)
        let expected: [PlannedAction] = try load("two-track-tie.plan.json")
        XCTAssertEqual(try RunPlanCompiler.compile(decoded), expected)
    }

    func testUnsupportedVersionIsRejectedEvenWhenEmpty() {
        expectError(MotionProject(schemaVersion: 2, id: "future"), .unsupportedVersion, path: "schemaVersion")
    }

    func testDuplicateIDsAreRejectedAcrossEntityKinds() {
        var p = sample(); p.tracks[0].id = p.takes[0].id
        expectError(p, .duplicateID, path: "tracks[0].id")
    }

    func testMalformedIDsAreRejected() {
        for id in ["", "a:b", "é", String(repeating: "x", count: 129)] {
            var p = sample(); p.id = id
            expectError(p, .invalidID, path: "id")
        }
    }

    func testAllNegativeTimesAndOrdersAreRejected() {
        var p = sample(); p.events[0].timeUs = -1; expectError(p, .invalidRange)
        p = sample(); p.events[0].order = -1; expectError(p, .invalidRange)
        p = sample(); p.clips[0].sourceInUs = -1; expectError(p, .invalidRange)
        p = sample(); p.clips[0].timelineStartUs = -1; expectError(p, .invalidRange)
        p = sample(); p.revision = -1; expectError(p, .invalidRange)
        p = sample(); p.tracks[0].order = -1; expectError(p, .invalidRange)
    }

    func testSourceRangesAndTakeDurationsMustBePositiveAndContained() {
        var p = sample(); p.takes[0].durationUs = 0; expectError(p, .invalidRange)
        p = sample(); p.clips[0].sourceOutUs = 0; expectError(p, .invalidRange)
        p = sample(); p.clips[0].sourceInUs = 500; p.clips[0].sourceOutUs = 400
        expectError(p, .invalidRange)
        p = sample(); p.clips[0].sourceOutUs = 1001; expectError(p, .invalidRange)
        p = sample(); p.events[0].timeUs = 1000; expectError(p, .invalidRange)
    }

    func testTimelineOverflowIsRejectedRatherThanTrapping() {
        var p = sample(); p.clips[0].timelineStartUs = Int64.max - 500
        expectError(p, .invalidRange)
    }

    func testInt64MaximumRangeMapsExactlyWithoutOverflow() throws {
        var p = sample(); p.takes[0].durationUs = Int64.max
        p.clips[0].sourceInUs = Int64.max - 100
        p.clips[0].sourceOutUs = Int64.max
        p.clips[0].timelineStartUs = Int64.max - 100
        p.events[0].timeUs = Int64.max - 1
        XCTAssertEqual(try RunPlanCompiler.compile(p).first?.timeUs, Int64.max - 1)
        p.clips[0].timelineStartUs += 1; expectError(p, .invalidRange)
        p.clips[0].sourceInUs = Int64.min; expectError(p, .invalidRange)
    }

    func testRepeatedSourceEventHasDistinctDeterministicallyOrderedOccurrenceIDs() throws {
        var p = sample(); var clone = p.clips[0]; clone.id = "clone"; p.clips.insert(clone, at: 0)
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.map(\.id), ["clip-one:move-one", "clone:move-one"])
        XCTAssertEqual(plan.map(\.sourceEventId), ["move-one", "move-one"])
        XCTAssertEqual(plan.map(\.timeUs), [100, 100])
    }

    func testTimesRemainExactAboveJavaScriptSafeIntegerRange() throws {
        var p = sample()
        p.clips[0].timelineStartUs = 9_007_199_254_740_992
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.first?.timeUs, 9_007_199_254_741_092)
        let decoded = try JSONDecoder().decode(MotionProject.self, from: JSONEncoder().encode(p))
        XCTAssertEqual(decoded.clips[0].timelineStartUs, 9_007_199_254_740_992)
    }

    func testOnlyOneTimesSpeedIsSupported() {
        for speed in [0.0, -1, 2, Double.nan, Double.infinity] {
            var p = sample(); p.clips[0].speed = speed; expectError(p, .invalidRange)
        }
    }

    func testAllReferencesAreValidatedIncludingUnusedEvents() {
        var p = sample(); p.clips[0].takeId = "missing"; expectError(p, .missingReference)
        p = sample(); p.clips[0].trackId = "missing"; expectError(p, .missingReference)
        p = sample(); p.events[0].takeId = "missing"; p.clips = []; expectError(p, .missingReference)
        p = sample(); p.events[0].monitorSlotId = "missing"; expectError(p, .missingReference)
        p = sample(); p.takes[0].monitorSlotIds = ["missing"]; expectError(p, .missingReference)
    }

    func testTakeMonitorMembershipIsRequiredForEventsAndClips() {
        var p = sample()
        p.monitorSlots.append(MonitorSlot(id: "other", name: "Other", geometry: p.monitorSlots[0].geometry))
        p.events[0].monitorSlotId = "other"; expectError(p, .missingReference)
        p.events[0].monitorSlotId = "main"; p.tracks[0].monitorSlotId = "other"
        expectError(p, .missingReference)
        p = sample(); p.takes[0].monitorSlotIds = []; expectError(p, .invalidRange)
        p = sample(); p.takes[0].monitorSlotIds = ["main", "main"]; expectError(p, .duplicateID)
    }

    func testCoordinatesMustBeFiniteAndInsideHalfOpenMonitorBounds() {
        for point in [Point(x: -1, y: 1), Point(x: 1920, y: 1), Point(x: 1, y: 1080),
                      Point(x: .nan, y: 1), Point(x: 1, y: .infinity)] {
            var p = sample(); p.events[0].x = point.x; p.events[0].y = point.y
            expectError(p, .invalidCoordinate)
        }
    }

    func testMonitorGeometryMustBeFinitePositiveAndSupported() {
        var p = sample(); p.monitorSlots[0].geometry.size.width = 0; expectError(p, .invalidGeometry)
        p = sample(); p.monitorSlots[0].geometry.origin.x = .infinity; expectError(p, .invalidGeometry)
        p = sample(); p.monitorSlots[0].geometry.scaleFactor = -1; expectError(p, .invalidGeometry)
        p = sample(); p.monitorSlots[0].geometry.rotationDegrees = 45; expectError(p, .invalidGeometry)
        p = sample(); p.monitorSlots[0].geometry.origin.x = .greatestFiniteMagnitude
        p.monitorSlots[0].geometry.size.width = .greatestFiniteMagnitude; expectError(p, .invalidGeometry)
    }

    func testActionDiscriminantsRequireOnlyTheirOwnFields() throws {
        var p = sample(); p.events[0].kind = .buttonDown; expectError(p, .invalidAction)
        p = sample(); p.events[0].button = .left; expectError(p, .invalidAction)
        p = sample(); p.events[0].deltaX = 1; expectError(p, .invalidAction)
        p = sample(); p.events[0].kind = .scroll; expectError(p, .invalidAction)
        p.events[0].deltaX = 1; p.events[0].deltaY = .infinity; expectError(p, .invalidAction)
        p.events[0].deltaY = -2
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.first?.kind, .scroll)
        XCTAssertEqual(plan.first?.x, 100)
        XCTAssertEqual(plan.first?.deltaX, 1)
        XCTAssertEqual(plan.first?.deltaY, -2)
    }

    func testBalancedAtomicGroupPreservesRawButtonsAndCloneIdentity() throws {
        var p = sample(); p.events = clickGroup()
        var dragMove = move("drag-move", time: 150); dragMove.atomicGroupId = "click"
        p.events.append(dragMove)
        var clone = p.clips[0]; clone.id = "clone"; clone.timelineStartUs = 1000; p.clips.append(clone)
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.map(\.sourceEventId), ["down", "drag-move", "up", "down", "drag-move", "up"])
        XCTAssertEqual(plan.map(\.id), ["clip-one:down", "clip-one:drag-move", "clip-one:up",
                                       "clone:down", "clone:drag-move", "clone:up"])
        XCTAssertEqual(plan.map(\.atomicGroupId), ["clip-one:click", "clip-one:click", "clip-one:click",
                                                 "clone:click", "clone:click", "clone:click"])
        XCTAssertEqual(plan.first?.button, .left)
    }

    func testIncompleteMismatchedOrReversedButtonGroupsAreRejected() {
        var p = sample(); p.events = [clickGroup()[0]]; expectError(p, .invalidAtomicGroup)
        p.events = clickGroup(); p.events[1].button = .right; expectError(p, .invalidAtomicGroup)
        p.events = clickGroup(); p.events[1].timeUs = 99; expectError(p, .invalidAtomicGroup)
        p.events = clickGroup(); p.events.append(p.events[1]); p.events[2].id = "up-again"
        expectError(p, .invalidAtomicGroup)
        p.events = clickGroup(); p.events[0].atomicGroupId = "bad:group"; expectError(p, .invalidID)
        p.events = [move("orphan-move", time: 150)]; p.events[0].atomicGroupId = "orphan"
        expectError(p, .invalidAtomicGroup)
    }

    func testAtomicGroupMustRemainInsideOneTake() {
        var p = sample(); p.events = clickGroup()
        p.takes.append(Take(id: "take-two", durationUs: 1000, monitorSlotIds: ["main"]))
        p.events[1].takeId = "take-two"; expectError(p, .invalidAtomicGroup)
    }

    func testLinkedClipsPreserveCrossDisplayDrag() throws {
        var p = sample(); p.events = clickGroup()
        p.monitorSlots.append(MonitorSlot(id: "other", name: "Other", geometry: p.monitorSlots[0].geometry))
        p.takes[0].monitorSlotIds.append("other"); p.events[1].monitorSlotId = "other"
        p.tracks.append(Track(id: "track-other", name: "Other", monitorSlotId: "other", order: 1))
        p.clips[0].linkGroupId = "linked"
        var linked = p.clips[0]; linked.id = "clip-other"; linked.trackId = "track-other"; p.clips.append(linked)
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.map(\.monitorSlotId), ["main", "other"])
        XCTAssertEqual(plan.map(\.atomicGroupId), ["link:linked:click", "link:linked:click"])
        p.clips.removeLast(); expectError(p, .atomicGroupSplit)
    }

    func testLinkedClipsRequireIdenticalSourceAndTimelineAndDistinctMonitors() {
        var p = sample(); p.clips[0].linkGroupId = "linked"
        var linked = p.clips[0]; linked.id = "clip-other"; linked.timelineStartUs = 1; p.clips.append(linked)
        expectError(p, .invalidRange)
        p.clips[1].timelineStartUs = 0; expectError(p, .invalidRange)
    }

    func testGroupMovesMustStayBetweenDownAndUp() {
        var p = sample(); p.events = clickGroup()
        var movement = move("drag-move", time: 99); movement.atomicGroupId = "click"; p.events.append(movement)
        expectError(p, .invalidAtomicGroup)
    }

    func testEqualTimeSourceButtonOrderMustBeBalanced() throws {
        var p = sample(); p.events = clickGroup(); p.events[1].timeUs = 100
        let plan = try RunPlanCompiler.compile(p)
        XCTAssertEqual(plan.map(\.kind), [.buttonDown, .buttonUp])
        p.events[0].order = 2; expectError(p, .invalidAtomicGroup)
    }

    func testHalfOpenTrimCannotKeepOnlyHalfOfAnAtomicGroup() {
        var p = sample(); p.events = clickGroup(); p.clips[0].sourceOutUs = 200
        expectError(p, .atomicGroupSplit)
        p.clips[0].sourceOutUs = 1000; p.clips[0].sourceInUs = 150; expectError(p, .atomicGroupSplit)
        p.clips[0].sourceOutUs = 180; expectError(p, .atomicGroupSplit)
    }

    func testDisjointTrimOmitsWholeAtomicGroup() throws {
        var p = sample(); p.events = clickGroup(); p.clips[0].sourceInUs = 201
        XCTAssertEqual(try RunPlanCompiler.compile(p), [])
    }

    func testMouseHoldConflictsAcrossClipsAreRejected() {
        var p = sample(); p.events = clickGroup()
        var clone = p.clips[0]; clone.id = "overlap"; clone.timelineStartUs = 50; p.clips.append(clone)
        expectError(p, .mouseConflict)
        p.clips.removeLast(); p.events.append(move("unrelated", time: 150)); expectError(p, .mouseConflict)
    }

    func testMediaPathsAreLexicallyContainedWithoutOpeningFiles() throws {
        for path in ["/tmp/video.mp4", "../video.mp4", "media/../video.mp4", "media//v.mp4",
                     "media/./v.mp4", "C:\\v.mp4", "media/%2e%2e/v.mp4", "https://x/v.mp4"] {
            var p = sample(); p.takes[0].media = [TakeMedia(monitorSlotId: "main", relativePath: path)]
            expectError(p, .unsafeMediaPath)
        }
        var p = sample(); p.takes[0].media = [TakeMedia(monitorSlotId: "main", relativePath: "media/take-one/main.mp4")]
        XCTAssertEqual(try RunPlanCompiler.compile(p).count, 1)
        p.takes[0].media[0].monitorSlotId = "missing"; expectError(p, .missingReference)
        p = sample(); let media = TakeMedia(monitorSlotId: "main", relativePath: "media/main.mp4")
        p.takes[0].media = [media, media]; expectError(p, .duplicateID)
    }

    func testUnknownActionKindAndMissingRequiredFieldsCannotDecode() throws {
        let data = try JSONEncoder().encode(sample())
        let raw = String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\"move\"", with: "\"teleport\"")
        XCTAssertThrowsError(try JSONDecoder().decode(MotionProject.self, from: Data(raw.utf8)))
        XCTAssertThrowsError(try JSONDecoder().decode(MotionProject.self, from: Data("{\"schemaVersion\":1}".utf8)))
    }

    func testNegativeOriginAndLetterboxingGoldenRoundTrip() throws {
        struct GeometryFixture: Decodable {
            let sourceSize: Size; let displayOrigin: Point; let viewport: Rect; let local: Point
            let expectedDesktop: Point; let expectedPreview: Point; let expectedFit: Rect; let letterboxPoint: Point
        }
        let f: GeometryFixture = try load("negative-origin.geometry.json")
        XCTAssertEqual(try CoordinateMapper.desktopPoint(local: f.local, displayOrigin: f.displayOrigin), f.expectedDesktop)
        XCTAssertEqual(try CoordinateMapper.aspectFitRect(sourceSize: f.sourceSize, viewport: f.viewport), f.expectedFit)
        let preview = try CoordinateMapper.previewPoint(local: f.local, sourceSize: f.sourceSize, viewport: f.viewport)
        XCTAssertEqual(preview, f.expectedPreview)
        XCTAssertEqual(try CoordinateMapper.localPoint(preview: preview, sourceSize: f.sourceSize, viewport: f.viewport), f.local)
        XCTAssertThrowsError(try CoordinateMapper.localPoint(preview: f.letterboxPoint, sourceSize: f.sourceSize, viewport: f.viewport))
    }

    func testPreviewTransformsRejectBadGeometryAndOutsidePoints() {
        let size = Size(width: 100, height: 200)
        let viewport = Rect(origin: Point(x: 0, y: 0), size: Size(width: 200, height: 100))
        XCTAssertThrowsError(try CoordinateMapper.aspectFitRect(sourceSize: Size(width: 0, height: 1), viewport: viewport))
        XCTAssertThrowsError(try CoordinateMapper.aspectFitRect(sourceSize: size, viewport: Rect(origin: Point(x: .nan, y: 0), size: size)))
        XCTAssertThrowsError(try CoordinateMapper.previewPoint(local: Point(x: 100, y: 0), sourceSize: size, viewport: viewport))
        XCTAssertThrowsError(try CoordinateMapper.localPoint(preview: Point(x: 125, y: 50), sourceSize: size, viewport: viewport))
        XCTAssertThrowsError(try CoordinateMapper.desktopPoint(local: Point(x: .infinity, y: 0), displayOrigin: Point(x: 0, y: 0)))
        XCTAssertThrowsError(try CoordinateMapper.desktopPoint(local: Point(x: .greatestFiniteMagnitude, y: 0), displayOrigin: Point(x: .greatestFiniteMagnitude, y: 0)))
    }

    func testPortraitAspectFitHandlesHorizontalLetterboxing() throws {
        let size = Size(width: 100, height: 200)
        let viewport = Rect(origin: Point(x: -10, y: 30), size: Size(width: 200, height: 100))
        XCTAssertEqual(try CoordinateMapper.aspectFitRect(sourceSize: size, viewport: viewport),
                       Rect(origin: Point(x: 65, y: 30), size: Size(width: 50, height: 100)))
        XCTAssertEqual(try CoordinateMapper.previewPoint(local: Point(x: 0, y: 0), sourceSize: size, viewport: viewport), Point(x: 65, y: 30))
    }
}
