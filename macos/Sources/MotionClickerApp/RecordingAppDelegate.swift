import AppKit

@MainActor final class RecordingAppDelegate: NSObject, NSApplicationDelegate {
    weak var model: ClickerModel?
    private var terminating = false
    func applicationShouldTerminate(_ sender: NSApplication) -> NSApplication.TerminateReply {
        guard let model else { return .terminateNow }
        guard model.recording.ownsOperation else {
            model.stop("Application closing")
            if model.runNeedsRelease { model.error="An owned button release is still pending. Retry Stop before quitting."; return .terminateCancel }
            return .terminateNow
        }
        guard !terminating else { return .terminateLater }
        terminating = true
        Task {
            await model.recording.stop(reason: "Application closing"); model.stop("Application closing")
            terminating=false
            sender.reply(toApplicationShouldTerminate: !model.runNeedsRelease)
        }
        return .terminateLater
    }
    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { true }
}
