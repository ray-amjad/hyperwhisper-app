//
//  AppcastItem.swift
//  hyperwhisper
//
//  APPCAST ITEM MODEL
//  This model represents a single release entry from the appcast.xml feed, as
//  the Recent Updates list renders it.
//
//  It decides nothing about the feed. Which field the version comes from, how
//  the date text is read, which entries are dropped and how the list is ordered
//  are `hw-releasenotes`' rules, shared with Windows (#353); the HTML notes are
//  read by the same core (#284). This type formats what it is given.
//
//  Design Goals:
//  - Hold one selected release from the shared appcast selection step
//  - Parse the release notes once, at construction
//  - Provide user-friendly date formatting
//  - Support releases with or without release notes

import Foundation

/// MODEL: AppcastItem
/// Represents a single software release from the appcast feed
///
/// Properties:
/// - version: The version string (e.g., "2.5.3"), resolved by the core from
///   `sparkle:shortVersionString`, else `sparkle:version`, else `<title>`
/// - buildNumber: The feed's `sparkle:version` (e.g., "116"), or `version` again
///   when the item carries none. Stored for completeness; nothing renders it.
/// - pubDate: When this version was released. `Jan 1, 1970` when the feed's
///   `<pubDate>` was absent or unreadable — which sorts the entry last.
/// - releaseNotes: Optional HTML content with release information
///
/// Usage:
/// This model is populated by AppcastParser when fetching the appcast.xml feed.
/// It provides formatted output for display in the UI.
struct AppcastItem: Identifiable, Equatable {
    // MARK: - Properties

    /// Unique identifier (uses version as ID)
    ///
    /// `RecentUpdatesView` renders with `ForEach(..., id: \.element.id)`, so two
    /// items sharing a version would hand SwiftUI duplicate `Identifiable` ids.
    /// The shared step dedupes by version (#353), so a feed that repeated one
    /// can no longer produce that.
    var id: String { version }

    /// Version string (e.g., "2.5.3")
    let version: String

    /// Build number — the feed's `sparkle:version` (e.g., "116"), falling back
    /// to `version`. Kept as a native passthrough; no view reads it.
    let buildNumber: String

    /// Publication date. `1970-01-01` stands for "the feed's `<pubDate>` was
    /// absent, blank or unreadable" — the core's sentinel (#353, decision D2),
    /// chosen because it sorts such an entry last. This head used to substitute
    /// the current date, which sorted it first and changed on every fetch.
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
    ///
    /// An item that comes from the feed always has them — the core drops an
    /// entry whose notes are absent, blank or behind a `sparkle:releaseNotesLink`
    /// — but `ReleaseNotesCard` still asks, and the `#if DEBUG` sample below
    /// still answers `false`.
    var hasReleaseNotes: Bool {
        return releaseNotes != nil && !releaseNotes!.isEmpty
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
