import Foundation
import Darwin
import MotionClickerCore

/// Capture-only store; externally serialized, no project.json writer or deletion/recovery policy.
public final class RecordingStore: EventJournal {
    private let files: RecordingFiles
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
        try RecordingFiles.id(takeId)
        files = try RecordingFiles(root: root)
        self.root = files.root; self.takeId = takeId; self.fault = fault
        staging = self.root.appendingPathComponent(".capture-" + UUID().uuidString.lowercased())
        media = self.root.appendingPathComponent("media").appendingPathComponent(takeId)
        metadata = self.root.appendingPathComponent("takes").appendingPathComponent(takeId)
        try check(media); try check(metadata)
        try recordingRequire(!RecordingFiles.exists(media) && !RecordingFiles.exists(metadata), "Take already exists")
        try ensureDirectory(self.root.appendingPathComponent("media")); try ensureDirectory(self.root.appendingPathComponent("takes"))
        try RecordingFiles.createDirectory(staging)
        try RecordingFiles.createDirectory(staging.appendingPathComponent("media"))
        try RecordingFiles.createDirectory(staging.appendingPathComponent("metadata"))
        handle = try RecordingFiles.exclusiveFile(staging.appendingPathComponent("metadata/events.jsonl"))
        gapHandle = try RecordingFiles.exclusiveFile(staging.appendingPathComponent("metadata/gaps.jsonl"))
    }
    deinit { try? handle?.close(); try? gapHandle?.close() }
    private func check(_ url: URL) throws { try files.check(url) }
    private func checkTree(_ url: URL) throws { try files.checkTree(url) }
    private func ensureDirectory(_ url: URL) throws { try files.ensureDirectory(url) }
    private var journalPath: URL { (manifestPublished ? metadata : staging.appendingPathComponent("metadata")).appendingPathComponent("events.jsonl") }
    private var gapPath: URL { (manifestPublished ? metadata : staging.appendingPathComponent("metadata")).appendingPathComponent("gaps.jsonl") }
    public func mediaFile(monitorSlotId: String) throws -> URL {
        try RecordingFiles.id(monitorSlotId)
        try recordingRequire(!closed && !mediaPublished && frozenManifest == nil && !poisoned, "Capture store no longer writable")
        try recordingRequire(!reserved.contains(monitorSlotId.lowercased()), "Duplicate media reservation")
        let path = staging.appendingPathComponent("media").appendingPathComponent(monitorSlotId + ".mp4")
        try check(path); try recordingRequire(!RecordingFiles.exists(path), "Media path collision")
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
        try gapHandle?.synchronize();try RecordingJournalReader.read(path: gapPath,check: check,visit: visit)
    }
    public func flush() throws {
        try recordingRequire(!poisoned, "Capture journal failed")
        do {
            try check(journalPath);try check(gapPath); try fault(.flush)
            if let handle { try handle.synchronize() };try gapHandle?.synchronize()
        } catch { poisoned = true; throw error }
    }
    public func read(visit: (RawPointerRecord) throws -> Void) throws {
        try handle?.synchronize();try RecordingJournalReader.read(path: journalPath,check: check,visit: visit)
    }
    private func validate(_ m: RecordingManifest) throws {
        try RecordingEvidenceValidation.validate(m,takeId: takeId,rawCount: rawCount,scratchRoot: staging,
            mediaFile: { (mediaPublished ? media : staging.appendingPathComponent("media")).appendingPathComponent($0 + ".mp4") },
            check: check,read: read,readGaps: readGaps)
    }
    public func publish(manifest: RecordingManifest) throws -> URL {
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(manifest)
        if let frozenManifest { try recordingRequire(frozenManifest == bytes, "Conflicting publication retry") }
        if manifestPublished { return metadata.appendingPathComponent("manifest.json") }
        try recordingRequire(!closed && !poisoned, "Capture store failed or closed")
        try check(staging); try check(media); try check(metadata)
        try recordingRequire(!RecordingFiles.exists(metadata) && (mediaPublished || !RecordingFiles.exists(media)), "Publish collision")
        try checkTree(mediaPublished ? media : staging.appendingPathComponent("media"))
        try checkTree(staging.appendingPathComponent("metadata"))
        try validate(manifest); try flush()
        let staged = staging.appendingPathComponent("metadata/manifest.json")
        if frozenManifest == nil {
            try check(staged)
            let output = try RecordingFiles.exclusiveFile(staged)
            do { try output.write(contentsOf: bytes); try output.synchronize(); try output.close() }
            catch { try? output.close(); poisoned = true; throw error }
            frozenManifest = bytes
        }
        try handle?.close(); handle = nil;try gapHandle?.close();gapHandle = nil
        if !mediaPublished {
            try fault(.moveMedia); try check(media); try checkTree(staging.appendingPathComponent("media"))
            try RecordingFiles.moveExclusive(staging.appendingPathComponent("media"),media); mediaPublished = true
        }
        try fault(.publishManifest); try check(metadata); try checkTree(staging.appendingPathComponent("metadata"))
        try RecordingFiles.moveExclusive(staging.appendingPathComponent("metadata"),metadata); manifestPublished = true
        return metadata.appendingPathComponent("manifest.json")
    }
    public func close() throws { try handle?.close(); handle = nil;try gapHandle?.close();gapHandle = nil; closed = true }
}
