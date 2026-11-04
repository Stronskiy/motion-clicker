import SwiftUI
import AVKit
import MotionClickerRecording
import MotionClickerCore

/// Historical source only. This view has no input backend or capture writer.
struct SavedVideoView: View {
    let bundle: InspectedBundle
    @State private var selected = ""
    @State private var player = AVPlayer()
    @State private var sourceUs: Double = 0
    @State private var scrubbing = false
    private let ticker = Timer.publish(every: 0.1, on: .main, in: .common).autoconnect()
    private var stream: StreamManifest? { bundle.snapshot.manifest.streams.first { $0.monitorSlotId == selected } }
    private var media: RecordedBundleMedia? { bundle.snapshot.media.first { $0.monitorSlotId == selected } }
    private var unavailable: String? {
        guard bundle.mediaError == nil else { return "Source video did not pass native validation." }
        guard let media else { return "No successful video for this monitor." }
        let time=SourcePlaybackTime.sourceUs(sourceUs)
        if time < media.mediaTimeZeroUs { return "No source image yet · first image at \(Double(media.mediaTimeZeroUs)/1_000_000) s" }
        if time >= media.mediaTimeZeroUs + media.encodedDurationUs { return "No source image after the valid video prefix." }
        if let gap=stream?.gaps.first(where: { time >= $0.startUs && time < $0.endUs }) { return "Known video gap: \(gap.kind.rawValue)" }
        return nil
    }
    var body: some View {
        VStack(alignment: .leading, spacing: 12) {
            Label("Source video · historical recording", systemImage: "film").font(.headline)
            if bundle.snapshot.manifest.state != .completed {
                Label("Partial recording — Run unavailable", systemImage: "exclamationmark.triangle").foregroundStyle(.red)
            }
            if let error=bundle.mediaError { Text(error).foregroundStyle(.red).textSelection(.enabled) }
            Picker("Recorded monitor", selection: $selected) {
                ForEach(bundle.snapshot.manifest.targets, id: \.id) { Text($0.name).tag($0.id) }
            }
            ZStack {
                NativeSourcePlayer(player: player)
                if let unavailable {
                    Rectangle().fill(.background)
                    ContentUnavailableView { Label(unavailable, systemImage: "video.slash") } description: { Text("Source time \(sourceUs/1_000_000, specifier: "%.3f") s. No replacement image is invented.") }
                }
            }.frame(minHeight: 200, idealHeight: 270, maxHeight: 340).clipShape(RoundedRectangle(cornerRadius: 8))
            HStack {
                Button { if player.rate == 0 { player.play() } else { player.pause() } } label: { Label("Play / Pause", systemImage: "playpause") }
                    .disabled(unavailable != nil)
                Text("Source \(sourceUs/1_000_000, specifier: "%.3f") / \(Double(bundle.snapshot.manifest.durationUs)/1_000_000, specifier: "%.3f") s").font(.caption).monospacedDigit()
            }
            Slider(value: $sourceUs, in: 0...max(1, Double(bundle.snapshot.manifest.durationUs)), onEditingChanged: { editing in
                scrubbing=editing; if editing { player.pause() } else { seek() }
            }).accessibilityLabel("Source take time")
            if let media {
                Text("File PTS 0 = source \(Double(media.mediaTimeZeroUs)/1_000_000, specifier: "%.6f") s. Source video and journals remain unchanged.").font(.caption).foregroundStyle(.secondary)
            }
            DisclosureGroup("Recorded actions · source time") {
                ScrollView {
                    LazyVStack(alignment: .leading, spacing: 6) {
                        ForEach(bundle.snapshot.project.events.filter { $0.monitorSlotId == selected }, id: \.id) { action in
                            Button { sourceUs=Double(action.timeUs); player.pause(); seek() } label: {
                                HStack {
                                    Text("\(Double(action.timeUs)/1_000_000, specifier: "%.6f") s").monospacedDigit()
                                    Text(action.kind.rawValue)
                                    Text("\(DisplayFormatting.number(action.x)), \(DisplayFormatting.number(action.y)) pt")
                                    Spacer()
                                }.font(.caption)
                            }.buttonStyle(.plain).accessibilityLabel("Inspect \(action.kind.rawValue) at \(action.timeUs) microseconds")
                        }
                    }
                }.frame(maxHeight: 160)
            }
            ForEach(bundle.snapshot.manifest.streams, id: \.monitorSlotId) { stream in
                if stream.writerStatus == .failed { Text("\(stream.monitorSlotId): \(stream.failureReason ?? "Video failed")").font(.caption).foregroundStyle(.red) }
            }
            if !bundle.snapshot.manifest.incompleteGroupIds.isEmpty {
                Text("Incomplete pointer gestures are retained in the journal and omitted from executable actions.").font(.caption).foregroundStyle(.secondary)
            }
        }
        .onAppear { selected=bundle.snapshot.manifest.targets.first?.id ?? ""; replaceSource() }
        .onChange(of: selected) { _,_ in replaceSource() }
        .onDisappear { player.pause(); player.replaceCurrentItem(with: nil) }
        .onReceive(ticker) { _ in
            guard !scrubbing, player.rate != 0, let media else { return }
            let time=player.currentTime()
            guard time.isNumeric else { return }
            sourceUs=Double(CMTimeConvertScale(time,timescale:1_000_000,method:.roundHalfAwayFromZero).value + media.mediaTimeZeroUs)
        }
    }
    private func replaceSource() {
        player.pause()
        player.replaceCurrentItem(with: bundle.mediaError == nil ? media.map { AVPlayerItem(url: $0.file) } : nil)
        seek()
    }
    private func seek() {
        guard let media else { return }
        let pts=SourcePlaybackTime.filePTS(sourceUs:SourcePlaybackTime.sourceUs(sourceUs),mediaOffsetUs:media.mediaTimeZeroUs,durationUs:media.encodedDurationUs)
        player.seek(to: CMTime(value: pts,timescale:1_000_000),toleranceBefore:.zero,toleranceAfter:.zero)
    }
}
private struct NativeSourcePlayer: NSViewRepresentable {
    let player: AVPlayer
    func makeNSView(context: Context) -> AVPlayerView { let view=AVPlayerView(); view.controlsStyle = .none; view.videoGravity = .resizeAspect; view.player=player; return view }
    func updateNSView(_ view: AVPlayerView, context: Context) { view.player=player }
}
