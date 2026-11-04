import SwiftUI
import AppKit
import UniformTypeIdentifiers
import Observation
import Carbon
import MotionClickerCore
import MotionClickerRuntime
import MotionClickerRecording

struct ClickPoint: Identifiable {
    let id=UUID()
    var displayID:String
    var x:Double=100
    var y:Double=100
    var pauseMs:Double=100
}
@MainActor @Observable final class ClickerModel {
    let operation = AppOperationGate()
    let controlRegions = ControlRegions()
    var recording: RecordingFlow!
    var recordSelected: Set<String> = []
    var bundle: InspectedBundle?
    var bundleRoot: URL?
    var generatedInspection = false
    var recoveryEntries: [URL] = []
    var mode="Quick Click"
    var displays:[RuntimeDisplay]=[]
    var selected:Set<String>=[]
    var points:[ClickPoint]=[]
    var currentPosition=false
    var button:MouseButton = .left
    var doubleClick=false
    var intervalMs:Double=100
    var delaySeconds:Double=3
    var limit="Count"
    var count:Int=10
    var durationSeconds:Double=10
    var hotkey=UInt32(kVK_F8)
    var status="Idle — no input is sent until you press Start"
    var snapshot=RunnerSnapshotView()
    var preview:[RuntimeStep]=[]
    var previewExpanded=true
    var previewPresentationRequest=UUID()
    var project:MotionProject?
    var projectName="No project loaded"
    var mapping:[String:String]=[:]
    var error:String?
    private let provider=MacMonitors()
    private let backend=MacInput()
    private let emergency=EmergencyHotkey()
    private var worker:RunWorker?
    private let runPreparation:(() throws -> Void)?
    private var panels:[NSPanel]=[]
    init(worker:RunWorker? = nil, runPreparation:(() throws -> Void)? = nil) {
        self.worker=worker; self.runPreparation=runPreparation
        recording = RecordingFlow(gate: operation, makeCapture: { [controlRegions] in NativeRecordingCapture(regions: controlRegions) })
        refreshDisplays()
        emergency.stop = { [weak self] in self?.stop("Emergency hotkey") }
        let center=NSWorkspace.shared.notificationCenter
        for name in [NSWorkspace.willSleepNotification,NSWorkspace.screensDidSleepNotification,NSWorkspace.sessionDidResignActiveNotification] {
            center.addObserver(forName:name,object:nil,queue:.main) { [weak self] _ in
                MainActor.assumeIsolated { self?.backend.setSession(false); self?.stop("Sleep or session inactive — restart required") }
            }
        }
        for name in [NSWorkspace.didWakeNotification,NSWorkspace.sessionDidBecomeActiveNotification] {
            center.addObserver(forName:name,object:nil,queue:.main) { [weak self] _ in MainActor.assumeIsolated { self?.backend.setSession(true) } }
        }
        NotificationCenter.default.addObserver(forName:NSApplication.willTerminateNotification,object:nil,queue:.main) { [weak self] _ in MainActor.assumeIsolated { self?.stop("Application closing") } }
    }
    var active:Bool { operation.busy }
    var canRun: Bool {
        guard !active || runPaused, mode != "Record" else { return false }
        guard mode == "Project" else { return true }
        guard let project, bundle == nil || bundle?.executable == true else { return false }
        let targets=project.monitorSlots.compactMap { mapping[$0.id] }
        guard targets.count == project.monitorSlots.count, Set(targets).count == targets.count else { return false }
        return project.monitorSlots.allSatisfy { slot in
            guard let id=mapping[slot.id], let display=displays.first(where:{$0.id == id}) else { return false }
            return slot.geometry.size == display.geometry.size && slot.geometry.scaleFactor == display.geometry.scaleFactor && slot.geometry.rotationDegrees == display.geometry.rotationDegrees
        }
    }
    var runNeedsRelease: Bool { operation.operation == .runReleasePending }
    var runActive: Bool { operation.operation == .run }
    var runPaused: Bool { operation.operation == .runPaused }
    func refreshDisplays() {
        guard !active else { return }
        displays=provider.displays()
        if selected.isEmpty, let first=displays.first { selected=[first.id]; if points.isEmpty { points=[ClickPoint(displayID:first.id)] } }
    }
    func requestPermissions() {
        guard !active else { return }
        // Deliberate user action only. No changing System Settings on the user's behalf.
        _ = CGRequestPostEventAccess(); _ = CGRequestListenEventAccess()
        status="Review Accessibility and Input Monitoring in System Settings. Restart if macOS requests it."
    }
    func loadProject() {
        guard !active else { return }
        let panel=NSOpenPanel(); panel.allowedContentTypes=[.json]; panel.allowsMultipleSelection=false
        panel.title="Import domain-only JSON"
        panel.begin { [weak self] response in
            guard response == .OK, let url=panel.url, let self, self.operation.acquireOpen() else { return }
            Task {
                defer { self.operation.release() }
                do {
                    let loaded=try await Task.detached {
                        let values=try url.resourceValues(forKeys:[.fileSizeKey])
                        guard (values.fileSize ?? 0) <= 16_777_216 else { throw RuntimeFailure.unsafe("Project JSON exceeds 16 MB") }
                        let project=try JSONDecoder().decode(MotionProject.self,from:Data(contentsOf:url))
                        _=try RunProgram.project(project); return project
                    }.value
                    self.bundle=nil; self.bundleRoot=nil
                    self.project=loaded; self.projectName=url.lastPathComponent; self.mapping=[:]
                    self.preview=PreviewRunner.events(try RunProgram.project(loaded)); self.mode="Project"
                    self.status="Domain-only JSON imported. Map every monitor explicitly before Run. No source video is associated."
                } catch { self.error="Project cannot be loaded: \(error)" }
            }
        }
    }
    func program() throws -> RunProgram {
        if mode == "Project" {
            guard let project else { throw RuntimeFailure.unsafe("Load a domain project JSON first") }
            return try RunProgram.project(project)
        }
        let chosen=displays.filter { selected.contains($0.id) }
        guard !chosen.isEmpty, intervalMs.isFinite, intervalMs >= 20, intervalMs <= 86_400_000,
              count > 0, durationSeconds.isFinite, durationSeconds > 0, durationSeconds <= 604_800 else { throw RuntimeFailure.unsafe("Choose displays and valid limits; minimum interval is 20 ms") }
        let slots=chosen.map { MonitorSlot(id:$0.id,name:$0.name,geometry:$0.geometry) }
        for point in points where !currentPosition {
            guard let display=chosen.first(where:{$0.id==point.displayID}) else { throw RuntimeFailure.unsafe("Choose a selected display for each point") }; _ = try SafeRunner.nativePoint(Point(x:point.x,y:point.y),display:display)
        }
        let targets:[(String,Point?)]=currentPosition ? [(chosen[0].id,nil)] : points.map { ($0.displayID,Point(x:$0.x,y:$0.y)) }
        guard targets.allSatisfy({ selected.contains($0.0) }) else { throw RuntimeFailure.unsafe("Each point must target a selected display") }
        let basic=try RunProgram.quick(slots:slots,points:targets,button:button,doubleClick:doubleClick,intervalUs:Int64(intervalMs*1_000),count:limit == "Count" ? Int64(count) : nil,durationUs:limit == "Duration" ? Int64(durationSeconds*1_000_000) : nil)
        if currentPosition { return basic }
        var steps:[RuntimeStep]=[]; var offset:Int64=0
        for (i,point) in points.enumerated() {
            guard point.pauseMs.isFinite, point.pauseMs >= 20, point.pauseMs <= 86_400_000 else { throw RuntimeFailure.unsafe("Each point pause must be between 20 ms and one day") }
            let perPoint=try RunProgram.quick(slots:slots,points:[targets[i]],button:button,doubleClick:doubleClick,intervalUs:Int64(point.pauseMs*1_000),count:1,durationUs:nil)
            steps += perPoint.steps.map { RuntimeStep(timeUs:$0.timeUs+offset,slot:$0.slot,point:$0.point,kind:$0.kind,button:$0.button,clickCount:$0.clickCount) }
            offset += Int64(point.pauseMs*1_000)
        }
        return RunProgram(steps:steps,slots:slots,periodUs:offset,count:basic.count,durationUs:basic.durationUs)
    }
    /// One presentation request for menu, keyboard, toolbar and visual QA.
    func requestPreview() {
        guard !active else { return }
        dryRun()
        previewExpanded=true
        previewPresentationRequest=UUID()
    }
    func dryRun() {
        do { preview=PreviewRunner.events(try program()); status="Dry run — first \(preview.count) events; zero input. One serialized pointer, no physical simultaneity." }
        catch { self.error="Preview unavailable: \(error)" }
    }
    func start() {
        do {
            guard !active || runPaused else { return }
            guard canRun else { error="Run unavailable. Map each project monitor to a distinct current display with matching size, scale and rotation; partial or unverified source video is inspection only."; return }
            guard operation.acquireRun() else { return }
            guard delaySeconds.isFinite, delaySeconds >= 0, delaySeconds <= 86_400 else { throw RuntimeFailure.unsafe("Start delay must be 0…86400 seconds") }
            if mode == "Record" { throw RuntimeFailure.unsafe("Use the recording controls in this workspace") }
            if mode == "Project", let bundle, !bundle.executable { throw RuntimeFailure.unsafe(bundle.mediaError ?? "Partial recording is inspection only") }
            let plan=try program()
            if let runPreparation { try runPreparation() }
            else { try emergency.register(keyCode:hotkey); try backend.beginMonitoring() }
            let runner=worker?.runner ?? SafeRunner(clock:ContinuousClock(),monitors:provider,backend:backend)
            // Keep the runner across restarts if a release failed: creating a new one must not erase ownership.
            let explicitMapping = mode == "Project" ? mapping : Dictionary(uniqueKeysWithValues:plan.slots.map { ($0.id,$0.id) })
            try runner.start(plan,mapping:explicitMapping,delayUs:Int64(delaySeconds*1_000_000),hotkeyReady:true)
            worker=RunWorker(runner); worker?.launch(); poll()
        } catch { poll(); self.error="Start blocked: \(error)" }
    }
    func poll() {
        guard let report=worker?.snapshot else {
            if operation.operation == .run || operation.operation == .runPaused || operation.operation == .runReleasePending { operation.release() }
            if !operation.busy { emergency.unregister() }
            return
        }
        snapshot=RunnerSnapshotView(state:report.state,dispatched:report.dispatched,maxLagUs:report.maxLagUs,hasUnreleasedInput:report.hasUnreleasedInput)
        status=report.reason
        if report.hasUnreleasedInput && report.state != .armed && report.state != .running { operation.retainRunRelease() }
        else if report.state == .paused { operation.pauseRun() }
        else if !report.hasUnreleasedInput && report.state != .armed && report.state != .running && (operation.operation == .run || operation.operation == .runPaused || operation.operation == .runReleasePending) { operation.release() }
        if !operation.busy { emergency.unregister() }
    }
    func stop(_ reason:String="Stopped",paused:Bool=false) {
        // Retry only input this SafeRunner still owns, before routing capture cleanup.
        worker?.stop(reason,paused:paused); poll()
        if recording.ownsOperation {
            Task { await recording.stop(reason: reason == "Stopped" ? nil : reason); poll() }
            return
        }
        if !paused && operation.operation == .runPaused { operation.release(); emergency.unregister() }
    }
    func prepareRecording() {
        guard !active else { return }
        let chosen = displays.filter { recordSelected.contains($0.id) }
        guard !chosen.isEmpty else { error="Select at least one display to record."; return }
        let panel=NSSavePanel(); panel.title="New local recording"; panel.nameFieldStringValue="Recording.motionclicker"
        panel.canCreateDirectories=true; panel.message="Choose a new .motionclicker directory. Existing bundles are never overwritten."
        panel.begin { [weak self] response in
            guard response == .OK, let url=panel.url, let self, !self.active else { return }
            let targets=chosen.map { RecordingTarget(slot:MonitorSlot(id:$0.id,name:$0.name,geometry:$0.geometry),display:$0) }
            Task { await self.recording.prepare(targets:targets,destination:url) }
        }
    }
    func beginRecordingCountdown() {
        do { try emergency.register(keyCode:hotkey); recording.countdown() }
        catch { self.error="Emergency stop unavailable: \(error)" }
    }
    func openBundle() {
        guard !active else { return }
        let panel=NSOpenPanel(); panel.title="Open recorded bundle"; panel.canChooseDirectories=true; panel.canChooseFiles=false; panel.allowsMultipleSelection=false
        panel.begin { [weak self] response in
            guard response == .OK, let url=panel.url, let self else { return }
            self.openBundle(url)
        }
    }
    func openBundle(_ url: URL) {
        guard operation.acquireOpen() else { return }
        status="Opening bundle and decoding source video…"
        Task {
            defer { operation.release() }
            do { install(try await BundleIO.open(url),root:url) }
            catch {
                recoveryEntries=await BundleIO.recovery(url)
                let message=(error as? RecordingError)?.message ?? error.localizedDescription
                self.error="Bundle cannot be opened: \(message). \(recoveryEntries.isEmpty ? "No recoverable entries found." : "Recoverable data remains at " + url.path)"
            }
        }
    }
    func inspectSavedRecording() {
        guard let saved=recording.saved, let root=recording.destination, !active else { return }
        install(saved,root:root)
    }
    private func install(_ saved: InspectedBundle, root: URL) {
        bundle=saved; bundleRoot=root; project=saved.snapshot.project; projectName=root.lastPathComponent; mapping=[:]
        preview=(try? RunProgram.project(saved.snapshot.project)).map { PreviewRunner.events($0) } ?? []
        mode="Project"
        status=saved.mediaError ?? (saved.executable ? "Source video verified. Map every monitor explicitly before Run." : "Partial recording — inspection only.")
    }
    func identify() {
        guard !active else { return }
        panels.forEach { $0.close() }; panels=[]
        for screen in NSScreen.screens {
            let number=screen.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber
            let panel=NSPanel(contentRect:NSRect(x:screen.frame.midX-150,y:screen.frame.midY-60,width:300,height:120),styleMask:[.borderless],backing:.buffered,defer:false)
            panel.level = .floating; panel.isOpaque=false; panel.backgroundColor = .clear; panel.ignoresMouseEvents=true
            panel.contentView=NSHostingView(rootView:Text("Display \(number?.stringValue ?? "?")").font(.largeTitle.bold()).padding(30).background(.regularMaterial,in:RoundedRectangle(cornerRadius:20)))
            panel.orderFrontRegardless(); panels.append(panel)
        }
        Task { try? await Task.sleep(for:.seconds(2)); panels.forEach { $0.close() }; panels=[] }
    }
    func move(_ id:UUID,by delta:Int) {
        guard let index=points.firstIndex(where: { $0.id == id }), points.indices.contains(index+delta) else { return }
        points.swapAt(index,index+delta)
    }
}
struct RunnerSnapshotView {
    var state:RunnerState = .idle
    var dispatched:Int64=0
    var maxLagUs:Int64=0
    var hasUnreleasedInput=false
}

@main struct MotionClickerApp: App {
    @State private var model=ClickerModel()
    @NSApplicationDelegateAdaptor(RecordingAppDelegate.self) private var appDelegate
    var body: some Scene {
        Window("Motion Clicker",id:"main") { WorkspaceView(model:model).frame(minWidth:980,minHeight:668).onAppear { appDelegate.model=model } }
            .defaultLaunchBehavior(.presented).defaultSize(width:1180,height:800).windowResizability(.contentMinSize).windowToolbarStyle(.unified)
            .commands {
                CommandGroup(replacing:.newItem) {
                    Button("Open Recorded Bundle…",action:model.openBundle).keyboardShortcut("o").disabled(model.active)
                    Button("Import Domain JSON…",action:model.loadProject).disabled(model.active)
                }
                CommandMenu("Run") {
                    Button("Preview",action:model.requestPreview).keyboardShortcut("p",modifiers:[.command,.shift]).disabled(model.active)
                    Button("Run",action:model.start).keyboardShortcut("r",modifiers:[.command,.shift]).disabled(!model.canRun)
                    Button("Stop") { model.stop() }.keyboardShortcut(".")
                }
            }
    }
}
