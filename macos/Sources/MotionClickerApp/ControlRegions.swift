import AppKit
import SwiftUI
import Synchronization

/// Native tap reads only this lock-protected immutable value. AppKit stays on main.
final class ControlRegions: Sendable {
    private let rects = Mutex<[CGRect]>([])
    func replace(appKitRects: [CGRect], primaryHeight: CGFloat) {
        let converted = appKitRects.map { CGRect(x: $0.minX, y: primaryHeight - $0.maxY, width: $0.width, height: $0.height) }
        rects.withLock { $0 = converted }
    }
    func contains(x: Double, y: Double) -> Bool { rects.withLock { $0.contains { $0.contains(CGPoint(x: x, y: y)) } } }
}

struct ControlWindowObserver: NSViewRepresentable {
    let regions: ControlRegions
    let close: () -> Void
    func makeNSView(context: Context) -> ObserverView {
        let view = ObserverView(); view.regions = regions; view.closeRequest = close; return view
    }
    func updateNSView(_ view: ObserverView, context: Context) { view.updateRegions() }
    @MainActor final class ObserverView: NSView, NSWindowDelegate {
        var regions: ControlRegions?
        var closeRequest: (() -> Void)?
        private var observers: [NSObjectProtocol] = []
        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            observers.forEach(NotificationCenter.default.removeObserver); observers = []
            guard let window else { updateRegions(); return }
            window.delegate = self
            for name in [NSWindow.didMoveNotification, NSWindow.didResizeNotification, NSWindow.didMiniaturizeNotification, NSWindow.didDeminiaturizeNotification, NSWindow.didChangeOcclusionStateNotification, NSWindow.didChangeScreenNotification] {
                observers.append(NotificationCenter.default.addObserver(forName: name, object: nil, queue: .main) { [weak self] _ in MainActor.assumeIsolated { self?.updateRegions() } })
            }
            updateRegions()
        }
        func updateRegions() {
            let primary = NSScreen.screens.first(where: { ($0.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value == CGMainDisplayID() })
            let height = primary?.frame.height ?? 0
            // All visible app windows, including transient permission/error surfaces.
            regions?.replace(appKitRects: NSApp.windows.filter { $0.isVisible && !$0.isMiniaturized }.map(\.frame), primaryHeight: height)
        }
        func windowShouldClose(_ sender: NSWindow) -> Bool { closeRequest?(); return true }
        func windowWillClose(_ notification: Notification) { regions?.replace(appKitRects: [], primaryHeight: 0) }
    }
}
