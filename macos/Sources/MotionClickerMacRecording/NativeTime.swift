import Foundation
import CoreMedia
import MotionClickerRecording

func requireNative(_ value: Bool, _ reason: String) throws {
    if !value { throw RecordingError(message: reason) }
}
func addNative(_ a: Int64, _ b: Int64) throws -> Int64 {
    let (r, overflow) = a.addingReportingOverflow(b)
    try requireNative(!overflow, "Native timestamp overflow"); return r
}
func subtractNative(_ a: Int64, _ b: Int64) throws -> Int64 {
    let (r, overflow) = a.subtractingReportingOverflow(b)
    try requireNative(!overflow, "Native timestamp overflow"); return r
}
/// Uses mach_absolute_time (CoreMedia host clock), never mach_continuous_time.
/// Source ticks are converted to integer microseconds before the portable clock mapper.
enum NativeTime {
    static func microseconds(ticks: UInt64, numerator: UInt32, denominator: UInt32) throws -> Int64 {
        try requireNative(denominator > 0 && numerator > 0, "Invalid mach timebase")
        let divisor = UInt64(denominator) * 1_000
        let product = ticks.multipliedFullWidth(by: UInt64(numerator))
        try requireNative(product.high < divisor, "Native timestamp overflow")
        let value = divisor.dividingFullWidth(product).quotient
        try requireNative(value <= UInt64(Int64.max), "Native timestamp overflow")
        return Int64(value)
    }
    static func hostUs(_ ticks: UInt64 = mach_absolute_time()) throws -> Int64 {
        var info = mach_timebase_info_data_t()
        try requireNative(mach_timebase_info(&info) == KERN_SUCCESS, "Unavailable mach timebase")
        return try microseconds(ticks: ticks, numerator: info.numer, denominator: info.denom)
    }
    /// CGEvent's documented startup nanoseconds are retained, NOT rebased to arrival.
    /// Runtime guard rejects a different sleep domain or implausibly delayed event.
    /// This guard bounds a hypothesis; physical source alignment still needs consent QA.
    static func pointerUs(timestampNs: UInt64, nowUs: Int64) throws -> Int64 {
        let us = timestampNs / 1_000
        try requireNative(timestampNs > 0 && us <= UInt64(Int64.max), "Invalid pointer source clock")
        let result = Int64(us), age = try subtractNative(nowUs, result)
        try requireNative(age >= 0 && age <= 1_000_000, "Pointer clock domain mismatch or excessive delivery delay")
        return result
    }
}
struct FrameStamp { let sourceUs: Int64; let ptsUs: Int64; let durationUs: Int64 }
struct FrameTimeline {
    let epochUs: Int64
    private(set) var originUs: Int64?
    private var lastSourceUs: Int64?
    private(set) var endUs: Int64 = 0
    init(epochUs: Int64) { self.epochUs = epochUs }
    mutating func propose(sourceUs: Int64, durationUs: Int64 = 33_333) throws -> FrameStamp? {
        let offset = try subtractNative(sourceUs, epochUs)
        if offset < 0 { return nil }
        let pts = try subtractNative(offset, originUs ?? offset)
        if let lastSourceUs {
            try requireNative(sourceUs > lastSourceUs && pts >= endUs, "Frame timestamp reversal or overlap")
        }
        try requireNative(durationUs > 0, "Invalid source frame duration")
        _ = try addNative(pts, durationUs)
        return FrameStamp(sourceUs: sourceUs, ptsUs: pts, durationUs: durationUs)
    }
    mutating func accept(_ stamp: FrameStamp) throws {
        if originUs == nil { originUs = try subtractNative(stamp.sourceUs, epochUs) }
        lastSourceUs = stamp.sourceUs
        endUs = try addNative(stamp.ptsUs, stamp.durationUs)
    }
}

struct SuspendClockGuard {
    let initialDifferenceUs: Int64
    func check(differenceUs: Int64) throws {
        let delta = try subtractNative(differenceUs, initialDifferenceUs)
        try requireNative(delta >= -1_000 && delta <= 1_000, "System suspend clock divergence; recording interrupted")
    }
    static func currentDifferenceUs() throws -> Int64 {
        // Compare domains solely to detect sleep; NEVER use continuous time as
        // a video/pointer epoch or anchor. Small tolerance covers read latency.
        try subtractNative(NativeTime.hostUs(mach_continuous_time()), NativeTime.hostUs())
    }
}
