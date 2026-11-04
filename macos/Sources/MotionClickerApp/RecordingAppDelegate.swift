import AppKit

@MainActor final class RecordingAppDelegate: NSObject, NSApplicationDelegate {
    weak var model: ClickerModel?
    private var terminating = false
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model, model.recording.ownsOperation else { model?.stop("Application closing"); return .terminateNow }
        guard !terminating else { return .terminateLater }
        terminating = true
        Task { await model.recording.stop(reason: "Application closing"); sender.reply(toApplicationShouldTerminate: true) }
        return .terminateLater
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
