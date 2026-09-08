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

/// Models a serial-writer transaction that fails before it can return a
/// committed value snapshot. The service must treat `nil` as a hard abort.
private final class FailedWriterSavePersistenceController: PersistenceController {
    private struct ExpectedSaveFailure: Error {}

    override func saveWriterContext(_ context: NSManagedObjectContext) throws {
        throw ExpectedSaveFailure()
    }
}

/// Pauses the real production transaction after it stages deletes but before
/// its save. A view-context save can then create the exact conflict from the
/// production race without copying the method under test.
private final class DelayedWriterSavePersistenceController: PersistenceController {
    let gate = AutoDeleteWriterGate()

    override func saveWriterContext(_ context: NSManagedObjectContext) throws {
        gate.block()
        try super.saveWriterContext(context)
    }
}

/// Models a fetch that fails before the auto-delete transaction can produce a
/// value snapshot. Core Data's in-memory store cannot deterministically stage a
/// fetch error, so this test double stops at the persistence boundary.
private final class FailedWriterFetchPersistenceController: PersistenceController {
    private(set) var attemptedCutoffDate: Date?

    override func deleteTranscriptsOlderThanInBackground(
        _ cutoffDate: Date
    ) async -> AutoDeleteTransactionSnapshot? {
        attemptedCutoffDate = cutoffDate
        return nil
    }
}

/// A deterministic barrier for a synchronous Core Data writer block. Tests wait
/// for `block()` to start without blocking the main actor, then release the
/// writer after they have inspected state or queued another transaction.
private final class AutoDeleteWriterGate: @unchecked Sendable {
    private let entered = DispatchSemaphore(value: 0)
    private let releaseSemaphore = DispatchSemaphore(value: 0)

    func block() {
        entered.signal()
        _ = releaseSemaphore.wait(timeout: .now() + 5)
    }

    func waitUntilBlocked() async -> Bool {
        await Task.detached {
            self.entered.wait(timeout: .now() + 5) == .success
        }.value
    }

    func release() {
        releaseSemaphore.signal()
    }
}

// MARK: - Tests

/// Coverage for `AutoDeleteCleanupService.performCleanup()` itself
/// (HYPERWHISPER-HF).
///
/// `FileDeletionTests` covers the pure deletion helper. These cover what the
/// main-actor-hang fix changed *inside* `performCleanup()` and what is
/// observable from outside it: the `defer` that now solely owns
/// `isCleanupInProgress`, the stats reported after the off-actor hop, and the
/// bail-out that must leave every file alone when the Core Data save does not
/// commit.
///
/// Writer gates below also pin the ordering itself: pending Core Data work yields
/// the main actor, and cleanup queued behind a path rewrite snapshots the path
/// that the writer commits before cleanup starts.
@MainActor
struct AutoDeleteCleanupServiceTests {

    private static func waitUntil(
        _ condition: @escaping @MainActor () -> Bool
    ) async {
        for _ in 0..<1_000 {
            if condition() { return }
            await Task.yield()
        }
        Issue.record("Timed out while waiting for auto-delete state")
    }

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

    /// The production persistence operation must return only the ordered value
    /// snapshot and delete only rows strictly older than the cutoff.
    @Test func productionTransactionReturnsPathsAndDeletesOnlyExpiredRows() async throws {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let cutoff = Date()
        let firstOriginalPath = "/test/expired-first.wav"
        let firstTrimmedPath = "/test/expired-first-trimmed.wav"
        let secondOriginalPath = "/test/expired-second.wav"

        insertTranscript(
            into: context,
            date: cutoff.addingTimeInterval(-120),
            audioFilePath: firstOriginalPath,
            trimmedAudioFilePath: firstTrimmedPath
        )
        insertTranscript(
            into: context,
            date: cutoff.addingTimeInterval(-60),
            audioFilePath: secondOriginalPath
        )
        insertTranscript(
            into: context,
            date: cutoff.addingTimeInterval(60),
            audioFilePath: "/test/recent.wav"
        )
        try context.save()

        let completedSnapshot = await persistence.deleteTranscriptsOlderThanInBackground(cutoff)
        let snapshot = try #require(completedSnapshot)

        #expect(snapshot.transcriptsDeleted == 2)
        #expect(snapshot.audioPaths == [firstOriginalPath, firstTrimmedPath, secondOriginalPath])
        #expect(!snapshot.hasMore)
        #expect(try transcriptCount(in: context) == 1)
    }

