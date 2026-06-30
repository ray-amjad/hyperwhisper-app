//
//  AppcastParser.swift
//  hyperwhisper
//
//  APPCAST XML PARSER
//  Fetches and parses the Sparkle appcast.xml feed to extract release information.
//  Uses XMLParser with a custom delegate to parse the feed structure.
//
//  Architecture:
//  - Singleton pattern with shared instance
//  - 60-second cache to minimize network calls
//  - XMLParserDelegate for custom parsing logic
//  - Async/await for modern concurrency
//
//  Feed Structure:
//  <rss>
//    <channel>
//      <item>
//        <title>2.5.3</title>
//        <pubDate>Sat, 18 Oct 2025 13:17:41 +0900</pubDate>
//        <sparkle:version>32</sparkle:version>
//        <description><![CDATA[<b>Title</b><ul><li>Feature</li></ul>]]></description>
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
/// - Parses Sparkle XML format using XMLParser
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

        // Parse XML
        logger.debug("📝 AppcastParser: Parsing XML data (\(data.count) bytes)")
        let parser = AppcastXMLParser()
        let releases = try parser.parse(data: data)

        // Filter to only releases WITH release notes
        let releasesWithNotes = releases.filter { $0.hasReleaseNotes }

        // Limit to max releases
        let limitedReleases = Array(releasesWithNotes.prefix(maxReleases))

        // Cache results
        cachedReleases = limitedReleases
        lastCacheUpdate = Date()

        logger.debug("✅ AppcastParser: Successfully parsed \(limitedReleases.count) releases with notes (out of \(releases.count) total)")
        return limitedReleases
    }

    /// Clear cached data
    func clearCache() {
        logger.debug("🗑️ AppcastParser: Clearing cache")
        cachedReleases = nil
        lastCacheUpdate = nil
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
/// This class handles the actual XML parsing logic using XMLParserDelegate
private class AppcastXMLParser: NSObject, XMLParserDelegate {
    // MARK: - State

    /// Parsed releases (accumulated during parsing)
    private var releases: [AppcastItem] = []

    /// Current element being parsed
    private var currentElement: String?

    /// Current item being constructed
    private var currentTitle: String?
    private var currentPubDate: String?
    private var currentVersion: String?
    private var currentDescription: String?

    /// Character data accumulator
    private var characterBuffer: String = ""

    /// Logger
    private let logger = Logger(subsystem: Bundle.main.bundleIdentifier ?? "hyperwhisper", category: "AppcastXMLParser")

    // MARK: - Parsing

    /// Parse XML data and return array of AppcastItem
    func parse(data: Data) throws -> [AppcastItem] {
        let parser = XMLParser(data: data)
        parser.delegate = self
        parser.shouldProcessNamespaces = true

        guard parser.parse() else {
            let error = parser.parserError?.localizedDescription ?? "Unknown error"
            logger.error("❌ XML parsing failed: \(error)")
            throw AppcastError.parseError(error)
        }

        return releases
    }

    // MARK: - XMLParserDelegate Methods

    /// Called when parser encounters a start tag
    func parser(_ parser: XMLParser,
                didStartElement elementName: String,
                namespaceURI: String?,
                qualifiedName qName: String?,
                attributes attributeDict: [String : String] = [:]) {
        currentElement = elementName
        characterBuffer = ""

        // Start of new item
        if elementName == "item" {
            currentTitle = nil
            currentPubDate = nil
            currentVersion = nil
            currentDescription = nil
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
        let trimmedValue = characterBuffer.trimmingCharacters(in: .whitespacesAndNewlines)

        // Store values based on element
        switch elementName {
        case "title":
            if currentTitle == nil { // Only capture first title (item title, not channel title)
                currentTitle = trimmedValue
            }
        case "pubDate":
            currentPubDate = trimmedValue
        case "version":
            if qName == "sparkle:version" {
                currentVersion = trimmedValue
            }
        case "description":
            currentDescription = trimmedValue
        case "item":
            // End of item - create AppcastItem if we have required data
            if let version = currentTitle,
               let buildNumber = currentVersion ?? currentTitle,
               let dateString = currentPubDate {
                let date = AppcastItem.parseDate(dateString)
                let item = AppcastItem(
                    version: version,
                    buildNumber: buildNumber,
                    pubDate: date,
                    releaseNotes: currentDescription
                )
                releases.append(item)
            }
        default:
            break
        }

        characterBuffer = ""
        currentElement = nil
    }

    /// Called when parser encounters an error
    func parser(_ parser: XMLParser, parseErrorOccurred parseError: Error) {
        logger.error("❌ XML parse error: \(parseError.localizedDescription)")
    }
}
