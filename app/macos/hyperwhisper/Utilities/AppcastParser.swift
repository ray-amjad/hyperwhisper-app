//
//  AppcastParser.swift
//  hyperwhisper
//
//  APPCAST XML PARSER
//  Fetches the Sparkle appcast.xml feed and turns it into the releases the
//  Recent Updates list renders. Uses XMLParser with a custom delegate to read
//  the feed structure.
//
//  The SELECTION RULES are not here. Issue #353 moved them into the shared Rust
//  core (`hw-releasenotes`' `appcast` module) and this file is a facade now:
//  read every <item> into a raw `HwAppcastFeedEntry` in document order, hand the
//  whole list to `appcastSelectReleases`, and map what comes back. Which field
//  the version comes from, which entries are dropped, how duplicate versions
//  collapse and how the list is ordered are decided once, in Rust, for this head
//  and Windows both — the two had drifted into different answers for every one
//  of those questions.
//
//  So there is NO filter, sort or dedupe below. Re-applying a rule here would
//  let this head drift again, which is the whole defect #353 closes. What stays
//  native is what is genuinely per-head: the XML reader itself (no XML crate in
//  the core, by design), the URL, the 60-second cache and the `maxReleases` cap.
//
//  Architecture:
//  - Singleton pattern with shared instance
//  - 60-second cache to minimize network calls
//  - XMLParserDelegate for custom parsing logic
//  - Async/await for modern concurrency
//
//  Feed Structure — every field below is read verbatim and passed to the core,
//  which decides what each one means:
//  <rss xmlns:sparkle="http://www.andymatuschak.org/xml-namespaces/sparkle">
//    <channel>
//      <title>hyperwhisper</title>            <!-- channel title: never an item's -->
//      <item>
//        <title>2.46.0</title>
//        <pubDate>Wed, 02 Sep 2026 12:06:28 +0000</pubDate>
//        <sparkle:version>116</sparkle:version>                  <!-- build number -->
//        <sparkle:shortVersionString>2.46.0</sparkle:shortVersionString>
//        <description><![CDATA[<b>Title</b><ul><li>Feature</li></ul>]]></description>
//        <!-- sparkle:releaseNotesLink, if present, means the notes are NOT
//             inline; the core drops such an entry because this UI cannot
//             fetch a link. The macOS feed has never carried one. -->
//      </item>
//    </channel>
//  </rss>

import Foundation
import os.log

/// PARSER: AppcastParser
/// Fetches and parses the appcast.xml feed to extract release information
///
/// Key Features:
/// - Fetches from https://www.hyperwhisper.com/appcast.xml
/// - Reads the Sparkle XML format using XMLParser, then hands every item to
///   `appcastSelectReleases` — the shared selection step (#353)
/// - Returns up to 5 most recent releases
/// - 60-second cache to reduce network load
/// - Comprehensive error handling
///
/// Usage:
/// ```swift
/// let releases = try await AppcastParser.shared.fetchReleases()
/// ```
class AppcastParser: NSObject {
    // MARK: - Singleton

    /// Shared instance for app-wide access
    static let shared = AppcastParser()

    // MARK: - Properties

    /// Appcast feed URL
    private let feedURL = URL(string: "https://www.hyperwhisper.com/appcast.xml")!

    /// Cache duration (60 seconds)
    private let cacheDuration: TimeInterval = 60

    /// Cached releases
    private var cachedReleases: [AppcastItem]?

    /// Timestamp of last cache update
    private var lastCacheUpdate: Date?

    /// Maximum number of releases to return
    private let maxReleases = 5

