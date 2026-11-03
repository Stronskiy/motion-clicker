import Foundation
import Darwin
import MotionClickerCore

/// Capture-only store; externally serialized, no project.json writer or deletion/recovery policy.
public final class RecordingStore: EventJournal {
    private let root: URL, staging: URL, media: URL, metadata: URL, takeId: String
    private let fault: (StoreOperation) throws -> Void
    private var handle: FileHandle?
    private var gapHandle: FileHandle?
    private var poisoned = false, closed = false, mediaPublished = false, manifestPublished = false
    private var reserved = Set<String>(), rawCount: Int64 = 0
    private var frozenManifest: Data?
    public var recovery: StoreRecovery {
        StoreRecovery(stagingPath: staging.path,mediaPath: media.path,manifestPath: metadata.appendingPathComponent("manifest.json").path,mediaPublished: mediaPublished,manifestPublished: manifestPublished)
    }
    public init(root: URL, takeId: String, fault: @escaping (StoreOperation) throws -> Void = { _ in }) throws {
        try Self.id(takeId)
        let initial = root.standardizedFileURL
        let attrs = try FileManager.default.attributesOfItem(atPath: initial.path)
        try recordingRequire(attrs[.type] as? FileAttributeType == .typeDirectory, "Root must be an ordinary directory")
        self.root = initial.resolvingSymlinksInPath(); self.takeId = takeId; self.fault = fault
        staging = self.root.appendingPathComponent(".capture-" + UUID().uuidString.lowercased())
        media = self.root.appendingPathComponent("media").appendingPathComponent(takeId)
        metadata = self.root.appendingPathComponent("takes").appendingPathComponent(takeId)
        try check(media); try check(metadata)
        try recordingRequire(!Self.exists(media) && !Self.exists(metadata), "Take already exists")
        try ensureDirectory(self.root.appendingPathComponent("media")); try ensureDirectory(self.root.appendingPathComponent("takes"))
        try Self.createDirectory(staging)
        try Self.createDirectory(staging.appendingPathComponent("media"))
        try Self.createDirectory(staging.appendingPathComponent("metadata"))
        handle = try Self.exclusiveFile(staging.appendingPathComponent("metadata/events.jsonl"))
        gapHandle = try Self.exclusiveFile(staging.appendingPathComponent("metadata/gaps.jsonl"))
    }
    deinit { try? handle?.close(); try? gapHandle?.close() }
    private static func id(_ id: String) throws {
        let stem = id.split(separator: ".", omittingEmptySubsequences: false).first.map(String.init)?.uppercased() ?? ""
        let reserved = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        try recordingRequire(!id.hasSuffix(".") && !reserved.contains(stem), "Nonportable filesystem ID")
        try recordingRequire(id.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",options: .regularExpression) != nil, "Invalid capture ID")
    }
    private static func exists(_ url: URL) -> Bool { (try? FileManager.default.attributesOfItem(atPath: url.path)) != nil }
    private static func createDirectory(_ url: URL) throws {
        guard mkdir(url.path, S_IRWXU) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
    private static func exclusiveFile(_ url: URL) throws -> FileHandle {
        let fd = open(url.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        return FileHandle(fileDescriptor: fd,closeOnDealloc: true)
    }
    // Check actual components and case aliases at each operation, including dangling symlinks.
    private func check(_ url: URL) throws {
        let base = root.pathComponents, path = url.standardizedFileURL.pathComponents
        try recordingRequire(path.starts(with: base), "Path outside capture root")
        var cursor = root
        let rootAttrs = try FileManager.default.attributesOfItem(atPath: root.path)
        try recordingRequire(rootAttrs[.type] as? FileAttributeType == .typeDirectory, "Root redirected")
        for component in path.dropFirst(base.count) {
            if Self.exists(cursor) {
                for name in try FileManager.default.contentsOfDirectory(atPath: cursor.path) where name.lowercased() == component.lowercased() {
                    try recordingRequire(name == component, "Case-folded path collision")
                }
            }
            cursor.appendPathComponent(component)
            if let attrs = try? FileManager.default.attributesOfItem(atPath: cursor.path) {
                try recordingRequire(attrs[.type] as? FileAttributeType != .typeSymbolicLink, "Symbolic link in capture path")
                if attrs[.type] as? FileAttributeType == .typeRegular {
                    try recordingRequire((attrs[.referenceCount] as? NSNumber)?.intValue == 1, "Hard-linked capture file")
                }
            }
        }
    }
    private func checkTree(_ url: URL) throws {
        try check(url)
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let type = attributes[.type] as? FileAttributeType
        try recordingRequire(type == .typeDirectory || type == .typeRegular, "Unsupported capture file type")
        if type == .typeDirectory {
            for child in try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil) { try checkTree(child) }
        }
    }
    private func ensureDirectory(_ url: URL) throws {
        try check(url)
        if !Self.exists(url) { try Self.createDirectory(url) }
        try recordingRequire((try FileManager.default.attributesOfItem(atPath: url.path))[.type] as? FileAttributeType == .typeDirectory, "Expected directory")
    }
    private var journalPath: URL { (manifestPublished ? metadata : staging.appendingPathComponent("metadata")).appendingPathComponent("events.jsonl") }
    private var gapPath: URL { (manifestPublished ? metadata : staging.appendingPathComponent("metadata")).appendingPathComponent("gaps.jsonl") }
    public func mediaFile(monitorSlotId: String) throws -> URL {
        try Self.id(monitorSlotId)
        try recordingRequire(!closed && !mediaPublished && frozenManifest == nil && !poisoned, "Capture store no longer writable")
        try recordingRequire(!reserved.contains(monitorSlotId.lowercased()), "Duplicate media reservation")
        let path = staging.appendingPathComponent("media").appendingPathComponent(monitorSlotId + ".mp4")
        try check(path); try recordingRequire(!Self.exists(path), "Media path collision")
        reserved.insert(monitorSlotId.lowercased()); return path
    }
    public func append(record: RawPointerRecord) throws {
        try recordingRequire(!closed && !poisoned && frozenManifest == nil, "Capture journal no longer writable")
        do {
            try check(journalPath); try recordingRequire(Int64(record.sequence) == rawCount, "Journal sequence mismatch")
            try fault(.append)
            guard let handle else { throw RecordingError(message: "Journal closed") }
            var bytes = try JSONEncoder().encode(record); bytes.append(10)
            try handle.write(contentsOf: bytes); rawCount = try recordingAdd(rawCount, 1)
        } catch { poisoned = true; throw error }
    }
    public func appendGap(record: JournalGapRecord) throws {
        try recordingRequire(!closed && !poisoned && frozenManifest == nil,"Capture journal no longer writable")
        do {
            try check(gapPath);try fault(.append)
            guard let gapHandle else { throw RecordingError(message: "Gap journal closed") }
            var bytes = try JSONEncoder().encode(record);bytes.append(10);try gapHandle.write(contentsOf: bytes)
        } catch { poisoned = true;throw error }
    }
    public func readGaps(visit: (JournalGapRecord) throws -> Void) throws {
        try gapHandle?.synchronize();try readLines(path: gapPath,visit: visit)
    }
    public func flush() throws {
        try recordingRequire(!poisoned, "Capture journal failed")
        do {
            try check(journalPath);try check(gapPath); try fault(.flush)
            if let handle { try handle.synchronize() };try gapHandle?.synchronize()
        } catch { poisoned = true; throw error }
    }
    public func read(visit: (RawPointerRecord) throws -> Void) throws {
        try handle?.synchronize();try readLines(path: journalPath,visit: visit)
    }
    private func readLines<T: Decodable>(path: URL,visit: (T) throws -> Void) throws {
        try check(path)
        let input = try FileHandle(forReadingFrom: path); defer { try? input.close() }
        var pending = Data()
        while let bytes = try input.read(upToCount: 65536), !bytes.isEmpty {
            pending.append(bytes)
            while let newline = pending.firstIndex(of: 10) {
                let line = pending[..<newline]
                try recordingRequire(!line.isEmpty && line.count <= 1048576, "Invalid journal line")
                try visit(JSONDecoder().decode(T.self,from: Data(line)))
                pending.removeSubrange(...newline)
            }
            try recordingRequire(pending.count <= 1048576, "Oversized journal record")
        }
        try recordingRequire(pending.isEmpty, "Truncated event journal")
    }
    private func validate(_ m: RecordingManifest) throws {
        try recordingRequire(m.manifestVersion == 1 && m.takeId == takeId && [.completed,.interrupted,.failed].contains(m.state) && m.durationUs > 0 && m.rawEventCount == rawCount, "Invalid terminal manifest")
        try recordingRequire(m.streams.count == m.targets.count && !m.streams.isEmpty, "Missing manifest streams")
        var seen = Set<String>()
        let incomplete = Set(m.incompleteGroupIds)
        for id in incomplete { try Self.id(id) }
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
                let file = (mediaPublished ? media : staging.appendingPathComponent("media")).appendingPathComponent(s.monitorSlotId + ".mp4")
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
        _ = try RecordingJournalValidation.validate(manifest: m,scratchRoot: staging,read: read)
    }
    public func publish(manifest: RecordingManifest) throws -> URL {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(manifest)
        if let frozenManifest { try recordingRequire(frozenManifest == bytes, "Conflicting publication retry") }
        if manifestPublished { return metadata.appendingPathComponent("manifest.json") }
        try recordingRequire(!closed && !poisoned, "Capture store failed or closed")
        try check(staging); try check(media); try check(metadata)
        try recordingRequire(!Self.exists(metadata) && (mediaPublished || !Self.exists(media)), "Publish collision")
        try checkTree(mediaPublished ? media : staging.appendingPathComponent("media"))
        try checkTree(staging.appendingPathComponent("metadata"))
        try validate(manifest); try flush()
        let staged = staging.appendingPathComponent("metadata/manifest.json")
        if frozenManifest == nil {
            try check(staged)
            let output = try Self.exclusiveFile(staged)
            do { try output.write(contentsOf: bytes); try output.synchronize(); try output.close() }
            catch { try? output.close(); poisoned = true; throw error }
            frozenManifest = bytes
        }
        try handle?.close(); handle = nil;try gapHandle?.close();gapHandle = nil
        if !mediaPublished {
            try fault(.moveMedia); try check(media); try checkTree(staging.appendingPathComponent("media"))
            try Self.moveExclusive(staging.appendingPathComponent("media"),media); mediaPublished = true
        }
        try fault(.publishManifest); try check(metadata); try checkTree(staging.appendingPathComponent("metadata"))
        try Self.moveExclusive(staging.appendingPathComponent("metadata"),metadata); manifestPublished = true
        return metadata.appendingPathComponent("manifest.json")
    }
    private static func moveExclusive(_ source: URL, _ destination: URL) throws {
        guard renamex_np(source.path,destination.path,UInt32(RENAME_EXCL)) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
    public func close() throws { try handle?.close(); handle = nil;try gapHandle?.close();gapHandle = nil; closed = true }
}
