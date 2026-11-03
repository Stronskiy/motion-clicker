import SwiftUI
import AppKit
import UniformTypeIdentifiers
import Observation
import Carbon
import MotionClickerCore
import MotionClickerRuntime

struct ClickPoint: Identifiable {
    let id=UUID()
    var displayID:String
    var x:Double=100
    var y:Double=100
    var pauseMs:Double=100
}
@MainActor @Observable final class ClickerModel {
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
    var project:MotionProject?
    var projectName="No project loaded"
    var mapping:[String:String]=[:]
    var error:String?
    private let provider=MacMonitors()
    private let backend=MacInput()
    private let emergency=EmergencyHotkey()
    private var worker:RunWorker?
    private var panels:[NSPanel]=[]
    init() {
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
    var active:Bool { snapshot.state == .running || snapshot.state == .armed }
    func refreshDisplays() {
        displays=provider.displays()
        if selected.isEmpty, let first=displays.first { selected=[first.id]; if points.isEmpty { points=[ClickPoint(displayID:first.id)] } }
    }
    func requestPermissions() {
        // Deliberate user action only. No changing System Settings on the user's behalf.
        _ = CGRequestPostEventAccess(); _ = CGRequestListenEventAccess()
        status="Review Accessibility and Input Monitoring in System Settings. Restart if macOS requests it."
    }
    func loadProject() {
        let panel=NSOpenPanel(); panel.allowedContentTypes=[.json]; panel.allowsMultipleSelection=false
        guard panel.runModal() == .OK, let url=panel.url else { return }
        do {
            let values=try url.resourceValues(forKeys:[.fileSizeKey]); guard (values.fileSize ?? 0) <= 16_777_216 else { throw RuntimeFailure.unsafe("Project JSON exceeds 16 MB") }
            let loaded=try JSONDecoder().decode(MotionProject.self,from:Data(contentsOf:url))
            let program=try RunProgram.project(loaded)
            stop("Project loaded")
            project=loaded; projectName=url.lastPathComponent; mapping=[:]; preview=PreviewRunner.events(program); mode="Project"
            status="Map every imported monitor explicitly before Start. Video playback and editing are unavailable in this preview."
        } catch { self.error="Project cannot be loaded: \(error)" }
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
    func dryRun() {
        do { preview=PreviewRunner.events(try program()); status="Dry run — first \(preview.count) events; zero input. One serialized pointer, no physical simultaneity." }
        catch { self.error="Preview unavailable: \(error)" }
    }
    func start() {
        do {
            guard !active else { return }
            guard delaySeconds.isFinite, delaySeconds >= 0, delaySeconds <= 86_400 else { throw RuntimeFailure.unsafe("Start delay must be 0…86400 seconds") }
            let plan=try program()
            try emergency.register(keyCode:hotkey)
            try backend.beginMonitoring()
            let runner=worker?.runner ?? SafeRunner(clock:ContinuousClock(),monitors:provider,backend:backend)
            // Keep the runner across restarts if a release failed: creating a new one must not erase ownership.
            let explicitMapping = mode == "Project" ? mapping : Dictionary(uniqueKeysWithValues:plan.slots.map { ($0.id,$0.id) })
            try runner.start(plan,mapping:explicitMapping,delayUs:Int64(delaySeconds*1_000_000),hotkeyReady:true)
            worker=RunWorker(runner); worker?.launch(); poll()
        } catch { emergency.unregister(); self.error="Start blocked: \(error)" }
    }
    func poll() {
        guard let report=worker?.runner.snapshot else { return }
        snapshot=RunnerSnapshotView(state:report.state,dispatched:report.dispatched,maxLagUs:report.maxLagUs)
        status=report.reason
        if !active { emergency.unregister() }
    }
    func stop(_ reason:String="Stopped",paused:Bool=false) { worker?.stop(reason,paused:paused); poll() }
    func identify() {
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
}

@main struct MotionClickerApp: App {
    @State private var model=ClickerModel()
    var body: some Scene {
        Window("Motion Clicker",id:"main") { WorkspaceView(model:model).frame(minWidth:980,minHeight:668) }
            .defaultSize(width:1180,height:800).windowResizability(.contentMinSize).windowToolbarStyle(.unified)
            .commands {
                CommandGroup(replacing:.newItem) { Button("Open Project JSON…",action:model.loadProject).keyboardShortcut("o").disabled(model.active) }
                CommandMenu("Run") {
                    Button("Preview",action:model.dryRun).keyboardShortcut("p",modifiers:[.command,.shift]).disabled(model.active)
                    Button("Run",action:model.start).keyboardShortcut("r",modifiers:[.command,.shift]).disabled(model.active)
                    Button("Stop") { model.stop() }.keyboardShortcut(".")
                }
            }
    }
}
