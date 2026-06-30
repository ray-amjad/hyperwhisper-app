//
//  AppcastItem.swift
//  hyperwhisper
//
//  APPCAST ITEM MODEL
//  This model represents a single release entry from the appcast.xml feed.
//  It parses version information, release dates, and HTML-formatted release notes.
//
//  Design Goals:
//  - Parse Sparkle appcast XML format
//  - Extract and format release notes from CDATA HTML
//  - Provide user-friendly date formatting
//  - Support releases with or without release notes

import Foundation

/// MODEL: AppcastItem
/// Represents a single software release from the appcast feed
///
/// Properties:
/// - version: The version string (e.g., "2.5.3")
/// - buildNumber: The build number (e.g., "32")
/// - pubDate: When this version was released
/// - releaseNotes: Optional HTML content with release information
///
/// Usage:
/// This model is populated by AppcastParser when fetching the appcast.xml feed.
/// It provides formatted output for display in the UI.
struct AppcastItem: Identifiable, Equatable {
    // MARK: - Properties

    /// Unique identifier (uses version as ID)
    var id: String { version }

    /// Version string (e.g., "2.5.3")
    let version: String

    /// Build number (e.g., "32")
    let buildNumber: String

    /// Publication date
    let pubDate: Date

    /// Optional HTML release notes from CDATA section
    /// Format: <b>Title</b> <ul><li>Feature 1</li><li>Feature 2</li></ul>
    let releaseNotes: String?

    // MARK: - Computed Properties

    /// User-friendly formatted date string
    /// Example: "Oct 18, 2025"
    var formattedDate: String {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .none
        return formatter.string(from: pubDate)
    }

    /// Relative date string (e.g., "2 days ago", "Yesterday")
    var relativeDateString: String {
        let formatter = RelativeDateTimeFormatter()
        formatter.unitsStyle = .full
        return formatter.localizedString(for: pubDate, relativeTo: Date())
    }

    /// Extract the title from HTML release notes
    /// Looks for content between <b> and </b> tags
    var releaseTitle: String? {
        guard let html = releaseNotes else { return nil }

        // Extract text between <b> and </b>
        if let startRange = html.range(of: "<b>"),
           let endRange = html.range(of: "</b>") {
            let titleRange = startRange.upperBound..<endRange.lowerBound
            return String(html[titleRange]).trimmingCharacters(in: .whitespacesAndNewlines)
        }

        return nil
    }

    /// Extract bullet points from HTML release notes
    /// Returns an array of strings, one for each <li> element
    var bulletPoints: [String] {
        guard let html = releaseNotes else { return [] }

        var points: [String] = []
        var searchString = html

        // Find all <li>...</li> elements
        while let startRange = searchString.range(of: "<li>"),
              let endRange = searchString.range(of: "</li>") {
            let contentRange = startRange.upperBound..<endRange.lowerBound
            let content = String(searchString[contentRange])
                .trimmingCharacters(in: .whitespacesAndNewlines)

            if !content.isEmpty {
                points.append(content)
            }

            // Continue searching after this </li>
            searchString = String(searchString[endRange.upperBound...])
        }

        return points
    }

    /// Check if this release has release notes
    var hasReleaseNotes: Bool {
        return releaseNotes != nil && !releaseNotes!.isEmpty
    }

    // MARK: - Static Methods

    /// RFC 2822 date formatter for parsing appcast dates
    /// Format: "Sat, 18 Oct 2025 13:17:41 +0900"
    static let rfcDateFormatter: DateFormatter = {
        let formatter = DateFormatter()
        formatter.dateFormat = "EEE, dd MMM yyyy HH:mm:ss Z"
        formatter.locale = Locale(identifier: "en_US_POSIX")
        return formatter
    }()

    /// Parse an RFC date string to Date
    /// - Parameter dateString: RFC 2822 formatted date string
    /// - Returns: Date object, or current date if parsing fails
    static func parseDate(_ dateString: String) -> Date {
        return rfcDateFormatter.date(from: dateString) ?? Date()
    }
}

// MARK: - Preview Helpers

#if DEBUG
extension AppcastItem {
    /// Sample release for previews and testing
    static let sample = AppcastItem(
        version: "2.5.3",
        buildNumber: "32",
        pubDate: Date(),
        releaseNotes: """
            <b>Enhanced Audio Recording Manager and UI Improvements</b>
            <ul>
                <li>Migrated to modular audio recording architecture for better maintainability</li>
                <li>Improved performance and stability of audio processing</li>
                <li>Enhanced recording dialog with smoother animations</li>
                <li>Fixed audio device management issues</li>
                <li>General bug fixes and performance optimizations</li>
            </ul>
            """
    )

    /// Sample release without notes
    static let sampleNoNotes = AppcastItem(
        version: "2.5.2",
        buildNumber: "31",
        pubDate: Date().addingTimeInterval(-7 * 24 * 60 * 60), // 7 days ago
        releaseNotes: nil
    )
}
#endif
