import Foundation
import Darwin
import MotionClickerCore

struct RecordingValidationStatistics { let records: Int64; let maximumBufferedIDs: Int }

/// Mandatory persistence validation never retains historical actions. Only explicit project() does.
enum RecordingJournalValidation {
    static func validate(manifest m: RecordingManifest, scratchRoot: URL,
                         read: (_ visit: (RawPointerRecord) throws -> Void) throws -> Void) throws -> RecordingValidationStatistics {
        let take = Take(id: m.takeId,durationUs: m.durationUs,monitorSlotIds: m.targets.map(\.id),media: m.streams.filter { $0.writerStatus == .succeeded }.map { TakeMedia(monitorSlotId: $0.monitorSlotId,relativePath: $0.relativePath) })
        // Snapshot-only validation is bounded by selected monitors, not journal length.
        _ = try RunPlanCompiler.compile(MotionProject(id: "validation-" + UUID().uuidString,monitorSlots: m.targets,takes: [take]))
        try recordingRequire(m.incompleteGroupIds.count <= 1 && (m.state != .completed || m.incompleteGroupIds.isEmpty),"Invalid incomplete group summary")
        if let id = m.incompleteGroupIds.first { try validateID(id) }
        let slots = Dictionary(uniqueKeysWithValues: m.targets.map { ($0.id,$0) })
        let ids = try RecordingIdentityIndex(root: scratchRoot);defer { ids.close() }
        try ids.add("entity:" + m.takeId)
        for slot in m.targets { try ids.add("entity:" + slot.id) }
        var timing = PointerTimingValidation()
        var actual: Int64 = 0, lastTime: Int64 = -1
        var held: String?, button: MouseButton?, crossed = false
        try read { r in
            try timing.accept(r.event, basis: m.pointerTimingBasis)
            try recordingRequire(!crossed && Int64(r.sequence) == actual && r.timeUs >= 0 && r.timeUs >= lastTime && r.timeUs < m.durationUs,"Invalid raw journal ordering/range")
            try recordingRequire(!r.event.isOwnInjected && !r.event.isControl && r.event.nativePosition.x.isFinite && r.event.nativePosition.y.isFinite && recordingSubtract(r.event.hostTimeUs,m.epochHostUs) == r.timeUs,"Invalid raw capture timestamp/origin")
            lastTime = r.timeUs;actual = try recordingAdd(actual,1)
            guard let a = r.action else {
                try recordingRequire(held != nil && r.atomicGroupId == held && m.incompleteGroupIds.first == held,"Unmapped event outside incomplete hold")
                crossed = true;return
            }
            try validateID(a.id);try ids.add("entity:" + a.id)
            try recordingRequire(a.takeId == m.takeId && a.timeUs == r.timeUs && a.order == Int(r.sequence) && a.atomicGroupId == r.atomicGroupId && a.kind == r.event.kind && a.button == r.event.button && a.deltaX == r.event.deltaX && a.deltaY == r.event.deltaY,"Raw/projection mismatch")
            guard let slot = slots[a.monitorSlotId] else { throw RecordingError(message: "Unknown action monitor") }
            try recordingRequire(a.x.isFinite && a.y.isFinite && a.x >= 0 && a.y >= 0 && a.x < slot.geometry.size.width && a.y < slot.geometry.size.height,"Invalid recorded coordinate")
            if let group = a.atomicGroupId { try validateID(group) }
            switch a.kind {
            case .buttonDown:
                try recordingRequire(held == nil && a.button != nil && a.atomicGroupId != nil && a.deltaX == nil && a.deltaY == nil,"Invalid down or overlapping hold")
                held = a.atomicGroupId;button = a.button;try ids.add("group:" + held!)
            case .buttonUp:
                try recordingRequire(held != nil && a.atomicGroupId == held && a.button == button && a.deltaX == nil && a.deltaY == nil,"Unmatched up")
                held = nil;button = nil
            case .move:
                try recordingRequire(a.atomicGroupId == held && a.button == nil && a.deltaX == nil && a.deltaY == nil,"Invalid move ownership")
            case .scroll:
                try recordingRequire(held == nil && a.atomicGroupId == nil && a.button == nil && a.deltaX?.isFinite == true && a.deltaY?.isFinite == true,"Invalid scroll ownership")
            }
        }
        try recordingRequire(actual == m.rawEventCount && m.incompleteGroupIds == (held.map { [$0] } ?? []),"Journal count or incomplete ownership mismatch")
        try ids.finish()
        return RecordingValidationStatistics(records: actual,maximumBufferedIDs: ids.maximumBufferedIDs)
    }
    private static func validateID(_ id: String) throws {
        func alnum(_ b: UInt8) -> Bool { (48...57).contains(b) || (65...90).contains(b) || (97...122).contains(b) }
        try recordingRequire(!id.isEmpty && id.utf8.count <= 128 && id.utf8.first.map(alnum) == true && id.utf8.allSatisfy { alnum($0) || $0 == 46 || $0 == 95 || $0 == 45 },"Invalid source ID")
    }
}

