import SwiftUI
import AppKit
import Carbon
import MotionClickerCore
import MotionClickerRuntime

struct WorkspaceView: View {
    @Bindable var model: ClickerModel
    @State private var focusedPoint: UUID?
    @State private var showPreview=true
    @State private var configuredQA=false
    @State private var previewRequest=UUID()
    private let ticker=Timer.publish(every:0.1,on:.main,in:.common).autoconnect()
    var body: some View {
        NavigationSplitView {
            List(selection:$model.mode) {
                Label("Quick Click",systemImage:"cursorarrow.click").tag("Quick Click")
                Label("Project",systemImage:"doc.text").tag("Project")
            }.navigationSplitViewColumnWidth(min:176,ideal:184,max:196).disabled(model.active)
        } detail: {
            GeometryReader { geometry in
                VStack(spacing:0) {
                    HStack(alignment:.top,spacing:0) {
                        ScrollViewReader { scroller in
                        ScrollView {
                            VStack(alignment:.leading,spacing:16) {
                                header
                                if model.mode == "Quick Click" { quickWorkspace } else { projectWorkspace }
                                if geometry.size.width < 900 {
                                    DisclosureGroup("Inspector") { inspectorContents.padding(.top,12) }
                                }
                                previewSection.id("event-preview")
                            }.padding(24).frame(maxWidth:.infinity,alignment:.leading)
                        }
                        .onChange(of:previewRequest) { _,_ in scroller.scrollTo("event-preview",anchor:.bottom) }
                        .onChange(of:model.preview.count) { _,_ in scroller.scrollTo("event-preview",anchor:.bottom) }
                        }
                        if geometry.size.width >= 900 {
                            Divider()
                            ScrollView { inspectorContents.padding(20) }.frame(width:290)
                                .background(.background.secondary)
                        }
                    }
                    Divider()
                    transport
                }.background(.background)
            }
        }
        .toolbar {
            ToolbarItem { Button(action:model.loadProject) { Label("Open Project",systemImage:"folder") }.disabled(model.active).help("Open Project JSON") }
        }
        .onReceive(ticker) { _ in model.poll() }
        .onDisappear { model.stop("Window closed") }
        .onAppear {
            guard !configuredQA else { return }; configuredQA=true
            focusedPoint=model.points.first?.id
            NSApp.setActivationPolicy(.regular); NSApp.activate(ignoringOtherApps:true)
            applyVisualQAArguments()
        }
        .alert("Motion Clicker",isPresented:Binding(get:{model.error != nil},set:{if !$0 { model.error=nil }})) {
            Button("OK") { model.error=nil }
        } message: { Text(model.error ?? "") }
    }
    private var header: some View {
        HStack(alignment:.firstTextBaseline) {
            VStack(alignment:.leading,spacing:5) {
                Text(model.mode).font(.system(size:26,weight:.semibold))
                Text(model.mode == "Quick Click" ? "Choose your displays and arrange the click order." : model.projectName)
                    .font(.callout).foregroundStyle(.secondary).lineLimit(2)
            }
            Spacer(minLength:8)
            Text(model.snapshot.state.rawValue.capitalized).font(.callout.weight(.medium)).foregroundStyle(.secondary)
        }
    }
    private var quickWorkspace: some View {
        VStack(alignment:.leading,spacing:16) {
            HStack {
                Text("Coordinate map · no screen capture").font(.headline)
                Spacer()
                Button("Identify",action:model.identify)
                Button { model.refreshDisplays() } label: { Image(systemName:"arrow.clockwise") }.help("Refresh displays").accessibilityLabel("Refresh displays")
            }
            MonitorMapView(displays:model.displays,selected:model.selected,points:model.points,focusedPoint:focusedPoint,currentPosition:model.currentPosition,selectPoint:{ focusedPoint=$0 })
            ViewThatFits(in:.horizontal) { displayToggles(horizontal:true); displayToggles(horizontal:false) }
            Divider()
            if model.currentPosition {
                Label("Use the pointer position at run time",systemImage:"cursorarrow").font(.headline)
                Text("Position the pointer during the start delay. Moving it after the run begins stops execution.").font(.callout).foregroundStyle(.secondary)
            } else { pointSection }
        }.disabled(model.active)
    }
    private func displayToggles(horizontal: Bool) -> some View {
        let layout=horizontal ? AnyLayout(HStackLayout(spacing:16)) : AnyLayout(VStackLayout(alignment:.leading,spacing:8))
        return layout {
            ForEach(model.displays,id:\.id) { display in
                Toggle(displayTitle(display,displays:model.displays),isOn:Binding(get:{model.selected.contains(display.id)},set:{if $0 { model.selected.insert(display.id) } else { model.selected.remove(display.id) }}))
                    .toggleStyle(.checkbox).help(display.name)
            }
        }
    }
    private var pointSection: some View {
        VStack(alignment:.leading,spacing:10) {
            HStack {
                Text("Ordered points").font(.headline)
                Text("\(model.points.count)").foregroundStyle(.secondary)
                Spacer()
                Button { addPoint() } label: { Label("Add Point",systemImage:"plus") }
                    .disabled(!model.displays.contains(where:{model.selected.contains($0.id)}))
            }
            if model.points.isEmpty {
                ContentUnavailableView { Label("No points yet",systemImage:"cursorarrow.click") } description: { Text("Add a point to set its display and coordinates.") } actions: { Button("Add Point",action:addPoint) }
            } else {
                List(selection:$focusedPoint) {
                    ForEach(model.points) { point in
                        HStack(spacing:12) {
                            Text("\((model.points.firstIndex(where:{$0.id==point.id}) ?? 0)+1)").font(.callout.monospacedDigit()).foregroundStyle(.secondary).frame(width:22)
                            Text(model.displays.first(where:{$0.id==point.displayID}).map{displayTitle($0,displays:model.displays)} ?? "Missing display").lineLimit(1)
                            Spacer()
                            Text("\(DisplayFormatting.number(point.x)), \(DisplayFormatting.number(point.y)) pt").monospacedDigit()
                            Text("\(DisplayFormatting.number(point.pauseMs)) ms").foregroundStyle(.secondary).monospacedDigit().frame(minWidth:60,alignment:.trailing)
                        }.font(.callout).padding(.vertical,3).tag(point.id)
                    }
                }.listStyle(.bordered).frame(height:CGFloat(min(4,model.points.count))*32+4)

            }
        }
    }
    private var inspectorContents: some View {
        VStack(alignment:.leading,spacing:16) {
            Text("Inspector").font(.headline)
            if model.mode == "Quick Click", !model.currentPosition, let id=focusedPoint,
               let index=model.points.firstIndex(where:{$0.id==id}) {
                Text("Point \(index+1)").font(.subheadline.weight(.semibold))
                PointEditor(point:$model.points[index],displays:model.displays.filter{model.selected.contains($0.id)},allDisplays:model.displays,
                            moveUp:{model.move(id,by:-1)},moveDown:{model.move(id,by:1)},remove:{model.points.removeAll{$0.id==id}; focusedPoint=model.points.first?.id},first:index==0,last:index==model.points.count-1)
                    .disabled(model.active)
                Divider()
            }
            InspectorView(model:model)
        }
    }
    private var projectWorkspace: some View {
        VStack(alignment:.leading,spacing:16) {
            if let project=model.project {
                Label("Monitor mapping",systemImage:"display.2").font(.headline)
                ForEach(project.monitorSlots,id:\.id) { slot in
                    VStack(alignment:.leading,spacing:6) {
                        Text(slot.name).font(.callout.weight(.medium)).lineLimit(2)
                        Text("\(DisplayFormatting.number(slot.geometry.size.width)) × \(DisplayFormatting.number(slot.geometry.size.height)) pt").font(.caption).foregroundStyle(.secondary)
                        Picker("Runtime display",selection:Binding(get:{model.mapping[slot.id] ?? ""},set:{model.mapping[slot.id]=$0})) {
                            Text("Choose display…").tag("")
                            ForEach(model.displays,id:\.id) { Text(displayTitle($0,displays:model.displays)).tag($0.id) }
                        }
                    }
                }
                Text("Map each slot explicitly. Size, scale and rotation must match.").font(.callout).foregroundStyle(.secondary)
            } else {
                ContentUnavailableView { Label("Open a project",systemImage:"doc.text") } description: {
                    Text("Inspect a domain project’s event plan and map its displays. Video playback and editing are not available.")
                } actions: { Button("Open Project JSON…",action:model.loadProject) }
                .frame(minHeight:300)
            }
        }.disabled(model.active)
    }
    private var previewSection: some View {
        DisclosureGroup(isExpanded:$showPreview) {
            if model.preview.isEmpty {
                HStack(spacing:10) {
                    Image(systemName:"play.rectangle").foregroundStyle(.secondary)
                    Text("Preview the event order without sending input.").foregroundStyle(.secondary)
                    Spacer()
                }.font(.callout).padding(.vertical,14)
            } else {
                EventPreviewView(events:model.preview,displays:model.displays,project:model.project)
            }
        } label: { Text("Event preview · no input").font(.headline) }
    }
    private var transport: some View {
        VStack(alignment:.leading,spacing:10) {
            HStack(spacing:10) {
                VStack(alignment:.leading,spacing:3) {
                    Text(transportStatus).font(.callout.weight(.medium)).lineLimit(2).help(model.status)
                    Text("Emergency stop: ⌃⌥⌘ \(model.hotkey == UInt32(kVK_F8) ? "F8" : "F9")").font(.caption).foregroundStyle(.secondary)
                }
                Spacer()
                Button { model.dryRun(); showPreview=true; previewRequest=UUID() } label: { Label("Preview",systemImage:"play.rectangle") }.disabled(model.active)
                if model.active {
                    Button { model.stop("Paused — Run restarts from the beginning",paused:true) } label: { Label("Pause",systemImage:"pause.fill") }
                    Button(role:.destructive) { model.stop() } label: { Label("Stop",systemImage:"stop.fill") }.keyboardShortcut(".")
                } else {
                    Button(action:model.start) { Label("Run",systemImage:"play.fill") }.buttonStyle(.borderedProminent)
                }
            }
            DisclosureGroup("Run details") {
                VStack(alignment:.leading,spacing:5) {
                    Text(model.status)
                    Text("Dispatched attempts: \(model.snapshot.dispatched) · scheduler lag: \(Double(model.snapshot.maxLagUs)/1000,specifier:"%.1f") ms").monospacedDigit()
                    Text("Pause releases a hold. Run restarts from the beginning.")
                }.font(.caption).foregroundStyle(.secondary).padding(.top,5)
            }.font(.caption)
        }.padding(.horizontal,24).padding(.vertical,14).background(.bar)
    }
    private var transportStatus: String {
        if model.status.hasPrefix("Dry run") { return "Preview ready · \(model.preview.count) events · no input" }
        if model.status.hasPrefix("Idle") { return "Ready to inspect or run" }
        return model.status
    }
    private func addPoint() {
        guard let id=model.displays.first(where:{model.selected.contains($0.id)})?.id else { return }
        let point=ClickPoint(displayID:id); model.points.append(point); focusedPoint=point.id
    }
    /// Explicit per-process visual QA only: no input dispatch or permission changes.
    private func applyVisualQAArguments() {
        let args=ProcessInfo.processInfo.arguments
        func value(_ key:String) -> String? { guard let i=args.firstIndex(of:key),args.indices.contains(i+1) else { return nil }; return args[i+1] }
        if let appearance=value("--appearance"),["light","dark"].contains(appearance) { NSApp.appearance=NSAppearance(named:appearance == "light" ? .aqua : .darkAqua) }
        if args.contains("--project-empty") { model.mode="Project" }
        if args.contains("--preview"),!args.contains("--project-empty") { model.dryRun(); previewRequest=UUID() }
        let width=value("--window-width").flatMap(Double.init) ?? 1180
        let height=value("--window-height").flatMap(Double.init) ?? 800
        guard width.isFinite,height.isFinite else { return }
        Task { @MainActor in
            try? await Task.sleep(for:.milliseconds(200))
            guard let window=NSApp.windows.first(where:{$0.title == "Motion Clicker"}) else { return }
            window.minSize=NSSize(width:980,height:720)
            var frame=window.frame
            frame.size=NSSize(width:min(2400,max(980,width)),height:min(1600,max(720,height)))
            window.setFrame(frame,display:true)
            window.center()
            window.makeKeyAndOrderFront(nil)
            NSApp.activate(ignoringOtherApps:true)
            if args.contains("--preview") { previewRequest=UUID() }
        }
    }
}

