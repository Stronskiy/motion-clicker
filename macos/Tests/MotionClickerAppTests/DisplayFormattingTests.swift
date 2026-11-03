import XCTest
@testable import MotionClickerApp

final class DisplayFormattingTests: XCTestCase {
    func testImportedGeometryAndPointLabelsSupportValuesBeyondIntRange() {
        XCTAssertEqual(DisplayFormatting.number(1e20), "100000000000000000000")
        XCTAssertEqual(DisplayFormatting.number(1e19), "10000000000000000000")
        XCTAssertEqual(DisplayFormatting.number(-1e20), "-100000000000000000000")
        XCTAssertFalse(DisplayFormatting.number(Double.greatestFiniteMagnitude).isEmpty)
    }
    func testOrdinaryCoordinatesKeepFractionalPrecision() {
        XCTAssertEqual(DisplayFormatting.number(123.25), "123.25")
        XCTAssertEqual(DisplayFormatting.number(100), "100")
    }
}