    /// Logger for debugging
    private let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "hyperwhisper", category: "AppcastParser")

    // MARK: - Public Methods

    /// Fetch releases from appcast feed
    /// Returns cached data if available and not expired
    /// - Parameter forceRefresh: If true, bypasses cache and fetches fresh data
    /// - Returns: Array of AppcastItem objects (up to 5 most recent)
    /// - Throws: AppcastError if fetch or parse fails
    func fetchReleases(forceRefresh: Bool = false) async throws -> [AppcastItem] {
        logger.debug("📡 AppcastParser: Fetching releases (forceRefresh: \(forceRefresh))")

        // Check cache first (unless force refresh)
        if !forceRefresh, let cached = cachedReleases, let lastUpdate = lastCacheUpdate {
            let cacheAge = Date().timeIntervalSince(lastUpdate)
            if cacheAge < cacheDuration {
                logger.debug("✅ AppcastParser: Returning cached releases (age: \(Int(cacheAge))s)")
                return cached
            }
        }

        // Fetch fresh data
        logger.debug("🌐 AppcastParser: Fetching from URL: \(self.feedURL.absoluteString)")
        let (data, response) = try await URLSession.shared.data(from: feedURL)

        // Validate response
        guard let httpResponse = response as? HTTPURLResponse else {
            logger.error("❌ AppcastParser: Invalid response type")
            throw AppcastError.invalidResponse
        }

        guard httpResponse.statusCode == 200 else {
            logger.error("❌ AppcastParser: HTTP error \(httpResponse.statusCode)")
            throw AppcastError.httpError(statusCode: httpResponse.statusCode)
        }

        // Parse XML and apply the shared selection rules.
        //
        // The cap stays native and stays HERE, before the cache, exactly as
        // before — Windows caps per call instead, so sharing this would force
        // one head to change its cache shape for no gain (#353, decision D6).
        // It is handed to `selectReleases` rather than applied to its result
        // because the core has already filtered, deduplicated and sorted by the
        // time it returns: the first five of its answer are the five this head
        // renders, whether the cap is applied before or after the map to
        // `AppcastItem`. Applying it after meant building 77 `AppcastItem`s —
        // 77 `releaseNotesParse` FFI calls and 77 `AttributedString`s — to keep
        // five.
        logger.debug("📝 AppcastParser: Parsing XML data (\(data.count) bytes)")
        let limitedReleases = try AppcastParser.selectReleases(from: data, limit: maxReleases)

        // Cache results
        cachedReleases = limitedReleases
        lastCacheUpdate = Date()

        logger.debug("✅ AppcastParser: Successfully selected \(limitedReleases.count) releases (cap \(self.maxReleases))")
        return limitedReleases
    }

    // MARK: - Selection (the seam the tests drive)

    /// Every `<item>` in the feed, exactly as the XML reader found it, in
    /// document order and with no rule applied.
    ///
    /// Split out so the reader — the only part of this step that is still this
    /// head's own — can be exercised without a network fetch. An absent element
    /// is `nil` and a present-but-empty one is `""`: that distinction belongs to
    /// the core (which skips a version candidate that is blank after trimming),
    /// not to this reader, so it is passed through rather than flattened. The
    /// values are untrimmed for the same reason; the core trims what it returns.
    static func feedEntries(from data: Data) throws -> [HwAppcastFeedEntry] {
        let parser = AppcastXMLParser()
        return try parser.parse(data: data)
    }

    /// The feed's releases as the Recent Updates list shows them: filtered,
    /// deduplicated and newest first.
    ///
    /// One `appcastSelectReleases` call does all of that. Nothing here re-filters,
    /// re-sorts or re-dedupes the result.
    ///
    /// - Parameter limit: How many releases to keep, or `nil` for all of them.
    ///   The *policy* still belongs to `fetchReleases`, which is the only caller
    ///   that passes a number (D6 keeps the cap native and pre-cache on this
    ///   head); this parameter only moves where the cap is *applied*. It is
    ///   applied to the core's answer, before the map — the core has already
    ///   filtered, deduplicated and ordered, so the first `limit` releases are
    ///   the same items either way, and each `AppcastItem` this avoids building
    ///   is a `releaseNotesParse` FFI call plus an `AttributedString`.
    static func selectReleases(from data: Data, limit: Int? = nil) throws -> [AppcastItem] {
        let entries = try feedEntries(from: data)

        let releases = appcastSelectReleases(entries: entries)
        let selected = limit.map { Array(releases.prefix($0)) } ?? releases

        return selected.map { release in
            AppcastItem(
                version: release.version,
                // `buildNumber` is this head's only native-only field (#353,
                // decision D6): the core carries the raw `sparkle:version`
                // through as a passthrough, and an entry that has none falls
                // back to the resolved version, as it did before.
                buildNumber: release.buildNumber ?? release.version,
                // Epoch seconds, and 0 when `<pubDate>` was absent, blank or
                // unparseable — which sorts the entry last. This head used to
                // substitute `Date()`, i.e. now, which put a malformed entry at
                // the TOP of the list and made the order change on every fetch.
                pubDate: Date(timeIntervalSince1970: Double(release.pubDateEpochSecs)),
                // Already trimmed and already known to be non-empty: the core
                // drops an entry with no inline notes, so the `hasReleaseNotes`
                // filter this method used to apply is gone rather than moved.
                releaseNotes: release.releaseNotes
            )
        }
    }
}

// MARK: - AppcastError

/// Errors that can occur during appcast parsing
enum AppcastError: LocalizedError {
    case invalidURL
    case networkError(Error)
    case invalidResponse
    case httpError(statusCode: Int)
    case parseError(String)

    var errorDescription: String? {
        switch self {
        case .invalidURL:
            return "Invalid appcast URL"
        case .networkError(let error):
            return "Network error: \(error.localizedDescription)"
        case .invalidResponse:
            return "Invalid server response"
        case .httpError(let statusCode):
            return "HTTP error: \(statusCode)"
        case .parseError(let message):
            return "Parse error: \(message)"
        }
    }
}

// MARK: - XML Parser Delegate

/// XML Parser delegate for parsing appcast XML
///
/// It accumulates raw fields only. It applies no rule: no field is trimmed, no
/// fallback is chosen between fields, and NO item is dropped for any reason —
/// the entry for an item with no title, no date and no notes is appended like
/// any other. Dropping is `appcastSelectReleases`' job, and an item this reader
/// swallowed could never be dropped by the same rule as its Windows twin.
private class AppcastXMLParser: NSObject, XMLParserDelegate {
    // MARK: - State

