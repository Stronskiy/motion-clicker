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
    @State private var gaps:SourceGapInspection
    @State private var playbackEndSourceUs:Int64?
    private let ticker = Timer.publish(every: 0.1, on: .main, in: .common).autoconnect()
    private var media: RecordedBundleMedia? { bundle.snapshot.media.first { $0.monitorSlotId == selected } }
    init(bundle:InspectedBundle) {
        self.bundle=bundle
        _gaps=State(initialValue:SourceGapInspection(lookup:CompleteGapLookup(snapshot:bundle.snapshot)))
    }
    private var unavailable: String? {
        guard bundle.mediaError == nil else { return "Source video did not pass native validation." }
        guard let media else { return "No successful video for this monitor." }
        return gaps.unavailable(media:media,sourceUs:SourcePlaybackTime.sourceUs(sourceUs))
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
                Button(action:togglePlayback) { Label("Play / Pause", systemImage: "playpause") }
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
        .onChange(of: sourceUs) { _,_ in inspectGaps() }
        .onChange(of:gaps.window) { _,_ in configurePlaybackEnd() }
        .onReceive(NotificationCenter.default.publisher(for:AVPlayerItem.didPlayToEndTimeNotification).receive(on:DispatchQueue.main)) { notification in
            guard let item=notification.object as? AVPlayerItem, item === player.currentItem, let end=playbackEndSourceUs else { return }
            player.pause(); sourceUs=Double(end); inspectGaps()
        }
        .onDisappear { player.pause(); player.replaceCurrentItem(with: nil); gaps.cancel() }
        .onReceive(ticker) { _ in
            guard !scrubbing, player.rate != 0, let media else { return }
            let time=player.currentTime()
            guard time.isNumeric else { return }
            sourceUs=Double(CMTimeConvertScale(time,timescale:1_000_000,method:.roundHalfAwayFromZero).value + media.mediaTimeZeroUs)
        }
    }
    private func replaceSource() {
        player.pause(); playbackEndSourceUs=nil
        player.actionAtItemEnd = .pause
        player.replaceCurrentItem(with: bundle.mediaError == nil ? media.map { AVPlayerItem(url: $0.file) } : nil)
        seek()
    }
    private func inspectGaps() {
        if let media { gaps.request(media:media,sourceUs:SourcePlaybackTime.sourceUs(sourceUs)) }
        else { gaps.cancel() }
    }
    private func togglePlayback() {
        if player.rate != 0 { player.pause(); return }
        configurePlaybackEnd()
        guard unavailable == nil, playbackEndSourceUs != nil else { return }
        player.play()
    }
    private func configurePlaybackEnd() {
        guard unavailable == nil, let media,
              let end=SourcePlaybackTime.playbackEndPTS(media:media,window:gaps.window,sourceUs:SourcePlaybackTime.sourceUs(sourceUs)) else {
            player.pause(); playbackEndSourceUs=nil; return
        }
        playbackEndSourceUs=media.mediaTimeZeroUs+end
        player.currentItem?.forwardPlaybackEndTime=CMTime(value:end,timescale:1_000_000)
    }
    private func seek() {
        player.pause(); playbackEndSourceUs=nil
        player.currentItem?.forwardPlaybackEndTime = .invalid
        inspectGaps()
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
