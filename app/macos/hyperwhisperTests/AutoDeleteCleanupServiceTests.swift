//
//  AutoDeleteCleanupServiceTests.swift
//  hyperwhisperTests
//

import CoreData
import Foundation
import SwiftUI
import Testing
@testable import HyperWhisper

// MARK: - Test double

/// An `AutoDeleteSettingsManager` whose settings are fixed by the test rather
/// than read from `UserDefaults.standard`.
///
/// This is not convenience — it is a safety requirement. The real manager is
/// `@AppStorage`-backed, so flipping `autoDeleteEnabled` for real would write
/// the defaults of the running host application (unit tests here run inside
/// `HyperWhisper.app` via `TEST_HOST`), and that app has a live
/// `AutoDeleteCleanupService` bound to `PersistenceController.shared` — i.e. to a
/// developer's actual recordings.
///
/// `performCleanup()` reads its settings once per pass, off the main actor,
/// through `settingsSnapshotFromDefaults()` (#880). This double overrides ONLY
/// that read. The gate and the cutoff are then computed by the production code
/// from the snapshot, so the tests exercise the real snapshot-to-cutoff path.
/// The default (1 minute) puts the cutoff 60 s in the past.
@MainActor
private final class FixedAutoDeleteSettings: AutoDeleteSettingsManager {
    private nonisolated let snapshot: AutoDeleteSettingsSnapshot

    init(enabled: Bool, timeUnit: AutoDeleteTimeUnit = .minutes, value: Int = 1) {
        self.snapshot = AutoDeleteSettingsSnapshot(enabled: enabled, timeUnit: timeUnit, value: value)
        super.init()
    }

    nonisolated override func settingsSnapshotFromDefaults() -> AutoDeleteSettingsSnapshot {
        snapshot
    }
}

/// An `AutoDeleteSettingsManager` whose off-main-actor settings read decodes a
/// private `UserDefaults` suite with the production decoder, instead of
/// `UserDefaults.standard` (see `FixedAutoDeleteSettings` for why a test must
/// never touch the host app's real defaults).
///
/// With a `SettingsReadGate`, the read parks until the test releases it — a
/// stand-in for a slow cfprefsd round trip (HYPERWHISPER-Y0, #880).
@MainActor
private final class SuiteBackedAutoDeleteSettings: AutoDeleteSettingsManager {
    private nonisolated let suiteName: String
    nonisolated let readGate: SettingsReadGate?

    init(suiteName: String, readGate: SettingsReadGate? = nil) {
        self.suiteName = suiteName
        self.readGate = readGate
        super.init()
    }

    nonisolated override func settingsSnapshotFromDefaults() -> AutoDeleteSettingsSnapshot {
        readGate?.block()
        guard let defaults = UserDefaults(suiteName: suiteName) else {
            return AutoDeleteSettingsSnapshot(enabled: false, timeUnit: .days, value: 30)
        }
        return AutoDeleteSettingsManager.settingsSnapshot(in: defaults)
    }
}

/// Parks a settings read until the test releases it, and records whether the
/// release came from the test (rather than the timeout). A read that ran on
/// the main actor would hold the test's own main-actor code out until the
/// timeout, so `releasedByTest` is the regression signal.
private final class SettingsReadGate: @unchecked Sendable {
    private let entered = DispatchSemaphore(value: 0)
    private let releaseSemaphore = DispatchSemaphore(value: 0)
    private let lock = NSLock()
    private var releasedInTime = false
    private var entries = 0

    func block() {
        lock.lock()
        entries += 1
        lock.unlock()
        entered.signal()
        let result = releaseSemaphore.wait(timeout: .now() + 2)
        lock.lock()
        releasedInTime = result == .success
        lock.unlock()
    }

    func waitUntilBlocked() async -> Bool {
        await waitOffThePool(for: entered, seconds: 5)
    }

    func release() {
        releaseSemaphore.signal()
    }

    var releasedByTest: Bool {
        lock.lock()
        defer { lock.unlock() }
        return releasedInTime
    }

    /// How many settings reads have entered the gate.
    var entryCount: Int {
        lock.lock()
        defer { lock.unlock() }
        return entries
    }
}

