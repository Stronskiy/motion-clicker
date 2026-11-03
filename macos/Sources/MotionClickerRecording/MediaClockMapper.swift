import Foundation
public struct MediaClockMapper {
    public init(sourceAnchorTicks: Int64, hostAnchorUs: Int64, ticksPerSecond: Int64, epochHostUs: Int64) throws { }
    public func map(sourceTicks: Int64) throws -> Int64 { throw RecordingError(message: "Not implemented") }
}
