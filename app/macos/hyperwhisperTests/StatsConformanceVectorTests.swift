//
//  StatsConformanceVectorTests.swift
//  hyperwhisperTests
//
//  Runs `shared-conformance/stats-vectors.json` against the Swift UniFFI
//  binding. Issue #285 replaced the accumulation loop in `HomeStatsBar.swift`
//  and its .NET twin with one `hw-stats` call, so there is exactly one set of
//  calendar boundaries and exactly one savings formula; these vectors prove
//  macOS reads that one implementation's answer unchanged. Rust and C# run the
//  same file:
//
//    shared-core-rs/crates/hw-core/tests/stats_vectors.rs
//    app/shared-dotnet/HyperWhisper.StatsConformance.Tests/Program.cs
//
//  Every case carries a `decision` field naming which native copy the unified
//  answer came from. The rows that say `macos`, `dotnet`, `neither` or `new`
//  are the behaviour changes: macOS rounded 2.5 saved minutes down, only .NET
//  guarded a non-finite or negative duration and clamped the week's savings,
//  macOS's week started on Sunday, neither copy saturated an absurd word total,
//  and no copy had ever been asked about nesting, future dates or pre-epoch
//  dates at all.
//
//  Every instant in the file is LOCAL epoch seconds — the host shifts each row
//  into the calendar time zone before it calls, so these vectors are read
//  verbatim and never re-shifted. A `durationSeconds` of null means the store
//  held a non-finite value; it is handed over as `Double.nan`.
//
//  Regenerate the vectors from Rust after an intended policy change:
//    cd shared-core-rs && cargo test -p hw-core --test stats_vectors -- --ignored regenerate
//

import Foundation
import Testing
@testable import HyperWhisper

@MainActor
struct StatsConformanceVectorTests {

    // MARK: - Vector shapes

    struct Document: Decodable {
        let description: String
        let cases: [StatsVector]
    }

    struct StatsVector: Decodable {
        let name: String
        let decision: String
        let was: String
        let typingSpeedWordsPerMinute: Int32
        let nowLocalEpochSeconds: Int64
        let transcripts: [TranscriptVector]
        let expected: SnapshotVector
    }

    struct TranscriptVector: Decodable {
        let createdAtLocalEpochSeconds: Int64
        let wordCount: UInt32
        /// `null` means the store held a non-finite value.
        let durationSeconds: Double?
        let status: String
    }

    struct SnapshotVector: Decodable {
        let thisWeek: PeriodVector
        let thisMonth: PeriodVector
        let thisYear: PeriodVector
        let allTime: PeriodVector
        let typingSpeedWordsPerMinute: Int32
        let averageWordsPerMinute: Int32
        let savedThisWeekMinutes: Int32
    }

    struct PeriodVector: Decodable {
        let wordCount: UInt32
        let durationSeconds: Double
        let averageWordsPerMinute: Int32
        let estimatedTypingMinutes: Double
        let estimatedTimeSavedMinutes: Double
    }

    // MARK: - Loading

    private let document: Document

    init() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("shared-conformance/stats-vectors.json")
        document = try JSONDecoder().decode(Document.self, from: Data(contentsOf: url))
    }

    private func transcripts(_ vectors: [TranscriptVector]) -> [HwStatsTranscript] {
        vectors.map { vector in
            HwStatsTranscript(
                createdAtLocalEpochSeconds: vector.createdAtLocalEpochSeconds,
                wordCount: vector.wordCount,
                durationSeconds: vector.durationSeconds ?? Double.nan,
                status: status(vector.status))
        }
    }

    private func status(_ name: String) -> HwTranscriptStatus {
        switch name {
        case "completed": return .completed
        case "failed": return .failed
        default: return .processing
        }
    }

    /// The figures are money-free decimals produced by the same IEEE-754
    /// arithmetic on both sides, so this only absorbs JSON round-tripping.
    private func expect(_ actual: Double, _ expected: Double, _ label: String) {
        #expect(abs(actual - expected) < 1e-9, "\(label): got \(actual), want \(expected)")
    }

    private func expect(_ actual: HwPeriodStats, _ expected: PeriodVector, _ label: String) {
        #expect(actual.wordCount == expected.wordCount, "\(label): wordCount")
        expect(actual.durationSeconds, expected.durationSeconds, "\(label): durationSeconds")
        #expect(
            actual.averageWordsPerMinute == expected.averageWordsPerMinute,
            "\(label): averageWordsPerMinute")
        expect(
            actual.estimatedTypingMinutes, expected.estimatedTypingMinutes,
            "\(label): estimatedTypingMinutes")
        expect(
            actual.estimatedTimeSavedMinutes, expected.estimatedTimeSavedMinutes,
            "\(label): estimatedTimeSavedMinutes")
    }

    // MARK: - The vectors

    @Test("the home statistics match the shared vectors")
    func snapshotMatchesVectors() {
        for vector in document.cases {
            let actual = statsCalculateHome(
                transcripts: transcripts(vector.transcripts),
                typingSpeedWordsPerMinute: vector.typingSpeedWordsPerMinute,
                nowLocalEpochSeconds: vector.nowLocalEpochSeconds)

            expect(actual.thisWeek, vector.expected.thisWeek, "\(vector.name): thisWeek")
            expect(actual.thisMonth, vector.expected.thisMonth, "\(vector.name): thisMonth")
            expect(actual.thisYear, vector.expected.thisYear, "\(vector.name): thisYear")
            expect(actual.allTime, vector.expected.allTime, "\(vector.name): allTime")

            #expect(
                actual.typingSpeedWordsPerMinute == vector.expected.typingSpeedWordsPerMinute,
                "\(vector.name): typingSpeedWordsPerMinute")
            #expect(
                actual.averageWordsPerMinute == vector.expected.averageWordsPerMinute,
                "\(vector.name): averageWordsPerMinute")
            #expect(
                actual.savedThisWeekMinutes == vector.expected.savedThisWeekMinutes,
                "\(vector.name): savedThisWeekMinutes")
        }
    }

    /// A decision table is only proof while it still has a row in every bucket
    /// that records a behaviour change.
    @Test("every changed-behaviour bucket still has a row")
    func everyChangedBucketHasARow() {
        for decision in ["macos", "dotnet", "neither", "new"] {
            #expect(
                document.cases.contains { $0.decision == decision },
                "decision bucket \(decision) lost its last vector")
        }
    }

    /// `HomeStatsBar` shows the ceiling to the user as a plain minute count, so
    /// a change to it is a change to what the strip can ever display.
    @Test("the saved-minutes ceiling is one week")
    func savedMinutesCeilingIsOneWeek() {
        #expect(statsSavedMinutesCeiling() == 7 * 24 * 60)
    }
}
