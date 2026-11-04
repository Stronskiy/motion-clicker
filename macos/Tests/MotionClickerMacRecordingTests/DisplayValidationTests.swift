import XCTest
import MotionClickerCore
import MotionClickerRecording
@testable import MotionClickerMacRecording

final class DisplayValidationTests: XCTestCase {
    func testExactDisplaySelectionAndPixelConfigurationAreFrozen() throws {
        let targets = engineTargets()
        let inventory = targets.map { NativeDisplaySnapshot(id: UInt32($0.display.id)!, geometry: $0.display.geometry, bounds: $0.display.nativeBounds, pixelWidth: 64, pixelHeight: 64) }
        let selected = try NativeDisplaySnapshot.select(targets: targets, inventory: inventory)
        XCTAssertEqual(selected.map(\.id), [1, 2])
        XCTAssertThrowsError(try NativeDisplaySnapshot.select(targets: targets, inventory: Array(inventory.prefix(1))))
        var wrong = inventory
        wrong[0] = NativeDisplaySnapshot(id: 1, geometry: inventory[0].geometry, bounds: inventory[0].bounds, pixelWidth: 128, pixelHeight: 64)
        XCTAssertThrowsError(try NativeDisplaySnapshot.select(targets: targets, inventory: wrong))
        wrong[0] = NativeDisplaySnapshot(id: 1, geometry: DisplayGeometry(origin: Point(x: 10, y: 0), size: Size(width: 64, height: 64), scaleFactor: 1, rotationDegrees: 0), bounds: inventory[0].bounds, pixelWidth: 64, pixelHeight: 64)
        XCTAssertThrowsError(try NativeDisplaySnapshot.select(targets: targets, inventory: wrong))
    }
}
