//
//  AppTypeClassifier.swift
//  hyperwhisper
//
//  Shared catalog-backed application classification for app-aware formatting.
//

import Foundation

public enum AppType: String, Codable {
    case email
    case ai
    case workMessaging
    case personalMessaging
    case document
    case code
    case terminal
    case sensitive
    case other

    var promptValue: String {
        switch self {
        case .workMessaging:
            return "work_messaging"
        case .personalMessaging:
            return "personal_messaging"
        default:
            return rawValue
        }
    }

    var category: String {
        switch self {
        case .email:
            return "Email Client"
        case .ai:
            return "AI"
        case .workMessaging, .personalMessaging:
            return "Communication"
        case .document:
            return "Document"
        case .code:
            return "Code Editor"
        case .terminal:
            return "Terminal"
        case .sensitive:
            return "Sensitive"
        case .other:
            return "Application"
        }
    }

    var textInputFormat: String {
        switch self {
        case .email:
            return "email"
        case .code:
            return "code"
        case .terminal:
            return "command"
        case .document:
            return "markdown"
        default:
            return "text"
        }
    }

    var catalogKey: String {
        switch self {
        case .workMessaging:
            return "workMessaging"
        case .personalMessaging:
            return "personalMessaging"
        default:
            return rawValue
        }
    }
}

public struct AppClassificationResult {
    let appType: AppType
    let confidence: String
    let source: String
    let matched: String?
}

private struct AppTypeCatalog: Decodable {
    let types: [String: AppTypeCatalogEntry]
}

private struct AppTypeCatalogEntry: Decodable {
    let macBundleIds: [String]
    let windowsProcesses: [String]
    let hosts: [String]
    let titleKeywords: [String]
}

private struct PreparedKeyword {
    let value: String
    let isSubstring: Bool
}

private struct PreparedEntry {
    let type: AppType
    let bundleIds: [String]
    let hosts: [String]
    let titleKeywords: [PreparedKeyword]
}

public final class AppTypeClassifier {
    public static let shared = AppTypeClassifier()
    private static let titleBoundaryCharacterSet = CharacterSet.alphanumerics.union(CharacterSet(charactersIn: "_"))
    private static let emailPattern = try? NSRegularExpression(
        pattern: #"\b[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}\b"#
    )

    private let catalog: AppTypeCatalog
    private let preparedEntries: [PreparedEntry]

    private init() {
        let loaded = Self.loadCatalog()
        self.catalog = loaded
        self.preparedEntries = Self.prepareEntries(from: loaded)
    }

    init(catalogData: Data) throws {
        let decoded = try JSONDecoder().decode(AppTypeCatalog.self, from: catalogData)
        self.catalog = decoded
        self.preparedEntries = Self.prepareEntries(from: decoded)
    }

    public func classify(
        bundleId: String,
        appName: String,
        browserHost: String?,
        browserTitle: String?,
        focusedElement: FocusedElementInfo?
    ) -> AppClassificationResult {
        let bundle = bundleId.trimmingCharacters(in: .whitespacesAndNewlines)
        let host = normalizeHost(browserHost)
        let title = browserTitle?.lowercased() ?? ""
        let name = appName.lowercased()

        if let hostMatch = matchHost(host) {
            return hostMatch
        }

        if let bundleMatch = matchMacBundle(bundle) {
            return bundleMatch
        }

        if let titleMatch = matchTitle(title) {
            return titleMatch
        }

        if let nameMatch = matchTitle(name) {
            return AppClassificationResult(
                appType: nameMatch.appType,
                confidence: "medium",
                source: "appName",
                matched: nameMatch.matched
            )
        }

        if let focusedMatch = matchFocusedElement(focusedElement) {
            return focusedMatch
        }

        return AppClassificationResult(appType: .other, confidence: "unknown", source: "default", matched: nil)
    }

    private func matchHost(_ host: String?) -> AppClassificationResult? {
        guard let host, !host.isEmpty else { return nil }
        for entry in preparedEntries {
            if let matched = entry.hosts.first(where: { host == $0 || host.hasSuffix("." + $0) }) {
                return AppClassificationResult(appType: entry.type, confidence: "strong", source: "browserHost", matched: matched)
            }
        }
        return nil
    }

    private func matchMacBundle(_ bundleId: String) -> AppClassificationResult? {
        guard !bundleId.isEmpty else { return nil }
        let lowered = bundleId.lowercased()
        for entry in preparedEntries {
            if entry.bundleIds.contains(lowered) {
                return AppClassificationResult(appType: entry.type, confidence: "strong", source: "bundleId", matched: lowered)
            }
        }
        return nil
    }

