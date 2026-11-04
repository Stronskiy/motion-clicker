import SwiftUI
import AppKit
import MotionClickerRecording

struct RecordingWorkspace: View {
    @Bindable var model: ClickerModel
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            Label("Record video and pointer actions", systemImage: "record.circle").font(.headline)
            Text("Select the displays for this take. Video and actions stay together in a new local .motionclicker bundle.")
                .font(.callout).foregroundStyle(.secondary)
            ForEach(model.displays, id: \.id) { display in
                Toggle(isOn: Binding(get: { model.recordSelected.contains(display.id) }, set: {
                    if $0 { model.recordSelected.insert(display.id) } else { model.recordSelected.remove(display.id) }
                })) {
                    VStack(alignment: .leading, spacing: 4) {
                        Text(displayTitle(display, displays: model.displays))
                        Text("\(DisplayFormatting.number(display.geometry.size.width)) × \(DisplayFormatting.number(display.geometry.size.height)) pt · scale \(DisplayFormatting.number(display.geometry.scaleFactor))")
                            .font(.caption).foregroundStyle(.secondary)
                    }
                }.toggleStyle(.checkbox).disabled(model.active)
            }
            HStack {
                Button("Refresh displays", action: model.refreshDisplays)
                Button("Choose New Destination…", action: model.prepareRecording)
                    .buttonStyle(.borderedProminent).disabled(model.recordSelected.isEmpty)
            }.disabled(model.active)
            Divider()
            RecordingStatus(flow: model.recording)
            if model.recording.phase == .ready {
                Button("Start 3-second Countdown", action: model.beginRecordingCountdown).buttonStyle(.borderedProminent)
            }
            if model.recording.canRetrySave { Button("Retry Project Save") { Task { await model.recording.retrySave() } } }
            if model.recording.saved != nil {
                Button("Inspect Saved Recording", action: model.inspectSavedRecording).disabled(model.active)
            }
            if let root=model.recording.destination, model.recording.phase == .failed || model.recording.phase == .cancelled {
                Button("Reveal Recovery Location") { NSWorkspace.shared.activateFileViewerSelecting([root]) }
                Text("Keep this directory for recovery. A published take without project.json is not a saved project.").font(.caption).foregroundStyle(.secondary)
            }
            Text("Screen Recording and Input Monitoring are required. Preparation only checks permissions. Capture starts after the deliberate countdown; this app’s windows are excluded from source footage.")
                .font(.caption).foregroundStyle(.secondary)
        }
    }
}
struct RecordingStatus: View {
    let flow: RecordingFlow
    var body: some View {
        VStack(alignment: .leading, spacing: 8) {
            HStack {
                if flow.ownsOperation { ProgressView().controlSize(.small) }
                Text(flow.phase.rawValue.capitalized).font(.headline)
                if flow.phase == .countdown { Text("\(flow.remaining)").font(.title.monospacedDigit()) }
            }
            Text(flow.message).font(.callout).textSelection(.enabled)
            if let destination=flow.destination { Text(destination.path).font(.caption).foregroundStyle(.secondary).textSelection(.enabled) }
            if let bytes=flow.availableBytes { Text("Available when prepared: \(ByteCountFormatter.string(fromByteCount:bytes,countStyle:.file)). Recording stops visibly on storage failure.").font(.caption).foregroundStyle(.secondary) }
            if flow.accepted > 0 || flow.dropped > 0 {
                Text("Accepted frames: \(flow.accepted) · dropped updates: \(flow.dropped)").font(.caption).monospacedDigit()
            }
            if let recovery=flow.recovery, flow.phase != .saved {
                Text("Recovery staging: \(recovery.stagingPath)").font(.caption).textSelection(.enabled)
            }
        }.frame(maxWidth: .infinity, alignment: .leading)
    }
}
