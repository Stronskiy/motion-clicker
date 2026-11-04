import Foundation

enum RecordingEvidenceValidation {
    static func validate(_ m: RecordingManifest, takeId: String, rawCount: Int64, scratchRoot: URL,
                         mediaFile: (String) -> URL, check: (URL) throws -> Void,
                         read: (_ visit: (RawPointerRecord) throws -> Void) throws -> Void,
                         readGaps: (_ visit: (JournalGapRecord) throws -> Void) throws -> Void) throws {
        try recordingRequire(m.manifestVersion == 1 && m.takeId == takeId && [.completed,.interrupted,.failed].contains(m.state) && m.durationUs > 0 && m.rawEventCount == rawCount, "Invalid terminal manifest")
        try recordingRequire(m.streams.count == m.targets.count && !m.streams.isEmpty, "Missing manifest streams")
        var seen = Set<String>()
        try recordingRequire(m.incompleteGroupIds.count <= 1,"Too many incomplete pointer groups")
        for s in m.streams {
            try recordingRequire(seen.insert(s.monitorSlotId).inserted && m.targets.contains(where: { $0.id == s.monitorSlotId }), "Invalid stream target")
            try recordingRequire(s.relativePath == "media/\(takeId)/\(s.monitorSlotId).mp4", "Unsafe stream media path")
            _ = try MediaClockMapper(sourceAnchorTicks: s.clock.sourceAnchorTicks,hostAnchorUs: s.clock.hostAnchorUs,ticksPerSecond: s.clock.ticksPerSecond,epochHostUs: m.epochHostUs)
            try recordingRequire(s.frameCount >= 0 && s.encodedDurationUs >= 0, "Invalid media counters")
            if let zero = s.mediaTimeZeroUs { try recordingRequire(zero >= 0 && (s.writerStatus == .failed || recordingAdd(zero, s.encodedDurationUs) <= m.durationUs), "Media beyond take") }
            try recordingRequire(s.gapCount >= 0 && s.gaps.count == min(s.gapCount,64) && s.gapJournalPath == "takes/\(takeId)/gaps.jsonl", "Invalid gap preview metadata")
            for gap in s.gaps { try recordingRequire(gap.startUs >= 0 && gap.endUs >= gap.startUs && gap.endUs <= m.durationUs, "Invalid gap range") }
            if s.writerStatus == .succeeded {
                try recordingRequire(s.frameCount > 0 && s.encodedDurationUs > 0 && s.mediaTimeZeroUs != nil, "Empty successful media")
                let file = mediaFile(s.monitorSlotId)
                try check(file)
                let a = try FileManager.default.attributesOfItem(atPath: file.path)
                try recordingRequire(a[.type] as? FileAttributeType == .typeRegular && (a[.size] as? NSNumber)?.int64Value ?? 0 > 0, "Missing or empty successful media")
            } else { try recordingRequire(m.state != .completed, "Failed writer labeled completed") }
        }
        var gapCounts = Dictionary(uniqueKeysWithValues: m.streams.map { ($0.monitorSlotId,Int64(0)) })
        let streamMap = Dictionary(uniqueKeysWithValues: m.streams.map { ($0.monitorSlotId,$0) })
        try readGaps { r in
            guard let stream = streamMap[r.monitorSlotId], let count = gapCounts[r.monitorSlotId] else { throw RecordingError(message: "Unknown gap source") }
            let gap = r.gap
            try recordingRequire(gap.startUs >= 0 && gap.endUs >= gap.startUs && gap.endUs <= m.durationUs,"Invalid journal gap range")
            if count < 64 { try recordingRequire(Int(count) < stream.gaps.count && stream.gaps[Int(count)] == gap,"Gap preview mismatch") }
            gapCounts[r.monitorSlotId] = try recordingAdd(count,1)
        }
        for stream in m.streams { try recordingRequire(gapCounts[stream.monitorSlotId] == stream.gapCount,"Gap journal count mismatch") }
        _ = try RecordingJournalValidation.validate(manifest: m,scratchRoot: scratchRoot,read: read)
    }
}