    /// A view-context update saved after cleanup stages its delete must not make
    /// the row survive while cleanup unlinks its audio file.
    @Test func cleanupDeleteWinsConcurrentViewContextSave() async throws {
        let persistence = DelayedWriterSavePersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let transcript = insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: "/test/concurrent.wav"
        )
        try context.save()

        let cleanup = Task {
            await persistence.deleteTranscriptsOlderThanInBackground(Date())
        }
        let saveBlocked = await persistence.gate.waitUntilBlocked()
        #expect(saveBlocked)

        transcript.text = "concurrent edit"
        try context.save()
        persistence.gate.release()

        let completedSnapshot = await cleanup.value
        let snapshot = try #require(completedSnapshot)
        #expect(snapshot.transcriptsDeleted == 1)
        #expect(try transcriptCount(in: context) == 0)
    }

    /// A trimmed path attached after cleanup snapshots the row cannot be added
    /// through the view context. The serialized setter observes the committed
    /// delete and removes the new file because no transcript owns it.
    @Test func cleanupRemovesTrimmedPathAttachedAfterItsSnapshot() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = DelayedWriterSavePersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let originalPath = try makeFile(in: directory, byteCount: 64)
        let lateTrimmedPath = try makeFile(in: directory, byteCount: 128)
        let transcript = insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: originalPath
        )
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)
        let cleanup = Task { await service.performCleanup() }
        let cleanupSaveBlocked = await persistence.gate.waitUntilBlocked()
        #expect(cleanupSaveBlocked)

        var attachStarted = false
        let attachPath = Task {
            attachStarted = true
            return await persistence.setTrimmedAudioPath(transcript, trimmedPath: lateTrimmedPath)
        }
        await Self.waitUntil { attachStarted }
        persistence.gate.release()

        let completedStats = await cleanup.value
        let stats = try #require(completedStats)
        let pathAttached = await attachPath.value

        #expect(stats.transcriptsDeleted == 1)
        #expect(!pathAttached)
        #expect(!FileManager.default.fileExists(atPath: originalPath))
        #expect(!FileManager.default.fileExists(atPath: lateTrimmedPath))
        #expect(try transcriptCount(in: context) == 0)
    }

    /// One production transaction is bounded even for a large old backlog.
    @Test func productionTransactionLimitsEachWriterBatch() async throws {
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        for index in 0..<250 {
            insertTranscript(
                into: context,
                date: Date().addingTimeInterval(TimeInterval(-index - 1)),
                audioFilePath: nil
            )
        }
        try context.save()

        let completedFirst = await persistence.deleteTranscriptsOlderThanInBackground(Date())
        let first = try #require(completedFirst)

        #expect(first.transcriptsDeleted == 100)
        #expect(first.hasMore)
        #expect(try transcriptCount(in: context) == 150)
    }

    /// The service must accumulate every bounded writer batch and stop after
    /// the final short batch. This covers the complete 100 + 100 + 5 loop.
    @Test func serviceAccumulatesMultipleBatchesAndTerminates() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        var paths: [String] = []
        for index in 0..<205 {
            let path = try makeFile(in: directory, byteCount: 1)
            paths.append(path)
            insertTranscript(
                into: context,
                date: Date().addingTimeInterval(TimeInterval(-index - 1)),
                audioFilePath: path
            )
        }
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 205)
        #expect(stats.audioFilesDeleted == 205)
        #expect(stats.bytesFreed == 205)
        #expect(try transcriptCount(in: context) == 0)
        #expect(paths.allSatisfy { !FileManager.default.fileExists(atPath: $0) })
        #expect(!service.isCleanupInProgress)
        #expect(service.lastCleanupDate != nil)
        #expect(service.lastCleanupStats?.transcriptsDeleted == 205)
    }

    /// A queued writer transaction must suspend cleanup without holding the main
    /// actor. The closed gate makes the pending Core Data work deterministic.
    @Test func pendingCoreDataWorkLeavesMainActorResponsive() async throws {
        let persistence = PersistenceController(inMemory: true)
        let gate = AutoDeleteWriterGate()
        let blocker = Task {
            await persistence.performWrite { _ in gate.block() }
        }
        let writerBlocked = await gate.waitUntilBlocked()
        #expect(writerBlocked)

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)
        let cleanup = Task { await service.performCleanup() }
        await Self.waitUntil { service.isCleanupInProgress }

        MainActor.assertIsolated()
        #expect(service.isCleanupInProgress)

        gate.release()
        await blocker.value
        let completedStats = await cleanup.value
        let stats = try #require(completedStats)
        #expect(stats.transcriptsDeleted == 0)
        #expect(!service.isCleanupInProgress)
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

    /// A fetch failure is a hard abort. The service must preserve all rows and
    /// files, record no success state, and release its in-progress flag.
    @Test func failedFetchDeletesNothingAndRecordsNoSuccess() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = FailedWriterFetchPersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let path = try makeFile(in: directory, byteCount: 128)
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: path
        )
        try context.save()

        let cutoff = Date()
        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: cutoff)
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let stats = await service.performCleanup()

        #expect(persistence.attemptedCutoffDate == cutoff)
        #expect(stats == nil)
        #expect(service.lastCleanupStats == nil)
        #expect(service.lastCleanupDate == nil)
        #expect(FileManager.default.fileExists(atPath: path))
        #expect(try transcriptCount(in: context) == 1)
        #expect(!service.isCleanupInProgress)
    }

    /// Cleanup queued behind an audio-path rewrite must read the committed path
    /// from the same writer. It must not unlink the stale pre-rewrite path.
    @Test func cleanupSnapshotsCommittedPathAfterSerializedRewrite() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let stalePath = try makeFile(in: directory, byteCount: 64)
        let committedPath = try makeFile(in: directory, byteCount: 128)
        let transcript = insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: stalePath
        )
        try context.save()
        let transcriptID = transcript.objectID

        let gate = AutoDeleteWriterGate()
        let rewrite = Task {
            await persistence.performWrite { writerContext in
                let writerTranscript = try? writerContext.existingObject(with: transcriptID) as? Transcript
                writerTranscript?.audioFilePath = committedPath
                gate.block()
            }
        }
        let rewritePending = await gate.waitUntilBlocked()
        #expect(rewritePending)

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)
        let cleanup = Task { await service.performCleanup() }
        await Self.waitUntil { service.isCleanupInProgress }

        gate.release()
        await rewrite.value
        let completedStats = await cleanup.value
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 1)
        #expect(stats.audioFilesDeleted == 1)
        #expect(stats.bytesFreed == 128)
        #expect(FileManager.default.fileExists(atPath: stalePath))
        #expect(!FileManager.default.fileExists(atPath: committedPath))
        #expect(try transcriptCount(in: context) == 0)
    }

    /// Pending, invalid edits on the view context must not reach the writer save
    /// and poison an otherwise valid cleanup transaction.
    @Test func pendingViewContextEditsDoNotPoisonWriterCleanup() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let expiredPath = try makeFile(in: directory, byteCount: 256)
        insertTranscript(
            into: context,
            date: Date().addingTimeInterval(-3600),
            audioFilePath: expiredPath
        )
        try context.save()

        // `text` is required. This unrelated pending insert would make a
        // view-context save fail, but it never enters the writer transaction.
        let pendingTranscript = Transcript(context: context)
        pendingTranscript.id = UUID()
        pendingTranscript.date = Date().addingTimeInterval(3600)
        pendingTranscript.duration = 1

        let settings = FixedAutoDeleteSettings(enabled: true, cutoff: Date())
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let completedStats = await service.performCleanup()
        let stats = try #require(completedStats)

        #expect(stats.transcriptsDeleted == 1)
        #expect(stats.audioFilesDeleted == 1)
        #expect(stats.bytesFreed == 256)
        #expect(!FileManager.default.fileExists(atPath: expiredPath))
        #expect(!pendingTranscript.isDeleted)
        #expect(context.hasChanges)
        #expect(try transcriptCount(in: context) == 1)
        #expect(!service.isCleanupInProgress)
    }

    /// A save that does not commit must not delete a single file.
    ///
    /// The cleanup transaction now runs on the private serial writer. This
    /// fixture overrides only the save boundary, so the production fetch, path
    /// snapshot, deletes, rollback, and service failure handling all run.
    @Test func failedSaveDeletesNoFilesAndRollsBackTheRows() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = FailedWriterSavePersistenceController(inMemory: true)
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

        // This unrelated invalid edit must remain pending across the failed
        // writer transaction. Cleanup must not roll back user work.
        let pendingTranscript = Transcript(context: context)
        pendingTranscript.id = UUID()
        pendingTranscript.date = Date().addingTimeInterval(3600)
        pendingTranscript.duration = 1

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
        // The failed writer transaction did not change the view-context row.
        let remaining = try transcriptCount(in: context)
        #expect(remaining == 2)
        // Cleanup does not roll back unrelated view-context work. The removed
        // rollback was an accidental side effect that could discard user edits.
        #expect(!pendingTranscript.isDeleted)
        #expect(context.hasChanges)
        // And the pass still released the flag, so the next tick can retry.
        #expect(!service.isCleanupInProgress)
    }
}
