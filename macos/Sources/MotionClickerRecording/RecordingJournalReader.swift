import Foundation

enum RecordingJournalReader {
    static func read<T: Decodable>(path: URL,check: (URL) throws -> Void, visit: (T) throws -> Void) throws {
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
}