    /// The Sparkle extension namespace, i.e. the `xmlns:sparkle` of the feed's
    /// `<rss>` element.
    private static let sparkleNamespace = "http://www.andymatuschak.org/xml-namespaces/sparkle"

    /// Parsed feed entries (accumulated during parsing), in document order
    private var entries: [HwAppcastFeedEntry] = []

    /// Current item being accumulated
    private var currentTitle: String?
    private var currentPubDate: String?
    private var currentVersion: String?
    private var currentShortVersionString: String?
    private var currentDescription: String?
    private var currentHasReleaseNotesLink = false

    /// Character data accumulator
    private var characterBuffer: String = ""

    /// Logger
    private let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "hyperwhisper", category: "AppcastXMLParser")

    // MARK: - Parsing

    /// Parse XML data and return one raw feed entry per `<item>`, in document order
    func parse(data: Data) throws -> [HwAppcastFeedEntry] {
        let parser = XMLParser(data: data)
        parser.delegate = self
        // Namespace processing is ON, so `elementName` below is the LOCAL name
        // ("version") and `namespaceURI` carries the URI. That is what lets a
        // `sparkle:` element be told apart from a same-named RSS one.
        parser.shouldProcessNamespaces = true

        guard parser.parse() else {
            let error = parser.parserError?.localizedDescription ?? "Unknown error"
            logger.error("❌ XML parsing failed: \(error)")
            throw AppcastError.parseError(error)
        }

        return entries
    }

    // MARK: - XMLParserDelegate Methods

    /// Called when parser encounters a start tag
    func parser(_ parser: XMLParser,
                didStartElement elementName: String,
                namespaceURI: String?,
                qualifiedName qName: String?,
                attributes attributeDict: [String : String] = [:]) {
        characterBuffer = ""

        // Start of new item
        if elementName == "item" {
            currentTitle = nil
            currentPubDate = nil
            currentVersion = nil
            currentShortVersionString = nil
            currentDescription = nil
            currentHasReleaseNotesLink = false
        }

        // `sparkle:releaseNotesLink` means the notes live at a URL instead of in
        // <description>. Only its PRESENCE matters, so it is noted here rather
        // than read; the core decides what presence implies (it drops the entry,
        // because no card on either head can fetch a link).
        if namespaceURI == AppcastXMLParser.sparkleNamespace, elementName == "releaseNotesLink" {
            currentHasReleaseNotesLink = true
        }
    }

    /// Called when parser encounters character data
    func parser(_ parser: XMLParser, foundCharacters string: String) {
        characterBuffer += string
    }

    /// Called when parser encounters CDATA block
    func parser(_ parser: XMLParser, foundCDATA CDATABlock: Data) {
        if let string = String(data: CDATABlock, encoding: .utf8) {
            characterBuffer += string
        }
    }

    /// Called when parser encounters an end tag
    func parser(_ parser: XMLParser,
                didEndElement elementName: String,
                namespaceURI: String?,
                qualifiedName qName: String?) {
        // The element's own text, VERBATIM. Trimming is the core's, so that
        // "absent" (nil) and "present but blank" ("") both reach it intact.
        let value = characterBuffer

        // Store values based on element
        switch elementName {
        case "title":
            // FIRST OCCURRENCE ONLY, and it matters more than it used to: the
            // channel's own <title> is "hyperwhisper", and <title> is now a
            // version candidate (the last one the core falls back to), so a leak
            // would show "hyperwhisper" as a release. The reset on <item> start
            // is what makes this per-item rather than per-feed.
            if currentTitle == nil {
                currentTitle = value
            }
        case "version", "shortVersionString":
            // NAMESPACE, NOT PREFIX. The old code tested `qName ==
            // "sparkle:version"`, and whether Foundation populates
            // `qualifiedName` at all while `shouldProcessNamespaces` is true is
            // undocumented — if it does not, that test never fired and the build
            // number silently fell back to <title>. `namespaceURI` plus the local
            // `elementName` is specified for this mode, so it is correct either
            // way, and it is the same expression Windows uses (`sparkle + "version"`).
            // The question itself cannot be settled without running on macOS;
            // this keys on the pair that does not depend on the answer.
            if namespaceURI == AppcastXMLParser.sparkleNamespace {
                if elementName == "version" {
                    currentVersion = value
                } else {
                    currentShortVersionString = value
                }
            }
        case "pubDate":
            currentPubDate = value
        case "description":
            currentDescription = value
        case "item":
            // End of item. EVERY item becomes an entry — no conditions. What is
            // worth showing is `appcastSelectReleases`' decision, not this
            // reader's.
            entries.append(HwAppcastFeedEntry(
                title: currentTitle,
                sparkleVersion: currentVersion,
                sparkleShortVersionString: currentShortVersionString,
                pubDate: currentPubDate,
                description: currentDescription,
                hasReleaseNotesLink: currentHasReleaseNotesLink
            ))
        default:
            break
        }

        characterBuffer = ""
    }

    /// Called when parser encounters an error
    func parser(_ parser: XMLParser, parseErrorOccurred parseError: Error) {
        logger.error("❌ XML parse error: \(parseError.localizedDescription)")
    }
}
