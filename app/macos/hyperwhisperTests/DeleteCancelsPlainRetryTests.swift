//
//  DeleteCancelsPlainRetryTests.swift
//  hyperwhisperTests
//
//  Issue #1743, sibling of #1458: History > failed row > Retry sets that same
//  row to "processing" and awaits the transcription from a task nobody kept by
//  the row. Deleting the row left the engine running. `TranscriptActionHandler`
//  now runs the plain Retry in a task kept by the row's ID, both delete paths
//  cancel it (pinned for both kinds of retry in `DeleteCancelsRetryWithTests`),
//  and `TranscriptionRetryController` writes nothing to a row whose retry was
//  cancelled.
//
//  A newer dictation that takes the pipeline over (#1425) cancels only the
//  retry's engine run, not the retry task, so that row still records its
//  failure (the control test below).
//

import AVFoundation
import CoreData
import Foundation
import Testing

@testable import HyperWhisper

@Suite("Deleting a row cancels its plain Retry (#1743)")
struct DeleteCancelsPlainRetryTests {

    private static let originalText = "Transcription failed: the engine could not decode the audio"
    private static let originalReason = "the engine could not decode the audio"

    // MARK: - Fixtures

    @MainActor
    private func makeFailedRow(in persistence: PersistenceController, audioFilePath: String) -> Transcript {
        let mode = Mode(context: persistence.container.viewContext)
        mode.id = UUID()
        mode.name = "LocNemo"
        mode.model = "nemotron-asr-3.5-latin"
        mode.isDefault = true
        mode.createdDate = Date()
        mode.modifiedDate = Date()

        let transcript = Transcript(context: persistence.container.viewContext)
        transcript.id = UUID()
        transcript.date = Date()
        transcript.duration = 2
        transcript.audioFilePath = audioFilePath
        transcript.mode = mode.name
        transcript.modeRelationship = mode
        transcript.text = Self.originalText
        transcript.setValue("failed", forKey: "status")
        transcript.setValue(Self.originalReason, forKey: "failedReason")
        persistence.save()
        return transcript
    }

