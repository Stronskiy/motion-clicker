import Foundation

/// UI positions never alter source timestamps. Clamp before Double→Int64 conversion
/// because Double(Int64.max) rounds upward to 2^63.
enum SourcePlaybackTime {
    static func sourceUs(_ value: Double) -> Int64 {
        guard value.isFinite, value > 0 else { return 0 }
        if value >= Double(Int64.max) { return Int64.max }
        return Int64(value)
    }
    static func filePTS(sourceUs: Int64, mediaOffsetUs: Int64, durationUs: Int64) -> Int64 {
        guard sourceUs >= mediaOffsetUs else { return 0 }
        return min(durationUs, sourceUs - mediaOffsetUs)
    }
}
