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
//  search never runs on the main-queue `viewContext`.
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
    private func seededStore() async throws -> PersistenceController {
        let persistence = PersistenceController(inMemory: true)
        try await Self.seed(persistence.container)
        return persistence
    }

    /// Seeds on a private context inside `perform`, so a test on any thread
    /// keeps to Core Data's queue rules.
    fileprivate static func seed(_ container: NSPersistentContainer) async throws {
        let context = container.newBackgroundContext()
        try await context.perform {
            for i in 0..<120 {
                let transcript = Transcript(context: context)
                transcript.id = UUID()
                transcript.date = base.addingTimeInterval(TimeInterval(i * 60))
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
        }
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

    /// A full page: `total` still counts every match, not the page.
    @Test func aFullPageCountsEveryMatch() async throws {
        let persistence = try await seededStore()
        let page = try await search(persistence, q: "vendor", limit: 50)
        #expect(page.ok)
        #expect(page.total == 70)
        #expect(page.returned == 50)
        #expect(page.recordings.count == 50)
    }

    /// Newest first: row 69 is the newest match.
    @Test func resultsAreNewestFirst() async throws {
        let persistence = try await seededStore()
        let page = try await search(persistence, q: "vendor", limit: 50)
        let dates = page.recordings.map(\.date)
        #expect(dates.first == Self.base.addingTimeInterval(69 * 60))
        #expect(dates == dates.sorted(by: >))
    }

    /// A page that holds every match takes the one-scan path; the match is
    /// case- and diacritic-insensitive across the three text columns.
    @Test func aShortPageHoldsEveryMatch() async throws {
        let persistence = try await seededStore()
        let all = try await search(persistence, q: "VeNdOr", limit: 500)
        #expect(all.total == 70)
        #expect(all.returned == 70)
    }

    /// Exactly `limit` matches: the count scan runs and agrees.
    @Test func aPageOfExactlyLimitCountsAgain() async throws {
        let persistence = try await seededStore()
        let exact = try await search(persistence, q: "vendor", limit: 70)
        #expect(exact.total == 70)
        #expect(exact.returned == 70)
    }

    /// No match is a full scan that returns nothing.
    @Test func noMatchReturnsNothing() async throws {
        let persistence = try await seededStore()
        let none = try await search(persistence, q: "zebra")
        #expect(none.total == 0)
        #expect(none.returned == 0)
        #expect(none.recordings.isEmpty)
    }

    /// No query: the whole table is counted.
    @Test func noQueryCountsTheWholeTable() async throws {
        let persistence = try await seededStore()
        let unfiltered = try await search(persistence, limit: 10)
        #expect(unfiltered.total == 120)
        #expect(unfiltered.returned == 10)
    }

    /// A date window, inclusive at both ends: rows 60...79, of which 60...69 match.
    @Test func aDateWindowIsInclusive() async throws {
        let persistence = try await seededStore()
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
        let persistence = try await seededStore()
        let result = try await search(persistence, q: "replied on row 1", limit: 500)
        // Rows 10, 13, 16 and 19 match too; pick row 1 by its exact text.
        let row = try #require(result.recordings.first { $0.postProcessedText == "the véndor replied on row 1" })
        #expect(row.text == "plain row 1")
        #expect(row.transcribedText == nil)
        #expect(row.duration == 2)
        #expect(row.status == "completed")
        #expect(UUID(uuidString: row.id) != nil)
    }

    /// The regression this issue is about: the search must never run on the
    /// main-queue `viewContext`. A spy container records every touch of it; the
    /// search must make its own background context instead.
    @MainActor
    @Test func searchNeverTouchesTheViewContext() async throws {
        let container = SpyContainer(name: "HyperWhisper", managedObjectModel: PersistenceController.managedObjectModel)
        container.persistentStoreDescriptions.first?.url = URL(fileURLWithPath: "/dev/null")
        container.persistentStoreDescriptions.first?.configuration = nil
        try await withCheckedThrowingContinuation { (continuation: CheckedContinuation<Void, Error>) in
            container.loadPersistentStores { _, error in
                if let error { continuation.resume(throwing: error) } else { continuation.resume() }
            }
        }
        try await Self.seed(container)
        container.resetRecords()

        let page = try await RecordingsEndpoint.fetchRecordings(
            .init(q: "vendor", since: nil, until: nil, limit: 50),
            in: container
        )
        #expect(page.total == 70)
        #expect(page.returned == 50)
        #expect(container.viewContextReads == 0, "the search used the main-queue viewContext")
        #expect(container.backgroundContextsMade == 1)
    }
}

/// Counts how a caller reaches the store. Only the test touches the counters,
/// from the main actor, after the awaited calls return.
private final class SpyContainer: NSPersistentContainer {
    private let lock = NSLock()
    private var reads = 0
    private var made = 0

    var viewContextReads: Int { lock.lock(); defer { lock.unlock() }; return reads }
    var backgroundContextsMade: Int { lock.lock(); defer { lock.unlock() }; return made }

    func resetRecords() { lock.lock(); reads = 0; made = 0; lock.unlock() }

    override var viewContext: NSManagedObjectContext {
        lock.lock(); reads += 1; lock.unlock()
        return super.viewContext
    }

    override func newBackgroundContext() -> NSManagedObjectContext {
        lock.lock(); made += 1; lock.unlock()
        return super.newBackgroundContext()
    }
}
