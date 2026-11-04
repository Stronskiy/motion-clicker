import Foundation
import MotionClickerCore

public enum BundleOperation { case writeProject, flushProject, publishProject }
public enum RecordedBundleAvailability { case requiresMonitorMapping, partial }
public struct RecordedBundleMedia: Equatable {
    public let monitorSlotId: String
    public let file: URL
    public let mediaTimeZeroUs: Int64
    public let encodedDurationUs: Int64
}
public struct RecordedBundleSnapshot: Equatable {
    public let project: MotionProject
    public let manifest: RecordingManifest
    public let manifestFile: URL
    public let eventsFile: URL
    public let gapsFile: URL
    public let media: [RecordedBundleMedia]
    public let availability: RecordedBundleAvailability
}
public enum RecordedBundleStore {
    public static func create(root: URL) throws { throw RecordingError(message: "Recorded bundle creation not implemented") }
    public static func saveInitial(root: URL, result: RecordingResult, validationScratch: URL,
                                   fault: (BundleOperation) throws -> Void = { _ in }) throws -> RecordedBundleSnapshot {
        throw RecordingError(message: "Recorded bundle save not implemented")
    }
    public static func coldOpen(root: URL, validationScratch: URL) throws -> RecordedBundleSnapshot {
        throw RecordingError(message: "Recorded bundle reopen not implemented")
    }
    public static func recoveryEntries(root: URL) throws -> [URL] { throw RecordingError(message: "Recorded bundle recovery not implemented") }
}
