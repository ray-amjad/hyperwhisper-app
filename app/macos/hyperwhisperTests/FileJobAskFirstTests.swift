//
//  FileJobAskFirstTests.swift
//  hyperwhisperTests
//
//  Issue #1425: a second Transcribe File (or History Retry with…) silently
//  cancelled the running one, because the pipeline let the latest request win
//  whatever it was. Ray's rules: a second FILE job asks first (Keep Running is
//  the default; "Cancel It and Start" is today's behaviour), and a dictation
//  during a file job runs beside it. Dictation still supersedes dictation.
//
//  These drive the real entry points (`FileTranscriptionFlow.transcribeOpenedFile`,
//  `TranscriptActionHandler.retryTranscription(_:with:)` and
//  `TranscriptionPipeline.transcribeWithDetails`) against a real pipeline whose
//  provider routing is replaced by a provider the test holds and releases.
//

import AVFoundation
import CoreData
import Foundation
import Testing
@testable import HyperWhisper

// MARK: - A provider the test controls

/// Blocks each pass until the test releases it; a cancel ends the pass.
final class HeldTranscriptionProvider: TranscriptionProvider, @unchecked Sendable {
    let name = "Held"
    var isAvailable: Bool { true }
    let supportsConcurrentPasses: Bool

    private let lock = NSLock()
    private var startedNames: [String] = []
    private var cancelledNames: [String] = []
    private var finishedNames: [String] = []
    private var releasedNames: Set<String> = []

    init(supportsConcurrentPasses: Bool) {
        self.supportsConcurrentPasses = supportsConcurrentPasses
    }

    /// Every pass is keyed by the audio file's name, minus its extension's
    /// copies: an import copies the file under a new name, so the test reads
    /// passes in the order they started.
    var started: [String] { lock.withLock { startedNames } }
    var cancelled: [String] { lock.withLock { cancelledNames } }
    var finished: [String] { lock.withLock { finishedNames } }

    func release(_ name: String) {
        lock.withLock { _ = releasedNames.insert(name) }
    }

    func releaseAll() {
        lock.withLock { releasedNames.formUnion(startedNames) }
    }

    func transcribe(audioURL: URL, language: String?, mode: Mode?, vocabulary: [Vocabulary]) async throws -> String {
        let name = audioURL.lastPathComponent
        lock.withLock { startedNames.append(name) }
        do {
            while !lock.withLock({ releasedNames.contains(name) }) {
                try await Task.sleep(nanoseconds: 10_000_000)
            }
        } catch {
            lock.withLock { cancelledNames.append(name) }
            throw error
        }
        lock.withLock { finishedNames.append(name) }
        return "held words"
    }
}

// MARK: - Helpers

@MainActor
private enum Rig {