    private func matchTitle(_ title: String) -> AppClassificationResult? {
        guard !title.isEmpty else { return nil }
        for entry in preparedEntries {
            if let matched = entry.titleKeywords.first(where: { titleKeywordMatches($0, in: title) }) {
                return AppClassificationResult(appType: entry.type, confidence: "medium", source: "title", matched: matched.value)
            }
        }
        return nil
    }

    private func titleKeywordMatches(_ keyword: PreparedKeyword, in title: String) -> Bool {
        if keyword.isSubstring {
            return title.contains(keyword.value)
        }

        var searchStart = title.startIndex
        while let range = title.range(of: keyword.value, options: [], range: searchStart..<title.endIndex) {
            let beforeIsBoundary = range.lowerBound == title.startIndex || isTitleBoundary(before: range.lowerBound, in: title)
            let afterIsBoundary = range.upperBound == title.endIndex || isTitleBoundary(at: range.upperBound, in: title)
            if beforeIsBoundary && afterIsBoundary {
                return true
            }
            searchStart = range.upperBound
        }
        return false
    }

    private func isTitleBoundary(before index: String.Index, in string: String) -> Bool {
        isTitleBoundary(at: string.index(before: index), in: string)
    }

    private func isTitleBoundary(at index: String.Index, in string: String) -> Bool {
        guard let scalar = string[index].unicodeScalars.first else { return true }
        return !Self.titleBoundaryCharacterSet.contains(scalar)
    }

    private func matchFocusedElement(_ focusedElement: FocusedElementInfo?) -> AppClassificationResult? {
        guard let focusedElement else { return nil }
        let pieces = [
            focusedElement.role,
            focusedElement.title,
            focusedElement.description,
            focusedElement.placeholder,
            focusedElement.value
        ]
        .compactMap { $0?.lowercased() }
        .joined(separator: " ")

        if pieces.contains("subject") || pieces.contains("compose") || pieces.contains("to:") || pieces.contains("cc:") {
            return AppClassificationResult(appType: .email, confidence: "medium", source: "focusedElement", matched: nil)
        }

        let range = NSRange(pieces.startIndex..., in: pieces)
        if Self.emailPattern?.firstMatch(in: pieces, range: range) != nil {
            return AppClassificationResult(appType: .email, confidence: "weak", source: "focusedElementText", matched: nil)
        }

        return nil
    }

    private static func prepareEntries(from catalog: AppTypeCatalog) -> [PreparedEntry] {
        let order: [AppType] = [.sensitive, .email, .terminal, .code, .ai, .workMessaging, .personalMessaging, .document]
        return order.compactMap { type in
            guard let entry = catalog.types[type.catalogKey] else { return nil }
            let preparedKeywords = entry.titleKeywords.compactMap { raw -> PreparedKeyword? in
                let normalized = raw.trimmingCharacters(in: .whitespacesAndNewlines).lowercased()
                guard !normalized.isEmpty else { return nil }
                let isSubstring = normalized.contains(".") || normalized.contains("/") || normalized.contains(" ")
                return PreparedKeyword(value: normalized, isSubstring: isSubstring)
            }
            return PreparedEntry(
                type: type,
                bundleIds: entry.macBundleIds.map { $0.lowercased() },
                hosts: entry.hosts,
                titleKeywords: preparedKeywords
            )
        }
    }

    private func normalizeHost(_ value: String?) -> String? {
        guard var value = value?.trimmingCharacters(in: .whitespacesAndNewlines).lowercased(), !value.isEmpty else {
            return nil
        }
        if !value.contains("://") {
            value = "https://" + value
        }
        if let host = URL(string: value)?.host {
            return host.hasPrefix("www.") ? String(host.dropFirst(4)) : host
        }
        return value.hasPrefix("www.") ? String(value.dropFirst(4)) : value
    }

    private static func loadCatalog() -> AppTypeCatalog {
        let urls = [
            Bundle.main.url(forResource: "app-type-catalog", withExtension: "json", subdirectory: "shared-app-classification"),
            Bundle.main.url(forResource: "app-type-catalog", withExtension: "json")
        ].compactMap { $0 }

        for url in urls {
            if let data = try? Data(contentsOf: url),
               let catalog = try? JSONDecoder().decode(AppTypeCatalog.self, from: data) {
                return catalog
            }
        }

        return AppTypeCatalog(types: [:])
    }
}
