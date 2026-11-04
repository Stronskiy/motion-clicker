import AppKit
import Darwin
import MotionClickerFixtureSupport

@main
struct FixtureMain {
    @MainActor static func main() {
        let app = NSApplication.shared
        let delegate = FixtureDelegate()
        app.delegate = delegate
        app.setActivationPolicy(.regular)
        withExtendedLifetime(delegate) { app.run() }
    }
}

@MainActor
final class FixtureDelegate: NSObject, NSApplicationDelegate {
    private var window: NSWindow!
    private let displays = NSPopUpButton()
    private let destination = NSTextField(wrappingLabelWithString: "Choose a new local log folder. No screen capture or input injection.")
    private var folder: URL?
    private var screens: [NSScreen] = []
    private var canvases: [CanvasController] = []

    func applicationDidFinishLaunching(_ notification: Notification) {
        window = NSWindow(contentRect: NSRect(x: 160, y: 160, width: 700, height: 360),
                          styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: false)
        window.title = "Motion Clicker · Generated target fixture"
        let title = NSTextField(labelWithString: "Controlled recording target")
        title.font = .systemFont(ofSize: 24, weight: .semibold)
        let info = NSTextField(wrappingLabelWithString: "Separate process · generated content only. Choose a display and open its canvas. Cover selected display is a deliberate action; verify every intended surface before recording. This fixture never starts the recorder.")
        screens = NSScreen.screens
        for (index, screen) in screens.enumerated() {
            displays.addItem(withTitle: "\(index + 1). \(screen.localizedName) · \(Int(screen.frame.width)) × \(Int(screen.frame.height)) points")
        }
        let choose = NSButton(title: "Choose log folder…", target: self, action: #selector(chooseFolder))
        let open = NSButton(title: "Open selected canvas", target: self, action: #selector(openCanvas))
        let protocolText = NSTextField(wrappingLabelWithString: "Reference sequence: left, right, middle, double-left, drag, second target = 7 down/up pairs. Then two opposite scroll gestures. Callback counts vary. One physical pointer; no audio. Escape restores a covered canvas.")
        let stack = NSStackView(views: [title, info, displays, choose, destination, open, protocolText])
        stack.orientation = .vertical; stack.alignment = .leading; stack.spacing = 14
        stack.translatesAutoresizingMaskIntoConstraints = false
        window.contentView!.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.leadingAnchor.constraint(equalTo: window.contentView!.leadingAnchor, constant: 24),
            stack.trailingAnchor.constraint(equalTo: window.contentView!.trailingAnchor, constant: -24),
            stack.topAnchor.constraint(equalTo: window.contentView!.topAnchor, constant: 24)
        ])
        window.center(); window.makeKeyAndOrderFront(nil)
        NSApp.activate()
    }
    @objc private func chooseFolder() {
        let panel = NSOpenPanel(); panel.canChooseDirectories = true; panel.canChooseFiles = false
        panel.canCreateDirectories = true; panel.allowsMultipleSelection = false
        panel.message = "Fixture logs stay local. Each canvas creates a fresh file without overwriting."
        if panel.runModal() == .OK, let url = panel.url { folder = url; destination.stringValue = url.path }
    }
    @objc private func openCanvas() {
        guard let folder, screens.indices.contains(displays.indexOfSelectedItem) else {
            destination.stringValue = "Choose a local log folder and a display first."; return
        }
        let screen = screens[displays.indexOfSelectedItem]
        let id = UUID().uuidString
        do {
            let controller = try CanvasController(screen: screen, url: folder.appendingPathComponent("fixture-\(id).jsonl"), id: id)
            canvases.append(controller); controller.window.makeKeyAndOrderFront(nil)
        } catch { showError(error) }
    }
    private func showError(_ error: Error) {
        let alert = NSAlert(error: error); alert.runModal()
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}

@MainActor
final class CanvasController: NSObject, NSWindowDelegate {
    let window: NSWindow
    private let screen: NSScreen
    private let canvas: TargetCanvas
    private var oldFrame: NSRect?
    private let cover = NSButton(title: "Cover selected display", target: nil, action: nil)
    init(screen: NSScreen, url: URL, id: String) throws {
        self.screen = screen
        var timebase = mach_timebase_info_data_t(); mach_timebase_info(&timebase)
        let displayId = (screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.stringValue ?? "unknown"
        let journal = try FixtureJournal(url: url, canvasId: id, displayId: displayId,
            hostClock: ["name": "mach_continuous_time", "units": "ticks", "numerator": String(timebase.numer), "denominator": String(timebase.denom), "ratioUnits": "nanosecondsPerTick"])
        canvas = TargetCanvas(journal: journal, label: screen.localizedName)
        window = NSWindow(contentRect: NSRect(x: screen.visibleFrame.minX + 60, y: screen.visibleFrame.minY + 60, width: 900, height: 660),
                          styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        super.init()
        window.title = "Generated canvas · \(screen.localizedName)"
        window.delegate = self; window.minSize = NSSize(width: 640, height: 480)
        let root = NSView(); window.contentView = root
        cover.target = self; cover.action = #selector(toggleCover)
        let close = NSButton(title: "Close canvas and flush log", target: self, action: #selector(closeCanvas))
        let instructions = NSTextField(labelWithString: "Only the target area is logged · Escape restores · \(url.lastPathComponent)")
        instructions.lineBreakMode = .byTruncatingMiddle
        let controls = NSStackView(views: [cover, close, instructions]); controls.spacing = 12
        controls.translatesAutoresizingMaskIntoConstraints = false; canvas.translatesAutoresizingMaskIntoConstraints = false
        root.addSubview(controls); root.addSubview(canvas)
        NSLayoutConstraint.activate([
            controls.leadingAnchor.constraint(equalTo: root.leadingAnchor, constant: 12),
            controls.trailingAnchor.constraint(lessThanOrEqualTo: root.trailingAnchor, constant: -12),
            controls.topAnchor.constraint(equalTo: root.topAnchor, constant: 12),
            canvas.leadingAnchor.constraint(equalTo: root.leadingAnchor), canvas.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            canvas.topAnchor.constraint(equalTo: controls.bottomAnchor, constant: 12), canvas.bottomAnchor.constraint(equalTo: root.bottomAnchor)
        ])
        canvas.restore = { [weak self] in if self?.oldFrame != nil { self?.toggleCover() } }
        window.makeFirstResponder(canvas); canvas.start()
    }
    @objc private func toggleCover() {
        if let frame = oldFrame {
            oldFrame = nil; window.styleMask = [.titled, .closable, .miniaturizable, .resizable]
            window.level = .normal; window.setFrame(frame, display: true); cover.title = "Cover selected display"
        } else {
            oldFrame = window.frame; window.styleMask = [.borderless, .resizable]
            window.level = .floating; window.setFrame(screen.frame, display: true); cover.title = "Restore window (Escape)"
        }
        window.makeFirstResponder(canvas)
    }
    @objc private func closeCanvas() { window.close() }
    func windowWillClose(_ notification: Notification) { canvas.finish() }
}

@MainActor
final class TargetCanvas: NSView {
    private let journal: FixtureJournal
    private let label: String
    private var timer: Timer?
    private var reference: UInt64 = 0
    private var hostStamp: UInt64 = 0
    private var deliveries = 0
    private var downs = 0
    private var ups = 0
    private var scrollY = 0.0
    private var last = "Ready for ordinary physical input"
    private var lastPoint: NSPoint?
    private var failed = false
    private var tracking: NSTrackingArea?
    var restore: (() -> Void)?
    override var isFlipped: Bool { true }
    override var acceptsFirstResponder: Bool { true }
    init(journal: FixtureJournal, label: String) {
        self.journal = journal; self.label = label; super.init(frame: .zero)
        setAccessibilityElement(true); setAccessibilityRole(.group)
        setAccessibilityLabel("Generated target canvas for \(label). Ordinary mouse input only.")
    }
    required init?(coder: NSCoder) { fatalError("init(coder:) is not supported") }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let tracking { removeTrackingArea(tracking) }
        let area = NSTrackingArea(rect: .zero, options: [.activeAlways, .inVisibleRect, .mouseMoved], owner: self)
        addTrackingArea(area); tracking = area
    }
    func start() {
        timer = Timer.scheduledTimer(withTimeInterval: 0.1, repeats: true) { [weak self] _ in
            MainActor.assumeIsolated { self?.mark() }
        }
    }
    func finish() {
        timer?.invalidate(); timer = nil
        do { try journal.close() } catch { fail(error) }
    }
    private var geometry: FixtureGeometry {
        let origin = window?.convertPoint(toScreen: convert(NSPoint(x: 0, y: bounds.height), to: nil)) ?? .zero
        return FixtureGeometry(originX: origin.x, originY: origin.y, width: bounds.width, height: bounds.height,
                               scale: Double(window?.backingScaleFactor ?? 1))
    }
    private func mark() {
        guard !failed, bounds.width > 0, bounds.height > 0 else { return }
        let stamp = mach_continuous_time()
        do {
            try journal.append(hostStamp: stamp, kind: "marker", geometry: geometry)
            hostStamp = stamp; reference += 1; needsDisplay = true
        } catch { fail(error) }
    }
    private func receive(_ event: NSEvent, kind: String, button: Int? = nil) {
        guard !failed else { return }
        let stamp = mach_continuous_time()
        let point = convert(event.locationInWindow, from: nil)
        do {
            try journal.append(hostStamp: stamp, kind: kind, geometry: geometry, button: button,
                x: point.x, y: point.y, deltaX: kind == "scroll" ? event.scrollingDeltaX : 0,
                deltaY: kind == "scroll" ? event.scrollingDeltaY : 0, nativeEventStamp: String(event.timestamp))
            deliveries += 1
            if kind == "down" { downs += 1 }
            if kind == "up" { ups += 1 }
            if kind == "scroll" { scrollY += event.scrollingDeltaY }
            last = "\(kind) · button \(button.map(String.init) ?? "–") · target event \(deliveries) · clickCount \(event.clickCount)"
            lastPoint = point; needsDisplay = true
        } catch { fail(error) }
    }
    private func fail(_ error: Error) {
        failed = true; timer?.invalidate(); timer = nil
        last = "LOG FAILED — stop acceptance: \(error.localizedDescription)"; needsDisplay = true
    }
    override func keyDown(with event: NSEvent) { if event.keyCode == 53 { restore?() } else { super.keyDown(with: event) } }
    override func mouseDown(with e: NSEvent) { receive(e, kind: "down", button: 0) }
    override func mouseUp(with e: NSEvent) { receive(e, kind: "up", button: 0) }
    override func rightMouseDown(with e: NSEvent) { receive(e, kind: "down", button: 1) }
    override func rightMouseUp(with e: NSEvent) { receive(e, kind: "up", button: 1) }
    override func otherMouseDown(with e: NSEvent) { receive(e, kind: "down", button: e.buttonNumber) }
    override func otherMouseUp(with e: NSEvent) { receive(e, kind: "up", button: e.buttonNumber) }
    override func mouseDragged(with e: NSEvent) { receive(e, kind: "drag", button: 0) }
    override func rightMouseDragged(with e: NSEvent) { receive(e, kind: "drag", button: 1) }
    override func otherMouseDragged(with e: NSEvent) { receive(e, kind: "drag", button: e.buttonNumber) }
    override func mouseMoved(with e: NSEvent) { receive(e, kind: "move") }
    override func scrollWheel(with e: NSEvent) { receive(e, kind: "scroll") }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.windowBackgroundColor.setFill(); bounds.fill()
        let corners: [(String, NSPoint, NSColor)] = [
            ("TOP LEFT", NSPoint(x: 20, y: 20), .systemBlue), ("TOP RIGHT", NSPoint(x: max(20, bounds.width - 180), y: 20), .systemOrange),
            ("BOTTOM LEFT", NSPoint(x: 20, y: max(20, bounds.height - 60)), .systemGreen),
            ("BOTTOM RIGHT", NSPoint(x: max(20, bounds.width - 180), y: max(20, bounds.height - 60)), .systemPurple)]
        for (text, point, color) in corners {
            color.setFill(); NSRect(x: point.x, y: point.y, width: 18, height: 18).fill()
            drawText(text, x: point.x + 26, y: point.y, size: 13)
        }
        let left: CGFloat = 44
        drawText("Generated target · \(label)", x: left, y: 100, size: 24)
        drawText("Reference \(reference) · host ticks \(hostStamp)", x: left, y: 150, size: 20)
        drawText("Down \(downs)   Up \(ups)   Scroll Y \(String(format: "%.2f", scrollY))", x: left, y: 190, size: 20)
        drawText(last, x: left, y: 230, size: 14)
        drawText("Click target A", x: left, y: 310, size: 22)
        drawText("Drag within this generated canvas →", x: left, y: 360, size: 18)
        drawText("Target B / second-screen target", x: left, y: 410, size: 18)
        drawText("10 Hz UI reference requests are not capture or hardware timing.", x: left, y: 460, size: 13)
        if let point = lastPoint {
            NSColor.controlAccentColor.setFill()
            NSBezierPath(ovalIn: NSRect(x: point.x - 10, y: point.y - 10, width: 20, height: 20)).fill()
        }
    }
    private func drawText(_ value: String, x: CGFloat, y: CGFloat, size: CGFloat) {
        (value as NSString).draw(in: NSRect(x: x, y: y, width: max(0, bounds.width - x - 20), height: 62), withAttributes: [.font: NSFont.monospacedDigitSystemFont(ofSize: size, weight: .regular), .foregroundColor: failed ? NSColor.systemRed : NSColor.labelColor])
    }
}