    /// Waits up to `seconds` for `condition`.
    static func eventually(_ seconds: Double = 5, _ condition: () -> Bool) async -> Bool {
        let deadline = Date().addingTimeInterval(seconds)
        while Date() < deadline {
            if condition() { return true }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return condition()
    }

    static func pipeline(_ provider: HeldTranscriptionProvider, answers: [Bool] = []) -> (TranscriptionPipeline, AskLog) {
        let pipeline = TranscriptionPipeline()
        pipeline.providerSelectionOverrideForTesting = { _ in provider }
        let log = AskLog(answers: answers)
        pipeline.fileJobs.confirmReplace = { log.answer() }
        return (pipeline, log)
    }

    static func mode(_ persistence: PersistenceController) -> Mode {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "Test 1425"
        mode.model = "base"
        mode.language = "en"
        mode.postProcessingMode = 0  // off: the held provider's text is the result
        mode.createdDate = Date()
        mode.modifiedDate = Date()
        // Saved, so every row save below succeeds and each row gets a
        // permanent ID the background writer can find.
        persistence.save()
        #expect(!mode.objectID.isTemporaryID, "the test mode did not save")
        return mode
    }

    static func flow(_ pipeline: TranscriptionPipeline, _ persistence: PersistenceController, appState: AppState? = nil) -> FileTranscriptionFlow {
        FileTranscriptionFlow(
            transcriptionPipeline: pipeline,
            settingsManager: nil,
            appState: appState,
            licenseManager: nil,
            persistence: persistence
        )
    }

    /// A short mono tone in its own temporary folder.
    static func toneWAV(_ name: String, seconds: Double = 1.5) throws -> URL {
        let folder = FileManager.default.temporaryDirectory
            .appendingPathComponent("hw-1425-\(UUID().uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = folder.appendingPathComponent(name)
        let sampleRate = 16_000.0
        let frames = AVAudioFrameCount(seconds * sampleRate)
        let settings: [String: Any] = [
            AVFormatIDKey: kAudioFormatLinearPCM,
            AVSampleRateKey: sampleRate,
            AVNumberOfChannelsKey: 1,
            AVLinearPCMBitDepthKey: 16,
            AVLinearPCMIsFloatKey: false,
            AVLinearPCMIsBigEndianKey: false,
        ]
        try autoreleasepool {
            let file = try AVAudioFile(forWriting: url, settings: settings)
            let buffer = try #require(AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: frames))
            let channel = try #require(buffer.floatChannelData?[0])
            buffer.frameLength = frames
            for index in 0..<Int(frames) {
                channel[index] = 0.25 * sinf(2 * Float.pi * 440 * Float(index) / Float(sampleRate))
            }
            try file.write(from: buffer)
        }
        return url
    }

    static func rows(_ persistence: PersistenceController) -> [Transcript] {
        let context = persistence.container.viewContext
        context.refreshAllObjects()
        let request = NSFetchRequest<Transcript>(entityName: "Transcript")
        return (try? context.fetch(request)) ?? []
    }

    /// Every row as the store holds it, read on a fresh context.
    static func describe(_ persistence: PersistenceController) -> String {
        let context = persistence.container.newBackgroundContext()
        return context.performAndWait {
            let request = NSFetchRequest<Transcript>(entityName: "Transcript")
            let rows = (try? context.fetch(request)) ?? []
            return rows.map { row in
                let name = (row.audioFilePath as NSString?)?.lastPathComponent ?? "nil"
                let status = row.value(forKey: "status") as? String ?? "nil"
                let reason = row.value(forKey: "failedReason") as? String ?? "nil"
                return "\(name)=\(status)/\(reason)/\(row.text ?? "nil")"
            }.joined(separator: ", ")
        }
    }

    static func status(_ row: Transcript) -> String? { row.value(forKey: "status") as? String }
    static func reason(_ row: Transcript) -> String? { row.value(forKey: "failedReason") as? String }

    /// Removes the audio an import copied into the recordings folder.
    static func removeCopiedAudio(_ persistence: PersistenceController) {
        for row in rows(persistence) {
            if let path = row.audioFilePath { try? FileManager.default.removeItem(atPath: path) }
        }
    }
}

/// Records each time the busy question is asked, and answers from a script.
@MainActor
final class AskLog {
    private var answers: [Bool]
    private(set) var asked = 0

    init(answers: [Bool]) { self.answers = answers }

    func answer() -> Bool {
        asked += 1
        return answers.isEmpty ? false : answers.removeFirst()
    }
}

// MARK: - The gate itself

@MainActor
@Suite("File-job gate (#1425)")
struct FileJobGateTests {

    @Test func theFirstJobStartsWithoutAsking() async {
        let gate = FileJobGate()
        var asked = 0
        gate.confirmReplace = { asked += 1; return true }
        let ticket = await gate.begin()
        #expect(ticket != nil)
        #expect(asked == 0)
        #expect(gate.running === ticket)
        gate.end(ticket)
        #expect(gate.running == nil)
    }

    @Test func keepRunningStartsNothingAndLeavesTheRunningJob() async throws {
        let gate = FileJobGate()
        gate.confirmReplace = { false }
        let first = try #require(await gate.begin())
        var cancelled = false
        first.onCancel { cancelled = true }
        let second = await gate.begin()
        #expect(second == nil)
        #expect(!cancelled)
        #expect(gate.running === first)
    }

    @Test func cancelItAndStartCancelsTheRunningJob() async throws {
        let gate = FileJobGate()
        gate.confirmReplace = { true }
        let first = try #require(await gate.begin())
        var cancelled = false
        first.onCancel { cancelled = true }
        let second = try #require(await gate.begin())
        #expect(cancelled)
        #expect(first.isCancelled)
        #expect(gate.running === second)
        // The replaced job's own end must not release the new one.
        gate.end(first)
        #expect(gate.running === second)
    }

    @Test func aCancelBeforeTheJobAttachesRunsWhenItAttaches() {
        let ticket = FileJobTicket()
        ticket.cancel()
        var ran = false
        ticket.onCancel { ran = true }
        #expect(ran)
    }

    /// Keep Running is the default (first, Return) button, and the other is
    /// destructive. Read from the source: showing the alert needs a click.
    @Test func keepRunningIsTheDefaultButton() throws {
        let alert = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionJobs.swift",
            from: "static func askToReplaceRunningJob() async -> Bool {",
            to: "final class TranscriptionEngineAccess"
        )
        let keep = try #require(alert.range(of: "\"transcribe.file.busy.keepRunning\".localized"))
        let replace = try #require(alert.range(of: "\"transcribe.file.busy.cancelAndStart\".localized"))
        #expect(keep.lowerBound < replace.lowerBound, "Keep Running must be the first (default) button")
        #expect(alert.contains("replace.hasDestructiveAction = true"))
        #expect(alert.contains("response == .alertSecondButtonReturn"), "only the second button replaces the job")
        #expect(!alert.contains("runModal()"))
    }

    @Test func theAlertStringsExistInEveryLocale() throws {
        let root = ProductionSource.url("app/macos/hyperwhisper/Localizations")
        let locales = try FileManager.default.contentsOfDirectory(atPath: root.path).filter { $0.hasSuffix(".lproj") }
        #expect(locales.count >= 40)
        for locale in locales {
            let table = try String(contentsOf: root.appendingPathComponent("\(locale)/Localizable.strings"), encoding: .utf8)
            for key in ["transcribe.file.busy.title", "transcribe.file.busy.message",
                        "transcribe.file.busy.keepRunning", "transcribe.file.busy.cancelAndStart"] {
                #expect(table.contains("\"\(key)\" = \""), "\(locale) lacks \(key)")
            }
        }
        #expect("transcribe.file.busy.keepRunning".localized != "transcribe.file.busy.keepRunning")
    }
}

// MARK: - The engine access

@MainActor
@Suite("Engine access by job kind (#1425)")
struct TranscriptionEngineAccessTests {
    private let key = ObjectIdentifier(NSObject())

