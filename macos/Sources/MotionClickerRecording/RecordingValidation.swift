import Foundation
import MotionClickerCore

struct RecordingValidationStatistics { let records: Int64; let maximumBufferedIDs: Int }
enum RecordingJournalValidation {
    static func validate(manifest m: RecordingManifest, scratchRoot: URL,
                         read: (_ visit: (RawPointerRecord) throws -> Void) throws -> Void) throws -> RecordingValidationStatistics {
        var events: [RecordedAction] = [], actual: Int64 = 0
        let incomplete = Set(m.incompleteGroupIds)
        try read { r in
            try recordingRequire(Int64(r.sequence) == actual && r.timeUs >= 0 && r.timeUs < m.durationUs, "Invalid raw journal ordering/range")
            actual += 1
            if let a = r.action {
                try recordingRequire(a.timeUs == r.timeUs && a.order == Int(r.sequence) && a.atomicGroupId == r.atomicGroupId, "Raw/projection mismatch")
                if a.atomicGroupId.map({ !incomplete.contains($0) }) ?? true { events.append(a) }
            }
        }
        try recordingRequire(actual == m.rawEventCount, "Journal count mismatch")
        let take = Take(id: m.takeId,durationUs: m.durationUs,monitorSlotIds: m.targets.map(\.id),media: m.streams.filter { $0.writerStatus == .succeeded }.map { TakeMedia(monitorSlotId: $0.monitorSlotId,relativePath: $0.relativePath) })
        _ = try RunPlanCompiler.compile(MotionProject(id: "validation-" + UUID().uuidString,monitorSlots: m.targets,takes: [take],events: events))
        try recordingRequire(m.state != .completed || incomplete.isEmpty, "Incomplete group labeled completed")
        return RecordingValidationStatistics(records: actual,maximumBufferedIDs: events.count)
    }
}
