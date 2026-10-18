//
//  PendingRetrySupersessionTests.swift
//  hyperwhisperTests
//
//  Issue #1276: a pending-file retry ("Audio file could not be read" → Retry)
//  runs inside `TranscriptionPipeline.transcribeWithDetails`, whose inner
//  unstructured `Task` does not inherit the retry's cancellation. A new
//  dictation started while the retry is still loading leaves it running, and
//  the dictation's own transcription then cancels it. The retry's `catch` used
//  to write `recordingState = .idle`, `lastTranscription = "Error: …"` and
//  `showRecordingDialog = true` over the new session. Its success path wrote
//  the old text and cleared the new session's mode in the same way.
//
//  The fix gives each flow that takes over `recordingState` a generation
//  (`AppState.beginTranscriptionSession()`), and the retry writes nothing once
//  the generation has moved past its own.
//
//  `retryTranscriptionFromPendingPath` is `@MainActor` and needs a live
//  `RecordingLifecycle` (device, audio-session and recorder managers) and a
//  pipeline to run, so it cannot be called here. The decision is lifted into
//  `PendingRetryIdentity` and `toggleTakesOverSession` and tested by calling
//  them; the last tests read the production source (`ProductionSource`) to pin
//  that every write site asks that decision first.
//

import Foundation
import Testing
@testable import HyperWhisper

struct PendingRetrySupersessionTests {

    // MARK: - The decision

    @Test func aRetryWithNoNewerFlowIsNotSuperseded() {
        let identity = PendingRetryIdentity(sessionGeneration: 7)
        #expect(!identity.isSuperseded(currentSessionGeneration: 7))
    }

    @Test func aRetryIsSupersededOnceANewerFlowBegins() {
        let identity = PendingRetryIdentity(sessionGeneration: 7)
        #expect(identity.isSuperseded(currentSessionGeneration: 8))
        #expect(identity.isSuperseded(currentSessionGeneration: 12))
    }

    @Test func theGenerationWrapsWithoutLosingTheSupersede() {
        let identity = PendingRetryIdentity(sessionGeneration: .max)
        #expect(identity.isSuperseded(currentSessionGeneration: UInt64.max &+ 1))
    }

    /// The issue's own sequence, on the real counter: Retry, then a new
    /// dictation, then the retry ends. A cancel with no newer flow (Escape on
    /// the retry itself) bumps nothing, so it keeps today's handling.
    ///
    /// No `await` here: `AppState()` starts a snapshot refresh that reads the
    /// shared store, and nothing this test asserts needs it to run.
    @MainActor
    @Test func aNewDictationAfterARetrySupersedesItAndACancelDoesNot() {
        let appState = AppState()

        let retry = PendingRetryIdentity(sessionGeneration: appState.beginTranscriptionSession())
        #expect(!retry.isSuperseded(currentSessionGeneration: appState.transcriptionSessionGeneration),
                "a retry cancelled with no newer flow must keep its error handling")