    @Test func theSameKindPassesStraightThrough() async throws {
        let access = TranscriptionEngineAccess()
        try await access.acquire(key, for: .dictation)
        try await access.acquire(key, for: .dictation)
        #expect(access.holders(of: .dictation, on: key) == 2)
        access.release(key, for: .dictation)
        access.release(key, for: .dictation)
        #expect(access.holders(of: .dictation, on: key) == 0)
    }

    @Test func theOtherKindWaitsAndIsLetInOnRelease() async throws {
        let access = TranscriptionEngineAccess()
        try await access.acquire(key, for: .fileJob)
        var granted = false
        let waiter = Task { @MainActor in
            try await access.acquire(key, for: .dictation)
            granted = true
        }
        #expect(await Rig.eventually { access.waiterCount(on: key) == 1 })
        #expect(!granted)
        access.release(key, for: .fileJob)
        try await waiter.value
        #expect(granted)
        #expect(access.holders(of: .dictation, on: key) == 1)
    }

    @Test func aCancelledWaiterLeavesTheQueueHoldingNothing() async throws {
        let access = TranscriptionEngineAccess()
        try await access.acquire(key, for: .fileJob)
        let waiter = Task { @MainActor in try await access.acquire(key, for: .dictation) }
        #expect(await Rig.eventually { access.waiterCount(on: key) == 1 })
        waiter.cancel()
        await #expect(throws: CancellationError.self) { try await waiter.value }
        #expect(access.waiterCount(on: key) == 0)
        #expect(access.holders(of: .dictation, on: key) == 0)
    }
}

// MARK: - The real entry points

@MainActor
@Suite("A second file job asks first; a dictation runs beside a file job (#1425)", .serialized)
struct FileJobAskFirstTests {

