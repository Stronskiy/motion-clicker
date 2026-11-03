public enum CoordinateMapper {
    /// Translation in canonical logical units. DPI/rotation conversion belongs to the OS adapter.
    public static func desktopPoint(local: Point, displayOrigin: Point) throws -> Point {
        let result = Point(x: local.x + displayOrigin.x, y: local.y + displayOrigin.y)
        guard finite(local), finite(displayOrigin), finite(result) else {
            throw ProjectError(.invalidCoordinate, path: "desktopPoint", message: "Coordinates must remain finite.")
        }
        return result
    }

    public static func aspectFitRect(sourceSize: Size, viewport: Rect) throws -> Rect {
        guard positive(sourceSize), valid(viewport) else {
            throw ProjectError(.invalidGeometry, path: "preview", message: "Source and viewport must have finite positive bounds.")
        }
        let widthScale = viewport.size.width / sourceSize.width
        let heightScale = viewport.size.height / sourceSize.height
        let scale = min(widthScale, heightScale)
        // Preserve the limiting viewport edge exactly instead of overflowing it by a rounding ULP.
        let size = widthScale <= heightScale
            ? Size(width: viewport.size.width, height: sourceSize.height * scale)
            : Size(width: sourceSize.width * scale, height: viewport.size.height)
        let result = Rect(origin: Point(x: viewport.origin.x + (viewport.size.width - size.width) / 2,
                                       y: viewport.origin.y + (viewport.size.height - size.height) / 2), size: size)
        guard scale.isFinite, scale > 0, valid(result) else {
            throw ProjectError(.invalidGeometry, path: "preview", message: "Aspect fit would overflow or collapse.")
        }
        return result
    }

    public static func previewPoint(local: Point, sourceSize: Size, viewport: Rect) throws -> Point {
        let fit = try aspectFitRect(sourceSize: sourceSize, viewport: viewport)
        guard contains(local, size: sourceSize) else {
            throw ProjectError(.invalidCoordinate, path: "local", message: "Point is outside the source's half-open bounds.")
        }
        let result = Point(x: fit.origin.x + (local.x / sourceSize.width) * fit.size.width,
                           y: fit.origin.y + (local.y / sourceSize.height) * fit.size.height)
        guard finite(result) else {
            throw ProjectError(.invalidCoordinate, path: "preview", message: "Preview coordinate would overflow.")
        }
        return result
    }

    /// Letterbox bars and the right/bottom edge are rejected; they never map to a neighboring display.
    public static func localPoint(preview: Point, sourceSize: Size, viewport: Rect) throws -> Point {
        let fit = try aspectFitRect(sourceSize: sourceSize, viewport: viewport)
        let offset = Point(x: preview.x - fit.origin.x, y: preview.y - fit.origin.y)
        guard contains(offset, size: fit.size) else {
            throw ProjectError(.invalidCoordinate, path: "preview", message: "Point lies outside the image or in a letterbox bar.")
        }
        let result = Point(x: (offset.x / fit.size.width) * sourceSize.width,
                           y: (offset.y / fit.size.height) * sourceSize.height)
        guard contains(result, size: sourceSize) else {
            throw ProjectError(.invalidCoordinate, path: "local", message: "Mapped point is outside the source bounds.")
        }
        return result
    }

    static func finite(_ point: Point) -> Bool { point.x.isFinite && point.y.isFinite }
    static func positive(_ size: Size) -> Bool {
        size.width.isFinite && size.height.isFinite && size.width > 0 && size.height > 0
    }
    static func valid(_ rect: Rect) -> Bool {
        finite(rect.origin) && positive(rect.size)
            && (rect.origin.x + rect.size.width).isFinite && (rect.origin.y + rect.size.height).isFinite
    }
    static func contains(_ point: Point, size: Size) -> Bool {
        finite(point) && point.x >= 0 && point.y >= 0 && point.x < size.width && point.y < size.height
    }
}
