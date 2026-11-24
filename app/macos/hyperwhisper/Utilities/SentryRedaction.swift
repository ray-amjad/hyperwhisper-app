//
//  SentryRedaction.swift
//  hyperwhisper
//
//  Credential scrubbing for everything the Sentry SDK builds on its own from a
//  network request: the HTTPClientError event's `request`, the `http.client`
//  span's data and description, and the http breadcrumb's data.
//
//  Why this exists: `failedRequestTargets` makes the SDK report every 5xx from
//  a remote host, and the request it attaches can carry the user's Account Key
//  and their own provider API keys — in `Authorization`, in an `x-api-key` /
//  `xi-api-key` / `x-goog-api-key` style header, or in the query (Gemini's
//  `?key=`). The SDK's own header filter only knows a fixed list of names.
//
//  Plain Swift types only, no Sentry import: `hyperwhisperTests` does not link
//  the Sentry package, so the decisions live here where a unit test can run
//  them, and the Sentry hooks in SentryService+Privacy.swift are thin wrappers.
//

import Foundation

enum SentryRedaction {

    static let redacted = "[redacted]"

    // MARK: - Names

    /// Substrings that mark a header, query parameter or data key as holding a
    /// credential, matched case-insensitively. Over-matching (`author`,
    /// `monkey`) only costs a redacted value, so the list is broad on purpose.
    static let sensitiveNameFragments: [String] = [
        "key", "token", "secret", "auth", "password", "passwd", "pwd",
        "signature", "credential", "session", "cookie"
    ]

    /// Short names that are credentials on their own but too short to match
    /// as a substring without hitting every word that contains them.
    static let sensitiveExactNames: Set<String> = ["sig", "code", "jwt", "pass"]

    /// True for a header, query parameter or data key whose value must not
    /// leave this machine. Case-insensitive; `-` and `_` are ignored so
    /// `X-Api-Key`, `xi-api-key`, `api_key` and `apiKey` all match.
    static func isSensitiveName(_ name: String) -> Bool {
        let lowered = name.lowercased()
        if sensitiveExactNames.contains(lowered) { return true }
        let squashed = lowered.replacingOccurrences(of: "-", with: "")
            .replacingOccurrences(of: "_", with: "")
        return sensitiveNameFragments.contains { lowered.contains($0) || squashed.contains($0) }
    }

    // MARK: - Headers

    /// Every header whose NAME looks like a credential (Authorization,
    /// Proxy-Authorization, Cookie, Set-Cookie, X-Api-Key, …) becomes
    /// `[redacted]`. The rest keep their value, with any key-shaped substring
    /// in it redacted too, so Content-Type and User-Agent still help a triage.
    static func scrubHeaders(_ headers: [String: String]) -> [String: String] {
        var result: [String: String] = [:]
        for (name, value) in headers {
            result[name] = isSensitiveName(name) ? redacted : scrubFreeText(value)
        }
        return result
    }

    // MARK: - URLs and queries

    /// A URL with its query, fragment and `user:password@` removed, and any
    /// key-shaped path segment redacted. Used for fields that name WHERE a
    /// request went (`request.url`, span `url`): the query has its own field.
    static func scrubURL(_ url: String) -> String {
        var base = url
        if let cut = base.firstIndex(where: { $0 == "?" || $0 == "#" }) {
            base = String(base[..<cut])
        }
        if let userInfo { base = replacing(userInfo, in: base, with: "$1") }
        return scrubFreeText(base)
    }

    /// A query string (`a=1&key=…`, with or without a leading `?`) with every
    /// sensitively-named parameter's value redacted and every other value
    /// passed through `scrubFreeText`. Nil and empty stay as they are.
    static func scrubQueryString(_ query: String?) -> String? {
        guard let query, !query.isEmpty else { return query }
        var body = Substring(query)
        let prefix = body.first == "?" ? "?" : ""
        if !prefix.isEmpty { body = body.dropFirst() }
        let pairs = body.split(separator: "&", omittingEmptySubsequences: false).map { pair -> String in
            guard let eq = pair.firstIndex(of: "=") else {
                // A bare value with no name: keep it only if nothing in it looks like a key.
                return scrubFreeText(String(pair))
            }
            let name = String(pair[..<eq])
            let value = String(pair[pair.index(after: eq)...])
            let decodedName = name.removingPercentEncoding ?? name
            return "\(name)=" + (isSensitiveName(decodedName) ? redacted : scrubFreeText(value))
        }
        return prefix + pairs.joined(separator: "&")
    }

    // MARK: - Free text

