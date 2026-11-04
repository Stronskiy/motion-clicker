import Foundation
import Darwin

/// Serial, target-owned diagnostic journal; never used as recorder source authority.
public final class FixtureJournal {
    private var file: FileHandle?
    private var sequence: UInt64 = 0
    private var lastStamp: UInt64 = 0
    private let encoder = JSONEncoder()
    public init(url: URL, canvasId: String, displayId: String, hostClock: [String: String]) throws {
        encoder.outputFormatting = [.sortedKeys, .withoutEscapingSlashes]
        let descriptor = Darwin.open(url.path, O_WRONLY | O_CREAT | O_EXCL, S_IRUSR | S_IWUSR)
        guard descriptor >= 0 else { throw POSIXError(POSIXErrorCode(rawValue: errno) ?? .EIO) }
        file = FileHandle(fileDescriptor: descriptor, closeOnDealloc: true)
        do {
            try write(Header(canvasId: canvasId, displayId: displayId, hostClock: hostClock))
        } catch {
            try? file?.close(); file = nil
            throw error
        }
    }
    public func append(hostStamp: UInt64, kind: String, geometry: FixtureGeometry, button: Int? = nil,
                       x: Double? = nil, y: Double? = nil, deltaX: Double = 0, deltaY: Double = 0,
                       nativeEventStamp: String? = nil) throws {
        guard hostStamp >= lastStamp else { throw JournalError.invalidOrder }
        let point = x.flatMap { px in y.map { geometry.nativePoint(x: px, y: $0) } }
        let sample = Sample(sequence: sequence + 1, hostStamp: hostStamp, kind: kind, button: button,
                            geometry: geometry, x: x, y: y, nativeX: point?.0, nativeY: point?.1,
                            inside: x.flatMap { px in y.map { geometry.contains(x: px, y: $0) } } ?? false,
                            deltaX: deltaX, deltaY: deltaY, nativeEventStamp: nativeEventStamp)
        try write(sample)
        sequence += 1; lastStamp = hostStamp
    }
    public func close() throws {
        guard let handle = file else { return }
        file = nil
        defer { try? handle.close() }
        try handle.synchronize()
    }
    private func write<T: Encodable>(_ value: T) throws {
        guard let file else { throw JournalError.closed }
        var data = try encoder.encode(value); data.append(10)
        try file.write(contentsOf: data)
    }
    private enum JournalError: Error { case invalidOrder, closed }
    private struct Header: Encodable {
        let recordType = "header"
        let schemaVersion = 1
        let platform = "macOS"
        let coordinateSpace = "desktopPointsBottomLeft"
        let nativeEventUnits = "NSEvent.timestamp seconds (native Double description)"
        let canvasId: String
        let displayId: String
        let hostClock: [String: String]
    }
    private struct Sample: Encodable {
        let recordType = "sample"
        let sequence: UInt64
        let hostStamp: UInt64
        let kind: String
        let button: Int?
        let geometry: FixtureGeometry
        let x: Double?
        let y: Double?
        let nativeX: Double?
        let nativeY: Double?
        let inside: Bool
        let deltaX: Double
        let deltaY: Double
        let nativeEventStamp: String?
    }
}