        appState.beginTranscriptionSession()  // the new dictation
        #expect(retry.isSuperseded(currentSessionGeneration: appState.transcriptionSessionGeneration),
                "the retry must leave the new dictation's state alone")
    }

    @MainActor
    @Test func aSecondRetrySupersedesTheFirst() {
        let appState = AppState()
        let first = PendingRetryIdentity(sessionGeneration: appState.beginTranscriptionSession())
        let second = PendingRetryIdentity(sessionGeneration: appState.beginTranscriptionSession())
        #expect(first.isSuperseded(currentSessionGeneration: appState.transcriptionSessionGeneration))
        #expect(!second.isSuperseded(currentSessionGeneration: appState.transcriptionSessionGeneration))
    }

    /// A toggle that starts or stops a session takes over the dialog. A
    /// stop-only toggle with nothing recording does nothing; if it superseded
    /// the retry, the retry's dialog would sit on "transcribing" for good.
    @Test func onlyAToggleThatStartsOrStopsASessionTakesOver() {
        typealias Flow = RecordingTranscriptionFlow
        #expect(Flow.toggleTakesOverSession(stopOnly: false, isRecording: false, isStreamingActive: false))
        #expect(Flow.toggleTakesOverSession(stopOnly: false, isRecording: true, isStreamingActive: false))
        #expect(Flow.toggleTakesOverSession(stopOnly: false, isRecording: false, isStreamingActive: true))
        #expect(Flow.toggleTakesOverSession(stopOnly: true, isRecording: true, isStreamingActive: false))
        #expect(Flow.toggleTakesOverSession(stopOnly: true, isRecording: false, isStreamingActive: true))
        #expect(!Flow.toggleTakesOverSession(stopOnly: true, isRecording: false, isStreamingActive: false))
    }

    // MARK: - Call sites, read from the production source

    private static let errorHandlingPath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift"
    private static let togglePath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+Toggle.swift"
    private static let fileFlowPath =
        "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"

    private static let supersedeCheck = "isPendingRetrySuperseded(identity)"

    /// The comment-free body of `retryTranscriptionFromPendingPath`.
    private static func retryBody() throws -> String {
        try ProductionSource.slice(
            of: errorHandlingPath,
            from: "private func retryTranscriptionFromPendingPath(",
            to: "func handleRecordingStartFailure("
        )
    }

    /// The retry's `catch` asks whether it was superseded before it writes
    /// any shared state. This is the write the issue reports.
    @Test func theRetryCatchChecksForANewerFlowBeforeItWrites() throws {
        let body = try Self.retryBody()
        let catchArm = try #require(body.range(of: "} catch {"), "the retry has no catch-all")
        let afterCatch = body[catchArm.upperBound...]
        let check = try #require(afterCatch.range(of: Self.supersedeCheck),
                                 "the retry's catch writes shared state without a supersede check")
        let firstWrite = try #require(afterCatch.range(of: "appState."), "the retry's catch writes nothing")
        #expect(check.lowerBound < firstWrite.lowerBound,
                "the supersede check must come before the catch's first appState write")
    }

    /// A stale retry that SUCCEEDS after a newer flow began must not write its
    /// text, `.idle`, or clear the newer session's mode either.
    @Test func theRetrySuccessChecksForANewerFlowBeforeItWrites() throws {
        let body = try Self.retryBody()
        let call = try #require(body.range(of: "transcribeWithDetails("), "the retry no longer transcribes")
        let afterCall = body[call.upperBound...]
        let catchArm = try #require(afterCall.range(of: "} catch {"), "the retry has no catch-all")
        let successPath = afterCall[..<catchArm.lowerBound]
        let check = try #require(successPath.range(of: Self.supersedeCheck),
                                 "the retry's success path writes shared state without a supersede check")
        for write in ["appState.lastTranscription = transcriptionResult.text", "clearActiveSessionMode()"] {
            let site = try #require(successPath.range(of: write), "\(write) not found on the success path")
            #expect(check.lowerBound < site.lowerBound, "\(write) is reachable before the supersede check")
        }
    }

    /// The mode lookup suspends, so the `.transcribing` write after it can
    /// also land on a newer session.
    @Test func theRetryChecksAgainAfterTheModeLookup() throws {
        let body = try Self.retryBody()
        let lookup = try #require(body.range(of: "resolvePendingRetryMode("),
                                  "the retry no longer resolves its mode")
        let afterLookup = body[lookup.upperBound...]
        let check = try #require(afterLookup.range(of: Self.supersedeCheck),
                                 "no supersede check after the mode lookup")
        let write = try #require(afterLookup.range(of: "appState.recordingState = .transcribing"),
                                 "the retry no longer marks itself transcribing")
        #expect(check.lowerBound < write.lowerBound)
    }

    /// Each retry takes a generation of its own and hands it to its work.
    @Test func eachRetryBeginsASession() throws {
        let body = try ProductionSource.slice(
            of: Self.errorHandlingPath,
            from: "func retryPendingFile(",
            to: "private func retryTranscriptionFromPendingPath("
        )
        #expect(body.contains("beginTranscriptionSession()"), "\(body)")
        #expect(body.contains("retryTranscriptionFromPendingPath(identity:"), "\(body)")
    }

    /// A dictation start or stop goes through the toggle, which must take the
    /// session over before it schedules its work.
    @Test func theToggleBeginsASessionBeforeItsWork() throws {
        let body = try ProductionSource.slice(
            of: Self.togglePath,
            from: "func toggleRecordingWithTranscription(",
            to: "toggleTask = Task {"
        )
        #expect(body.contains("appState?.beginTranscriptionSession()"), "\(body)")
        #expect(body.contains("toggleTakesOverSession("), "\(body)")
    }

    /// A file transcription takes `recordingState` over too, and the pipeline
    /// call that follows cancels the retry.
    @Test func aFileTranscriptionBeginsASessionWhenItTakesTheState() throws {
        let code = try ProductionSource.code(of: Self.fileFlowPath)
        let takeOver = try #require(code.range(of: "appState?.recordingState = .transcribing"),
                                    "the file flow no longer marks itself transcribing")
        let before = code[..<takeOver.lowerBound]
        let begin = try #require(before.range(of: "beginTranscriptionSession()", options: .backwards),
                                 "the file flow takes recordingState without beginning a session")
        let between = before[begin.upperBound...]
        #expect(!between.contains("await"), "a suspension between the bump and the write: \(between)")
    }
}
