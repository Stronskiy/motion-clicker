import Foundation

func recordingRequire(_ condition: Bool, _ message: String) throws {
    if !condition { throw RecordingError(message: message) }
}
func recordingAdd(_ a: Int64, _ b: Int64) throws -> Int64 {
    let (v, overflow) = a.addingReportingOverflow(b)
    try recordingRequire(!overflow, "Timestamp overflow"); return v
}
func recordingSubtract(_ a: Int64, _ b: Int64) throws -> Int64 {
    let (v, overflow) = a.subtractingReportingOverflow(b)
    try recordingRequire(!overflow, "Timestamp overflow"); return v
}
public struct MediaClockMapper {
    private let anchor: Int64, offset: Int64, scale: Int64
    public init(sourceAnchorTicks: Int64, hostAnchorUs: Int64, ticksPerSecond: Int64, epochHostUs: Int64) throws {
        try recordingRequire(ticksPerSecond > 0 && ticksPerSecond <= Int32.max, "Invalid source clock scale")
        anchor = sourceAnchorTicks; scale = ticksPerSecond
        offset = try recordingSubtract(hostAnchorUs, epochHostUs)
    }
    public func map(sourceTicks: Int64) throws -> Int64 {
        let delta = try recordingSubtract(sourceTicks, anchor)
        let (whole, overflow) = (delta / scale).multipliedReportingOverflow(by: 1000000)
        try recordingRequire(!overflow, "Timestamp overflow")
        // Remainder magnitude < Int32.max: this multiplication cannot overflow Int64.
        let numerator = (delta % scale) * 1000000
        let fraction = numerator / scale - (numerator < 0 && numerator % scale != 0 ? 1 : 0)
        return try recordingAdd(offset, recordingAdd(whole, fraction))
    }
}
