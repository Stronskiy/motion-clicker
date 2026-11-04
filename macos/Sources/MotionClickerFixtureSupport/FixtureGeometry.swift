import Foundation

public struct FixtureGeometry: Codable, Equatable, Sendable {
    public let originX: Double
    public let originY: Double
    public let width: Double
    public let height: Double
    public let scale: Double
    public init(originX: Double, originY: Double, width: Double, height: Double, scale: Double) {
        self.originX = originX; self.originY = originY; self.width = width; self.height = height; self.scale = scale
    }
    public func contains(x: Double, y: Double) -> Bool { x.isFinite && y.isFinite && x >= 0 && y >= 0 && x < width && y < height }
    public func nativePoint(x: Double, y: Double) -> (Double, Double) { (originX + x, originY + height - y) }
}
