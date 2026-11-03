import CoreGraphics
import MotionClickerCore
import MotionClickerRecording

struct NativeDisplaySnapshot: Equatable, Sendable {
    let id: UInt32
    let geometry: DisplayGeometry
    let bounds: Rect
    let pixelWidth: Int, pixelHeight: Int
    static func select(targets: [RecordingTarget], inventory: [NativeDisplaySnapshot]) throws -> [NativeDisplaySnapshot] {
        try requireNative(!targets.isEmpty, "Select at least one display")
        return try targets.map { target in
            guard let id = UInt32(target.display.id), String(id) == target.display.id,
                  let display = inventory.first(where: { $0.id == id }) else {
                throw RecordingError(message: "Selected display is unavailable")
            }
            try requireNative(display.geometry == target.display.geometry && display.geometry == target.slot.geometry && display.bounds == target.display.nativeBounds, "Selected display geometry changed")
            let expectedWidth = display.geometry.size.width * display.geometry.scaleFactor
            let expectedHeight = display.geometry.size.height * display.geometry.scaleFactor
            try requireNative(Double(display.pixelWidth) == expectedWidth && Double(display.pixelHeight) == expectedHeight,
                              "Selected display pixel geometry changed or is unsupported")
            try requireNative(display.pixelWidth > 0 && display.pixelHeight > 0 && display.pixelWidth % 2 == 0 && display.pixelHeight % 2 == 0, "Unsupported H.264 display size")
            return display
        }
    }
    static func current() throws -> [NativeDisplaySnapshot] {
        var count: UInt32 = 0
        try requireNative(CGGetActiveDisplayList(0, nil, &count) == .success, "Cannot enumerate displays")
        var ids = [CGDirectDisplayID](repeating: 0, count: Int(count))
        try requireNative(CGGetActiveDisplayList(count, &ids, &count) == .success, "Cannot enumerate displays")
        return ids.prefix(Int(count)).filter { CGDisplayMirrorsDisplay($0) == kCGNullDirectDisplay }.map { id in
            let b = CGDisplayBounds(id), w = CGDisplayPixelsWide(id), h = CGDisplayPixelsHigh(id)
            let geometry = DisplayGeometry(origin: Point(x: b.minX, y: b.minY), size: Size(width: b.width, height: b.height), scaleFactor: Double(w) / b.width, rotationDegrees: Int(CGDisplayRotation(id)))
            return NativeDisplaySnapshot(id: id, geometry: geometry, bounds: Rect(origin: geometry.origin, size: geometry.size), pixelWidth: w, pixelHeight: h)
        }.sorted { $0.id < $1.id }
    }
}
