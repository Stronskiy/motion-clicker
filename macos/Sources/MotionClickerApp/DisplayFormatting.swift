import Foundation

enum DisplayFormatting {
    // Imported geometry is finite binary64, not bounded by the platform Int range.
    static func number(_ value: Double) -> String {
        value.formatted(.number.locale(Locale(identifier: "en_US_POSIX"))
            .grouping(.never).precision(.fractionLength(0...2)))
    }
}