private struct PointEditor: View {
    @Binding var point: ClickPoint
    let displays:[RuntimeDisplay]
    let allDisplays:[RuntimeDisplay]
    let moveUp:()->Void
    let moveDown:()->Void
    let remove:()->Void
    let first:Bool
    let last:Bool
    var body: some View {
        VStack(alignment:.leading,spacing:10) {
            Text("X/Y are local logical points from the selected display’s top-left.").font(.caption).foregroundStyle(.secondary)
            Picker("Display",selection:$point.displayID) { ForEach(displays,id:\.id) { Text(displayTitle($0,displays:allDisplays)).tag($0.id) } }
            coordinate("X",value:$point.x,unit:"pt")
            coordinate("Y",value:$point.y,unit:"pt")
            coordinate("Pause",value:$point.pauseMs,unit:"ms")
            HStack {
                Button(action:moveUp) { Image(systemName:"arrow.up") }.disabled(first).help("Move point earlier").accessibilityLabel("Move point earlier")
                Button(action:moveDown) { Image(systemName:"arrow.down") }.disabled(last).help("Move point later").accessibilityLabel("Move point later")
                Spacer()
                Button(role:.destructive,action:remove) { Image(systemName:"trash") }.help("Remove point").accessibilityLabel("Remove point")
            }
        }.font(.callout)
    }
    private func coordinate(_ label:String,value:Binding<Double>,unit:String) -> some View {
        HStack(spacing:5) { Text(label); Spacer(); TextField(label,value:value,format:.number).textFieldStyle(.roundedBorder).frame(width:100); Text(unit).foregroundStyle(.secondary).frame(width:20,alignment:.leading) }.font(.callout)
    }
}

