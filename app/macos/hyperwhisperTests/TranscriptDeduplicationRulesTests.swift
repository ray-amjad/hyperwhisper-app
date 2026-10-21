//
//  TranscriptDeduplicationRulesTests.swift
//  hyperwhisperTests
//
//  Cover for issue #909.
//
//  `PersistenceController` has two rules that delete transcript rows:
//  - the idempotency guard: a new processing row for an audio path that
//    already has one reuses it, and collapses any extra processing rows;
//  - the transient-twin collapse: completing a row deletes a processing or
//    completed row for the same path within 20 s and (0.5 s of duration or
//    the same text).
//  Each rule used to be written twice, once for the serial writer and once for
//  `viewContext`. They are now one helper each. These tests drive all four
//  entry points against an in-memory store, so both contexts are held to the
//  same rule, window edges included.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Both Core Data contexts apply the same transcript de-duplication rules (#909)")
struct TranscriptDeduplicationRulesTests {

    private struct Seed {
        var status: String
        var date: Date
        var duration: TimeInterval = 3
        var text: String = "seeded text"
        var transcribedText: String? = nil
        var path: String
    }

    private struct Row: Sendable, Equatable {
        let id: UUID
        let status: String?
    }

    /// Saves the seeds to the store through `viewContext` and returns their ids.
    @MainActor
    private func seed(_ seeds: [Seed], in persistence: PersistenceController) throws -> [UUID] {
        let context = persistence.container.viewContext
        let ids = seeds.map { seed -> UUID in
            let transcript = Transcript(context: context)
            let id = UUID()
            transcript.id = id
            transcript.text = seed.text
            transcript.date = seed.date
            transcript.duration = seed.duration
            transcript.audioFilePath = seed.path
            transcript.setValue(seed.status, forKey: "status")
            transcript.setValue(seed.transcribedText, forKey: "transcribedText")
            return id
        }
        try context.save()
        return ids
    }

