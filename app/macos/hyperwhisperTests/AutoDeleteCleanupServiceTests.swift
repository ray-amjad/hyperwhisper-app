//
//  AutoDeleteCleanupServiceTests.swift
//  hyperwhisperTests
//

import CoreData
import Foundation
import Testing
@testable import HyperWhisper

// MARK: - Test double

/// An `AutoDeleteSettingsManager` whose answers are fixed by the test rather
/// than by `UserDefaults.standard`.
///
/// This is not convenience — it is a safety requirement. The real manager is
/// `@AppStorage`-backed, so flipping `autoDeleteEnabled` for real would write
/// the defaults of the running host application (unit tests here run inside
/// `HyperWhisper.app` via `TEST_HOST`), and that app has a live
/// `AutoDeleteCleanupService` bound to `PersistenceController.shared` — i.e. to a
/// developer's actual recordings. Overriding the only two properties
/// `performCleanup()` reads from settings keeps every test confined to its own
/// in-memory store and its own temporary directory.
///
/// `@testable import` is what makes overriding an `internal` class member legal
/// from the test module.
@MainActor
private final class FixedAutoDeleteSettings: AutoDeleteSettingsManager {
    private var enabledValue: Bool
    private let cutoff: Date?

    init(enabled: Bool, cutoff: Date?) {
        self.enabledValue = enabled
        self.cutoff = cutoff
        super.init()
    }

    override var autoDeleteEnabled: Bool {
        get { enabledValue }
        set { enabledValue = newValue }
    }

    override var deletionCutoffDate: Date? { cutoff }
}

// MARK: - Tests

/// Coverage for `AutoDeleteCleanupService.performCleanup()` itself
/// (HYPERWHISPER-HF).
///
/// `FileDeletionTests` covers the pure deletion helper. These cover the three
/// things the main-actor-hang fix changed *inside* `performCleanup()`: the
/// `defer` that now solely owns `isCleanupInProgress`, the ordering that puts
/// the whole Core Data half before the off-actor hop, and the stats reported
/// afterwards.
@MainActor
struct AutoDeleteCleanupServiceTests {

    private func makeTemporaryDirectory() throws -> URL {
        let directory = FileManager.default.temporaryDirectory
            .appendingPathComponent("AutoDeleteCleanupServiceTests-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true)
        return directory
    }

    @discardableResult
    private func makeFile(in directory: URL, byteCount: Int) throws -> String {
        let url = directory.appendingPathComponent("\(UUID().uuidString).wav")
        try Data(repeating: 0x41, count: byteCount).write(to: url)
        return url.path
    }

    /// Inserts a transcript into `context` without saving.
    @discardableResult
    private func insertTranscript(
        into context: NSManagedObjectContext,
        date: Date,
        audioFilePath: String?,
        trimmedAudioFilePath: String? = nil
    ) -> Transcript {
        let transcript = Transcript(context: context)
        transcript.id = UUID()
        transcript.text = "test transcript"
        transcript.date = date
        transcript.duration = 1
        transcript.audioFilePath = audioFilePath
        // Matches how production reads it — the column is addressed by key
        // throughout `PersistenceController` and `HistoryView`.
        transcript.setValue(trimmedAudioFilePath, forKey: "trimmedAudioFilePath")
        return transcript
    }

    private func transcriptCount(in context: NSManagedObjectContext) throws -> Int {
        // Explicitly typed for the same reason `AutoDeleteCleanupService` does it:
        // `fetchRequest()` is overloaded between `NSManagedObject` and the
        // generated `Transcript` subclass.
        let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
        return try context.count(for: request)
    }

    /// A pass that finds nothing expired must leave `isCleanupInProgress` false.
    ///
    /// This is the regression this suite exists for. The empty-backlog branch
    /// returns early and no longer clears the flag explicitly — it depends
    /// entirely on the `defer` placed just after the flag is set. If that
    /// `defer` is ever lost in a refactor, the very first launch-time pass
    /// (which for most users finds nothing expired) latches the flag forever,
    /// every later tick hits the "already in progress" guard, and auto-delete is
    /// silently dead for the whole session. Nothing else in the suite would
    /// notice.
    @Test func emptyBacklogPassClearsTheInProgressFlag() async throws {
        let persistence = PersistenceController(inMemory: true)
        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 0)
        #expect(stats.audioFilesDeleted == 0)
        #expect(stats.bytesFreed == 0)
        #expect(!service.isCleanupInProgress)
        #expect(service.lastCleanupDate != nil)
        #expect(service.lastCleanupStats != nil)
    }

