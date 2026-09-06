//
//  HistoryProviderDisplayName.swift
//  hyperwhisper
//

import Foundation

/// Normalizes provider labels when history renders them.
/// Stored transcript values stay unchanged for backup and migration compatibility.
enum HistoryProviderDisplayName {
    static func normalize(_ storedName: String) -> String {
        let trimmedName = storedName.trimmingCharacters(in: .whitespacesAndNewlines)
        let streamingSuffix = " (Streaming)"
        let hasStreamingSuffix = trimmedName.lowercased().hasSuffix(streamingSuffix.lowercased())
        let baseName = hasStreamingSuffix
            ? String(trimmedName.dropLast(streamingSuffix.count)).trimmingCharacters(in: .whitespaces)
            : trimmedName

        guard baseName.caseInsensitiveCompare("xAI") == .orderedSame
            || baseName.caseInsensitiveCompare("SpaceXAI") == .orderedSame else {
            return storedName
        }

        return "SpaceXAI" + (hasStreamingSuffix ? streamingSuffix : "")
    }
}
