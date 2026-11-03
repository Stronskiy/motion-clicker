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
        Window("Motion Clicker",id:"main") { MainView(model:model).frame(minWidth:850,minHeight:650) }
            .defaultSize(width:1040,height:790).windowResizability(.contentMinSize).windowToolbarStyle(.unified)
            .commands {
                CommandGroup(replacing:.newItem) { Button("Open Project JSON…",action:model.loadProject).keyboardShortcut("o") }
                CommandMenu("Run") {
                    Button("Preview",action:model.dryRun).keyboardShortcut("p",modifiers:[.command,.shift])
                    Button("Start",action:model.start).keyboardShortcut("r",modifiers:[.command,.shift])
                    Button("Stop") { model.stop() }.keyboardShortcut(".")
                }
            }
    }
}
struct MainView: View {
    @Bindable var model:ClickerModel
    private let ticker=Timer.publish(every:0.1,on:.main,in:.common).autoconnect()
    var body:some View {
        NavigationSplitView {
            List(selection:$model.mode) {
                Label("Quick Click",systemImage:"cursorarrow.click").tag("Quick Click")
                Label("Project",systemImage:"doc.text").tag("Project")
            }.navigationSplitViewColumnWidth(180)
            .safeAreaInset(edge:.bottom) {
                VStack(alignment:.leading,spacing:6) {
                    Text("LOCAL PREVIEW").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                    Text("Recording, video and timeline editing are coming later.").font(.caption).foregroundStyle(.secondary)
                }.padding()
            }
        } detail: {
            ScrollView {
                VStack(alignment:.leading,spacing:22) {
                    HStack(alignment:.firstTextBaseline) {
                        VStack(alignment:.leading,spacing:6) {
                            Text(model.mode).font(.largeTitle.weight(.semibold))
                            Text(model.mode == "Quick Click" ? "Repeat precise actions across your selected displays." : model.projectName).foregroundStyle(.secondary)
                        }
                        Spacer()
                        Text(model.snapshot.state.rawValue.capitalized).font(.callout.weight(.medium)).padding(.horizontal,14).padding(.vertical,7).glassEffect()
                    }
                    if model.mode == "Quick Click" { quickControls } else { projectControls }
                    previewSection
                    VStack(alignment:.leading,spacing:7) {
                        Text(model.status).font(.callout)
                        Text("Dispatched attempts: \(model.snapshot.dispatched) · maximum scheduler lag: \(Double(model.snapshot.maxLagUs)/1000,specifier:"%.1f") ms").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                        Text("Pause safely releases a hold. Start restarts from the beginning. Avoid moving the mouse or typing while running.").font(.caption).foregroundStyle(.secondary)
                    }.accessibilityElement(children:.combine)
                }.padding(28).frame(maxWidth:1000,alignment:.leading)
            }.background(.background)
        }
        .toolbar {
            ToolbarItem { Button(action:model.loadProject) { Label("Open",systemImage:"folder") }.disabled(model.active) }
            ToolbarItem { Button(action:model.dryRun) { Label("Preview",systemImage:"play.rectangle") }.disabled(model.active) }
            ToolbarItem { Button(action:model.start) { Label("Start",systemImage:"play.fill") }.disabled(model.active) }
            ToolbarItem { Button { model.stop("Paused — Start restarts from the beginning",paused:true) } label: { Label("Pause",systemImage:"pause.fill") }.disabled(!model.active) }
            ToolbarItem { Button { model.stop() } label: { Label("Stop",systemImage:"stop.fill") }.keyboardShortcut(".").tint(.red) }
        }
        .onReceive(ticker) { _ in model.poll() }
        .onDisappear { model.stop("Window closed") }
        .alert("Motion Clicker",isPresented:Binding(get:{model.error != nil},set:{if !$0 { model.error=nil }})) { Button("OK") { model.error=nil } } message: { Text(model.error ?? "") }
        .onAppear {
            NSApp.setActivationPolicy(.regular); NSApp.activate(ignoringOtherApps:true)
            let args=ProcessInfo.processInfo.arguments
            if let index=args.firstIndex(of:"--snapshot"), args.indices.contains(index+1) {
                let path=args[index+1]
                Task { try? await Task.sleep(for:.seconds(2)); captureOwnWindow(path) }
            }
        }
    }
    private var quickControls:some View {
        VStack(alignment:.leading,spacing:18) {
            GroupBox("Displays") {
                VStack(alignment:.leading,spacing:10) {
                    ForEach(model.displays,id:\.id) { display in
                        Toggle(display.name,isOn:Binding(get:{model.selected.contains(display.id)},set:{ if $0 { model.selected.insert(display.id) } else { model.selected.remove(display.id) } }))
                    }
                    HStack { Button("Identify",action:model.identify); Button("Refresh",action:model.refreshDisplays) }
                }.padding(8)
            }
            HStack(spacing:18) {
                Picker("Button",selection:$model.button) { Text("Left").tag(MouseButton.left); Text("Right").tag(MouseButton.right); Text("Middle").tag(MouseButton.middle) }.frame(maxWidth:220)
                Toggle("Double-click",isOn:$model.doubleClick)
                Toggle("Current pointer position",isOn:$model.currentPosition)
            }
            if model.currentPosition {
                LabeledContent("Interval (ms)") { TextField("100",value:$model.intervalMs,format:.number).frame(width:100) }
                Text("During the start delay, position the pointer on a selected display. Moving it after execution begins stops the run.").font(.caption).foregroundStyle(.secondary)
            } else {
                VStack(alignment:.leading,spacing:10) {
                    HStack { Text("Ordered points").font(.headline); Spacer(); Button("Add Point") { if let id=model.displays.first(where:{model.selected.contains($0.id)})?.id { model.points.append(ClickPoint(displayID:id)) } } }
                    ForEach($model.points) { $point in
                        HStack(spacing:10) {
                            Picker("Display",selection:$point.displayID) { ForEach(model.displays.filter{model.selected.contains($0.id)},id:\.id) { Text($0.name).tag($0.id) } }.labelsHidden().frame(maxWidth:200)
                            numeric("X",value:$point.x); numeric("Y",value:$point.y); numeric("Pause ms",value:$point.pauseMs)
                            Button { model.move(point.id,by:-1) } label:{ Image(systemName:"arrow.up") }.help("Move point earlier").accessibilityLabel("Move point earlier")
                            Button { model.move(point.id,by:1) } label:{ Image(systemName:"arrow.down") }.help("Move point later").accessibilityLabel("Move point later")
                            Button { model.points.removeAll{$0.id==point.id} } label:{ Image(systemName:"minus") }.help("Remove point").accessibilityLabel("Remove point")
                        }
                    }
                    Text("X and Y are logical coordinates inside the selected display. Each pause follows that point; minimum 20 ms.").font(.caption).foregroundStyle(.secondary)
                }
            }
            Divider()
            HStack(spacing:18) {
                Picker("Limit",selection:$model.limit) { Text("Count").tag("Count"); Text("Duration").tag("Duration"); Text("Infinite").tag("Infinite") }.frame(maxWidth:260)
                if model.limit == "Count" { LabeledContent("Passes") { TextField("10",value:$model.count,format:.number).frame(width:70) } }
                if model.limit == "Duration" { numeric("Seconds",value:$model.durationSeconds) }
                numeric("Start delay (s)",value:$model.delaySeconds)
            }
            if model.limit == "Infinite" { Text("Infinite mode runs until stopped. The emergency hotkey must register before Start.").font(.callout).foregroundStyle(.orange) }
            safetyControls
        }.disabled(model.active)
    }
    private var projectControls:some View {
        VStack(alignment:.leading,spacing:14) {
            if let project=model.project {
                ForEach(project.monitorSlots,id:\.id) { slot in
                    Picker("\(slot.name) — \(DisplayFormatting.number(slot.geometry.size.width)) × \(DisplayFormatting.number(slot.geometry.size.height))",selection:Binding(get:{model.mapping[slot.id] ?? ""},set:{model.mapping[slot.id]=$0})) {
                        Text("Choose runtime display…").tag("")
                        ForEach(model.displays,id:\.id) { Text($0.name).tag($0.id) }
                    }
                }
                Text("Size, scaling and rotation must match. Imported slots are never assigned automatically.").font(.caption).foregroundStyle(.secondary)
            } else { ContentUnavailableView("Open a project",systemImage:"doc.text",description:Text("Choose a validated domain project JSON to inspect its event plan.")) }
            numeric("Start delay (s)",value:$model.delaySeconds)
            safetyControls
        }.disabled(model.active)
    }
    private var safetyControls:some View {
        HStack {
            Picker("Emergency stop",selection:$model.hotkey) { Text("⌃⌥⌘ F8").tag(UInt32(kVK_F8)); Text("⌃⌥⌘ F9").tag(UInt32(kVK_F9)) }.frame(maxWidth:280)
            Spacer()
            Button("Request Input Permissions…",action:model.requestPermissions)
        }
    }
    private var previewSection:some View {
        GroupBox("Event preview · no input") {
            VStack(alignment:.leading,spacing:8) {
                if model.preview.isEmpty { Text("Press Preview to inspect the planned order.").foregroundStyle(.secondary).padding(12) }
                else {
                    ScrollView {
                        VStack(alignment:.leading,spacing:6) {
                            ForEach(previewRows) { row in
                                let event=row.event
                                Text("\(Double(event.timeUs)/1000,specifier:"%.1f") ms  ·  \(event.kind.rawValue)  ·  \(event.slot)  ·  \(event.point.map { "(\(DisplayFormatting.number($0.x)), \(DisplayFormatting.number($0.y)))" } ?? "current pointer")").font(.caption.monospaced())
                            }
                        }.frame(maxWidth:.infinity,alignment:.leading).padding(10)
                    }.frame(height:120)
                }
            }.frame(maxWidth:.infinity,alignment:.leading)
        }
    }
    private var previewRows:[PreviewRow] { model.preview.enumerated().map { PreviewRow(id:$0.offset,event:$0.element) } }
    private struct PreviewRow:Identifiable { let id:Int; let event:RuntimeStep }
    private func numeric(_ label:String,value:Binding<Double>) -> some View {
        LabeledContent(label) { TextField(label,value:value,format:.number).frame(minWidth:55,maxWidth:95) }
    }
    private func captureOwnWindow(_ path:String) {
        guard let view=NSApp.windows.first(where:{$0.title == "Motion Clicker"})?.contentView,
              let bitmap=view.bitmapImageRepForCachingDisplay(in:view.bounds) else { return }
        view.cacheDisplay(in:view.bounds,to:bitmap)
        try? bitmap.representation(using:.png,properties:[:])?.write(to:URL(fileURLWithPath:path))
    }
}