/// Bounded external sort: 4096 keys per run, two input cursors per merge, numeric run counters.
private final class RecordingIdentityIndex {
    private let directory: URL
    private var keys: [String] = [], runs: Int64 = 0
    private(set) var maximumBufferedIDs = 0
    init(root: URL) throws {
        directory = root.appendingPathComponent(".validation-" + UUID().uuidString)
        guard mkdir(directory.path,S_IRWXU) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        keys.reserveCapacity(4096)
    }
    func close() {
        // The flat scratch directory can have many runs: enumerate lazily during failure cleanup.
        if let entries = FileManager.default.enumerator(at: directory,includingPropertiesForKeys: nil,options: [.skipsSubdirectoryDescendants]) {
            for case let file as URL in entries { try? FileManager.default.removeItem(at: file) }
        }
        try? FileManager.default.removeItem(at: directory)
    }
    func add(_ key: String) throws {
        keys.append(key);maximumBufferedIDs = max(maximumBufferedIDs,keys.count)
        if keys.count == 4096 { try spill() }
    }
    private func path(_ pass: Int, _ run: Int64) -> URL { directory.appendingPathComponent("p\(pass)-r\(run)") }
    private func spill() throws {
        guard !keys.isEmpty else { return }
        keys.sort()
        if keys.count > 1 { for i in 1..<keys.count { try recordingRequire(keys[i] != keys[i-1],"Duplicate source identity") } }
        try Data((keys.joined(separator: "\n") + "\n").utf8).write(to: path(0,runs),options: .withoutOverwriting)
        runs = try recordingAdd(runs,1);keys.removeAll(keepingCapacity: true)
    }
    func finish() throws {
        try spill();var pass = 0
        while runs > 1 {
            var next: Int64 = 0, i: Int64 = 0
            while i < runs {
                let first = path(pass,i), output = path(pass+1,next)
                if i+1 == runs { try FileManager.default.moveItem(at: first,to: output) }
                else {
                    let second = path(pass,i+1)
                    try merge(first,second,output)
                    try FileManager.default.removeItem(at: first);try FileManager.default.removeItem(at: second)
                }
                i += 2;next += 1
            }
            runs = next;pass += 1
        }
    }
    private func merge(_ first: URL, _ second: URL, _ destination: URL) throws {
        let left = try IdentityReader(first), right = try IdentityReader(second)
        defer { left.close();right.close() }
        let fd = open(destination.path,O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW,S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        let output = FileHandle(fileDescriptor: fd,closeOnDealloc: true);defer { try? output.close() }
        var a = try left.next(), b = try right.next(), pending = Data()
        while a != nil || b != nil {
            let value: String
            if let av = a, let bv = b {
                try recordingRequire(av != bv,"Duplicate source identity")
                if av < bv { value = av;a = try left.next() } else { value = bv;b = try right.next() }
            } else if let av = a { value = av;a = try left.next() }
            else { value = b!;b = try right.next() }
            pending.append(contentsOf: value.utf8);pending.append(10)
            if pending.count >= 65536 { try output.write(contentsOf: pending);pending.removeAll(keepingCapacity: true) }
        }
        if !pending.isEmpty { try output.write(contentsOf: pending) }
    }
}
private final class IdentityReader {
    private let handle: FileHandle
    private var pending = Data()
    init(_ path: URL) throws { handle = try FileHandle(forReadingFrom: path) }
    func close() { try? handle.close() }
    func next() throws -> String? {
        while true {
            if let end = pending.firstIndex(of: 10) {
                guard let value = String(data: pending[..<end],encoding: .utf8) else { throw RecordingError(message: "Invalid identity spool") }
                pending.removeSubrange(...end);return value
            }
            try recordingRequire(pending.count <= 135,"Invalid identity spool line")
            guard let bytes = try handle.read(upToCount: 65536), !bytes.isEmpty else {
                try recordingRequire(pending.isEmpty,"Truncated identity spool");return nil
            }
            pending.append(bytes)
        }
    }
}
