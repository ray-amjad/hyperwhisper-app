//
//  TranscriptionTimestamps.swift
//  hyperwhisper
//
//  Provider-agnostic timestamp DTOs used to surface segment- and word-level
//  timings off a transcription provider via a side-channel (mirrors the
//  `detectedLanguage` pattern — no change to the `transcribe(...) -> String`
//  protocol signature). Times are in float seconds.
//

import Foundation

/// Which timestamp granularities a caller wants. Opt-in: an empty set means
/// "text only" and providers do no extra work. Mirrors OpenAI's
/// `timestamp_granularities` request param.
struct TimestampGranularities: OptionSet, Sendable {
    let rawValue: Int
    init(rawValue: Int) { self.rawValue = rawValue }

    static let segment = TimestampGranularities(rawValue: 1 << 0)
    static let word = TimestampGranularities(rawValue: 1 << 1)

    /// Parse the wire form (`["segment"]`, `["word"]`, or both). Unknown
    /// entries are ignored.
    init(wire: [String]?) {
        var set = TimestampGranularities()
        for raw in wire ?? [] {
            switch raw.lowercased() {
            case "segment", "segments": set.insert(.segment)
            case "word", "words": set.insert(.word)
            default: break
            }
        }
        self = set
    }
}

/// One word with approximate start/end (seconds) and avg token probability.
struct TranscriptionWordTimestamp: Sendable {
    let word: String
    let start: Double
    let end: Double
    let probability: Double?
}

/// One segment with start/end (seconds) and text.
struct TranscriptionSegmentTimestamp: Sendable {
    let id: Int
    let start: Double
    let end: Double
    let text: String
}

/// Timestamps produced by a provider for its most recent `transcribe(...)`
/// call. `rawText` is the uncleaned text the timings align to (the alignment
/// basis); never align timestamps to post-processed / cleaned text.
struct TranscriptionTimestamps: Sendable {
    let segments: [TranscriptionSegmentTimestamp]
    let words: [TranscriptionWordTimestamp]?
    let rawText: String
}

// MARK: - Persistence (JSON blob)

extension TranscriptionTimestamps {
    /// Wire/persistence shape stored in `Transcript.wordTimestampsJSON`.
    /// `basis` records what the timings align to — always "raw_text" in v1, so
    /// future readers never mistake them for being aligned to post-processed text.
    private struct PersistedBlob: Codable {
        struct Segment: Codable { let id: Int; let start: Double; let end: Double; let text: String }
        struct Word: Codable { let word: String; let start: Double; let end: Double; let probability: Double? }
        let basis: String
        let raw_text: String
        let segments: [Segment]
        let words: [Word]?
    }

    /// Serialize to the JSON string persisted on the Transcript, or nil when
    /// there's nothing meaningful to store.
    func wordTimestampsJSON() -> String? {
        guard !segments.isEmpty || !(words?.isEmpty ?? true) else { return nil }
        let blob = PersistedBlob(
            basis: "raw_text",
            raw_text: rawText,
            segments: segments.map { .init(id: $0.id, start: $0.start, end: $0.end, text: $0.text) },
            words: words?.map { .init(word: $0.word, start: $0.start, end: $0.end, probability: $0.probability) }
        )
        guard let data = try? JSONEncoder().encode(blob) else { return nil }
        return String(data: data, encoding: .utf8)
    }
}