    /// Starts a Transcribe File (the Open With entry point, no picker) and
    /// waits until its pass is on the engine.
    private func startImport(
        _ flow: FileTranscriptionFlow,
        _ file: URL,
        _ mode: Mode,
        provider: HeldTranscriptionProvider,
        passes: Int
    ) async -> Task<Void, Never> {
        let task = Task { @MainActor in await flow.transcribeOpenedFile(file, mode: mode) }
        _ = await Rig.eventually { provider.started.count == passes }
        return task
    }

    @Test func fileThenFileKeepRunningLeavesNoRowAndTheFirstFinishes() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, asks) = Rig.pipeline(provider, answers: [false])
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let first = try Rig.toneWAV("first.wav")
        let second = try Rig.toneWAV("second.wav")

        let running = await startImport(Rig.flow(pipeline, persistence), first, mode, provider: provider, passes: 1)
        #expect(provider.started.count == 1)
        #expect(Rig.rows(persistence).count == 1)

        // Keep Running: returns at once, nothing new on disk or in History.
        await Rig.flow(pipeline, persistence).transcribeOpenedFile(second, mode: mode)
        #expect(asks.asked == 1)
        #expect(provider.started.count == 1)
        #expect(provider.cancelled.isEmpty, "Keep Running must not touch the running job")
        #expect(Rig.rows(persistence).count == 1, "Keep Running must leave no junk row")

