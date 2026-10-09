//
//  PendingRetrySavesTranscriptTests.swift
//  hyperwhisperTests
//
//  Regression cover for issue #1636.
//
//  The recording pill's pending-file Retry (offered when the recorded file
//  could not be read) showed "Pasted!" on success but only set
//  `lastTranscription`: nothing was pasted or copied, and the failed History
//  row ("Audio file could not be read") never got the transcript.
//
//  The fix: the stop flow's delivery and row write are shared helpers
//  (`deliverBatchTranscript`, `saveBatchTranscript`) and the retry calls them;
//  the failed row's write is kept in `AppState.pendingRetryFailedRowWrite` so
//  the retry awaits it and completes that row in place.
//
//  `retryTranscriptionFromPendingPath` needs a live `RecordingLifecycle` and a
//  pipeline, so it cannot run here (see `PendingRetrySupersessionTests`). The
//  save is tested by calling `savePendingRetryTranscript` on an in-memory
//  store; the source tests pin that the retry and the stop flow route through
//  the shared helpers.
//

import CoreData
import Foundation
import Testing

@testable import HyperWhisper

/// DIAG #1653: records every writer save error.
func diag1653(_ line: String) {
    let text = "\(Date().timeIntervalSince1970) \(line)\n"
    let url = URL(fileURLWithPath: "/tmp/diag1653.log")
    if let handle = try? FileHandle(forWritingTo: url) {
        handle.seekToEndOfFile(); handle.write(text.data(using: .utf8)!); try? handle.close()
    } else {
        try? text.data(using: .utf8)!.write(to: url)
    }
}

final class DiagPersistence1653: PersistenceController {
    private let lock = NSLock()
    private var errors: [String] = []
    var saveErrors: [String] { lock.lock(); defer { lock.unlock() }; return errors }
    override func saveWriterContext(_ context: NSManagedObjectContext) throws {
        do {
            try super.saveWriterContext(context)
            diag1653("saveOK inserted-after-save")
        } catch {
            diag1653("saveThrew \(error)")
            let ns = error as NSError
            let detail = "DIAG writerSave domain=\(ns.domain) code=\(ns.code) desc=\(ns.localizedDescription) userInfo=\(ns.userInfo)"
            lock.lock(); errors.append(String(detail.prefix(3000))); lock.unlock()
            throw error
        }
    }
    override func performWriteRequiringSave<T: Sendable>(_ block: @escaping (NSManagedObjectContext) -> T?) async -> T? {
        await super.performWriteRequiringSave { [weak self] context in
            let value = block(context)
            let cm = context.persistentStoreCoordinator?.managedObjectModel
            diag1653("block value=\(value == nil ? "nil" : "some") inserted=\(context.insertedObjects.map { "\($0.entity.name ?? "?") same=\($0.entity.managedObjectModel === cm) temp=\($0.objectID.isTemporaryID)" }) classEntitySame=\(Transcript.entity().managedObjectModel === cm)")
            if value == nil {
                let coordinatorModel = context.persistentStoreCoordinator?.managedObjectModel
                var detail = "DIAG blockReturnedNil inserted=\(context.insertedObjects.count)"
                for object in context.insertedObjects {
                    detail += " entity=\(object.entity.name ?? "?") sameModelAsCoordinator=\(object.entity.managedObjectModel === coordinatorModel)"
                    do { try context.obtainPermanentIDs(for: [object]) } catch { detail += " permIDError=\(error)" }
                }
                let classEntity = Transcript.entity()
                detail += " classEntitySameModel=\(classEntity.managedObjectModel === coordinatorModel)"
                self?.lock.lock(); self?.errors.append(String(detail.prefix(3000))); self?.lock.unlock()
            }
            return value
        }
    }
    func recordErrors(_ site: String) {
        for e in saveErrors { Issue.record(Comment(rawValue: site + " " + e)) }
    }
}

@Suite("A successful pending-file Retry delivers and saves its transcript (#1636)")
struct PendingRetrySavesTranscriptTests {

