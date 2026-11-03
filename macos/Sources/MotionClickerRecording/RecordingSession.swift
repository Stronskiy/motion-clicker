import Foundation
import MotionClickerCore
import MotionClickerRuntime

/// Caller-confined to one serial owner. Never pass directly across native callback queues.
public final class RecordingSession {
    private struct Stream {
        var clock: ClockAnchor?
        var zero: Int64?
        var count: Int64 = 0
        var gapCount: Int64 = 0
        var end: Int64 = 0
        var lastTicks: Int64?
        var gaps: [RecordingGap] = []
        var terminal: Terminal?
        var sourceFailure: String?
    }
    private struct Terminal: Equatable { let status: WriterStatus; let duration: Int64; let reason: String? }
    public private(set) var state: RecordingState = .preparing
    public private(set) var result: RecordingManifest?
    private let takeId: String, targets: [RecordingTarget], journal: EventJournal
    private var streams: [String: Stream]
    private var tapReady = false, epoch: Int64 = 0, duration: Int64 = 1, count: Int64 = 0
    private var lastHost: Int64?, heldGroup: String?, heldButton: MouseButton?
    private var incomplete = Set<String>(), warnings: [String] = []
    private var terminalState: RecordingState = .completed, reason: String?, journalFailed = false

    public init(takeId: String, targets: [RecordingTarget], journal: EventJournal) throws {
        try recordingRequire(!targets.isEmpty, "No recording targets")
        _ = try RunPlanCompiler.compile(MotionProject(id: takeId, monitorSlots: targets.map(\.slot)))
        var displayIds = Set<String>()
        for (i, t) in targets.enumerated() {
            try recordingRequire(displayIds.insert(t.display.id).inserted, "Duplicate runtime display")
            try recordingRequire(t.slot.geometry == t.display.geometry, "Snapshot geometry mismatch")
            let b = t.display.nativeBounds
            try recordingRequire(Self.valid(b), "Invalid native bounds")
            for previous in targets.prefix(i) {
                let a = previous.display.nativeBounds
                try recordingRequire(!(a.origin.x < b.origin.x + b.size.width && b.origin.x < a.origin.x + a.size.width && a.origin.y < b.origin.y + b.size.height && b.origin.y < a.origin.y + a.size.height), "Overlapping native targets")
            }
        }
        self.takeId = takeId; self.targets = targets; self.journal = journal
        streams = Dictionary(uniqueKeysWithValues: targets.map { ($0.slot.id, Stream()) })
    }
    private static func valid(_ r: Rect) -> Bool {
        r.origin.x.isFinite && r.origin.y.isFinite && r.size.width.isFinite && r.size.height.isFinite && r.size.width > 0 && r.size.height > 0 && (r.origin.x + r.size.width).isFinite && (r.origin.y + r.size.height).isFinite
    }
    private static func contains(_ p: Point, _ r: Rect) -> Bool {
        p.x >= r.origin.x && p.y >= r.origin.y && p.x < r.origin.x + r.size.width && p.y < r.origin.y + r.size.height
    }
    private func stream(_ id: String) throws -> Stream {
        guard let stream = streams[id] else { throw RecordingError(message: "Unknown monitor source") }; return stream
    }
    private func mapper(_ s: Stream) throws -> MediaClockMapper {
        guard let c = s.clock else { throw RecordingError(message: "Source not ready") }
        return try MediaClockMapper(sourceAnchorTicks: c.sourceAnchorTicks, hostAnchorUs: c.hostAnchorUs, ticksPerSecond: c.ticksPerSecond, epochHostUs: epoch)
    }
    public func sourceReady(monitorSlotId: String, clock: ClockAnchor) throws {
        try recordingRequire(state == .preparing, "Source readiness after preparation")
        var s = try stream(monitorSlotId); try recordingRequire(s.clock == nil, "Duplicate source readiness")
        _ = try MediaClockMapper(sourceAnchorTicks: clock.sourceAnchorTicks, hostAnchorUs: clock.hostAnchorUs, ticksPerSecond: clock.ticksPerSecond, epochHostUs: clock.hostAnchorUs)
        s.clock = clock; streams[monitorSlotId] = s
    }
    public func eventTapReady() throws { try recordingRequire(state == .preparing, "Tap readiness after preparation"); tapReady = true }
    public func begin(epochHostUs: Int64) throws {
        try recordingRequire(state == .preparing && tapReady && streams.values.allSatisfy { $0.clock != nil }, "Required sources are not ready")
        for s in streams.values { let c = s.clock!; _ = try MediaClockMapper(sourceAnchorTicks: c.sourceAnchorTicks, hostAnchorUs: c.hostAnchorUs, ticksPerSecond: c.ticksPerSecond, epochHostUs: epochHostUs) }
        epoch = epochHostUs; state = .recording
    }
    public func preparationFailed(reason: String) {
        if state == .preparing { self.reason = reason; state = .failed }
    }
    private func interrupt(_ message: String, failed: Bool = false) {
        if failed { terminalState = .failed } else if terminalState == .completed { terminalState = .interrupted }
        if reason == nil { reason = message }
        if let group = heldGroup { incomplete.insert(group) }
        if state == .recording { state = .finalizing }
    }
    private func checkedRecording(_ body: () throws -> Void) throws {
        do { try body() } catch { interrupt(String(describing: error), failed: true); throw error }
    }
    public func receive(event: CapturedPointerEvent) throws {
        guard state == .recording, !event.isOwnInjected, !event.isControl else { return }
        try checkedRecording {
            let time = try recordingSubtract(event.hostTimeUs, epoch)
            if time < 0 { return }
            try recordingRequire(lastHost == nil || event.hostTimeUs >= lastHost!, "Reversed pointer timestamp")
            try recordingRequire(event.nativePosition.x.isFinite && event.nativePosition.y.isFinite, "Invalid pointer coordinate")
            lastHost = event.hostTimeUs
            guard let target = targets.first(where: { Self.contains(event.nativePosition, $0.display.nativeBounds) }) else {
                if heldGroup != nil {
                    try append(event, time: time, action: nil, group: heldGroup)
                    interrupt("Held group left selected displays")
                }
                return
            }
            let b = target.display.nativeBounds, size = target.slot.geometry.size
            let x = (event.nativePosition.x - b.origin.x) / b.size.width * size.width
            let y = (event.nativePosition.y - b.origin.y) / b.size.height * size.height
            try recordingRequire(x.isFinite && y.isFinite && x >= 0 && y >= 0 && x < size.width && y < size.height, "Mapped point outside target")
            var group = heldGroup
            switch event.kind {
            case .buttonDown:
                try recordingRequire(heldGroup == nil && event.button != nil && event.deltaX == nil && event.deltaY == nil, "Invalid down or simultaneous hold")
                group = "g-" + UUID().uuidString.lowercased()
            case .buttonUp:
                try recordingRequire(heldGroup != nil && event.button == heldButton && event.deltaX == nil && event.deltaY == nil, "Unmatched up")
            case .move:
                try recordingRequire(event.button == nil && event.deltaX == nil && event.deltaY == nil, "Invalid move")
            case .scroll:
                try recordingRequire(heldGroup == nil && event.button == nil && event.deltaX?.isFinite == true && event.deltaY?.isFinite == true, "Invalid scroll or interleaved hold")
            }
            try recordingRequire(count <= Int32.max, "Event order exhausted")
            let action = RecordedAction(id: "e-" + UUID().uuidString.lowercased(), takeId: takeId, monitorSlotId: target.slot.id, timeUs: time, order: Int(count), kind: event.kind, x: x, y: y, button: event.button, atomicGroupId: group, deltaX: event.deltaX, deltaY: event.deltaY)
            try append(event, time: time, action: action, group: group)
            // Only accepted journal history may establish or release pointer ownership.
            if event.kind == .buttonDown { heldGroup = group; heldButton = event.button }
            else if event.kind == .buttonUp { heldGroup = nil; heldButton = nil }
        }
    }
    private func append(_ event: CapturedPointerEvent, time: Int64, action: RecordedAction?, group: String?) throws {
        try recordingRequire(count <= Int32.max, "Event order exhausted")
        let nextDuration = try recordingAdd(time, 1)
        do { try journal.append(record: RawPointerRecord(sequence: Int32(count), event: event, timeUs: time, action: action, atomicGroupId: group)) }
        catch { journalFailed = true; throw error }
        count += 1; duration = max(duration, nextDuration)
    }
    private func appendGap(_ gap: RecordingGap, monitorSlotId: String, stream: inout Stream) throws {
        let next = try recordingAdd(stream.gapCount,1)
        do { try journal.appendGap(record: JournalGapRecord(monitorSlotId: monitorSlotId,gap: gap)) }
        catch { journalFailed = true; throw error }
        stream.gapCount = next
        if stream.gaps.count < 64 { stream.gaps.append(gap) }
    }
    public func acceptedFrame(monitorSlotId: String, sourceTicks: Int64, encodedTimeUs: Int64, durationUs: Int64) throws {
        guard state == .recording || state == .finalizing else { return }
        try checkedRecording {
            var s = try stream(monitorSlotId); try recordingRequire(s.terminal == nil, "Frame after writer terminal")
            let time = try mapper(s).map(sourceTicks: sourceTicks); if time < 0 { return }
            try recordingRequire(s.lastTicks == nil || sourceTicks > s.lastTicks!, "Reversed or duplicate frame timestamp")
            try recordingRequire(encodedTimeUs >= s.end && durationUs > 0, "Invalid encoded frame range")
            let zero = s.zero ?? time
            if s.zero == nil { try recordingRequire(encodedTimeUs == 0, "First encoded frame must start at zero") }
            let expected = try recordingAdd(zero, encodedTimeUs)
            let error = try recordingSubtract(time, expected)
            try recordingRequire(error >= -1 && error <= 1, "Encoder rebased source timing")
            let end = try recordingAdd(encodedTimeUs,durationUs), nextDuration = try recordingAdd(zero,end), nextCount = try recordingAdd(s.count,1)
            if s.zero == nil && time > 0 { try appendGap(RecordingGap(startUs: 0,endUs: time,kind: .leading),monitorSlotId: monitorSlotId,stream: &s) }
            s.zero = zero; s.end = end; s.count = nextCount; s.lastTicks = sourceTicks
            duration = max(duration,nextDuration); streams[monitorSlotId] = s
        }
    }
    public func idle(monitorSlotId: String, sourceTicks: Int64) throws {
        guard state == .recording else { return }
        try checkedRecording { _ = try mapper(stream(monitorSlotId)).map(sourceTicks: sourceTicks) }
    }
    public func reportGap(monitorSlotId: String, startTicks: Int64, endTicks: Int64, kind: GapKind, reason: String? = nil) throws {
        guard state == .recording || state == .finalizing else { return }
        try checkedRecording {
            var s = try stream(monitorSlotId); try recordingRequire(s.terminal == nil && (kind == .dropped || kind == .backpressure), "Invalid gap report")
            let m = try mapper(s), start = try m.map(sourceTicks: startTicks), end = try m.map(sourceTicks: endTicks)
            try recordingRequire(end > start, "Invalid gap range")
            if end <= 0 { return }
            try appendGap(RecordingGap(startUs: max(0,start),endUs: end,kind: kind,reason: reason),monitorSlotId: monitorSlotId,stream: &s); streams[monitorSlotId] = s
            duration = max(duration, end)
        }
    }
    public func sourceFailed(monitorSlotId: String, hostTimeUs: Int64, reason: String) throws {
        if state == .preparing { _ = try stream(monitorSlotId); preparationFailed(reason: reason); return }
        guard state == .recording || state == .finalizing else { return }
        try checkedRecording {
            var s = try stream(monitorSlotId)
            let time = max(0, try recordingSubtract(hostTimeUs, epoch))
            try appendGap(RecordingGap(startUs: time,endUs: time,kind: .sourceFailure,reason: reason),monitorSlotId: monitorSlotId,stream: &s)
            s.sourceFailure = reason; streams[monitorSlotId] = s
            duration = max(duration, time); interrupt(reason)
        }
    }
    public func stop(hostTimeUs: Int64, reason: String? = nil) throws {
        if state == .preparing { preparationFailed(reason: reason ?? "Stopped before all sources ready"); return }
        guard state == .recording else { return }
        try checkedRecording {
            let end = try recordingSubtract(hostTimeUs, epoch); try recordingRequire(end >= 0 && (lastHost == nil || hostTimeUs >= lastHost!), "Stop timestamp precedes input")
            duration = max(duration, end)
            if let reason { interrupt(reason) }
            if heldGroup != nil { interrupt("Stopped during held group") }
            state = .finalizing
        }
    }
    public func writerFinished(monitorSlotId: String, status: WriterStatus, encodedDurationUs: Int64, reason: String? = nil) throws {
        var s = try stream(monitorSlotId)
        let terminal = Terminal(status: status,duration: encodedDurationUs,reason: reason)
        if let existing = s.terminal { try recordingRequire(existing == terminal, "Conflicting writer terminal"); return }
        try recordingRequire(state == .finalizing, "Writer terminal before stop")
        try recordingRequire(encodedDurationUs >= 0, "Negative writer duration")
        s.terminal = terminal; streams[monitorSlotId] = s
        if status == .failed || s.count == 0 || encodedDurationUs < s.end { interrupt(reason ?? "Empty or incomplete writer") }
        if streams.values.allSatisfy({ $0.terminal != nil }) { try finalize() }
    }
    private func finalize() throws {
        var media: [StreamManifest] = []
        for target in targets {
            let id = target.slot.id
            var s = streams[id]!
            let t = s.terminal!
            var successful = t.status == .succeeded && s.count > 0 && t.duration >= s.end && s.zero != nil
            var failure = t.reason
            if let zero = s.zero {
                do { duration = max(duration, try recordingAdd(zero, t.duration)) }
                catch { successful = false; failure = "Writer duration overflow"; interrupt(failure!, failed: true) }
            }
            if !successful {
                do { try appendGap(RecordingGap(startUs: duration,endUs: duration,kind: .encoderFailure,reason: failure ?? "Empty or incomplete writer"),monitorSlotId: id,stream: &s) }
                catch { journalFailed = true; interrupt(String(describing: error),failed: true) }
            }
            streams[id] = s
            if s.gapCount > 0 { warnings.append("Stream \(id) contains gaps or failures") }
            media.append(StreamManifest(monitorSlotId: id,relativePath: "media/\(takeId)/\(id).mp4",clock: s.clock!,mediaTimeZeroUs: s.zero,encodedDurationUs: t.duration,frameCount: s.count,gaps: s.gaps,writerStatus: successful ? .succeeded : .failed,gapCount: s.gapCount,gapJournalPath: "takes/\(takeId)/gaps.jsonl",failureReason: successful ? s.sourceFailure : failure ?? "Empty or incomplete writer"))
        }
        do { try journal.flush() } catch { journalFailed = true; interrupt(String(describing: error),failed: true) }
        state = terminalState
        result = RecordingManifest(manifestVersion: 1,takeId: takeId,state: state,epochHostUs: epoch,durationUs: duration,targets: targets.map(\.slot),streams: media,rawEventCount: count,incompleteGroupIds: incomplete.sorted(),warnings: warnings,reason: reason)
    }
    public func project() throws -> RecordingResult {
        guard let result else { throw RecordingError(message: "Recording has no final manifest") }
        var events: [RecordedAction] = []
        if !journalFailed {
            try journal.read { r in
                if let action = r.action, r.atomicGroupId.map({ !incomplete.contains($0) }) ?? true { events.append(action) }
            }
        }
        let take = Take(id: takeId,durationUs: result.durationUs,monitorSlotIds: targets.map { $0.slot.id },media: result.streams.filter { $0.writerStatus == .succeeded }.map { TakeMedia(monitorSlotId: $0.monitorSlotId,relativePath: $0.relativePath) })
        return RecordingResult(manifest: result,take: take,events: events)
    }
}
