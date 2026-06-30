//
//  HyperWhisperCloudCredits.swift
//  hyperwhisper
//
//  HYPERWHISPER CLOUD CREDIT SYSTEM
//  Data models for tracking usage credits in the HyperWhisper Cloud service.
//
//  CREDIT MODEL:
//  - Trial users are tracked by device_id; licensed users by license_key.
//  - Credit balances and per-minute pricing are computed and enforced
//    server-side; this client only renders the values it is handed.
//
//  ARCHITECTURE:
//  - Credit balance is owned server-side.
//  - This model represents the response from the usage endpoint.
//  - Manager class handles fetching and caching credit balance.
//

import Foundation

/// Credit balance response from HyperWhisper Cloud
/// Returned by GET /usage?identifier=<device_id_or_license_key>
struct HyperWhisperCloudCredits: Codable {
    /// Current credit balance (in credits, not dollars)
    let creditsRemaining: Double

    /// Estimated minutes remaining based on credit balance (computed server-side)
    let minutesRemaining: Int

    /// Conversion factor provided by the server indicating credits consumed per minute of audio
    let creditsPerMinute: Double

    /// Whether this is a licensed user (Polar-tracked) or anonymous user (IP-limited)
    let isLicensed: Bool

    /// Whether this is an anonymous user (IP-based rate limiting)
    let isAnonymous: Bool

    /// When the daily limit resets (for anonymous users only)
    let resetsAt: Date?

    /// Polar customer ID (for licensed users only)
    let customerId: String?

    /// Optional message from server (e.g., for new users)
    let message: String?

    /// Coding keys for JSON decoding (snake_case to camelCase conversion)
    enum CodingKeys: String, CodingKey {
        case creditsRemaining = "credits_remaining"
        case minutesRemaining = "minutes_remaining"
        case creditsPerMinute = "credits_per_minute"
        case isLicensed = "is_licensed"
        case isAnonymous = "is_anonymous"
        case resetsAt = "resets_at"
        case customerId = "customer_id"
        case message
    }

    /// Custom decoder to handle ISO 8601 date strings
    init(from decoder: Decoder) throws {
        let container = try decoder.container(keyedBy: CodingKeys.self)
        if let decimalCredits = try? container.decode(Double.self, forKey: .creditsRemaining) {
            creditsRemaining = decimalCredits
        } else {
            // Older workers returned whole-number credits; normalize to Double for compatibility
            let wholeCredits = try container.decode(Int.self, forKey: .creditsRemaining)
            creditsRemaining = Double(wholeCredits)
        }
        minutesRemaining = try container.decode(Int.self, forKey: .minutesRemaining)
        creditsPerMinute = try container.decode(Double.self, forKey: .creditsPerMinute)
        isLicensed = try container.decode(Bool.self, forKey: .isLicensed)
        isAnonymous = try container.decode(Bool.self, forKey: .isAnonymous)
        customerId = try container.decodeIfPresent(String.self, forKey: .customerId)
        message = try container.decodeIfPresent(String.self, forKey: .message)

        // Decode date string if present
        if let dateString = try container.decodeIfPresent(String.self, forKey: .resetsAt) {
            let formatter = ISO8601DateFormatter()
            resetsAt = formatter.date(from: dateString)
        } else {
            resetsAt = nil
        }
    }

    /// Formatted string for display in UI
    /// Example: "$0.87 remaining (~87 minutes)"
    var formattedBalance: String {
        let dollars = creditsRemaining / 1000.0
        return String(format: "$%.2f remaining (~%d minutes)", dollars, minutesRemaining)
    }

    /// Whether user has exhausted their credits
    var isExhausted: Bool {
        creditsRemaining <= 0
    }

    /// Whether user has less than 10 minutes remaining (warning threshold)
    var isLow: Bool {
        minutesRemaining < 10 && minutesRemaining > 0
    }
}