private struct InspectorView: View {
    @Bindable var model:ClickerModel
    var body: some View {
        VStack(alignment:.leading,spacing:16) {
            if model.mode == "Quick Click" {
                section("Click") {
                    Picker("Button",selection:$model.button) { Text("Left").tag(MouseButton.left); Text("Right").tag(MouseButton.right); Text("Middle").tag(MouseButton.middle) }
                    Toggle("Double-click",isOn:$model.doubleClick)
                    Toggle("Current pointer position",isOn:$model.currentPosition)
                    if model.currentPosition { number("Interval",value:$model.intervalMs,unit:"ms") }
                }
                Divider()
                section("Repeat") {
                    Picker("Limit",selection:$model.limit) { Text("Count").tag("Count"); Text("Duration").tag("Duration"); Text("Infinite").tag("Infinite") }
                    if model.limit == "Count" { HStack { Text("Passes"); Spacer(); TextField("Passes",value:$model.count,format:.number).frame(width:85).textFieldStyle(.roundedBorder) } }
                    if model.limit == "Duration" { number("Duration",value:$model.durationSeconds,unit:"s") }
                    if model.limit == "Infinite" { Label("Runs until stopped",systemImage:"infinity").font(.caption).foregroundStyle(.secondary) }
                }
                Divider()
            }
            section("Start & stop") {
                number("Start delay",value:$model.delaySeconds,unit:"s")
                VStack(alignment:.leading,spacing:6) {
                    Text("Emergency stop").font(.caption).foregroundStyle(.secondary)
                    Picker("Emergency stop",selection:$model.hotkey) { Text("⌃⌥⌘ F8").tag(UInt32(kVK_F8)); Text("⌃⌥⌘ F9").tag(UInt32(kVK_F9)) }.labelsHidden()
                }
                Button("Input Permissions…",action:model.requestPermissions)
            }
        }.font(.callout).toggleStyle(.checkbox).frame(maxWidth:.infinity,alignment:.leading).disabled(model.active)
    }
    private func section<Content:View>(_ title:String,@ViewBuilder content:()->Content) -> some View {
        VStack(alignment:.leading,spacing:12) { Text(title).font(.subheadline.weight(.semibold)); content() }
    }
    private func number(_ label:String,value:Binding<Double>,unit:String) -> some View {
        HStack { Text(label); Spacer(); TextField(label,value:value,format:.number).frame(width:70).textFieldStyle(.roundedBorder); Text(unit).foregroundStyle(.secondary).frame(width:20,alignment:.leading) }
    }
}

private struct EventPreviewView: View {
    let events:[RuntimeStep]
    let displays:[RuntimeDisplay]
    let project:MotionProject?
    var body: some View {
        ScrollView {
            LazyVStack(alignment:.leading,spacing:8) {
                ForEach(Array(events.enumerated()),id:\.offset) { _,event in
                    HStack(spacing:12) {
                        Text("\(Double(event.timeUs)/1000,specifier:"%.1f") ms").monospacedDigit().frame(width:80,alignment:.leading)
                        Text(event.kind.rawValue).frame(width:80,alignment:.leading)
                        Text(displayName(event.slot)).lineLimit(1)
                        Spacer(minLength:0)
                        Text(event.point.map{"\(DisplayFormatting.number($0.x)), \(DisplayFormatting.number($0.y))"} ?? "Current pointer").monospacedDigit().lineLimit(1)
                    }.font(.caption)
                }
            }.padding(.vertical,12)
        }.frame(height:116)
    }
    private func displayName(_ id:String)->String {
        if let d=displays.first(where:{$0.id==id}) { return displayTitle(d,displays:displays) }
        return project?.monitorSlots.first(where:{$0.id==id})?.name ?? "Unmapped display"
    }
}