    // MARK: - Fixtures

    private struct Row: Sendable {
        let status: String?
        let failedReason: String?
        let text: String?
        let transcribedText: String?
        let postProcessedText: String?
        let transcriptionProvider: String?
        let audioFilePath: String?
        let retryCount: Int16
        let lastRetryDate: Date?
    }

    /// Every Transcript row, read on a fresh context so it sees what the
    /// serial writer actually saved.
    @MainActor
    private func rows(in persistence: PersistenceController) async -> [Row] {
        (persistence as? DiagPersistence1653)?.recordErrors("rows")
        let context = persistence.container.newBackgroundContext()
        return await context.perform {
            let request: NSFetchRequest<Transcript> = Transcript.fetchRequest()
            let fetched = (try? context.fetch(request)) ?? []
            return fetched.map { transcript in
                Row(
                    status: transcript.value(forKey: "status") as? String,
                    failedReason: transcript.value(forKey: "failedReason") as? String,
                    text: transcript.text,
                    transcribedText: transcript.value(forKey: "transcribedText") as? String,
                    postProcessedText: transcript.value(forKey: "postProcessedText") as? String,
                    transcriptionProvider: transcript.value(forKey: "transcriptionProvider") as? String,
                    audioFilePath: transcript.audioFilePath,
                    retryCount: transcript.value(forKey: "retryCount") as? Int16 ?? 0,
                    lastRetryDate: transcript.value(forKey: "lastRetryDate") as? Date
                )
            }
        }
    }

    private static let audioPath = "/tmp/hw-1636-missing-recording.wav"

    /// The row the stop flow writes when the recorded file cannot be read.
    @MainActor
    private func makeFailedRow(in persistence: PersistenceController) async throws -> NSManagedObjectID {
        let id = await persistence.createFailedTranscriptInBackground(
            duration: 4.2,
            mode: "LocNemo",
            audioFilePath: Self.audioPath,
            failedReason: "Audio file could not be read",
            errorText: "Error: Audio file could not be read"
        )
        if id == nil { (persistence as? DiagPersistence1653)?.recordErrors("makeFailedRow") }
        return try #require(id)
    }

    private func makeResult(postProcessed: Bool) -> TranscriptionResult {
        TranscriptionResult(
            text: postProcessed ? "The quick brown fox." : "the quick brown fox",
            rawText: "the quick brown fox",
            timestamp: Date(),
            duration: 0,
            mode: nil,
            provider: "local",
            wasPostProcessed: postProcessed,
            postProcessingProvider: postProcessed ? "openai" : nil
        )
    }

    // MARK: - The save