/// Error types for HyperWhisper Cloud operations
enum HyperWhisperCloudError: LocalizedError {
    case insufficientCredits(remaining: Int, required: Int)
    /// Transient network failure (no internet, retry exhaustion) — suppressed in Sentry.
    case transientNetwork(String)
    /// Server contract violation (missing HTTPURLResponse, decode fail, unexpected status) — reported to Sentry.
    case invalidResponse(String)
    case serverError(String)

    var errorDescription: String? {
        switch self {
        case .insufficientCredits:
            return "hyperwhisperCloud.error.insufficientCredits".localized
        case .transientNetwork(let message):
            return "hyperwhisperCloud.error.network".localized(arguments: message)
        case .invalidResponse:
            return "hyperwhisperCloud.error.invalidResponse".localized
        case .serverError(let message):
            return "hyperwhisperCloud.error.server".localized(arguments: message)
        }
    }
}

/// Parsed details from a HyperWhisper Cloud 402 response.
///
/// The backend may serialize credit values as decimals. Keep that precision while
/// deciding whether a denial is an expected billing state or a contradictory
/// server response, then down-convert only for the legacy `TranscriptionError`
/// payload.
struct HyperWhisperCloudCreditDenial: Equatable {
    let remaining: Double?
    let required: Double?
    let limit: Double?
    let message: String?

    init(errorJson: [String: Any]?, message: String?) {
        let messageCredits = Self.parseMessageCredits(message)
        self.remaining = Self.number(from: errorJson?["credits_remaining"]) ?? messageCredits.remaining
        self.required = Self.number(from: errorJson?["credits_required"])
        self.limit = Self.number(from: errorJson?["credits_limit"]) ?? messageCredits.limit
        self.message = message
    }

    var remainingForTranscriptionError: Int {
        Self.legacyCreditInt(remaining)
    }

    var requiredForTranscriptionError: Int {
        Self.legacyCreditInt(required)
    }

    var invalidExhaustedBalanceMessage: String? {
        guard let remaining, remaining > 0 else { return nil }
        guard message?.range(of: "exhausted", options: [.caseInsensitive, .diacriticInsensitive]) != nil,
              message?.range(of: "credit", options: [.caseInsensitive, .diacriticInsensitive]) != nil else {
            return nil
        }

        guard let required, required < remaining else { return nil }

        let remainingText = Self.formatCredit(remaining)
        if let limit {
            return "HyperWhisper Cloud returned HTTP 402 for exhausted credits while reporting \(remainingText) of \(Self.formatCredit(limit)) credits remaining"
        }
        return "HyperWhisper Cloud returned HTTP 402 for exhausted credits while reporting \(remainingText) credits remaining"
    }

    private static func legacyCreditInt(_ value: Double?) -> Int {
        guard let value else { return 0 }
        return max(0, Int(value.rounded(.down)))
    }

    private static func number(from value: Any?) -> Double? {
        switch value {
        case let number as NSNumber:
            return number.doubleValue
        case let string as String:
            return Double(string.trimmingCharacters(in: .whitespacesAndNewlines))
        default:
            return nil
        }
    }

    private static func parseMessageCredits(_ message: String?) -> (remaining: Double?, limit: Double?) {
        guard let message else { return (nil, nil) }

        let pattern = #"\byou\s+have\s+([0-9]+(?:\.[0-9]+)?)\s+of\s+([0-9]+(?:\.[0-9]+)?)\s+credits?\s+remaining\b"#
        guard let regex = try? NSRegularExpression(pattern: pattern, options: [.caseInsensitive]) else {
            return (nil, nil)
        }

        let range = NSRange(message.startIndex..<message.endIndex, in: message)
        guard let match = regex.firstMatch(in: message, range: range),
              match.numberOfRanges == 3,
              let remainingRange = Range(match.range(at: 1), in: message),
              let limitRange = Range(match.range(at: 2), in: message) else {
            return (nil, nil)
        }

        return (Double(message[remainingRange]), Double(message[limitRange]))
    }

    private static func formatCredit(_ value: Double) -> String {
        if value.rounded(.towardZero) == value {
            return String(Int(value))
        }
        return String(format: "%.1f", value)
    }
}
