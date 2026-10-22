//
//  HomeStatsModelTests.swift
//  hyperwhisperTests
//
//  Issue #1683: the Home stats strip no longer fetches every transcript on the
//  main thread on each visit. These tests drive the real loader against a real
//  in-memory Core Data store, and the real model's visit and save hooks:
//
//  - the loader gives the same four figures the core gives for the same rows;
//  - a repeat visit with no new transcript starts no load;
//  - a save that touches a Transcript while Home is visible updates the figures;
//  - a save while Home is hidden waits for the next visit.
//
//  Issue #2019: the tests used to poll the figures against a 5 s wall-clock
//  deadline. A recompute crosses the main actor three times (the save
//  notification's hop, the debounce, the detached load's hand-back), and on a
//  loaded CI runner the main actor stalled long enough that the deadline ran
//  out first. They now wait on the model's published figures, which ends the
//  moment the expected value is set however slow the runner is; the suite's
//  time limit is only a backstop for a recompute that never comes.
//

import Combine
import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
@Suite(.timeLimit(.minutes(2)))
struct HomeStatsModelTests {

    // MARK: - Fixtures

    private struct Row {
        let text: String
        let date: Date
        let duration: Double
        let status: String
    }

    private func insert(_ rows: [Row], into container: NSPersistentContainer) throws {
        let context = container.newBackgroundContext()
        var saveError: Error?
        context.performAndWait {
            for row in rows {
                let transcript = Transcript(context: context)
                transcript.id = UUID()
                transcript.text = row.text
                transcript.date = row.date
                transcript.duration = row.duration
                transcript.status = row.status
            }
            do { try context.save() } catch { saveError = error }
        }
        if let saveError { throw saveError }
    }

    /// Waits until the model publishes figures that satisfy `predicate`, and
    /// returns them. `@Published` hands a new subscriber its current value and
    /// keeps the latest one until it is asked for, so a value set before or
    /// during the wait is never missed. Returns nil only when the test task is
    /// cancelled, which the suite's time limit does.
    private func figures(
        of model: HomeStatsModel,
        where predicate: (HomeStatsFigures) -> Bool
    ) async -> HomeStatsFigures? {
        for await figures in model.$figures.values where predicate(figures) {
            return figures
        }
        return nil
    }

    private let now = Date()

    private var sampleRows: [Row] {
        let calendar = Calendar.current
        let lastYear = calendar.date(byAdding: .year, value: -1, to: now)!
        return [
            Row(text: "one two three four", date: now, duration: 6, status: "completed"),
            Row(text: "  five\nsix\tseven  ", date: now, duration: 3, status: "completed"),
            Row(text: "eight nine", date: lastYear, duration: 2, status: "completed"),
            Row(text: "failed rows never count", date: now, duration: 9, status: "failed"),
            Row(text: "nor do rows still processing", date: now, duration: 9, status: "processing"),
        ]
    }

    // MARK: - Loader

    @Test("the loader gives the core's figures for the same rows")
    func loaderMatchesTheCoreForTheSameRows() throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)

        let loaded = try HomeStatsLoader.load(
            container: persistence.container, typingSpeed: 40, now: now)

        // The same completed rows, built by hand, straight into the core.
        let expectedRows = sampleRows
            .filter { $0.status == "completed" }
            .map { row in
                HwStatsTranscript(
                    createdAtLocalEpochSeconds: HomeStatsLoader.localEpochSeconds(for: row.date),
                    wordCount: UInt32(row.text.split(whereSeparator: { $0.isWhitespace }).count),
                    durationSeconds: row.duration,
                    status: .completed)
            }
        let expected = HomeStatsLoader.figures(rows: expectedRows, typingSpeed: 40, now: now)

        #expect(loaded == expected)
        #expect(loaded.monthWords == 7)
        #expect(loaded.yearWords == 7)
        #expect(loaded.averageWPM > 0)
    }

    @Test("an empty store gives zeros")
    func emptyStoreGivesZeros() throws {
        let persistence = PersistenceController(inMemory: true)
        let loaded = try HomeStatsLoader.load(
            container: persistence.container, typingSpeed: 40, now: now)
        #expect(loaded == .zero)
    }

    // MARK: - Model

    @Test("a repeat visit with no new transcript starts no load")
    func repeatVisitWithNoChangeDoesNoWork() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(model.loadCount == 1)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)
        model.homeDisappeared()

        model.homeAppeared(typingSpeed: 40)
        model.homeDisappeared()
        model.homeAppeared(typingSpeed: 40)

        #expect(model.loadCount == 1)
        #expect(model.figures.monthWords == 7)
    }

    @Test("a new transcript updates the figures while Home is visible")
    func saveWhileVisibleRecomputes() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)

        try insert(
            [Row(text: "ten eleven twelve", date: now, duration: 2, status: "completed")],
            into: persistence.container)

        #expect(await figures(of: model) { $0.monthWords == 10 } != nil)
        #expect(model.loadCount == 2)
    }

    @Test("a save while Home is hidden waits for the next visit")
    func saveWhileHiddenRecomputesOnNextVisit() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)
        model.homeDisappeared()

        try insert(
            [Row(text: "ten eleven twelve", date: now, duration: 2, status: "completed")],
            into: persistence.container)
        try await Task.sleep(for: .milliseconds(300))
        #expect(model.loadCount == 1)
        #expect(model.figures.monthWords == 7)

        model.homeAppeared(typingSpeed: 40)
        #expect(model.loadCount == 2)
        #expect(await figures(of: model) { $0.monthWords == 10 } != nil)
    }

    @Test("leaving Home during the debounce waits for the next visit")
    func leavingDuringTheDebounceSkipsTheReload() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(200))

        model.homeAppeared(typingSpeed: 40)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)

        try insert(
            [Row(text: "ten eleven twelve", date: now, duration: 2, status: "completed")],
            into: persistence.container)
        // Let the save notification reach the main actor, then leave Home
        // before the debounce ends.
        try await Task.sleep(for: .milliseconds(50))
        model.homeDisappeared()
        try await Task.sleep(for: .milliseconds(400))
        #expect(model.loadCount == 1)

        model.homeAppeared(typingSpeed: 40)
        #expect(model.loadCount == 2)
        #expect(await figures(of: model) { $0.monthWords == 10 } != nil)
    }

    @Test("a save into another container does not wake the model")
    func saveIntoAnotherContainerIsIgnored() async throws {
        let persistence = PersistenceController(inMemory: true)
        let other = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)

        try insert(sampleRows, into: other.container)
        try await Task.sleep(for: .milliseconds(300))
        #expect(model.loadCount == 1)
    }

    @Test("a new typing speed re-runs the core")
    func typingSpeedChangeRecomputes() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await figures(of: model) { $0.monthWords == 7 } != nil)

        model.typingSpeedChanged(to: 100)
        #expect(model.loadCount == 2)
    }
}
