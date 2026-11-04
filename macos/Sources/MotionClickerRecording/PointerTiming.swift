import Foundation

public enum PointerTimingBasis: String, Codable, Sendable { case nativeHostObservation }
public enum PointerHostClock: String, Codable, Sendable { case machAbsolute, queryPerformanceCounter }
public enum PointerPacketClock: String, Codable, Sendable { case cgEventTimestamp, windowsLowLevelMouseTime }
public enum PointerPacketUnits: String, Codable, Sendable { case nanoseconds, milliseconds }

/// The packet timestamp is diagnostic, never an input to canonical action time.
public struct PointerTiming: Codable, Sendable, Equatable {
    public let basis: PointerTimingBasis
    public let hostClock: PointerHostClock
    public let packetClock: PointerPacketClock
    public let packetUnits: PointerPacketUnits
    public let packetTimestamp: String
    public let ownerDelayUs: Int64
    public init(basis: PointerTimingBasis, hostClock: PointerHostClock, packetClock: PointerPacketClock,
                packetUnits: PointerPacketUnits, packetTimestamp: String, ownerDelayUs: Int64) throws {
        self.basis = basis; self.hostClock = hostClock; self.packetClock = packetClock
        self.packetUnits = packetUnits; self.packetTimestamp = packetTimestamp; self.ownerDelayUs = ownerDelayUs
        try validate()
    }
    private enum CodingKeys: String, CodingKey { case basis, hostClock, packetClock, packetUnits, packetTimestamp, ownerDelayUs }
    public init(from decoder: Decoder) throws {
        let c = try decoder.container(keyedBy: CodingKeys.self)
        try self.init(basis: c.decode(PointerTimingBasis.self, forKey: .basis),
                      hostClock: c.decode(PointerHostClock.self, forKey: .hostClock),
                      packetClock: c.decode(PointerPacketClock.self, forKey: .packetClock),
                      packetUnits: c.decode(PointerPacketUnits.self, forKey: .packetUnits),
                      packetTimestamp: c.decode(String.self, forKey: .packetTimestamp),
                      ownerDelayUs: c.decode(Int64.self, forKey: .ownerDelayUs))
    }
    func validate() throws {
        try recordingRequire(ownerDelayUs >= 0 && !packetTimestamp.isEmpty && packetTimestamp.utf8.count <= 20 &&
            packetTimestamp.utf8.allSatisfy { (48...57).contains($0) } &&
            (packetTimestamp == "0" || packetTimestamp.utf8.first != 48), "Invalid pointer timing diagnostic")
        guard let stamp = UInt64(packetTimestamp) else { throw RecordingError(message: "Pointer packet timestamp overflow") }
        switch (hostClock, packetClock, packetUnits) {
        case (.machAbsolute, .cgEventTimestamp, .nanoseconds): break
        case (.queryPerformanceCounter, .windowsLowLevelMouseTime, .milliseconds):
            try recordingRequire(stamp <= UInt32.max, "Windows pointer timestamp overflow")
        default: throw RecordingError(message: "Incompatible pointer clocks/units")
        }
    }
}

/// One scalar of consistency state, independent of journal size.
struct PointerTimingValidation {
    private var hostClock: PointerHostClock?
    mutating func accept(_ event: CapturedPointerEvent, basis: PointerTimingBasis?) throws {
        try recordingRequire(event.timing?.basis == basis, "Pointer/take timing basis mismatch")
        if let timing = event.timing {
            try timing.validate()
            try recordingRequire(event.hostTimeUs >= 0 && (hostClock == nil || hostClock == timing.hostClock), "Invalid or mixed observation host clocks")
            hostClock = timing.hostClock
        }
    }
}