    /// The issue's own sequence: the failed row exists, the retry succeeds.
    /// That row now holds the transcript and no longer reads as failed.
    @MainActor
    @Test func theFailedRowIsCompletedInPlace() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)

        let savedID = await RecordingTranscriptionFlow.savePendingRetryTranscript(
            makeResult(postProcessed: false),
            failedRowWrite: Task { failedID },
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "LocNemo",
            persistence: persistence
        )

        #expect(savedID == failedID)
        let all = await rows(in: persistence)
        #expect(all.count == 1, "the retry added a row instead of completing the failed one")
        let row = try #require(all.first)
        #expect(row.status == "completed")
        #expect(row.failedReason == nil, "History reads any failedReason as failed: \(row.failedReason ?? "")")
        #expect(row.text == "the quick brown fox")
        #expect(row.transcribedText == "the quick brown fox")
        #expect(row.transcriptionProvider == "local")
        #expect(row.audioFilePath == Self.audioPath)
    }

    /// Review r1: completing the failed row counts as a retry, as History's
    /// own Retry counts it (`TranscriptionRetryController`): History shows
    /// "retried N times" from `retryCount`.
    @MainActor
    @Test func completingTheFailedRowCountsTheRetry() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)
        let before = Date()

        await RecordingTranscriptionFlow.savePendingRetryTranscript(
            makeResult(postProcessed: false),
            failedRowWrite: Task { failedID },
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "LocNemo",
            persistence: persistence
        )

        let savedRows = await rows(in: persistence)
        let row = try #require(savedRows.first)
        #expect(row.retryCount == 1)
        let retried = try #require(row.lastRetryDate, "the retry did not record lastRetryDate")
        #expect(retried >= before.addingTimeInterval(-1))
    }

    /// The retry saves the same fields a dictation saves: raw text, and the
    /// post-processed text as the row's text when post-processing ran.
    @MainActor
    @Test func aPostProcessedRetrySavesBothTexts() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)

        await RecordingTranscriptionFlow.savePendingRetryTranscript(
            makeResult(postProcessed: true),
            failedRowWrite: Task { failedID },
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "LocNemo",
            persistence: persistence
        )

        let savedRows = await rows(in: persistence)
        let row = try #require(savedRows.first)
        #expect(row.text == "The quick brown fox.")
        #expect(row.postProcessedText == "The quick brown fox.")
        #expect(row.transcribedText == "the quick brown fox")
    }

    /// No failed row (no write was handed over): the transcript is still
    /// saved, as a new completed row for the same file.
    @MainActor
    @Test func withNoFailedRowANewRowHoldsTheTranscript() async throws {
        let persistence = DiagPersistence1653(inMemory: true)

        let savedID = await RecordingTranscriptionFlow.savePendingRetryTranscript(
            makeResult(postProcessed: false),
            failedRowWrite: nil,
            audioURL: URL(fileURLWithPath: Self.audioPath),
            modeName: "LocNemo",
            persistence: persistence
        )

        let diagAll = await rows(in: persistence)
        if savedID == nil { Issue.record(Comment(rawValue: "DIAG noFailedRow rows=\(diagAll.map { "\($0.status ?? "nil")|\($0.text ?? "nil")" })")) }
        #expect(savedID != nil)
        let all = await rows(in: persistence)
        #expect(all.count == 1)
        let row = try #require(all.first)
        #expect(row.status == "completed")
        #expect(row.text == "the quick brown fox")
        #expect(row.audioFilePath == Self.audioPath)
    }

    /// Control: a dictation's own completion (a processing row) does not
    /// touch `failedReason`, as before.
    @MainActor
    @Test func aDictationCompletionLeavesFailedReasonAlone() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)

        let saved = await RecordingTranscriptionFlow.saveBatchTranscript(
            makeResult(postProcessed: false),
            to: failedID,
            persistence: persistence
        )

        #expect(saved)
        let savedRows = await rows(in: persistence)
        let row = try #require(savedRows.first)
        #expect(row.failedReason == "Audio file could not be read")
        #expect(row.retryCount == 0, "a dictation's completion is not a retry")
        #expect(row.lastRetryDate == nil)
    }

    /// Review r1: a Retry that finishes before the failed row's write lands
    /// waits for it and completes that row. Before, it saved a new row, the
    /// failed row landed after it, and History showed both.
    @MainActor
    @Test func aRetryThatEndsBeforeTheFailedRowLandsLeavesOneRow() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let (gate, release) = AsyncStream<Void>.makeStream()
        diag1653("test2 start")
        let failedRowWrite = Task { () -> NSManagedObjectID? in
            for await _ in gate { break }
            diag1653("test2 gate released cancelled=\(Task.isCancelled)")
            return await persistence.createFailedTranscriptInBackground(
                duration: 4.2,
                mode: "LocNemo",
                audioFilePath: Self.audioPath,
                failedReason: "Audio file could not be read",
                errorText: "Error: Audio file could not be read"
            )
        }
        let result = makeResult(postProcessed: false)
        let save = Task {
            await RecordingTranscriptionFlow.savePendingRetryTranscript(
                result,
                failedRowWrite: failedRowWrite,
                audioURL: URL(fileURLWithPath: Self.audioPath),
                modeName: "LocNemo",
                persistence: persistence
            )
        }
        for _ in 0..<20 { await Task.yield() }
        let beforeTheWrite = await rows(in: persistence)
        #expect(beforeTheWrite.isEmpty, "the retry saved a row of its own before the failed row landed")

        release.yield()
        release.finish()
        let savedID = await save.value
        let failedID = await failedRowWrite.value
        diag1653("test2 savedID=\(String(describing: savedID)) failedID=\(String(describing: failedID))")
        if failedID == nil { (persistence as? DiagPersistence1653)?.recordErrors("retryBeforeRow") }

        #expect(savedID != nil)
        #expect(savedID == failedID, "the retry did not complete the failed row")
        let all = await rows(in: persistence)
        #expect(all.count == 1, "History shows \(all.count) rows for one recording")
        let row = try #require(all.first)
        #expect(row.status == "completed")
        #expect(row.failedReason == nil)
    }

    // MARK: - The row write's lifetime

    /// The row write belongs to one pending file. A new file, or none (a new
    /// dictation, a retry that ended), drops it.
    @MainActor
    @Test func theRowWriteResetsWhenThePendingFileChanges() async throws {
        let persistence = DiagPersistence1653(inMemory: true)
        let failedID = try await makeFailedRow(in: persistence)
        let write = Task<NSManagedObjectID?, Never> { failedID }
        let appState = AppState()

        appState.pendingRetryAudioPath = "/tmp/a.wav"
        appState.pendingRetryFailedRowWrite = write

        appState.pendingRetryAudioPath = "/tmp/a.wav"
        #expect(appState.pendingRetryFailedRowWrite == write, "re-setting the same file must keep its row")

        appState.pendingRetryAudioPath = "/tmp/b.wav"
        #expect(appState.pendingRetryFailedRowWrite == nil)

        appState.pendingRetryFailedRowWrite = write
        appState.pendingRetryAudioPath = nil
        #expect(appState.pendingRetryFailedRowWrite == nil)
    }

    // MARK: - Call sites, read from the production source

    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"
    private static let stopPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StopRecording.swift"
    private static let deliveryPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+Delivery.swift"

    /// The retry's success path, from the transcription call to its catch.
    private static func retrySuccessPath() throws -> String {
        let body = try ProductionSource.slice(
            of: errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
        guard let call = body.range(of: "transcribeWithDetails("),
              let catchArm = body[call.upperBound...].range(of: "} catch {") else {
            throw ProductionSource.Failure.anchorNotFound(anchor: "transcribeWithDetails( … } catch {", file: errorHandlingPath)
        }
        return String(body[call.upperBound..<catchArm.lowerBound])
    }

    /// The bug itself: a success that delivers nothing and saves nothing.
    @Test func theRetrySuccessDeliversAndSaves() throws {
        let success = try Self.retrySuccessPath()
        #expect(success.contains("deliverBatchTranscript("), "the retry no longer delivers: \(success)")
        #expect(success.contains("savePendingRetryTranscript("), "the retry no longer saves: \(success)")
        #expect(success.contains("failedRowWrite: failedRowWrite"),
                "the retry no longer completes the failed row: \(success)")
        #expect(success.contains("appState.pendingRetryFailedRowWrite"), "\(success)")
    }

    /// A stale retry must still write nothing (#1276): delivery and the save
    /// come after the supersede check.
    @Test func theRetryDeliversOnlyAfterTheSupersedeCheck() throws {
        let success = try Self.retrySuccessPath()
        let check = try #require(success.range(of: "isPendingRetrySuperseded(identity)"))
        for site in ["deliverBatchTranscript(", "savePendingRetryTranscript("] {
            let at = try #require(success.range(of: site))
            #expect(check.lowerBound < at.lowerBound, "\(site) is reachable before the supersede check")
        }
    }

    private static let startPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StartRecording.swift"

    /// A Retry is a new delivery (review r1): it pastes where the user is at
    /// the click, not into the app that was frontmost when the failed
    /// recording began, and it captures the target the same way a recording
    /// start does.
    @Test func theRetryCapturesThePasteTargetAtTheClick() throws {
        let click = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "func retryPendingFile(",
            to: "private func isPendingRetrySuperseded("
        )
        #expect(click.contains("capturePasteTarget()"), "the Retry pastes into the old recording's app: \(click)")

        let start = try ProductionSource.slice(
            of: Self.startPath,
            from: "appState?.lastDeliveryWasQuickCapture = false",
            to: "if appState?.isStreamingShortcutTriggered == true {"
        )
        #expect(start.contains("capturePasteTarget()"),
                "a recording start no longer shares the Retry's paste-target capture: \(start)")
    }

    /// The paste's clipboard restore writes back a snapshot taken for THIS
    /// Retry, before the request, not the failed recording's (review r1).
    @Test func theRetryTakesAFreshClipboardSnapshotBeforeTheRequest() throws {
        let body = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
        let snapshot = try #require(body.range(of: "AccessibilityHelper.shared.startRecordingSession()"),
                                    "the Retry restores the failed recording's clipboard snapshot")
        let request = try #require(body.range(of: "transcribeWithDetails("))
        #expect(snapshot.lowerBound < request.lowerBound, "the snapshot is taken after the request")
        let modeGate = try #require(body.range(of: "guard let transcriptionMode = resolvedMode"))
        #expect(modeGate.lowerBound < snapshot.lowerBound,
                "the mode-picker exit sends nothing and must not take the clipboard snapshot")
    }

    /// The stop flow hands the failed row's write to the retry, in the same
    /// main-actor turn as the path that shows the Retry button, so no Retry
    /// can run without it (review r1).
    @Test func theUnreadableFileBranchHandsOverItsRowWrite() throws {
        let branch = try ProductionSource.slice(
            of: Self.stopPath,
            from: "Audio file not readable after stop-flow check.",
            to: "let vadStart = Date()"
        )
        let write = try #require(branch.range(of: "createFailedTranscriptInBackground("), "\(branch)")
        let path = try #require(branch.range(of: "appState?.pendingRetryAudioPath = audioURL.path"), "\(branch)")
        let handOver = try #require(branch.range(of: "appState?.pendingRetryFailedRowWrite = failedRowWrite"),
                                    "the failed row's write is discarded again: \(branch)")
        #expect(write.lowerBound < path.lowerBound, "the Retry button can show before the row write exists")
        #expect(path.lowerBound < handOver.lowerBound,
                "the path's didSet drops a write handed over before it")
        let turn = String(branch[path.lowerBound..<handOver.lowerBound])
        #expect(!turn.contains("await"), "the path and the write are set in different main-actor turns: \(turn)")
    }

    /// One delivery and one row write for a dictation and a retry, so the two
    /// cannot drift apart again.
    @Test func theStopFlowUsesTheSharedHelpers() throws {
        let flow = try ProductionSource.slice(
            of: Self.stopPath,
            from: "func handleStopRecordingWithTranscription(",
            to: "static func stageExtras("
        )
        #expect(flow.contains("deliverBatchTranscript("), "\(flow)")
        #expect(flow.contains("Self.saveBatchTranscript("), "\(flow)")
        #expect(!flow.contains("handleAutoPaste("), "the stop flow pastes on its own again")
        #expect(!flow.contains("updateTranscriptWithTranscriptionInBackground("),
                "the stop flow writes the row on its own again")
    }

    /// "Pasted!" only after a real paste: a failed or disabled delivery marks
    /// the paste failed, so the pill shows the Copy state instead.
    @Test func aFailedDeliveryMarksThePasteFailed() throws {
        let helper = try ProductionSource.slice(
            of: Self.deliveryPath,
            from: "func deliverBatchTranscript(",
            to: "static func saveBatchTranscript("
        )
        #expect(helper.contains("autoPasteHandler.handleAutoPaste("), "\(helper)")
        #expect(helper.components(separatedBy: "appState?.transcriptionPasteFailed = true").count - 1 == 2,
                "both the failed paste and the auto-paste-off arm must mark the paste failed: \(helper)")
    }
}
