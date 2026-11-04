import XCTest
@testable import MotionClickerFixtureSupport

final class FixtureGeometryTests: XCTestCase {
    func testNegativeDesktopOriginFlipsOnlyLocalYWithoutApplyingBackingScale() {
        let geometry = FixtureGeometry(originX: -1440, originY: 200, width: 1440, height: 900, scale: 2)
        let point = geometry.nativePoint(x: 120, y: 80)
        XCTAssertEqual(point.0, -1320)
        XCTAssertEqual(point.1, 1020)
    }
    func testHalfOpenCanvasRejectsNonfiniteAndOutsideDeliveryWithoutClamping() {
        let geometry = FixtureGeometry(originX: 0, originY: 0, width: 800, height: 600, scale: 1)
        XCTAssertTrue(geometry.contains(x: 0, y: 0))
        for point in [(800.0, 20.0), (20, 600), (-1, 30), (Double.nan, 0), (0, Double.infinity)] {
            XCTAssertFalse(geometry.contains(x: point.0, y: point.1))
        }
    }
}