        provider.releaseAll()
        await running.value
        let row = try #require(Rig.rows(persistence).first)
        #expect(Rig.status(row) == "completed")
        #expect(pipeline.fileJobs.running == nil)
    }

    @Test func fileThenFileCancelItAndStartReplacesTheFirst() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, asks) = Rig.pipeline(provider, answers: [true])
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let first = try Rig.toneWAV("first.wav")
        let second = try Rig.toneWAV("second.wav")

        let firstFlow = Rig.flow(pipeline, persistence)
        let running = await startImport(firstFlow, first, mode, provider: provider, passes: 1)
        let firstPass = try #require(provider.started.first)
        #expect(firstFlow.processingTranscriptID != nil, "the first import's row exists")

        let replacing = await startImport(Rig.flow(pipeline, persistence), second, mode, provider: provider, passes: 2)
        #expect(asks.asked == 1)
        #expect(await Rig.eventually { provider.cancelled == [firstPass] }, "the first job is cancelled")
        await running.value
        #expect(provider.started.count == 2, "the second job runs")

        // The replaced import resolved its row through its own cancel.
        #expect(firstFlow.processingTranscriptID == nil)
        let failedWrite = try #require(firstFlow.pendingFailureWrite, "the cancel wrote no failed row")
        await failedWrite.value
        let afterCancel = Rig.describe(persistence)

        provider.releaseAll()
        await replacing.value
        let statuses = Rig.rows(persistence).map { Rig.status($0) ?? "nil" }.sorted()
        #expect(statuses == ["completed", "failed"], "after cancel: \(afterCancel) | at end: \(Rig.describe(persistence))")
        let failed = try #require(Rig.rows(persistence).first { Rig.status($0) == "failed" })
        #expect(Rig.reason(failed) == "cancelled")
    }

    @Test func retryWithDuringAFileJobAsksFirst() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, asks) = Rig.pipeline(provider, answers: [false, true])
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let first = try Rig.toneWAV("first.wav")
        let retryAudio = try Rig.toneWAV("retry-source.wav")
        let source = persistence.createProcessingTranscript(duration: 1.5, mode: mode.name, audioFilePath: retryAudio.path)
        source.setValue("failed", forKey: "status")
        persistence.save()
        let handler = TranscriptActionHandler(transcriptionPipeline: pipeline, persistenceController: persistence)
        let folder = retryAudio.deletingLastPathComponent()

        let running = await startImport(Rig.flow(pipeline, persistence), first, mode, provider: provider, passes: 1)
        let rowsBefore = Rig.rows(persistence).count

        // Keep Running: no copy of the audio, no new row, nothing cancelled.
        let kept = await handler.retryTranscription(source, with: mode)
        #expect(!kept)
        #expect(asks.asked == 1)
        #expect(try FileManager.default.contentsOfDirectory(atPath: folder.path).count == 1, "no audio copied")
        #expect(Rig.rows(persistence).count == rowsBefore, "no junk row")
        #expect(provider.cancelled.isEmpty)

        // Cancel It and Start: the import is cancelled, the retry runs.
        let started = await handler.retryTranscription(source, with: mode)
        #expect(started)
        #expect(asks.asked == 2)
        #expect(await Rig.eventually { provider.started.count == 2 && provider.cancelled.count == 1 })
        await running.value

        provider.releaseAll()
        #expect(await Rig.eventually {
            Rig.rows(persistence).contains { Rig.status($0) == "completed" && $0.objectID != source.objectID }
        })
        #expect(await Rig.eventually { pipeline.fileJobs.running == nil })
    }

    @Test func aFileJobDuringRetryWithAsksFirst() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, asks) = Rig.pipeline(provider, answers: [false])
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let retryAudio = try Rig.toneWAV("retry-source.wav")
        let source = persistence.createProcessingTranscript(duration: 1.5, mode: mode.name, audioFilePath: retryAudio.path)
        source.setValue("failed", forKey: "status")
        persistence.save()
        let handler = TranscriptActionHandler(transcriptionPipeline: pipeline, persistenceController: persistence)

        #expect(await handler.retryTranscription(source, with: mode))
        #expect(await Rig.eventually { provider.started.count == 1 })
        let rowsBefore = Rig.rows(persistence).count

        await Rig.flow(pipeline, persistence).transcribeOpenedFile(try Rig.toneWAV("file.wav"), mode: mode)
        #expect(asks.asked == 1)
        #expect(provider.started.count == 1)
        #expect(provider.cancelled.isEmpty)
        #expect(Rig.rows(persistence).count == rowsBefore)

        provider.releaseAll()
        #expect(await Rig.eventually { pipeline.fileJobs.running == nil })
    }

    @Test(arguments: [true, false])
    func aDictationDuringAFileJobCancelsNeither(engineRunsBoth: Bool) async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: engineRunsBoth)
        let (pipeline, asks) = Rig.pipeline(provider)
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let appState = AppState()
        let flow = Rig.flow(pipeline, persistence, appState: appState)

        let fileJob = await startImport(flow, try Rig.toneWAV("file.wav"), mode, provider: provider, passes: 1)
        let filePass = try #require(provider.started.first)

        // The recording flow's stop runs exactly this call, with the default kind.
        let dictationAudio = try Rig.toneWAV("dictation.wav")
        let dictation = Task { @MainActor in
            try await pipeline.transcribeWithDetails(audioURL: dictationAudio, mode: mode)
        }

        if engineRunsBoth {
            #expect(await Rig.eventually { provider.started.contains("dictation.wav") })
            provider.release("dictation.wav")
            let result = try await dictation.value
            #expect(result.rawText == "held words")
            #expect(provider.cancelled.isEmpty, "the dictation must not cancel the file job")
            #expect(pipeline.state.isActiveRun, "the file job still runs, so the pipeline still reads busy")
            #expect(!ModePreparationGate.allowsPreparation(during: pipeline.state))
            provider.release(filePass)
        } else {
            // One pass at a time: the dictation waits for the file job.
            try await Task.sleep(nanoseconds: 300_000_000)
            #expect(!provider.started.contains("dictation.wav"))
            provider.release(filePass)
            #expect(await Rig.eventually { provider.started.contains("dictation.wav") })
            provider.release("dictation.wav")
            _ = try await dictation.value
        }

        await fileJob.value
        #expect(provider.cancelled.isEmpty, "neither job was cancelled")
        #expect(provider.finished.count == 2)
        #expect(asks.asked == 0, "a dictation never asks")
        #expect(Rig.status(try #require(Rig.rows(persistence).first)) == "completed")
        #expect(await Rig.eventually { pipeline.state == .idle })
    }

    @Test func aDictationStillSupersedesADictation() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: false)
        let (pipeline, _) = Rig.pipeline(provider)
        let persistence = PersistenceController(inMemory: true)
        let mode = Rig.mode(persistence)
        let firstAudio = try Rig.toneWAV("first-dictation.wav")
        let secondAudio = try Rig.toneWAV("second-dictation.wav")

        let first = Task { @MainActor in
            try await pipeline.transcribeWithDetails(audioURL: firstAudio, mode: mode)
        }
        #expect(await Rig.eventually { provider.started.contains("first-dictation.wav") })
        let second = Task { @MainActor in
            try await pipeline.transcribeWithDetails(audioURL: secondAudio, mode: mode)
        }
        #expect(await Rig.eventually { provider.cancelled.contains("first-dictation.wav") })
        await #expect(throws: (any Error).self) { try await first.value }

        #expect(await Rig.eventually { provider.started.contains("second-dictation.wav") })
        provider.release("second-dictation.wav")
        _ = try await second.value
        #expect(provider.finished == ["second-dictation.wav"])
        #expect(pipeline.state == .idle)
    }

    @Test func theDictationCancelLeavesAFileJobRunning() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, _) = Rig.pipeline(provider)
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)

        let fileJob = await startImport(Rig.flow(pipeline, persistence), try Rig.toneWAV("file.wav"), mode, provider: provider, passes: 1)
        let dictationAudio = try Rig.toneWAV("dictation.wav")
        let dictation = Task { @MainActor in
            try await pipeline.transcribeWithDetails(audioURL: dictationAudio, mode: mode)
        }
        #expect(await Rig.eventually { provider.started.count == 2 })

        // The recording dialog's and the cancel shortcut's cancel.
        pipeline.cancelTranscription(.dictation)
        await #expect(throws: (any Error).self) { try await dictation.value }
        #expect(provider.cancelled == ["dictation.wav"])
        #expect(pipeline.runningTasks[.fileJob] != nil, "the file job still runs")

        provider.releaseAll()
        await fileJob.value
        #expect(Rig.status(try #require(Rig.rows(persistence).first)) == "completed")
    }

    /// A dictation that takes `recordingState` while a file job runs keeps it:
    /// the file job's end must not reset it under the dictation.
    @Test func theFileJobLeavesADictationsRecordingStateAlone() async throws {
        let provider = HeldTranscriptionProvider(supportsConcurrentPasses: true)
        let (pipeline, _) = Rig.pipeline(provider)
        let persistence = PersistenceController(inMemory: true)
        defer { Rig.removeCopiedAudio(persistence) }
        let mode = Rig.mode(persistence)
        let appState = AppState()
        appState.recordingState = .idle

        let fileJob = await startImport(Rig.flow(pipeline, persistence, appState: appState), try Rig.toneWAV("file.wav"), mode, provider: provider, passes: 1)
        #expect(appState.recordingState == .transcribing)

        // A dictation starts: the toggle begins a session and records.
        _ = appState.beginTranscriptionSession()
        appState.recordingState = .recording

        provider.releaseAll()
        await fileJob.value
        #expect(appState.recordingState == .recording, "the file job reset the dictation's state")
    }
}
