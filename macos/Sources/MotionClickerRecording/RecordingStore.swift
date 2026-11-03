import Foundation
public final class RecordingStore: EventJournal {
    public var recovery: StoreRecovery { StoreRecovery(stagingPath: "", mediaPath: "", manifestPath: "", mediaPublished: false, manifestPublished: false) }
    public init(root: URL, takeId: String, fault: @escaping (StoreOperation) throws -> Void = { _ in }) throws { }
    public func mediaFile(monitorSlotId: String) throws -> URL { throw RecordingError(message: "Not implemented") }
    public func append(record: RawPointerRecord) throws { }
    public func flush() throws { }
    public func read(visit: (RawPointerRecord) throws -> Void) throws { }
    public func publish(manifest: RecordingManifest) throws -> URL { throw RecordingError(message: "Not implemented") }
    public func close() throws { }
}
