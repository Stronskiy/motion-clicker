import Foundation
import Darwin

/// Shared path rules for capture publication and read-only recorded bundle access.
final class RecordingFiles {
    let root: URL
    init(root: URL) throws {
        let initial = root.standardizedFileURL
        let attrs = try FileManager.default.attributesOfItem(atPath: initial.path)
        try recordingRequire(attrs[.type] as? FileAttributeType == .typeDirectory, "Root must be an ordinary directory")
        self.root = initial.resolvingSymlinksInPath()
    }
    static func id(_ id: String) throws {
        let stem = id.split(separator: ".", omittingEmptySubsequences: false).first.map(String.init)?.uppercased() ?? ""
        let reserved = ["CON", "PRN", "AUX", "NUL"] + (1...9).flatMap { ["COM\($0)", "LPT\($0)"] }
        try recordingRequire(!id.hasSuffix(".") && !reserved.contains(stem), "Nonportable filesystem ID")
        try recordingRequire(id.range(of: "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$",options: .regularExpression) != nil, "Invalid capture ID")
    }
    static func exists(_ url: URL) -> Bool { (try? FileManager.default.attributesOfItem(atPath: url.path)) != nil }
    static func createDirectory(_ url: URL) throws {
        guard mkdir(url.path, S_IRWXU) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
    static func exclusiveFile(_ url: URL) throws -> FileHandle {
        let fd = open(url.path, O_WRONLY | O_CREAT | O_EXCL | O_NOFOLLOW, S_IRUSR | S_IWUSR)
        guard fd >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        return FileHandle(fileDescriptor: fd,closeOnDealloc: true)
    }
    // Check actual components and case aliases at each operation, including dangling symlinks.
    func check(_ url: URL) throws {
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
    func checkTree(_ url: URL) throws {
        try check(url)
        let attributes = try FileManager.default.attributesOfItem(atPath: url.path)
        let type = attributes[.type] as? FileAttributeType
        try recordingRequire(type == .typeDirectory || type == .typeRegular, "Unsupported capture file type")
        if type == .typeDirectory {
            for child in try FileManager.default.contentsOfDirectory(at: url, includingPropertiesForKeys: nil) { try checkTree(child) }
        }
    }
    func ensureDirectory(_ url: URL) throws {
        try check(url)
        if !Self.exists(url) { try Self.createDirectory(url) }
        try recordingRequire((try FileManager.default.attributesOfItem(atPath: url.path))[.type] as? FileAttributeType == .typeDirectory, "Expected directory")
    }
    static func moveExclusive(_ source: URL, _ destination: URL) throws {
        guard renamex_np(source.path,destination.path,UInt32(RENAME_EXCL)) == 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
    }
}