/// A counter only the main actor touches.
@MainActor
private final class MainActorCounter {
    private(set) var value = 0

    func increment() {
        value += 1
    }
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
        await waitOffThePool(for: entered, seconds: 5)
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

    /// Yields first, then polls every millisecond, for up to 5 seconds. The
    /// gate read in `performCleanup()` hops to a background queue (#880), and
    /// on a loaded runner that hop can outlast a fixed number of yields.
    private static func waitUntil(
        _ condition: @escaping @MainActor () -> Bool
    ) async {
        let deadline = Date().addingTimeInterval(5)
        var yields = 0
        while Date() < deadline {
            if condition() { return }
            if yields < 1_000 {
                yields += 1
                await Task.yield()
            } else {
                try? await Task.sleep(nanoseconds: 1_000_000)
            }
        }
        if condition() { return }
        Issue.record("Timed out while waiting for auto-delete state")
    }

    /// A private `UserDefaults` suite, so no test writes the host app's real
    /// defaults. Call `removePersistentDomain(forName:)` when done.
    private func makeDefaultsSuite() throws -> (name: String, defaults: UserDefaults) {
        let name = "AutoDeleteCleanupServiceTests-\(UUID().uuidString)"
        let defaults = try #require(UserDefaults(suiteName: name))
        return (name, defaults)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

    /// A successful retry path replacement removes the pre-upgrade artifact
    /// only after the new path has a Core Data owner.
    @Test func replacingTrimmedPathRemovesUnownedPreviousArtifact() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let originalPath = try makeFile(in: directory, byteCount: 64)
        let legacyPath = try makeFile(in: directory, byteCount: 128)
        let replacementPath = try makeFile(in: directory, byteCount: 256)
        let transcript = insertTranscript(
            into: context,
            date: Date(),
            audioFilePath: originalPath,
            trimmedAudioFilePath: legacyPath
        )
        try context.save()

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: replacementPath)

