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

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

@MainActor
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

    private func waitUntil(
        timeout: Duration = .seconds(5),
        _ condition: () -> Bool
    ) async -> Bool {
        let clock = ContinuousClock()
        let deadline = clock.now.advanced(by: timeout)
        while clock.now < deadline {
            if condition() { return true }
            try? await Task.sleep(for: .milliseconds(20))
        }
        return condition()
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
        #expect(await waitUntil { model.figures.monthWords == 7 })
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
        #expect(await waitUntil { model.figures.monthWords == 7 })

        try insert(
            [Row(text: "ten eleven twelve", date: now, duration: 2, status: "completed")],
            into: persistence.container)

        #expect(await waitUntil { model.figures.monthWords == 10 })
        #expect(model.loadCount == 2)
    }

    @Test("a save while Home is hidden waits for the next visit")
    func saveWhileHiddenRecomputesOnNextVisit() async throws {
        let persistence = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await waitUntil { model.figures.monthWords == 7 })
        model.homeDisappeared()

        try insert(
            [Row(text: "ten eleven twelve", date: now, duration: 2, status: "completed")],
            into: persistence.container)
        try await Task.sleep(for: .milliseconds(300))
        #expect(model.loadCount == 1)
        #expect(model.figures.monthWords == 7)

        model.homeAppeared(typingSpeed: 40)
        #expect(model.loadCount == 2)
        #expect(await waitUntil { model.figures.monthWords == 10 })
    }

    @Test("a save into another container does not wake the model")
    func saveIntoAnotherContainerIsIgnored() async throws {
        let persistence = PersistenceController(inMemory: true)
        let other = PersistenceController(inMemory: true)
        try insert(sampleRows, into: persistence.container)
        let model = HomeStatsModel(container: persistence.container, debounce: .milliseconds(50))

        model.homeAppeared(typingSpeed: 40)
        #expect(await waitUntil { model.figures.monthWords == 7 })

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
        #expect(await waitUntil { model.figures.monthWords == 7 })

        model.typingSpeedChanged(to: 100)
        #expect(model.loadCount == 2)
    }
}
