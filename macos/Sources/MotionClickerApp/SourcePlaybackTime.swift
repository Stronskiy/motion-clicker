import Foundation
import MotionClickerRecording

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
    /// Native playback must stop at the inspected clear interval, independently
    /// of the UI display ticker. Nil never grants a playable interval.
    static func playbackEndPTS(media:RecordedBundleMedia, window:SourceGapWindow?, sourceUs:Int64) -> Int64? {
        guard let window, window.kind == nil, window.contains(monitorSlotId:media.monitorSlotId,sourceUs:sourceUs),
              sourceUs >= media.mediaTimeZeroUs, sourceUs < media.mediaTimeZeroUs + media.encodedDurationUs else { return nil }
        return filePTS(sourceUs:min(window.endUs,media.mediaTimeZeroUs+media.encodedDurationUs),mediaOffsetUs:media.mediaTimeZeroUs,durationUs:media.encodedDurationUs)
    }

}
