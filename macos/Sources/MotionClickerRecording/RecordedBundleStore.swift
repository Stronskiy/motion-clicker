import Foundation
import Darwin
import MotionClickerCore

public enum BundleOperation { case writeProject, flushProject, publishProject }
public enum RecordedBundleAvailability { case requiresMonitorMapping, partial }
public struct RecordedBundleMedia: Equatable {
    public let monitorSlotId: String
    public let file: URL
    public let mediaTimeZeroUs: Int64
    public let encodedDurationUs: Int64
}
public struct RecordedBundleSnapshot: Equatable {
    public let project: MotionProject
    public let manifest: RecordingManifest
    public let manifestFile: URL
    public let eventsFile: URL
    public let gapsFile: URL
    public let media: [RecordedBundleMedia]
    public let availability: RecordedBundleAvailability
}
private struct RecordedBundleDocument: Codable {
    let bundleVersion: Int
    let manifestPath: String
    let project: MotionProject
}

/// Initial project publication only. Open never constructs capture/input dependencies.
public enum RecordedBundleStore {
    private static let owner = Data("motionclicker-recorded-bundle-v1\n".utf8)

    public static func create(root: URL) throws {
        let destination = root.standardizedFileURL
        try recordingRequire(destination.pathExtension == "motionclicker", "Expected .motionclicker directory")
        let parent = try RecordingFiles(root: destination.deletingLastPathComponent())
        let path = parent.root.appendingPathComponent(destination.lastPathComponent)
        try parent.check(path); try recordingRequire(!RecordingFiles.exists(path), "Bundle already exists")
        try RecordingFiles.createDirectory(path)
        let handle = try RecordingFiles.exclusiveFile(path.appendingPathComponent(".bundle-owner"))
        defer { try? handle.close() }
        try handle.write(contentsOf: owner); try handle.synchronize()
    }

    public static func saveInitial(root: URL, result: RecordingResult, validationScratch: URL,
                                   fault: (BundleOperation) throws -> Void = { _ in }) throws -> RecordedBundleSnapshot {
        let files = try openRoot(root), projectFile = files.root.appendingPathComponent("project.json")
        try files.check(projectFile); try recordingRequire(!RecordingFiles.exists(projectFile), "Initial project already exists")
        let snapshot = try inspect(files: files,takeId: result.manifest.takeId,validationScratch: validationScratch)
        let expected = RecordingResult(manifest: snapshot.manifest,take: snapshot.project.takes[0],events: snapshot.project.events)
        try recordingRequire(result == expected, "Recording result differs from published evidence")
        let document = RecordedBundleDocument(bundleVersion: 1,manifestPath: "takes/\(result.manifest.takeId)/manifest.json",project: snapshot.project)
        let encoder = JSONEncoder(); encoder.outputFormatting = [.sortedKeys]
        let bytes = try encoder.encode(document)
        let stage = files.root.appendingPathComponent(".project-" + UUID().uuidString.lowercased())
        try files.check(stage)
        let handle = try RecordingFiles.exclusiveFile(stage)
        do {
            try fault(.writeProject); try handle.write(contentsOf: bytes)
            try fault(.flushProject); try handle.synchronize(); try handle.close()
        } catch { try? handle.close(); throw error }
        try fault(.publishProject)
        try files.check(stage); try files.check(projectFile)
        try RecordingFiles.moveExclusive(stage,projectFile)
        return snapshot
    }

    public static func coldOpen(root: URL, validationScratch: URL) throws -> RecordedBundleSnapshot {
        let files = try openRoot(root), projectFile = files.root.appendingPathComponent("project.json")
        let document = try JSONDecoder().decode(RecordedBundleDocument.self,from: readFile(projectFile,files: files))
        try recordingRequire(document.bundleVersion == 1 && document.project.takes.count == 1, "Unsupported recorded bundle")
        let takeId = document.project.takes[0].id
        try RecordingFiles.id(takeId)
        try recordingRequire(document.manifestPath == "takes/\(takeId)/manifest.json", "Invalid source manifest reference")
        _ = try RunPlanCompiler.compile(document.project)
        let snapshot = try inspect(files: files,takeId: takeId,validationScratch: validationScratch)
        try recordingRequire(document.project == snapshot.project, "Project differs from initial source projection")
        return snapshot
    }

    public static func recoveryEntries(root: URL) throws -> [URL] {
        let files = try openRoot(root)
        var result: [URL] = []
        for name in try FileManager.default.contentsOfDirectory(atPath: files.root.path) where name.hasPrefix(".capture-") || name.hasPrefix(".project-") {
            let path = files.root.appendingPathComponent(name); try files.check(path); result.append(path)
        }
        let projectFile = files.root.appendingPathComponent("project.json"); try files.check(projectFile)
        if !RecordingFiles.exists(projectFile) {
            for name in ["media","takes"] {
                let directory = files.root.appendingPathComponent(name); try files.check(directory)
                if RecordingFiles.exists(directory) {
                    try recordingRequire((try FileManager.default.attributesOfItem(atPath: directory.path))[.type] as? FileAttributeType == .typeDirectory, "Invalid recovery directory")
                    for entry in try FileManager.default.contentsOfDirectory(at: directory,includingPropertiesForKeys: nil) { try files.check(entry); result.append(entry) }
                }
            }
        }
        return result.sorted { $0.path < $1.path }
    }

