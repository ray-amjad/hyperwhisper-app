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

    /// Heading shown above the bullet list, if the feed has one.
    ///
    /// The rule is `hw-releasenotes`' and is shared with Windows (#284,
    /// decision (c)): the first `<h2>` case-insensitively if the note has one,
    /// otherwise the content before the first `<ul>` — or before the first
    /// `<li>` when there is no `<ul>` — which is what this head did on its own.
    /// The macOS feed carries no heading, so it still shows no title; a feed
    /// that grows one is now read the same way on both platforms.
    ///
    /// Only the markup *before* the list counts under the second rule: a `<b>`
    /// inside the first `<li>` emphasises that bullet, it is not a title.
    let releaseTitle: AttributedString?

    /// Bullet points from the release notes, one per `<li>` element, with
    /// `<b>`/`<i>` emphasis and `<a href>` links preserved as styled runs.
    /// An item that carries no text — an empty or whitespace-only `<li>` — is
    /// dropped by the core.
    let bulletPoints: [AttributedString]

    // MARK: - Initialization

    /// PARSE ONCE, AT CONSTRUCTION.
    ///
    /// `releaseTitle` and `bulletPoints` used to be computed properties, so
    /// SwiftUI re-ran the whole HTML parse on every `body` pass of every
    /// `ReleaseNotesCard` — for every release in the Recent Updates list, on
    /// every redraw. They are stored now, and this initializer is the one place
    /// the note is read.
    ///
    /// The labels and their order are the memberwise initializer's, deliberately:
    /// `AppcastParser`, the `#if DEBUG` samples below and `ReleaseNotesHTMLTests`
    /// all build an item positionally and keep compiling untouched.
    init(version: String, buildNumber: String, pubDate: Date, releaseNotes: String?) {
        self.version = version
        self.buildNumber = buildNumber
        self.pubDate = pubDate
        self.releaseNotes = releaseNotes

        // A feed entry with no notes parses as the empty fragment: no title, no
        // bullets. There is no second call and no re-parse anywhere below.
        let note = releaseNotesParse(html: releaseNotes ?? "")
        self.releaseTitle = note.title.map { ReleaseNotesHTML.attributed(ReleaseNotesHTML.runs(from: $0.runs)) }
        self.bulletPoints = note.bullets.map { ReleaseNotesHTML.attributed(ReleaseNotesHTML.runs(from: $0.runs)) }
    }

    // MARK: - Computed Properties

    /// User-friendly formatted date string
    /// Example: "Oct 18, 2025"
    var formattedDate: String {
        let formatter = DateFormatter()
        formatter.dateStyle = .medium
        formatter.timeStyle = .none
        return formatter.string(from: pubDate)
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