    /// Credential shapes redacted wherever they appear in text, URL or not.
    /// Narrow on purpose: each must not match ordinary words or identifiers.
    private static let secretPatterns: [(regex: NSRegularExpression, template: String)] = ([
        // `Authorization: Bearer …`, `Basic …`, `Token …` written into text.
        // The lookahead wants a digit in the token, so prose such as
        // "Basic settings" or "Token refreshing" is left alone.
        (#"(?i)\b(Bearer|Basic|Token)\s+(?=[A-Za-z._~+/=-]*[0-9])[A-Za-z0-9._~+/=-]{8,}"#, "$1 [redacted]"),
        // OpenAI / Anthropic (`sk-…`, `sk-ant-…`, `sk-proj-…`) and
        // ElevenLabs (`sk_…`) style keys.
        (#"\bsk[-_][A-Za-z0-9_-]{8,}"#, "[redacted]"),
        // Groq.
        (#"\bgsk_[A-Za-z0-9]{8,}"#, "[redacted]"),
        // Google API keys (Gemini `?key=`).
        (#"\bAIza[0-9A-Za-z_-]{20,}"#, "[redacted]"),
        // The HyperWhisper Account Key: `HW-` and four groups of four
        // (nextjs/src/lib/account-key-redaction.ts). No leading `\b`: in a
        // percent-encoded `%3DHW-` the `D` before it is a word character.
        (#"(?i)HW-[A-Z0-9]{4}(?:-[A-Z0-9]{4}){3}(?![A-Z0-9])"#, "[redacted]")
    ] as [(String, String)]).compactMap { entry in
        // Compiled once: `beforeSendSpan` runs this on every span.
        (try? NSRegularExpression(pattern: entry.0)).map { (regex: $0, template: entry.1) }
    }

    private static let userInfo = try? NSRegularExpression(pattern: #"(://)[^/@\s]*@"#)

    /// `?name=value`, `&name=value`, `#name=value` or `;name=value` inside text.
    private static let embeddedParameter = try? NSRegularExpression(
        pattern: #"([?&#;])([^=&#;?\s"'<>]+)=([^&#;\s"'<>]*)"#
    )

    /// Text from an exception value, a message, a span description or a data
    /// value, with every sensitively-named URL parameter's value and every
    /// key-shaped substring redacted. Everything else is left byte for byte.
    static func scrubFreeText(_ text: String) -> String {
        var result = redactEmbeddedParameters(in: text)
        for (regex, template) in secretPatterns {
            result = replacing(regex, in: result, with: template)
        }
        return result
    }

    private static func redactEmbeddedParameters(in text: String) -> String {
        guard let regex = embeddedParameter, text.contains("=") else { return text }
        let ns = text as NSString
        var result = ""
        var cursor = 0
        for match in regex.matches(in: text, range: NSRange(location: 0, length: ns.length)) {
            let name = ns.substring(with: match.range(at: 2))
            guard isSensitiveName(name.removingPercentEncoding ?? name) else { continue }
            let valueRange = match.range(at: 3)
            result += ns.substring(with: NSRange(location: cursor, length: valueRange.location - cursor))
            result += redacted
            cursor = valueRange.location + valueRange.length
        }
        result += ns.substring(from: cursor)
        return result
    }

    private static func replacing(_ regex: NSRegularExpression, in text: String, with template: String) -> String {
        return regex.stringByReplacingMatches(
            in: text,
            range: NSRange(text.startIndex..., in: text),
            withTemplate: template
        )
    }

    // MARK: - Span, breadcrumb and context data

    /// Data keys that hold a whole URL. The SDK writes `url` (network span and
    /// http breadcrumb); `http.url` and `url.full` are the OTel spellings.
    static let urlDataKeys: Set<String> = ["url", "http.url", "url.full"]
    /// Data keys that hold only a query string.
    static let queryDataKeys: Set<String> = ["http.query", "url.query"]
    /// Data keys that hold only a fragment, which can carry an OAuth token.
    static let fragmentDataKeys: Set<String> = ["http.fragment", "url.fragment"]

    /// A span's, breadcrumb's or event context's data with every URL, query
    /// and credential scrubbed: URL keys lose their query, query keys keep
    /// only benign parameters, fragments go, `headers` dictionaries go through
    /// `scrubHeaders`, sensitively-named keys are redacted, and every other
    /// String is scrubbed as free text. Numbers and Bools pass through;
    /// nested dictionaries and arrays are walked.
    static func scrubData(_ data: [String: Any]) -> [String: Any] {
        var result: [String: Any] = [:]
        for (key, value) in data {
            result[key] = scrubDataValue(value, forKey: key)
        }
        return result
    }

    private static func scrubDataValue(_ value: Any, forKey key: String) -> Any {
        let lowered = key.lowercased()
        if lowered == "headers", let headers = value as? [String: Any] {
            var flat: [String: String] = [:]
            var result: [String: Any] = [:]
            for (name, headerValue) in headers {
                if let string = headerValue as? String { flat[name] = string } else {
                    result[name] = isSensitiveName(name) ? redacted : scrubAny(headerValue)
                }
            }
            scrubHeaders(flat).forEach { result[$0.key] = $0.value }
            return result
        }
        if fragmentDataKeys.contains(lowered) { return redacted }
        if let string = value as? String {
            if urlDataKeys.contains(lowered) { return scrubURL(string) }
            if queryDataKeys.contains(lowered) { return scrubQueryString(string) ?? string }
            if isSensitiveName(key) { return redacted }
            return scrubFreeText(string)
        }
        if isSensitiveName(key), !(value is Bool), !(value is NSNumber) { return redacted }
        return scrubAny(value)
    }

    private static func scrubAny(_ value: Any) -> Any {
        switch value {
        case let string as String: return scrubFreeText(string)
        case let dictionary as [String: Any]: return scrubData(dictionary)
        case let array as [Any]: return array.map(scrubAny)
        default: return value
        }
    }
}