    private static func openRoot(_ root: URL) throws -> RecordingFiles {
        try recordingRequire(root.standardizedFileURL.pathExtension == "motionclicker", "Expected .motionclicker directory")
        let files = try RecordingFiles(root: root)
        try recordingRequire(readFile(files.root.appendingPathComponent(".bundle-owner"),files: files) == owner, "Unowned recorded bundle")
        return files
    }
    private static func readFile(_ path: URL, files: RecordingFiles) throws -> Data {
        try files.check(path)
        try recordingRequire((try FileManager.default.attributesOfItem(atPath: path.path))[.type] as? FileAttributeType == .typeRegular, "Expected regular file")
        return try Data(contentsOf: path)
    }
    private static func inspect(files: RecordingFiles, takeId: String, validationScratch: URL) throws -> RecordedBundleSnapshot {
        try RecordingFiles.id(takeId)
        let manifestFile = files.root.appendingPathComponent("takes/\(takeId)/manifest.json")
        let m = try JSONDecoder().decode(RecordingManifest.self,from: readFile(manifestFile,files: files))
        for target in m.targets { try RecordingFiles.id(target.id) }
        try files.checkTree(files.root.appendingPathComponent("takes/\(takeId)"))
        try files.checkTree(files.root.appendingPathComponent("media/\(takeId)"))
        let eventsFile = files.root.appendingPathComponent("takes/\(takeId)/events.jsonl")
        let gapsFile = files.root.appendingPathComponent("takes/\(takeId)/gaps.jsonl")
        // Journal readers must reject directories too, including empty gap-journal replacements.
        for path in [eventsFile,gapsFile] {
            try files.check(path)
            try recordingRequire((try FileManager.default.attributesOfItem(atPath: path.path))[.type] as? FileAttributeType == .typeRegular, "Expected journal file")
        }
        let read: (_ visit: (RawPointerRecord) throws -> Void) throws -> Void = { visit in try RecordingJournalReader.read(path: eventsFile,check: files.check,visit: visit) }
        let readGaps: (_ visit: (JournalGapRecord) throws -> Void) throws -> Void = { visit in try RecordingJournalReader.read(path: gapsFile,check: files.check,visit: visit) }
        try withScratch(outside: files.root,root: validationScratch) { scratch in
            try RecordingEvidenceValidation.validate(m,takeId: takeId,rawCount: m.rawEventCount,scratchRoot: scratch,
                mediaFile: { files.root.appendingPathComponent("media/\(takeId)/\($0).mp4") },check: files.check,read: read,readGaps: readGaps)
        }
        var events: [RecordedAction] = [], sourceIDs: [String] = []
        try read { r in
            if let action = r.action { sourceIDs.append(action.id) }
            if let action = r.action, !m.incompleteGroupIds.contains(r.atomicGroupId ?? "") { events.append(action) }
        }
        let project = try makeProject(manifest: m,events: events,sourceIDs: sourceIDs)
        var media: [RecordedBundleMedia] = []
        for stream in m.streams where stream.writerStatus == .succeeded {
            guard let zero = stream.mediaTimeZeroUs else { throw RecordingError(message: "Missing media offset") }
            media.append(RecordedBundleMedia(monitorSlotId: stream.monitorSlotId,file: files.root.appendingPathComponent(stream.relativePath),mediaTimeZeroUs: zero,encodedDurationUs: stream.encodedDurationUs))
        }
        return RecordedBundleSnapshot(project: project,manifest: m,manifestFile: manifestFile,eventsFile: eventsFile,gapsFile: gapsFile,media: media,
            availability: m.state == .completed ? .requiresMonitorMapping : .partial)
    }
    private static func withScratch<T>(outside bundle: URL, root: URL, body: (URL) throws -> T) throws -> T {
        let files = try RecordingFiles(root: root)
        let a = bundle.pathComponents, b = files.root.pathComponents
        try recordingRequire(!a.starts(with: b) && !b.starts(with: a), "Validation scratch must be external to bundle")
        let scratch = files.root.appendingPathComponent(".bundle-validation-" + UUID().uuidString)
        try files.check(scratch); try RecordingFiles.createDirectory(scratch)
        defer { try? FileManager.default.removeItem(at: scratch) }
        return try body(scratch)
    }
    private static func makeProject(manifest m: RecordingManifest, events: [RecordedAction], sourceIDs: [String]) throws -> MotionProject {
        var ids = Set([m.takeId] + m.targets.map(\.id) + sourceIDs)
        func allocate(_ stem: String) -> String {
            var value = stem, suffix = 0
            while !ids.insert(value).inserted { suffix += 1; value = stem + "-" + String(suffix) }
            return value
        }
        let projectId = allocate("bundle-project")
        let take = Take(id: m.takeId,durationUs: m.durationUs,monitorSlotIds: m.targets.map(\.id),media: m.streams.filter { $0.writerStatus == .succeeded }.map { TakeMedia(monitorSlotId: $0.monitorSlotId,relativePath: $0.relativePath) })
        var tracks: [Track] = [], clips: [TimelineClip] = []
        for (i,slot) in m.targets.enumerated() {
            let trackId = allocate("bundle-track-\(i)"), clipId = allocate("bundle-clip-\(i)")
            tracks.append(Track(id: trackId,name: slot.name,monitorSlotId: slot.id,order: i))
            clips.append(TimelineClip(id: clipId,trackId: trackId,takeId: m.takeId,sourceInUs: 0,sourceOutUs: m.durationUs,timelineStartUs: 0,speed: 1,linkGroupId: "bundle-link"))
        }
        let project = MotionProject(id: projectId,monitorSlots: m.targets,takes: [take],tracks: tracks,clips: clips,events: events)
        _ = try RunPlanCompiler.compile(project)
        return project
    }
}