    /// The disabled early return must not latch the flag either — it happens
    /// before the flag is set, so the `defer` never registers.
    @Test func disabledPassReturnsNilAndLeavesTheFlagClear() async throws {
        let persistence = PersistenceController(inMemory: true)
        let settings = FixedAutoDeleteSettings(enabled: false, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let stats = await service.performCleanup()

        #expect(stats == nil)
        #expect(!service.isCleanupInProgress)
        #expect(service.lastCleanupDate == nil)
    }

    /// The whole contract of the fix in one pass: an expired row's audio files
    /// leave the disk, the row leaves Core Data, the reported stats describe
    /// exactly what happened, and the flag is clear at the end.
    ///
    /// `transcriptsDeleted` in particular has to be honest. The pre-fix ordering
    /// reported the *fetched* count while a skip guard could drop rows after the
    /// suspension point, so the stat, the completion log and the Sentry
    /// breadcrumb could all over-report.
    @Test func deletesExpiredTranscriptItsAudioFilesAndReportsMatchingStats() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let originalPath = try makeFile(in: directory, byteCount: 1024)
        let trimmedPath = try makeFile(in: directory, byteCount: 256)
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: originalPath,
            trimmedAudioFilePath: trimmedPath
        )
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 1)
        #expect(stats.audioFilesDeleted == 2)
        #expect(stats.bytesFreed == 1280)
        #expect(!FileManager.default.fileExists(atPath: originalPath))
        #expect(!FileManager.default.fileExists(atPath: trimmedPath))
        let remaining = try transcriptCount(in: context)
        #expect(remaining == 0)
        #expect(!service.isCleanupInProgress)
    }

    /// Only rows older than the cutoff go. A pass that swept everything would
    /// still satisfy the test above, so this pins the predicate down.
    @Test func leavesTranscriptsNewerThanTheCutoffAlone() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let expiredPath = try makeFile(in: directory, byteCount: 64)
        let recentPath = try makeFile(in: directory, byteCount: 64)
        insertTranscript(into: context, date: Date().addingTimeInterval(-3600), audioFilePath: expiredPath)
        insertTranscript(into: context, date: Date(), audioFilePath: recentPath)
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date().addingTimeInterval(-60))
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 1)
        #expect(stats.audioFilesDeleted == 1)
        #expect(!FileManager.default.fileExists(atPath: expiredPath))
        #expect(FileManager.default.fileExists(atPath: recentPath))
        let remaining = try transcriptCount(in: context)
        #expect(remaining == 1)
    }

    /// A transcript whose original and trimmed paths are the same string must
    /// count once, and a missing file must not be reported as a failure — both
    /// paths still produce truthful stats.
    @Test func duplicateAndMissingPathsProduceTruthfulStats() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let sharedPath = try makeFile(in: directory, byteCount: 512)
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: sharedPath,
            trimmedAudioFilePath: sharedPath
        )
        // A row whose audio file was already cleaned up by hand.
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: directory.appendingPathComponent("\(UUID().uuidString).wav").path
        )
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 2)
        #expect(stats.audioFilesDeleted == 1)
        #expect(stats.bytesFreed == 512)
        let remaining = try transcriptCount(in: context)
        #expect(remaining == 0)
    }

    /// A save that does not commit must not delete a single file.
    ///
    /// `PersistenceController.save()` swallows its error, so a failed save looks
    /// exactly like a successful one to the caller and the pending row deletes
    /// simply stay pending. Before the bail-out guard, the pass unlinked every
    /// collected path anyway and reported a clean success — leaving live History
    /// rows whose play buttons silently fail, the precise failure the
    /// save-before-unlink ordering exists to prevent. Disk-full
    /// (`NSFileWriteOutOfSpaceError`) is the realistic trigger, and it is the
    /// exact condition auto-delete is supposed to relieve.
    ///
    /// The save is made to fail honestly rather than by mocking: an unsaved
    /// `Transcript` with none of its mandatory attributes set fails
    /// `validateForInsert` on the *whole context's* next save. That is also a
    /// faithful model of the production trigger — one bad pending edit anywhere
    /// on the app-wide `viewContext` sinks the auto-delete save with it.
    @Test func failedSaveDeletesNoFilesAndRollsBackTheRows() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext

        let originalPath = try makeFile(in: directory, byteCount: 1024)
        let trimmedPath = try makeFile(in: directory, byteCount: 256)
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: originalPath,
            trimmedAudioFilePath: trimmedPath
        )
        try context.save()

        // Inserted AFTER the good save, so only the cleanup's save sees it.
        // `text` is non-optional in the model and is left nil, so this row fails
        // `validateForInsert` and takes the whole context's save down with it.
        // It is given a future date and a real id so that it is well-formed for
        // the `date < cutoff` fetch and is simply too new to be collected — the
        // save is the only thing it breaks.
        let unsavable = Transcript(context: context)
        unsavable.id = UUID()
        unsavable.date = Date().addingTimeInterval(3600)
        unsavable.duration = 1

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let stats = await service.performCleanup()

        // No stats claimed, and no stale success recorded for the UI to show.
        #expect(stats == nil)
        #expect(service.lastCleanupStats == nil)
        #expect(service.lastCleanupDate == nil)
        // The files are still referenced by a live row, so they must survive.
        #expect(FileManager.default.fileExists(atPath: originalPath))
        #expect(FileManager.default.fileExists(atPath: trimmedPath))
        // Rolled back: the transcript is back, the invalid row is gone, and the
        // context is clean for whatever runs next.
        let remaining = try transcriptCount(in: context)
        #expect(remaining == 1)
        #expect(!context.hasChanges)
        // And the pass still released the flag, so the next tick can retry.
        #expect(!service.isCleanupInProgress)
    }
}