        #expect(saved)
        #expect(!FileManager.default.fileExists(atPath: legacyPath))
        #expect(FileManager.default.fileExists(atPath: replacementPath))
    }

    /// A path shared by another row remains owned and must not be deleted when
    /// one retry replaces its path.
    @Test func replacingSharedTrimmedPathPreservesOwnedArtifact() async throws {
        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }

        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let sharedLegacyPath = try makeFile(in: directory, byteCount: 128)
        let replacementPath = try makeFile(in: directory, byteCount: 256)
        let transcript = insertTranscript(
            into: context,
            date: Date(),
            audioFilePath: try makeFile(in: directory, byteCount: 64),
            trimmedAudioFilePath: sharedLegacyPath
        )
        insertTranscript(
            into: context,
            date: Date(),
            audioFilePath: try makeFile(in: directory, byteCount: 64),
            trimmedAudioFilePath: sharedLegacyPath
        )
        try context.save()

        let saved = await persistence.setTrimmedAudioPath(transcript, trimmedPath: replacementPath)

        #expect(saved)
        #expect(FileManager.default.fileExists(atPath: sharedLegacyPath))
        #expect(FileManager.default.fileExists(atPath: replacementPath))
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
                // Older than the 1-minute cutoff the double's snapshot gives.
                date: Date().addingTimeInterval(TimeInterval(-3600 - index)),
                audioFilePath: path
            )
        }
        try context.save()

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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
        let settings = FixedAutoDeleteSettings(enabled: true)
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
        let settings = FixedAutoDeleteSettings(enabled: false)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true, timeUnit: .minutes, value: 1)
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let before = Date()
        let stats = await service.performCleanup()
        let after = Date()

        // The cutoff the pass tried is the snapshot's: 1 minute before "now".
        let attempted = try #require(persistence.attemptedCutoffDate)
        #expect(attempted >= before.addingTimeInterval(-60))
        #expect(attempted <= after.addingTimeInterval(-60))
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

        let settings = FixedAutoDeleteSettings(enabled: true)
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

    // MARK: - Off-main-actor settings gate (HYPERWHISPER-Y0, #880)

    /// With auto-delete disabled in UserDefaults, the gate read must not hold
    /// the main actor: a main-actor counter keeps incrementing while the read
    /// is parked (a stand-in for a slow cfprefsd), and the pass still returns
    /// `nil` without touching the in-progress flag.
    @Test func disabledGateReadLeavesMainActorResponsive() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        suite.defaults.set(false, forKey: AutoDeleteDefaultsKey.enabled)

        let readGate = SettingsReadGate()
        let settings = SuiteBackedAutoDeleteSettings(suiteName: suite.name, readGate: readGate)
        let service = AutoDeleteCleanupService(
            settingsManager: settings,
            persistenceController: PersistenceController(inMemory: true)
        )

        let cleanup = Task { await service.performCleanup() }
        let readParked = await readGate.waitUntilBlocked()
        #expect(readParked)

        // The gate read is parked now. The main actor must keep running.
        let counter = MainActorCounter()
        let ticker = Task { @MainActor in
            while !Task.isCancelled {
                counter.increment()
                await Task.yield()
            }
        }
        await Self.waitUntil { counter.value >= 100 }
        ticker.cancel()
        let ticksWhileParked = counter.value

        readGate.release()
        let stats = await cleanup.value

        #expect(ticksWhileParked >= 100)
        // Released by the test, not by the timeout: the main actor ran the
        // lines above while the read was still parked.
        #expect(readGate.releasedByTest)
        #expect(stats == nil)
        #expect(!service.isCleanupInProgress)
        #expect(service.lastCleanupDate == nil)
    }

    /// The gate reads the live value on every pass, never a copy cached at
    /// launch: turning auto-delete on, then off again, while the app runs is
    /// seen by the very next pass.
    @Test func gateSeesALiveToggleOnEveryPass() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        suite.defaults.set(false, forKey: AutoDeleteDefaultsKey.enabled)

        let settings = SuiteBackedAutoDeleteSettings(suiteName: suite.name)
        let service = AutoDeleteCleanupService(
            settingsManager: settings,
            persistenceController: PersistenceController(inMemory: true)
        )

        let whileDisabled = await service.performCleanup()
        #expect(whileDisabled == nil)

        suite.defaults.set(true, forKey: AutoDeleteDefaultsKey.enabled)
        let whileEnabled = await service.performCleanup()
        let stats = try #require(whileEnabled)
        #expect(stats.transcriptsDeleted == 0)
        #expect(service.lastCleanupDate != nil)

        suite.defaults.set(false, forKey: AutoDeleteDefaultsKey.enabled)
        let afterDisabling = await service.performCleanup()
        #expect(afterDisabling == nil)
    }

    /// The plain readers must decode exactly what the `@AppStorage` properties
    /// decode, including the defaults when a key is absent and an unknown time
    /// unit falling back to days.
    @Test func defaultsReadersMatchAppStorage() throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let defaults = suite.defaults

        func appStorageEnabled() -> Bool {
            AppStorage(wrappedValue: false, AutoDeleteDefaultsKey.enabled, store: defaults).wrappedValue
        }
        func appStorageTimeUnit() -> AutoDeleteTimeUnit {
            let raw = AppStorage(
                wrappedValue: AutoDeleteTimeUnit.days.rawValue,
                AutoDeleteDefaultsKey.timeUnit,
                store: defaults
            ).wrappedValue
            return AutoDeleteTimeUnit(rawValue: raw) ?? .days
        }

        // Absent keys: the declared defaults.
        #expect(AutoDeleteSettingsManager.isAutoDeleteEnabled(in: defaults) == false)
        #expect(AutoDeleteSettingsManager.isAutoDeleteEnabled(in: defaults) == appStorageEnabled())
        #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == .days)
        #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == appStorageTimeUnit())

        for enabled in [true, false] {
            defaults.set(enabled, forKey: AutoDeleteDefaultsKey.enabled)
            #expect(AutoDeleteSettingsManager.isAutoDeleteEnabled(in: defaults) == enabled)
            #expect(AutoDeleteSettingsManager.isAutoDeleteEnabled(in: defaults) == appStorageEnabled())
        }

        for unit in AutoDeleteTimeUnit.allCases {
            defaults.set(unit.rawValue, forKey: AutoDeleteDefaultsKey.timeUnit)
            #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == unit)
            #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == appStorageTimeUnit())
        }

        defaults.set("fortnights", forKey: AutoDeleteDefaultsKey.timeUnit)
        #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == .days)
        #expect(AutoDeleteSettingsManager.autoDeleteTimeUnit(in: defaults) == appStorageTimeUnit())

        func appStorageValue() -> Int {
            AppStorage(wrappedValue: 30, AutoDeleteDefaultsKey.value, store: defaults).wrappedValue
        }

        // Absent value key: the declared default.
        #expect(AutoDeleteSettingsManager.autoDeleteValue(in: defaults) == 30)
        #expect(AutoDeleteSettingsManager.autoDeleteValue(in: defaults) == appStorageValue())

        for value in [1, 7, 30, 365, 0, -5] {
            defaults.set(value, forKey: AutoDeleteDefaultsKey.value)
            #expect(AutoDeleteSettingsManager.autoDeleteValue(in: defaults) == value)
            #expect(AutoDeleteSettingsManager.autoDeleteValue(in: defaults) == appStorageValue())
        }
    }

    /// The cutoff the timer now computes from its off-main snapshot must equal
    /// the one the old main-actor `deletionCutoffDate` computed from the
    /// `@AppStorage` values, for every unit, and both must say "no cutoff"
    /// when auto-delete is off or the value is below 1.
    ///
    /// The old path is reproduced here from SwiftUI's own `AppStorage` reads on
    /// the same private suite (never `.standard`), with its exact formula.
    @Test func snapshotCutoffMatchesTheOldMainActorCutoff() throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        let defaults = suite.defaults
        let now = Date()

        /// The removed `AutoDeleteSettingsManager.deletionCutoffDate`, fed by
        /// `@AppStorage` reads with the manager's keys and defaults.
        func oldMainActorCutoff() -> Date? {
            let enabled = AppStorage(wrappedValue: false, AutoDeleteDefaultsKey.enabled, store: defaults).wrappedValue
            let raw = AppStorage(
                wrappedValue: AutoDeleteTimeUnit.days.rawValue,
                AutoDeleteDefaultsKey.timeUnit,
                store: defaults
            ).wrappedValue
            let unit = AutoDeleteTimeUnit(rawValue: raw) ?? .days
            let value = AppStorage(wrappedValue: 30, AutoDeleteDefaultsKey.value, store: defaults).wrappedValue
            guard enabled else { return nil }
            guard value > 0 else { return nil }
            return now.addingTimeInterval(-unit.toSeconds(value))
        }
        func snapshotCutoff() -> Date? {
            AutoDeleteSettingsManager.settingsSnapshot(in: defaults).deletionCutoffDate(now: now)
        }

        // All keys absent: off, so no cutoff on either path.
        #expect(snapshotCutoff() == nil)
        #expect(oldMainActorCutoff() == nil)

        // On, with the unit and value keys absent: 30 days on both paths.
        defaults.set(true, forKey: AutoDeleteDefaultsKey.enabled)
        #expect(snapshotCutoff() == now.addingTimeInterval(-30 * 24 * 60 * 60))
        #expect(snapshotCutoff() == oldMainActorCutoff())

        for unit in AutoDeleteTimeUnit.allCases {
            for value in [1, 7, 45] {
                defaults.set(unit.rawValue, forKey: AutoDeleteDefaultsKey.timeUnit)
                defaults.set(value, forKey: AutoDeleteDefaultsKey.value)
                let expected = now.addingTimeInterval(-unit.toSeconds(value))
                #expect(snapshotCutoff() == expected)
                #expect(snapshotCutoff() == oldMainActorCutoff())
            }
        }

        // A value below 1 gives no cutoff on both paths.
        defaults.set(0, forKey: AutoDeleteDefaultsKey.value)
        #expect(snapshotCutoff() == nil)
        #expect(oldMainActorCutoff() == nil)

        // Off again: no cutoff, whatever the unit and value say.
        defaults.set(5, forKey: AutoDeleteDefaultsKey.value)
        defaults.set(false, forKey: AutoDeleteDefaultsKey.enabled)
        #expect(snapshotCutoff() == nil)
        #expect(oldMainActorCutoff() == nil)
    }

    /// With auto-delete ENABLED, the pass's one settings read must not hold the
    /// main actor either: a main-actor counter keeps incrementing while the
    /// read is parked, and the in-progress flag stays clear until it returns.
    /// Then the pass deletes by the cutoff from that same read (1 minute, from
    /// the private suite), which proves no other settings source was used: the
    /// row 1 hour old goes, the row 10 seconds old stays.
    @Test func enabledSnapshotReadLeavesMainActorResponsive() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        suite.defaults.set(true, forKey: AutoDeleteDefaultsKey.enabled)
        suite.defaults.set(AutoDeleteTimeUnit.minutes.rawValue, forKey: AutoDeleteDefaultsKey.timeUnit)
        suite.defaults.set(1, forKey: AutoDeleteDefaultsKey.value)

        let directory = try makeTemporaryDirectory()
        defer { try? FileManager.default.removeItem(at: directory) }
        let persistence = PersistenceController(inMemory: true)
        let context = persistence.container.viewContext
        let expiredPath = try makeFile(in: directory, byteCount: 64)
        let recentPath = try makeFile(in: directory, byteCount: 64)
        insertTranscript(into: context, date: Date().addingTimeInterval(-3600), audioFilePath: expiredPath)
        insertTranscript(into: context, date: Date().addingTimeInterval(-10), audioFilePath: recentPath)
        try context.save()

        let readGate = SettingsReadGate()
        let settings = SuiteBackedAutoDeleteSettings(suiteName: suite.name, readGate: readGate)
        let service = AutoDeleteCleanupService(settingsManager: settings, persistenceController: persistence)

        let cleanup = Task { await service.performCleanup() }
        let readParked = await readGate.waitUntilBlocked()
        #expect(readParked)

        // The settings read is parked now. The main actor must keep running.
        let counter = MainActorCounter()
        let ticker = Task { @MainActor in
            while !Task.isCancelled {
                counter.increment()
                await Task.yield()
            }
        }
        await Self.waitUntil { counter.value >= 100 }
        ticker.cancel()
        let ticksWhileParked = counter.value
        let inProgressWhileParked = service.isCleanupInProgress

        readGate.release()
        let completedStats = await cleanup.value
        let stats = try #require(completedStats)

        #expect(ticksWhileParked >= 100)
        #expect(readGate.releasedByTest)
        #expect(!inProgressWhileParked)
        #expect(readGate.entryCount == 1)
        #expect(stats.transcriptsDeleted == 1)
        #expect(stats.audioFilesDeleted == 1)
        #expect(!FileManager.default.fileExists(atPath: expiredPath))
        #expect(FileManager.default.fileExists(atPath: recentPath))
        #expect(try transcriptCount(in: context) == 1)
        #expect(!service.isCleanupInProgress)
    }

    /// While one pass waits on a stuck settings read, a second pass (the next
    /// timer tick) returns `nil` at once and queues no second read behind it.
    @Test func passSkipsWhileAnEarlierSettingsReadIsInFlight() async throws {
        let suite = try makeDefaultsSuite()
        defer { suite.defaults.removePersistentDomain(forName: suite.name) }
        suite.defaults.set(true, forKey: AutoDeleteDefaultsKey.enabled)

        let readGate = SettingsReadGate()
        let settings = SuiteBackedAutoDeleteSettings(suiteName: suite.name, readGate: readGate)
        let service = AutoDeleteCleanupService(
            settingsManager: settings,
            persistenceController: PersistenceController(inMemory: true)
        )

        let first = Task { await service.performCleanup() }
        let readParked = await readGate.waitUntilBlocked()
        #expect(readParked)

        // Returns without waiting: a queued read would sit behind the parked
        // one until the gate's 2 s timeout, and `releasedByTest` would fail.
        let second = await service.performCleanup()
        #expect(second == nil)

        readGate.release()
        let firstStats = await first.value
        #expect(firstStats != nil)
        #expect(readGate.releasedByTest)
        #expect(readGate.entryCount == 1)

        // The flag is clear again, so the next pass reads normally. Release
        // ahead of time so that read does not park.
        readGate.release()
        let third = await service.performCleanup()
        #expect(third != nil)
        #expect(readGate.entryCount == 2)
    }
}