    /// Every row the store holds for `path`, read through a fresh context.
    private func rows(for path: String, in persistence: PersistenceController) async -> [Row] {
        let context = persistence.container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            request.predicate = NSPredicate(format: "audioFilePath == %@", path)
            let found = (try? context.fetch(request)) ?? []
            return found.compactMap { transcript in
                transcript.id.map { Row(id: $0, status: transcript.value(forKey: "status") as? String) }
            }
        }
    }

    enum Writer: CaseIterable, Sendable {
        case serialWriter
        case viewContext
    }

    // MARK: - The tolerances

    @Test func theTolerancesKeepTheirValues() {
        #expect(PersistenceController.reusableTranscriptStatus == "processing")
        #expect(PersistenceController.transientTwinDateWindow == 20)
        #expect(PersistenceController.transientTwinDurationWindow == 0.5)
    }

    // MARK: - Idempotency guard

    @MainActor
    private func createProcessing(
        _ writer: Writer,
        path: String,
        duration: TimeInterval,
        in persistence: PersistenceController
    ) async throws -> UUID? {
        switch writer {
        case .serialWriter:
            let objectID = try #require(await persistence.createProcessingTranscriptInBackground(
                duration: duration,
                mode: "Dedup",
                audioFilePath: path,
                trimmedAudioPath: nil
            ))
            let context = persistence.container.newBackgroundContext()
            return await context.perform {
                (try? context.existingObject(with: objectID) as? Transcript)?.id
            }
        case .viewContext:
            return persistence.createProcessingTranscript(duration: duration, mode: "Dedup", audioFilePath: path).id
        }
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aSecondCreateReusesTheNewestProcessingRowAndCollapsesTheRest(_ writer: Writer) async throws {
        let persistence = PersistenceController(inMemory: true)
        let path = "/tmp/hw-909-reuse-\(writer).wav"
        let now = Date()
        let ids = try seed([
            Seed(status: "processing", date: now.addingTimeInterval(-600), duration: 2, path: path),
            Seed(status: "processing", date: now.addingTimeInterval(-60), duration: 2, path: path),
            Seed(status: "completed", date: now.addingTimeInterval(-30), duration: 2, path: path),
        ], in: persistence)

        // A padded path is the same file.
        let reusedID = try await createProcessing(writer, path: "  \(path)\n", duration: 5, in: persistence)

        #expect(reusedID == ids[1], "the newest processing row is the one reused")
        let left = await rows(for: path, in: persistence)
        #expect(Set(left.map(\.id)) == [ids[1], ids[2]], "the older processing duplicate goes; the completed row stays")
        #expect(left.first { $0.id == ids[1] }?.status == "processing")
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aCreateWithNoProcessingRowMakesANewOne(_ writer: Writer) async throws {
        let persistence = PersistenceController(inMemory: true)
        let path = "/tmp/hw-909-new-\(writer).wav"
        let ids = try seed([Seed(status: "completed", date: Date(), path: path)], in: persistence)

        let createdID = try #require(try await createProcessing(writer, path: path, duration: 3, in: persistence))

        #expect(createdID != ids[0])
        let left = await rows(for: path, in: persistence)
        #expect(Set(left.map(\.id)) == [ids[0], createdID])
    }

    // MARK: - Transient-twin collapse

    /// Seeds an anchor processing row plus `twin`, completes the anchor through
    /// `writer`, and returns whether the twin survived.
    @MainActor
    private func twinSurvives(_ writer: Writer, _ twin: (String) -> Seed, label: String) async throws -> Bool {
        let persistence = PersistenceController(inMemory: true)
        let path = "/tmp/hw-909-twin-\(writer)-\(label).wav"
        let ids = try seed([
            Seed(status: "processing", date: Date(), duration: 3, text: "Processing transcription...", path: path),
            twin(path),
        ], in: persistence)

        switch writer {
        case .serialWriter:
            let context = persistence.container.viewContext
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            request.predicate = NSPredicate(format: "id == %@", ids[0] as CVarArg)
            let anchor = try #require(try context.fetch(request).first)
            let saved = await persistence.updateTranscriptWithTranscriptionInBackground(
                transcriptID: anchor.objectID,
                transcribedText: "anchor words"
            )
            #expect(saved)
        case .viewContext:
            let context = persistence.container.viewContext
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            request.predicate = NSPredicate(format: "id == %@", ids[0] as CVarArg)
            let anchor = try #require(try context.fetch(request).first)
            persistence.updateTranscriptWithTranscription(anchor, transcribedText: "anchor words")
        }

        let left = await rows(for: path, in: persistence).map(\.id)
        #expect(left.contains(ids[0]), "the completed row itself is never deleted")
        return left.contains(ids[1])
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aNearNowTwinIsDeleted(_ writer: Writer) async throws {
        let survived = try await twinSurvives(writer, { path in
            Seed(status: "processing", date: Date().addingTimeInterval(-19), duration: 3.4, path: path)
        }, label: "near")
        #expect(!survived)
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aCompletedTwinWithTheSameRawTextIsDeletedWhateverItsDuration(_ writer: Writer) async throws {
        let survived = try await twinSurvives(writer, { path in
            Seed(status: "completed", date: Date().addingTimeInterval(-5), duration: 9, transcribedText: "anchor words", path: path)
        }, label: "same-text")
        #expect(!survived)
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aRowOutsideTheDateWindowStays(_ writer: Writer) async throws {
        let survived = try await twinSurvives(writer, { path in
            Seed(status: "completed", date: Date().addingTimeInterval(-25), duration: 3, transcribedText: "anchor words", path: path)
        }, label: "old")
        #expect(survived)
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aRowOutsideTheDurationWindowWithOtherTextStays(_ writer: Writer) async throws {
        let survived = try await twinSurvives(writer, { path in
            Seed(status: "completed", date: Date().addingTimeInterval(-5), duration: 3.7, transcribedText: "other words", path: path)
        }, label: "long")
        #expect(survived)
    }

    @MainActor
    @Test(arguments: Writer.allCases)
    func aFailedRowStaysEvenWhenNearNow(_ writer: Writer) async throws {
        let survived = try await twinSurvives(writer, { path in
            Seed(status: "failed", date: Date(), duration: 3, transcribedText: "anchor words", path: path)
        }, label: "failed")
        #expect(survived)
    }
}
