import XCTest
import CoreMedia
import MotionClickerRecording
@testable import MotionClickerMacRecording

// A literal fixture anchor is known by construction; it is not native evidence.
let syntheticPointerClock = VerifiedPointerClock(anchor: ClockAnchor(sourceAnchorTicks: 0, hostAnchorUs: 0, ticksPerSecond: 1_000_000_000))

final class NativeTimingTests: XCTestCase {
    func testLargeRationalMachConversionAndOverflow() throws {
        // Above Double's exact integer range; 125/3 ns per tick.
        XCTAssertEqual(try NativeTime.microseconds(ticks: 9_007_199_254_740_993, numerator: 125, denominator: 3), 375_299_968_947_541)
        XCTAssertThrowsError(try NativeTime.microseconds(ticks: .max, numerator: .max, denominator: 1))
        XCTAssertThrowsError(try NativeTime.microseconds(ticks: 1, numerator: 1, denominator: 0))
    }
    func testSDKHostClockAgreesWithMachAbsoluteRationalConversion() throws {
        let ticks = mach_absolute_time()
        let sdk = CMTimeConvertScale(CMClockMakeHostTimeFromSystemUnits(ticks), timescale: 1_000_000, method: .roundTowardNegativeInfinity).value
        let converted = try NativeTime.hostUs(ticks)
        XCTAssertLessThanOrEqual(abs(try subtractNative(converted, sdk)), 1)
    }
    func testNativeSourceOffsetIsNotResetPerDisplay() throws {
        var t = FrameTimeline(epochUs: 1_000_000)
        XCTAssertNil(try t.propose(sourceUs: 999_999))
        XCTAssertEqual(try t.propose(sourceUs: 1_500_000)?.ptsUs, 0)
        // A rejected encoder append must not establish the stream's first frame.
        let first = try XCTUnwrap(t.propose(sourceUs: 1_600_000))
        try t.accept(first)
        XCTAssertEqual(t.originUs, 600_000)
        XCTAssertEqual(try t.propose(sourceUs: 1_700_000)?.ptsUs, 100_000)
        XCTAssertThrowsError(try t.propose(sourceUs: 1_600_000))
        XCTAssertThrowsError(try t.propose(sourceUs: 1_600_001)) // overlaps nominal frame duration
    }
    func testSuspendClockDivergenceFailsInsteadOfReanchoring() throws {
        let guardClock = SuspendClockGuard(initialDifferenceUs: 5_000_000)
        XCTAssertNoThrow(try guardClock.check(differenceUs: 5_000_100))
        XCTAssertThrowsError(try guardClock.check(differenceUs: 6_000_000))
        XCTAssertThrowsError(try guardClock.check(differenceUs: 4_000_000))
    }
    func testVerifiedFixtureAnchorMapsSourceOffsetWithoutUsingDeliveryTime() throws {
        let clock = VerifiedPointerClock(anchor: ClockAnchor(sourceAnchorTicks: 1_000_000_000, hostAnchorUs: 1_500_000, ticksPerSecond: 1_000_000_000))
        XCTAssertEqual(try NativeTime.pointerUs(timestampNs: 1_500_000_000, nowUs: 2_000_001, clock: clock), 2_000_000)
        XCTAssertEqual(try NativeTime.pointerUs(timestampNs: 1_500_000_000, nowUs: 2_500_000, clock: clock), 2_000_000)
    }
    func testInWindowWrongDomainIsRejectedWithoutVerifiedAnchor() {
        // Delivery age500ms used to pass despite no source-domain evidence.
        XCTAssertThrowsError(try NativeTime.pointerUs(timestampNs: 1_500_000_000, nowUs: 2_000_000))
    }
    func testPointerClockFailsClosedWithoutReplacingSourceTime() throws {
        XCTAssertEqual(try NativeTime.pointerUs(timestampNs: 9_007_199_254_740_993, nowUs: 9_007_199_254_741, clock: syntheticPointerClock), 9_007_199_254_740)
        XCTAssertThrowsError(try NativeTime.pointerUs(timestampNs: 0, nowUs: 100, clock: syntheticPointerClock))
        XCTAssertThrowsError(try NativeTime.pointerUs(timestampNs: 2_000_000_000, nowUs: 1_000_000, clock: syntheticPointerClock))
        XCTAssertThrowsError(try NativeTime.pointerUs(timestampNs: 1, nowUs: 5_000_000, clock: syntheticPointerClock))
    }
}