    private func makeAudioFile() throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("retry-1743-\(UUID().uuidString).wav")
        try Data(repeating: 0, count: 64).write(to: url)
        return url
    }

    // MARK: - Cancelled retry

    /// The delete cancels the retry task before it removes the row. The
    /// controller then stops before the engine and leaves the row exactly as it
    /// was: no "processing" flip, no retryCount bump, no "Retry failed" text.
    @MainActor
    @Test func aCancelledRetryStopsBeforeTheEngineAndWritesNoFailure() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        let transcript = makeFailedRow(in: persistence, audioFilePath: audio.path)
        // A nil pipeline: had the controller got past the cancel check, it
        // would throw `providerNotAvailable` and write "Retry failed: …".
        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)

        let retry = Task { try await controller.retryTranscription(for: transcript) }
        retry.cancel()

        do {
            _ = try await retry.value
            Issue.record("Expected the cancelled retry to throw")
        } catch is CancellationError {
            // Expected: stopped by the cancel, not by the engine.
        } catch {
            Issue.record("Expected CancellationError, got \(error)")
        }

        #expect(transcript.value(forKey: "status") as? String == "failed")
        #expect(transcript.value(forKey: "failedReason") as? String == Self.originalReason)
        #expect(transcript.text == Self.originalText)
        #expect((transcript.value(forKey: "retryCount") as? Int16 ?? 0) == 0)
        #expect(transcript.value(forKey: "lastRetryDate") == nil)
    }

    // MARK: - Control (#1425)

    /// Not cancelled: a failure from the engine (here the nil pipeline, which
    /// stands for a newer dictation superseding the run) still marks the row
    /// failed, exactly as before.
    @MainActor
    @Test func aRetryThatWasNotCancelledStillRecordsItsFailure() async throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }

        let persistence = PersistenceController(inMemory: true)
        let transcript = makeFailedRow(in: persistence, audioFilePath: audio.path)
        let controller = TranscriptionRetryController(transcriptionPipeline: nil, persistence: persistence)

        let retry = Task { try await controller.retryTranscription(for: transcript) }
        await #expect(throws: TranscriptionError.self) {
            _ = try await retry.value
        }

        #expect(transcript.value(forKey: "status") as? String == "failed")
        #expect(transcript.text?.hasPrefix("Retry failed:") == true)
    }

    // MARK: - Tracking

    /// A finished run of a row does not untrack a newer run of the same row
    /// (History and the recording dialog each have a handler that can retry it).
    @MainActor
    @Test func anOlderRunDoesNotUntrackANewerRunOfTheSameRow() async {
        let rowId = UUID()
        let older = Task<Void, Never> {}
        let newer = Task<Void, Never> {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let olderToken = UUID()
        TranscriptActionHandler.trackRetryTask(older, for: rowId, token: olderToken)
        TranscriptActionHandler.trackRetryTask(newer, for: rowId, token: UUID())

        TranscriptActionHandler.untrackRetryTask(for: rowId, token: olderToken)
        #expect(TranscriptActionHandler.hasRetryTask(for: rowId))

        TranscriptActionHandler.cancelRetryTask(for: rowId)
        await newer.value
        #expect(newer.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    /// Two handlers retrying the same row at once: deleting the row cancels
    /// both runs, not only the newer one.
    @MainActor
    @Test func aDeleteCancelsEveryRunningRetryOfTheRow() async {
        let rowId = UUID()
        let spin: @Sendable () async -> Void = {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
        let first = Task<Void, Never> { await spin() }
        let second = Task<Void, Never> { await spin() }
        TranscriptActionHandler.trackRetryTask(first, for: rowId, token: UUID())
        TranscriptActionHandler.trackRetryTask(second, for: rowId, token: UUID())

        TranscriptActionHandler.cancelRetryTask(for: rowId)
        await first.value
        await second.value
        #expect(first.isCancelled)
        #expect(second.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    // MARK: - Cancelled retry's files and writes

    /// A cancelled retry removes the trimmed WAV and its M4A upload copy that
    /// its VAD step wrote, and never the recording it started from.
    @MainActor
    @Test func aCancelledRetryRemovesOnlyTheFilesItsVADWrote() {
        let folder = URL(fileURLWithPath: "/tmp/retry-1743", isDirectory: true)
        let original = folder.appendingPathComponent("rec.wav")
        let trimmed = folder.appendingPathComponent("rec_wav_trimmed.wav")
        let uploadCopy = folder.appendingPathComponent("rec_wav_trimmed.m4a")
        let trim = TrimResult(outputURL: trimmed, originalDuration: 40, trimmedDuration: 30, segmentCount: 2)

        let converted = VADProcessingResult(finalAudioURL: uploadCopy, trimResult: trim, wasProcessed: true, originalAudioURL: original)
        #expect(TranscriptionRetryController.filesMadeByRetry(converted) == [trimmed.path, uploadCopy.path])

        let trimmedOnly = VADProcessingResult(finalAudioURL: trimmed, trimResult: trim, wasProcessed: true, originalAudioURL: original)
        #expect(TranscriptionRetryController.filesMadeByRetry(trimmedOnly) == [trimmed.path])

        let skipped = VADProcessingResult(finalAudioURL: original, trimResult: nil, wasProcessed: false, originalAudioURL: original)
        #expect(TranscriptionRetryController.filesMadeByRetry(skipped).isEmpty)
    }

    /// The controller slice for the two rules above, sliced from the source
    /// because driving the engine needs a live `TranscriptionPipeline`: the
    /// deleted-row check runs again after the trimmed-path save awaits and
    /// before the result is written, and the cancelled path removes the VAD
    /// output before it rethrows.
    @Test func aCancelledRetryRechecksAfterTheTrimmedPathSaveAndCleansUp() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift")
        let source = try String(contentsOf: url, encoding: .utf8)

        let start = try #require(source.range(of: "func retryTranscription(for transcript: Transcript)"))
        let end = try #require(source.range(of: "func canRetryTranscript(", range: start.upperBound..<source.endIndex))
        let body = source[start.lowerBound..<end.lowerBound]

        let save = try #require(body.range(of: "await persistence.setTrimmedAudioPath("))
        let write = try #require(body.range(of: "persistence.updateTranscriptWithTranscription("))
        let recheck = try #require(body.range(of: "transcript.retryWasStoppedByDelete()", range: save.upperBound..<write.lowerBound))
        #expect(save.lowerBound < recheck.lowerBound && recheck.lowerBound < write.lowerBound)
        #expect(!body[recheck.upperBound..<write.lowerBound].contains("await "), "no await between the last check and the write")

        let catchStart = try #require(body.range(of: "} catch {", range: write.upperBound..<body.endIndex))
        let owners = try #require(body.range(of: "PersistenceController.audioPathsNoTranscriptOwns(", range: catchStart.upperBound..<body.endIndex))
        let made = try #require(body.range(of: "Self.filesMadeByRetry(vadResult)", range: owners.upperBound..<body.endIndex))
        let cleanup = try #require(body.range(of: "FileDeletion.deleteFiles(at: leftovers)", range: made.upperBound..<body.endIndex))
        let rethrow = try #require(body.range(of: "throw error", range: cleanup.upperBound..<body.endIndex))
        #expect(cleanup.lowerBound < rethrow.lowerBound)
    }

    /// Two rows recorded from the same audio share the VAD output's names. A
    /// cancelled retry of one keeps the trimmed WAV and its M4A copy the other
    /// row still records or derives, and removes only what nobody owns.
    @MainActor
    @Test func aCancelledRetryKeepsAFileAnotherRowOwns() throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }
        let folder = audio.deletingLastPathComponent()
        let sharedTrimmed = folder.appendingPathComponent("shared_wav_trimmed.wav")
        let sharedUploadCopy = folder.appendingPathComponent("shared_wav_trimmed.m4a")
        let unowned = folder.appendingPathComponent("unowned_wav_trimmed.wav")

        let persistence = PersistenceController(inMemory: true)
        let other = makeFailedRow(in: persistence, audioFilePath: audio.path)
        other.setValue(sharedTrimmed.path, forKey: "trimmedAudioFilePath")
        persistence.save()

        let leftovers = PersistenceController.audioPathsNoTranscriptOwns(
            [sharedTrimmed.path, sharedUploadCopy.path, unowned.path, audio.path],
            in: persistence.container.viewContext
        )
        #expect(leftovers == [unowned.path])
    }

    /// The cleanup runs after the delete, so the deleted row itself no longer
    /// owns the trimmed WAV it recorded: the file is removed. Before the
    /// delete the same row still owns it, which is why the order matters.
    @MainActor
    @Test func theDeletedRowNoLongerOwnsItsTrimmedFile() throws {
        let audio = try makeAudioFile()
        defer { try? FileManager.default.removeItem(at: audio) }
        let trimmed = audio.deletingPathExtension().appendingPathExtension("trimmed.wav")

        let persistence = PersistenceController(inMemory: true)
        let row = makeFailedRow(in: persistence, audioFilePath: audio.path)
        row.setValue(trimmed.path, forKey: "trimmedAudioFilePath")
        persistence.save()
        let context = persistence.container.viewContext

        #expect(PersistenceController.audioPathsNoTranscriptOwns([trimmed.path], in: context).isEmpty)

        persistence.deleteTranscript(row)
        #expect(PersistenceController.audioPathsNoTranscriptOwns([trimmed.path], in: context) == [trimmed.path])
    }

    /// The plain Retry runs the pipeline in a task it tracks by the row's ID
    /// before it awaits it, so a delete during the retry finds it. Sliced from
    /// the source, because driving it needs a live `TranscriptionPipeline`.
    @Test func plainRetryTracksItsTaskBeforeAwaitingIt() throws {
        let url = URL(fileURLWithPath: #filePath)
            .deletingLastPathComponent()
            .deletingLastPathComponent()
            .appendingPathComponent("hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift")
        let source = try String(contentsOf: url, encoding: .utf8)

        let start = try #require(source.range(of: "func retryTranscription(_ transcript: Transcript) async -> Bool"))
        let end = try #require(source.range(of: "func retryTranscription(_ transcript: Transcript, with mode: Mode)", range: start.upperBound..<source.endIndex))
        let body = source[start.lowerBound..<end.lowerBound]

        let run = try #require(body.range(of: "pipeline.retryTranscription(for: transcript)"))
        let track = try #require(body.range(of: "Self.trackRetryTask(retryTask, for: transcriptId"))
        let wait = try #require(body.range(of: "try await retryTask.value"))
        #expect(run.lowerBound < track.lowerBound)
        #expect(track.lowerBound < wait.lowerBound, "the retry must be tracked before it is awaited")
    }

    /// Cancelling the retry reaches whisper.cpp itself: the provider forwards
    /// its caller's cancel to the pass task, and the pass task trips the abort
    /// signal `whisper_full` polls. Without both, a deleted row's local Retry
    /// decoded the rest of the file on the GPU (#1743, verify on the Mac).
    @Test func aCancelledRetryAbortsTheWhisperDecode() throws {
        let provider = "app/macos/hyperwhisper/Managers/Transcription/Providers/Local/LibWhisperProvider.swift"

        let decode = try ProductionSource.slice(
            of: provider,
            from: "let abortSignal = WhisperAbortSignal()",
            to: "var success = await decode()"
        )
        #expect(decode.contains("abortSignal: abortSignal"))
        #expect(decode.contains("abortSignal.trigger()"))

        let afterDecode = try ProductionSource.slice(
            of: provider,
            from: "var success = await decode()",
            to: "guard success else {"
        )
        #expect(afterDecode.contains("try Task.checkCancellation()"), "an aborted decode must surface as a cancel, not a decode failure")

        let wait = try ProductionSource.slice(
            of: provider,
            from: "let output = try await withTaskCancellationHandler {",
            to: "return output"
        )
        #expect(wait.contains("task.cancel()"))

        let whisper = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Whisper/LibWhisper.swift",
            from: "if let abortSignal {",
            to: "whisper_reset_timings(context)"
        )
        #expect(whisper.contains("params.abort_callback = {"))
        #expect(whisper.contains("params.encoder_begin_callback = {"))
    }

    @Test func theAbortSignalStaysTrippedOnceTriggered() {
        let signal = WhisperAbortSignal()
        #expect(!signal.isTriggered)
        signal.trigger()
        #expect(signal.isTriggered)
        #expect(signal.isTriggered)
    }

    /// A run a newer dictation took over (#1425) now aborts within a window, so
    /// its quiet cancel lands while the newer run is transcribing. It must not
    /// reset the pipeline to idle under that run.
    @Test func aTakenOverRunDoesNotIdleTheNewerRun() throws {
        let cancelled = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift",
            from: "if error is CancellationError || task.isCancelled {",
            to: "} else {"
        )
        let guardRange = try #require(cancelled.range(of: "if currentTask == task {"))
        let idleRange = try #require(cancelled.range(of: "state = .idle"))
        #expect(guardRange.lowerBound < idleRange.lowerBound, "only the run that still owns the pipeline may idle it")
    }

    // MARK: - Review round 7 / 6 (inbox #1043)

    /// Writes a mono 16-bit PCM WAV of a 440 Hz tone, and closes it.
    private func makeToneWAV(seconds: Double) throws -> URL {
        let url = FileManager.default.temporaryDirectory
            .appendingPathComponent("retry-1743-tone-\(UUID().uuidString).wav")
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
        // The scope releases the writer, which finalises the RIFF header.
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

    /// Waits up to two seconds for `task` to end; a cancel ends it.
    @MainActor
    private func ended(_ task: Task<Void, Never>) async -> Bool {
        for _ in 0..<200 {
            if task.isCancelled { return true }
            try? await Task.sleep(nanoseconds: 10_000_000)
        }
        return task.isCancelled
    }

    private func spinningTask() -> Task<Void, Never> {
        Task<Void, Never> {
            while !Task.isCancelled {
                try? await Task.sleep(nanoseconds: 5_000_000)
            }
        }
    }

    /// R7-3: a delete during VAD stops the audio load: the converter's read
    /// loop sees the cancel and throws, not a full decode of a long file.
    @MainActor
    @Test func aCancelledCallerStopsTheAudioLoad() async throws {
        let audio = try makeToneWAV(seconds: 30)
        defer { try? FileManager.default.removeItem(at: audio) }

        let load = Task { try await AudioConverter().convert(from: audio) }
        load.cancel()
        await #expect(throws: CancellationError.self) {
            _ = try await load.value
        }
    }

    /// R7-3: the trimmer stops before analysis and writes no trimmed file.
    @MainActor
    @Test func aCancelledTrimWritesNoFile() async throws {
        let audio = try makeToneWAV(seconds: 30)
        defer { try? FileManager.default.removeItem(at: audio) }
        let trimmer = SilenceTrimmer()
        let output = trimmer.generateOutputURL(for: audio)
        defer { try? FileManager.default.removeItem(at: output) }

        let trim = Task { try await trimmer.trimSilence(from: audio) }
        trim.cancel()
        await #expect(throws: CancellationError.self) {
            _ = try await trim.value
        }
        #expect(!FileManager.default.fileExists(atPath: output.path))
    }

    /// R7-3: VAD hands a cancelled caller the original audio, unprocessed,
    /// and leaves no trimmed WAV or M4A behind.
    @MainActor
    @Test func aCancelledVADReturnsTheOriginalAndWritesNothing() async throws {
        let audio = try makeToneWAV(seconds: 30)
        defer { try? FileManager.default.removeItem(at: audio) }
        let outputs = TranscriptionRetryController.vadOutputPaths(for: audio)

        let service = VADProcessingService()
        let vad = Task {
            await service.processAudioForTranscription(audioURL: audio, duration: 30, vadEnabled: true, context: .retry)
        }
        vad.cancel()
        let result = await vad.value

        #expect(!result.wasProcessed)
        #expect(result.trimResult == nil)
        #expect(result.finalAudioURL == audio)
        for path in outputs {
            #expect(!FileManager.default.fileExists(atPath: path))
        }
    }

    /// R7-3: the trimmer checks for the cancel before each costly step, the
    /// last one right before it writes.
    @Test func theTrimmerChecksForACancelBeforeEachCostlyStep() throws {
        let file = "app/macos/hyperwhisper/Managers/AudioRecording/Processing/SilenceTrimmer.swift"
        let beforeAnalysis = try ProductionSource.slice(of: file, from: "let vad = VoiceActivityDetector.shared", to: "vadResult = try await vad.analyzeAudio(")
        #expect(beforeAnalysis.contains("try Task.checkCancellation()"))
        let beforeWrite = try ProductionSource.slice(of: file, from: "let finalOutputURL = outputURL ?? generateOutputURL(for: inputURL)", to: "try await writeAudioFile(")
        #expect(beforeWrite.contains("try Task.checkCancellation()"))
    }

    /// R7-1 / R6-2: every retry of a recording writes the same VAD names. A
    /// cancelled run skips the files another in-flight run still holds.
    @MainActor
    @Test func aCancelledRetryKeepsTheVADOutputAnotherRunUses() {
        let source = URL(fileURLWithPath: "/tmp/retry-1743/shared-\(UUID().uuidString).wav")
        let outputs = TranscriptionRetryController.vadOutputPaths(for: source)
        #expect(outputs.count == 2)
        #expect(outputs[0].hasSuffix("_wav_trimmed.wav"))
        #expect(outputs[1].hasSuffix("_wav_trimmed.m4a"))

        // Two runs on the same source; the first is cancelled and releases.
        TranscriptionRetryController.claimVADOutputs(outputs)
        TranscriptionRetryController.claimVADOutputs(outputs)
        TranscriptionRetryController.releaseVADOutputs(outputs)
        #expect(TranscriptionRetryController.vadOutputsNoRunUses(outputs).isEmpty)

        // The second run ends too: now nothing holds them.
        TranscriptionRetryController.releaseVADOutputs(outputs)
        #expect(TranscriptionRetryController.vadOutputsNoRunUses(outputs) == outputs)
    }

    /// R7-1 / R6-2: the run claims the names before VAD writes them, and its
    /// cleanup releases its own claim before it asks which files are free.
    @Test func theRetryClaimsItsVADOutputBeforeVADAndReleasesBeforeCleanup() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift",
            from: "func retryTranscription(for transcript: Transcript)",
            to: "func canRetryTranscript("
        )
        let claim = try #require(body.range(of: "Self.claimVADOutputs(claimedVADOutputs)"))
        let vad = try #require(body.range(of: "vadProcessingService.processAudioForTranscription("))
        #expect(claim.lowerBound < vad.lowerBound)

        let release = try #require(body.range(of: "Self.releaseVADOutputs(claimedVADOutputs)", range: vad.upperBound..<body.endIndex))
        let filter = try #require(body.range(of: "Self.vadOutputsNoRunUses(Self.filesMadeByRetry(vadResult))"))
        let cleanup = try #require(body.range(of: "FileDeletion.deleteFiles(at: leftovers)"))
        #expect(release.lowerBound < filter.lowerBound && filter.lowerBound < cleanup.lowerBound)
    }

    /// R7-2: a superseded run that still succeeds does not idle the pipeline
    /// under the newer run either.
    @Test func aSupersededSuccessDoesNotIdleTheNewerRun() throws {
        let success = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift",
            from: "let result = try await Self.value(of: task)",
            to: "let now = Date()"
        )
        let guardRange = try #require(success.range(of: "if currentTask == task {"))
        let idleRange = try #require(success.range(of: "state = .idle"))
        #expect(guardRange.lowerBound < idleRange.lowerBound, "only the run that still owns the pipeline may idle it")
    }

    /// R6-4: a retry the delete stopped is its own outcome, with no error
    /// text, and the recording dialog closes quietly on it instead of
    /// showing an empty error.
    @Test func theRecordingDialogTreatsAStopByDeleteAsAQuietCancel() throws {
        let handler = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
            from: "if transcript.retryWasStoppedByDelete(taskCancelled: retryTask.isCancelled) {",
            to: "await showRetryErrorAlert(errorMessage)"
        )
        let stopped = try #require(handler.range(of: "return .stoppedByDelete"))
        let setsError = try #require(handler.range(of: "lastError = errorMessage"))
        #expect(stopped.lowerBound < setsError.lowerBound, "a stop by delete returns before any error is set")

        let dialog = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/RecordingDialog.swift",
            from: "let outcome = await handler.retryTranscriptionOutcome(transcript)",
            to: "VisualEffectBackground"
        )
        let quiet = try #require(dialog.range(of: "if outcome == .stoppedByDelete {"))
        let leave = try #require(dialog.range(of: "return", range: quiet.upperBound..<dialog.endIndex))
        let error = try #require(dialog.range(of: "hasError = true"))
        #expect(leave.lowerBound < error.lowerBound, "no error state for a stop by delete")
        #expect(dialog[quiet.upperBound..<leave.lowerBound].contains("closeDialog()"))
    }

    /// R6-3: the store's own single delete stops the row's running retry,
    /// whoever calls it.
    @MainActor
    @Test func theStoresDeleteStopsTheRowsRetry() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = makeFailedRow(in: persistence, audioFilePath: "/tmp/retry-1743/\(UUID().uuidString).wav")
        let rowId = try #require(row.id)
        let retry = spinningTask()
        TranscriptActionHandler.trackRetryTask(retry, for: rowId, token: UUID())

        persistence.deleteTranscript(row)

        #expect(retry.isCancelled)
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }

    /// R6-3: the store's bulk delete stops the retries of every deleted row,
    /// and only theirs.
    @MainActor
    @Test func theStoresBulkDeleteStopsEachDeletedRowsRetry() async throws {
        let persistence = PersistenceController(inMemory: true)
        let deleted = makeFailedRow(in: persistence, audioFilePath: "/tmp/retry-1743/\(UUID().uuidString).wav")
        let kept = makeFailedRow(in: persistence, audioFilePath: "/tmp/retry-1743/\(UUID().uuidString).wav")
        let deletedId = try #require(deleted.id)
        let keptId = try #require(kept.id)
        let deletedRetry = spinningTask()
        let keptRetry = spinningTask()
        TranscriptActionHandler.trackRetryTask(deletedRetry, for: deletedId, token: UUID())
        TranscriptActionHandler.trackRetryTask(keptRetry, for: keptId, token: UUID())
        defer { TranscriptActionHandler.cancelRetryTask(for: keptId) }

        persistence.deleteTranscripts([deleted])

        #expect(deletedRetry.isCancelled)
        #expect(!keptRetry.isCancelled)
        #expect(TranscriptActionHandler.hasRetryTask(for: keptId))
    }

    /// R6-3: auto-delete's batch delete on the writer context stops the
    /// running retry of an expired row once the delete commits.
    @MainActor
    @Test func autoDeleteStopsTheRetryOfAnExpiredRow() async throws {
        let persistence = PersistenceController(inMemory: true)
        let row = makeFailedRow(in: persistence, audioFilePath: "/tmp/retry-1743/\(UUID().uuidString).wav")
        row.date = Date(timeIntervalSinceNow: -400 * 24 * 3600)
        persistence.save()
        let rowId = try #require(row.id)
        let retry = spinningTask()
        TranscriptActionHandler.trackRetryTask(retry, for: rowId, token: UUID())
        defer { TranscriptActionHandler.cancelRetryTask(for: rowId) }

        let snapshot = await persistence.deleteTranscriptsOlderThanInBackground(Date(timeIntervalSinceNow: -30 * 24 * 3600))

        #expect(snapshot?.transcriptIDs == [rowId])
        #expect(await ended(retry), "the expired row's retry must stop")
        #expect(!TranscriptActionHandler.hasRetryTask(for: rowId))
    }
}
