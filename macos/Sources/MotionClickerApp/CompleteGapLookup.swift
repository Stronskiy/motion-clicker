import Foundation
import Observation
import MotionClickerRecording

struct SourceGapWindow: Sendable, Equatable {
    let monitorSlotId: String
    let startUs: Int64, endUs: Int64
    let kind: GapKind?
    func contains(monitorSlotId: String, sourceUs: Int64) -> Bool {
        self.monitorSlotId == monitorSlotId && sourceUs >= startUs && sourceUs < endUs
    }
}

/// Read-only source history. Actor work never calls AppKit or input/capture APIs.
actor CompleteGapLookup {
    private let streams: [String: StreamManifest]
    private let durationUs: Int64
    private let file: URL
    init(snapshot: RecordedBundleSnapshot) {
        streams=Dictionary(uniqueKeysWithValues:snapshot.manifest.streams.map { ($0.monitorSlotId,$0) })
        durationUs=snapshot.manifest.durationUs; file=snapshot.gapsFile
    }
    func window(monitorSlotId: String, sourceUs: Int64) throws -> SourceGapWindow {
        guard streams[monitorSlotId] != nil, sourceUs >= 0, sourceUs < durationUs else { throw RecordingError(message:"Invalid source gap query") }
        var start:Int64=0, end=durationUs
        var covering:RecordingGap?
        var counts=Dictionary(uniqueKeysWithValues:streams.keys.map { ($0,Int64(0)) })
        try read { record in
            try Task.checkCancellation()
            guard let stream=streams[record.monitorSlotId], let count=counts[record.monitorSlotId] else { throw RecordingError(message:"Unknown source in complete gap journal") }
            let gap=record.gap
            guard gap.startUs >= 0, gap.endUs >= gap.startUs, gap.endUs <= durationUs, count < stream.gapCount else { throw RecordingError(message:"Invalid complete gap history") }
            // Preview is checked as a prefix only; it is never the lookup source.
            if count < 64 {
                guard Int(count) < stream.gaps.count, stream.gaps[Int(count)] == gap else { throw RecordingError(message:"Gap preview differs from complete history") }
            }
            counts[record.monitorSlotId]=count+1
            guard record.monitorSlotId == monitorSlotId, gap.startUs < gap.endUs else { return }
            if sourceUs >= gap.startUs && sourceUs < gap.endUs { covering=covering ?? gap }
            else if gap.endUs <= sourceUs { start=max(start,gap.endUs) }
            else if gap.startUs > sourceUs { end=min(end,gap.startUs) }
        }
        guard streams.allSatisfy({ counts[$0.key] == $0.value.gapCount }) else { throw RecordingError(message:"Complete gap journal count mismatch") }
        if let covering { return .init(monitorSlotId:monitorSlotId,startUs:covering.startUs,endUs:covering.endUs,kind:covering.kind) }
        return .init(monitorSlotId:monitorSlotId,startUs:start,endUs:end,kind:nil)
    }
    /// Fixed64KiB reads, at most one1MiB JSON record, counts per monitor and
    /// one covering gap. No complete journal array or per-position Task backlog.
    private func read(_ visit:(JournalGapRecord) throws -> Void) throws {
        let attributes=try FileManager.default.attributesOfItem(atPath:file.path)
        guard attributes[.type] as? FileAttributeType == .typeRegular else { throw RecordingError(message:"Gap journal is not an ordinary file") }
        let input=try FileHandle(forReadingFrom:file); defer { try? input.close() }
        var pending=Data()
        while let bytes=try input.read(upToCount:65_536), !bytes.isEmpty {
            try Task.checkCancellation()
            pending.append(bytes)
            while let newline=pending.firstIndex(of:10) {
                let line=pending[..<newline]
                guard !line.isEmpty, line.count <= 1_048_576 else { throw RecordingError(message:"Invalid gap journal record") }
                try visit(JSONDecoder().decode(JournalGapRecord.self,from:Data(line)))
                pending.removeSubrange(...newline)
            }
            guard pending.count <= 1_048_576 else { throw RecordingError(message:"Oversized gap journal record") }
        }
        guard pending.isEmpty else { throw RecordingError(message:"Truncated gap journal record") }
    }
}

/// Coalesces source-time/monitor changes to one in-flight complete-history query.
/// A result is usable only in the interval and monitor it actually inspected.
@MainActor @Observable final class SourceGapInspection {
    private(set) var window:SourceGapWindow?
    private(set) var error:String?
    @ObservationIgnored private let lookup:CompleteGapLookup
    @ObservationIgnored private var task:Task<Void,Never>?
    @ObservationIgnored private var latest:Query?
    private struct Query { let media:RecordedBundleMedia; let sourceUs:Int64 }
    var isLoading:Bool { task != nil }
    init(lookup:CompleteGapLookup) { self.lookup=lookup }
    func unavailable(media:RecordedBundleMedia, sourceUs:Int64) -> String? {
        if sourceUs < media.mediaTimeZeroUs { return "No source image yet · first image at \(Double(media.mediaTimeZeroUs)/1_000_000) s" }
        if sourceUs >= media.mediaTimeZeroUs + media.encodedDurationUs { return "No source image after the valid video prefix." }
        if let error { return error }
        guard let window, window.contains(monitorSlotId:media.monitorSlotId,sourceUs:sourceUs) else { return "Inspecting complete video gap history…" }
        return window.kind.map { "Known video gap: \($0.rawValue)" }
    }
    func request(media:RecordedBundleMedia, sourceUs:Int64) {
        guard sourceUs >= media.mediaTimeZeroUs, sourceUs < media.mediaTimeZeroUs + media.encodedDurationUs else { latest=nil; return }
        latest=Query(media:media,sourceUs:sourceUs)
        guard error == nil, window?.contains(monitorSlotId:media.monitorSlotId,sourceUs:sourceUs) != true, task == nil else { return }
        task=Task { @MainActor in
            do {
                while let query=latest {
                    let inspected=try await lookup.window(monitorSlotId:query.media.monitorSlotId,sourceUs:query.sourceUs)
                    try Task.checkCancellation()
                    if let current=latest, inspected.contains(monitorSlotId:current.media.monitorSlotId,sourceUs:current.sourceUs) { window=inspected; break }
                }
            } catch is CancellationError { }
            catch { self.error="Complete video gap history unavailable. Source video is hidden: \((error as? RecordingError)?.message ?? error.localizedDescription)" }
            task=nil
            if error == nil, let current=latest, window?.contains(monitorSlotId:current.media.monitorSlotId,sourceUs:current.sourceUs) != true { request(media:current.media,sourceUs:current.sourceUs) }
        }
    }
    func cancel() { latest=nil; task?.cancel() }
}
