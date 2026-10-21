//
//  LocalAPIRecordingsSearchTests.swift
//  hyperwhisperTests
//
//  Issue #1043: `GET /recordings/search` ran its `LIKE[cd]` scan and built
//  every result row on the main thread (`@MainActor` + `viewContext`), which
//  froze the app for ~400 ms at 20,000 transcripts and hung it for 10 s+ in
//  the field (Sentry HYPERWHISPER-ZY). The search now runs on a background
//  context. These tests pin the two things that must hold after that move:
//  the wire values (`total`, `returned`, the order) are unchanged, and the
//  search finishes while the main thread is busy.
//

import Foundation
import CoreData
import Testing
@testable import HyperWhisper

struct LocalAPIRecordingsSearchTests {

    private static let base = Date(timeIntervalSince1970: 1_790_000_000)

    /// 120 transcripts, one minute apart, newest last. 70 of them mention the
    /// vendor, spread over the three searched columns, in mixed case and with
    /// a diacritic, so the `[cd]` semantics are part of what is pinned.
    private func seededStore() throws -> PersistenceController {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        for i in 0..<120 {
            let transcript = Transcript(context: context)
            transcript.id = UUID()
            transcript.date = Self.base.addingTimeInterval(TimeInterval(i * 60))
            transcript.duration = 2
            transcript.text = "plain row \(i)"
            if i < 70 {
                switch i % 3 {
                case 0: transcript.text = "Call the VENDOR about row \(i)"
                case 1: transcript.setValue("the véndor replied on row \(i)", forKey: "postProcessedText")
                default: transcript.setValue("vendor raw words \(i)", forKey: "transcribedText")
                }
            }
            transcript.setValue("completed", forKey: "status")
        }
        try context.save()
        return persistence
    }

    private func search(
        _ persistence: PersistenceController,
        q: String? = nil,
        since: Date? = nil,
        until: Date? = nil,
        limit: Int = 50
    ) async throws -> RecordingsListResponse {
        try await RecordingsEndpoint.fetchRecordings(
            .init(q: q, since: since, until: until, limit: limit),
            in: persistence.container
        )
    }

    @Test func totalAndReturnedMatchTheSeededStore() async throws {
        let persistence = try seededStore()

        // A full page: `total` still counts every match, not the page.
        let page = try await search(persistence, q: "vendor", limit: 50)
        #expect(page.ok)
        #expect(page.total == 70)
        #expect(page.returned == 50)
        #expect(page.recordings.count == 50)
        // Newest first: row 69 is the newest match.
        #expect(page.recordings.first?.date == Self.base.addingTimeInterval(69 * 60))
        #expect(page.recordings.map(\.date) == page.recordings.map(\.date).sorted(by: >))

        // A page that holds every match (the one-scan path).
        let all = try await search(persistence, q: "VeNdOr", limit: 500)
        #expect(all.total == 70)
        #expect(all.returned == 70)

        // Exactly `limit` matches: the count scan runs and agrees.
        let exact = try await search(persistence, q: "vendor", limit: 70)
        #expect(exact.total == 70)
        #expect(exact.returned == 70)

        // No match is a full scan that returns nothing.
        let none = try await search(persistence, q: "zebra")
        #expect(none.total == 0)
        #expect(none.returned == 0)
        #expect(none.recordings.isEmpty)

        // No query: the whole table is counted.
        let unfiltered = try await search(persistence, limit: 10)
        #expect(unfiltered.total == 120)
        #expect(unfiltered.returned == 10)

        // A date window, inclusive at both ends: rows 60...79, of which 60...69 match.
        let since = Self.base.addingTimeInterval(60 * 60)
        let until = Self.base.addingTimeInterval(79 * 60)
        let windowed = try await search(persistence, since: since, until: until, limit: 500)
        #expect(windowed.total == 20)
        let windowedMatches = try await search(persistence, q: "vendor", since: since, until: until, limit: 5)
        #expect(windowedMatches.total == 10)
        #expect(windowedMatches.returned == 5)
    }

    /// The DTO carries the same column values the main-actor version read.
    @Test func dtoFieldsComeFromTheRow() async throws {
        let persistence = try seededStore()
        let result = try await search(persistence, q: "replied on row 1", limit: 500)
        // Rows 10, 13, 16 and 19 match too; pick row 1 by its exact text.
        let row = try #require(result.recordings.first { $0.postProcessedText == "the véndor replied on row 1" })
        #expect(row.text == "plain row 1")
        #expect(row.transcribedText == nil)
        #expect(row.duration == 2)
        #expect(row.status == "completed")
        #expect(UUID(uuidString: row.id) != nil)
    }

    /// The regression this issue is about: the search must not need the main
    /// thread. The main thread is held on a semaphore while a detached task runs
    /// the search; a main-actor search can only finish after the wait times out.
    @MainActor
    @Test func searchFinishesWhileTheMainThreadIsBlocked() throws {
        let persistence = try seededStore()
        let container = persistence.container
        let done = DispatchSemaphore(value: 0)
        let box = ResultBox()

        Task.detached {
            let response = try? await RecordingsEndpoint.fetchRecordings(
                .init(q: "zebra", since: nil, until: nil, limit: 50),
                in: container
            )
            box.set(response?.total)
            done.signal()
        }

        let waited = done.wait(timeout: .now() + 10)
        #expect(waited == .success, "the search waited for the blocked main thread")
        #expect(box.value == 0)
    }
}

private final class ResultBox: @unchecked Sendable {
    private let lock = NSLock()
    private var stored: Int?
    func set(_ value: Int?) { lock.lock(); stored = value; lock.unlock() }
    var value: Int? { lock.lock(); defer { lock.unlock() }; return stored }
}
