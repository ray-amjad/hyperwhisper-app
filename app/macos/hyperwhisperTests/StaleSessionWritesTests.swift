//
//  StaleSessionWritesTests.swift
//  hyperwhisperTests
//
//  Issue #1394, the four sibling paths PR #1390's review carried: a
//  superseded pending-file retry or a stale pipeline request writing over a
//  newer session.
//
//  1. The pipeline's `recordingState = .postProcessing` write. A superseded
//     retry gets past the post-processing health check (an unstructured task
//     its cancel does not reach) and could set `.postProcessing` over a new
//     recording. Fixed: the run captures the session generation on entry and
//     writes only while it is unchanged and the run is not cancelled.
//  2. A toggle that begins a session and then starts nothing (microphone
//     permission denied, a newer toggle). Its bump superseded the retry, which
//     writes nothing, so `.transcribing` stayed. Fixed: the toggle releases the
//     state when nothing newer began and nothing records.
//  3. History re-transcribe cancelling an in-flight pending-file retry:
//     unreachable. Both History call sites run as `.fileJob`, the pending
//     retry as `.dictation`, and the pipeline cancels only the same kind
//     (#1425). Driven for real in `FileJobAskFirstTests`
//     (`aHistoryRetryWithLeavesADictationRunning`,
//     `aHistoryRetryLeavesADictationRunning`); pinned here at the call sites.
//  4. A superseded retry that succeeds leaving a Retry button: unreachable.
//     The button needs `pendingRetryAudioPath != nil` and a dialog that is not
//     loading; every flow that supersedes a retry and shows the dialog clears
//     the path first, and path 2's release hides the dialog.
//
//  The flows themselves need a live recorder and pipeline, so the decisions
//  are pure functions tested by calling them, and the wiring is read from the
//  production source (`ProductionSource`, comments stripped).
//

import Foundation
import Testing
@testable import HyperWhisper

struct StaleSessionWritesTests {

    // MARK: - Path 1: the pipeline's `.postProcessing` write

    private typealias Pipeline = TranscriptionPipeline

    @Test func aDictationInItsOwnSessionShowsPostProcessing() {
        #expect(Pipeline.mayShowPostProcessingState(
            jobKind: .dictation, dictationRunning: true, recordingState: .transcribing,
            sessionGenerationAtStart: 7, currentSessionGeneration: 7, isCancelled: false
        ))
    }

    /// The issue's path: a retry (a dictation-kind run) superseded by a new
    /// recording reaches post-processing while that recording shows
    /// `.recording`.
    @Test func aRunANewerFlowTookOverLeavesTheStateAlone() {
        for kind in TranscriptionJobKind.allCases {
            for state: RecordingState in [.recording, .transcribing, .postProcessing, .idle] {
                #expect(!Pipeline.mayShowPostProcessingState(
                    jobKind: kind, dictationRunning: false, recordingState: state,
                    sessionGenerationAtStart: 7, currentSessionGeneration: 8, isCancelled: false
                ), "\(kind) relabelled \(state) after a newer flow began")
            }
        }
    }

    /// A newer retry superseding an older one is `.transcribing` too, so the
    /// state alone cannot tell the two apart: the generation does.
    @Test func aRetrySupersededByANewerRetryLeavesItsTranscribingState() {
        #expect(!Pipeline.mayShowPostProcessingState(
            jobKind: .dictation, dictationRunning: true, recordingState: .transcribing,
            sessionGenerationAtStart: 3, currentSessionGeneration: 4, isCancelled: false
        ))
    }

    @Test func aCancelledRunLeavesTheStateAlone() {
        for kind in TranscriptionJobKind.allCases {
            #expect(!Pipeline.mayShowPostProcessingState(
                jobKind: kind, dictationRunning: false, recordingState: .transcribing,
                sessionGenerationAtStart: 7, currentSessionGeneration: 7, isCancelled: true
            ))
        }
    }

    /// #1425 unchanged: a file job relabels only its own `.transcribing`,
    /// and never while a dictation runs.
    @Test func aFileJobStillRelabelsOnlyTheStateItShows() {
        #expect(Pipeline.mayShowPostProcessingState(
            jobKind: .fileJob, dictationRunning: false, recordingState: .transcribing,
            sessionGenerationAtStart: 7, currentSessionGeneration: 7, isCancelled: false
        ))
        #expect(!Pipeline.mayShowPostProcessingState(
            jobKind: .fileJob, dictationRunning: true, recordingState: .transcribing,
            sessionGenerationAtStart: 7, currentSessionGeneration: 7, isCancelled: false
        ))
        #expect(!Pipeline.mayShowPostProcessingState(
            jobKind: .fileJob, dictationRunning: false, recordingState: .recording,
            sessionGenerationAtStart: 7, currentSessionGeneration: 7, isCancelled: false
        ))
    }

    /// No `AppState` (the tests' pipelines): the decision allows, and the
    /// optional-chained write is a no-op.
    @Test func noAppStateKeepsTheOldDecision() {
        #expect(Pipeline.mayShowPostProcessingState(
            jobKind: .dictation, dictationRunning: true, recordingState: nil,
            sessionGenerationAtStart: nil, currentSessionGeneration: nil, isCancelled: false
        ))
    }

    /// The issue's sequence on the real counter: a retry begins and calls
    /// the pipeline, a new recording's toggle begins a session.
    @MainActor
    @Test func aNewRecordingAfterARetryStopsItsPostProcessingWrite() {
        let appState = AppState()
        appState.beginTranscriptionSession()  // the pending-file retry
        let atStart = appState.transcriptionSessionGeneration  // read on entry to the pipeline
        #expect(Pipeline.mayShowPostProcessingState(
            jobKind: .dictation, dictationRunning: true, recordingState: .transcribing,
            sessionGenerationAtStart: atStart,
            currentSessionGeneration: appState.transcriptionSessionGeneration, isCancelled: false
        ), "with no newer flow the retry still shows post-processing")

        appState.beginTranscriptionSession()  // the new recording's toggle
        #expect(!Pipeline.mayShowPostProcessingState(
            jobKind: .dictation, dictationRunning: true, recordingState: .recording,
            sessionGenerationAtStart: atStart,
            currentSessionGeneration: appState.transcriptionSessionGeneration, isCancelled: false
        ), "the stale retry must leave the new recording's state alone")
    }

    private static let pipelinePath =
        "app/macos/hyperwhisper/Managers/Transcription/Pipeline/TranscriptionPipeline+Transcription.swift"

    /// The generation is read before the first suspension (the 100 ms cancel
    /// sleep), so a flow that begins during it is "newer", not "own".
    @Test func thePipelineReadsTheGenerationBeforeItsFirstSuspension() throws {
        let entry = try ProductionSource.slice(
            of: Self.pipelinePath,
            from: "func transcribeWithDetails(",
            to: "if let previous = runningTasks[jobKind] {"
        )
        #expect(entry.contains("let sessionGenerationAtStart = appState?.transcriptionSessionGeneration"), "\(entry)")
        #expect(!entry.contains("await"), "a suspension before the generation is read: \(entry)")
    }

    /// The branch's write asks the decision, with the captured generation and
    /// the run's own cancel.
    @Test func thePostProcessingWriteGoesThroughTheDecision() throws {
        let branch = try ProductionSource.slice(
            of: Self.pipelinePath,
            from: "} else if runClientPostProcessing {",
            to: "markStage(\"post_processing\")"
        )
        let decision = try #require(branch.range(of: "Self.mayShowPostProcessingState("), "\(branch)")
        let write = try #require(branch.range(of: "appState?.recordingState = .postProcessing"), "\(branch)")
        #expect(decision.lowerBound < write.lowerBound)
        let call = branch[decision.upperBound..<write.lowerBound]
        #expect(call.contains("sessionGenerationAtStart: sessionGenerationAtStart"), "\(call)")
        #expect(call.contains("currentSessionGeneration: appState?.transcriptionSessionGeneration"), "\(call)")
        #expect(call.contains("isCancelled: Task.isCancelled"), "\(call)")
        #expect(call.contains("jobKind: jobKind"), "\(call)")
    }

    /// That write is the only `.postProcessing` write to `recordingState` in
    /// the app, so no other path can relabel a newer session this way.
    @Test func thePipelineHoldsTheOnlyPostProcessingWrite() throws {
        let appRoot = ProductionSource.url("app/macos/hyperwhisper")
        var sites: [String] = []
        for file in try ProductionSource.swiftFiles(under: appRoot) {
            let code = try ProductionSource.text(of: file)
                .components(separatedBy: .newlines)
                .filter { !$0.trimmingCharacters(in: .whitespaces).hasPrefix("//") }
                .joined(separator: "\n")
            if code.contains("recordingState = .postProcessing") {
                sites.append(file.lastPathComponent)
            }
        }
        #expect(sites == ["TranscriptionPipeline+Transcription.swift"], "\(sites)")
    }

    // MARK: - Path 2: a toggle that begins a session and starts nothing

    private typealias Flow = RecordingTranscriptionFlow

    /// The issue's path: Retry, then a record toggle whose start is refused
    /// (microphone permission denied). The retry is superseded and writes
    /// nothing; the toggle releases the state it left.
    @Test func aStartThatRecordedNothingReleasesTheSupersededState() {
        for state: RecordingState in [.transcribing, .postProcessing] {
            #expect(Flow.shouldReleaseStateAfterToggle(
                takeOverGeneration: 5, currentSessionGeneration: 5,
                isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: state
            ), "\(state) was left behind")
        }
    }

    @Test func aStartThatRecordsKeepsItsState() {
        #expect(!Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: 5, currentSessionGeneration: 5,
            isRecording: true, isStreamingActive: false, isStopInProgress: false, recordingState: .transcribing
        ))
        #expect(!Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: 5, currentSessionGeneration: 5,
            isRecording: false, isStreamingActive: true, isStopInProgress: false, recordingState: .transcribing
        ))
    }

    /// A newer flow (another toggle, a retry, a file import) owns the state.
    @Test func aNewerFlowKeepsItsState() {
        #expect(!Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: 5, currentSessionGeneration: 6,
            isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: .transcribing
        ))
    }

    /// A toggle that began no session (a stop, or a stop-only with nothing
    /// recording) never releases anything.
    @Test func aToggleThatBeganNoSessionReleasesNothing() {
        #expect(!Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: nil, currentSessionGeneration: 5,
            isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: .transcribing
        ))
    }

    /// Only a superseded flow's "busy" state is released: a failed start's
    /// `.idle`, a recording's `.recording`, a result are left as they are.
    @Test func onlyABusyStateIsReleased() {
        for state: RecordingState in [.idle, .recording, .processing, .complete("x"), .error("x")] {
            #expect(!Flow.shouldReleaseStateAfterToggle(
                takeOverGeneration: 5, currentSessionGeneration: 5,
                isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: state
            ), "\(state) was released")
        }
    }

    /// The sequence on the real counter: retry, toggle, refused start.
    @MainActor
    @Test func aRefusedStartAfterARetryReleasesTheStateAndAFileImportKeepsIt() {
        let appState = AppState()
        appState.beginTranscriptionSession()  // the pending-file retry
        appState.recordingState = .transcribing
        let toggle = appState.beginTranscriptionSession()  // the record toggle
        #expect(Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: toggle, currentSessionGeneration: appState.transcriptionSessionGeneration,
            isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: appState.recordingState
        ))

        appState.beginTranscriptionSession()  // a file import took the state meanwhile
        #expect(!Flow.shouldReleaseStateAfterToggle(
            takeOverGeneration: toggle, currentSessionGeneration: appState.transcriptionSessionGeneration,
            isRecording: false, isStreamingActive: false, isStopInProgress: false, recordingState: appState.recordingState
        ))
    }

    private static let togglePath =
        "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+Toggle.swift"

    /// A start that recorded, then was stopped during the start's own later
    /// awaits (a lost capture, the max-duration auto-stop: neither bumps),
    /// leaves the user's dictation `.transcribing` with nothing recording.
    /// Its stop holds `isStopInProgress` throughout, so nothing is released.
    @Test func aStartStoppedDuringItsOwnStartKeepsItsDictation() {
        for state: RecordingState in [.transcribing, .postProcessing] {
            #expect(!Flow.shouldReleaseStateAfterToggle(
                takeOverGeneration: 5, currentSessionGeneration: 5,
                isRecording: false, isStreamingActive: false, isStopInProgress: true, recordingState: state
            ), "the toggle released its own dictation's \(state)")
        }
    }

    /// The toggle begins a session through the #1276 decision, makes ONE
    /// start/stop decision per run, and releases only on the start side:
    /// on the pre-run cancel (gated by that decision), the pre-start cancel
    /// and after the start returns. Never in the stop arm.
    @Test func theToggleReleasesAfterAStartThatStartedNothing() throws {
        let toggle = try ProductionSource.slice(
            of: Self.togglePath,
            from: "func toggleRecordingWithTranscription(",
            to: "private func releaseStateNoSessionTookOver("
        )
        #expect(toggle.contains("let takeOverGeneration: UInt64? = Self.toggleTakesOverSession("), "\(toggle)")
        #expect(toggle.contains("? appState?.beginTranscriptionSession() : nil"), "\(toggle)")
        let release = "releaseStateNoSessionTookOver(takeOverGeneration)"
        let releases = toggle.components(separatedBy: release).count - 1
        #expect(releases == 3, "the pre-run cancel, the pre-start cancel and the post-start release: \(releases)")

        // One decision, read once, used by the cancel guard and the branch.
        #expect(toggle.components(separatedBy: "recordingLifecycle.isRecording || isStreamingActive").count - 1 == 1,
                "the start/stop decision is stated more than once: \(toggle)")
        #expect(toggle.contains("let startsASession = !stopsASession && !stopOnly"), "\(toggle)")
        #expect(toggle.contains("if stopsASession {"), "\(toggle)")
        #expect(toggle.contains("} else if startsASession {"), "\(toggle)")

        let preRun = try #require(toggle.range(of: "guard !Task.isCancelled else {"), "\(toggle)")
        let preRunBody = toggle[preRun.upperBound...]
        let preRunEnd = try #require(preRunBody.range(of: "return"))
        let preRunArm = preRunBody[..<preRunEnd.lowerBound]
        let gate = try #require(preRunArm.range(of: "if startsASession {"), "the pre-run release is not gated: \(preRunArm)")
        let gatedRelease = try #require(preRunArm.range(of: release), "\(preRunArm)")
        #expect(gate.lowerBound < gatedRelease.lowerBound)

        let start = try #require(toggle.range(of: "await handleStartRecording(mode: modeToUse)"), "\(toggle)")
        #expect(toggle[start.upperBound...].contains(release), "no release after the start")
        let stop = try #require(toggle.range(of: "await handleStopRecordingWithTranscription("), "\(toggle)")
        let stopArm = toggle[stop.upperBound...]
        let stopArmEnd = try #require(stopArm.range(of: "} else if startsASession {"))
        #expect(!stopArm[..<stopArmEnd.lowerBound].contains("releaseStateNoSessionTookOver"),
                "a stop's own flow owns the state it leaves")
    }

    /// The release asks the decision, and clears the text before it writes
    /// `.idle` (the dialog's idle handler would otherwise show a stale error),
    /// then hides the dialog.
    @Test func theReleaseAsksTheDecisionAndClearsTheTextFirst() throws {
        let release = try ProductionSource.slice(
            of: Self.togglePath,
            from: "private func releaseStateNoSessionTookOver(",
            to: "nonisolated static func shouldReleaseStateAfterToggle("
        )
        let decision = try #require(release.range(of: "Self.shouldReleaseStateAfterToggle("), "\(release)")
        #expect(release.contains("isStopInProgress: isStopInProgress"), "the release ignores a stop in progress: \(release)")
        let text = try #require(release.range(of: "appState.lastTranscription = \"\""), "\(release)")
        let idle = try #require(release.range(of: "appState.recordingState = .idle"), "\(release)")
        let hide = try #require(release.range(of: "appState.showRecordingDialog = false"), "\(release)")
        #expect(decision.lowerBound < text.lowerBound)
        #expect(text.lowerBound < idle.lowerBound)
        #expect(idle.lowerBound < hide.lowerBound)
    }

    /// Idling is right when a file import owned the state: the toggle's bump
    /// means the import's own release no longer resets it.
    @Test func aSupersededFileImportNoLongerResetsTheState() throws {
        let release = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift",
            from: "private func releaseRecordingState() {",
            to: "func "
        )
        #expect(release.contains("if appState?.transcriptionSessionGeneration == generation {"), "\(release)")
    }

    // MARK: - Path 3: History re-transcribe never cancels the pending retry

    /// Both History call sites run as file jobs; the pending-file retry runs
    /// as a dictation (no `jobKind:`, the default). The pipeline cancels only
    /// a run of the same kind.
    @Test func historyRetriesRunAsFileJobsAndThePendingRetryAsADictation() throws {
        let retryWith = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
            from: "func retryTranscription(_ transcript: Transcript, with mode: Mode) async -> Bool {",
            to: "refuseDeletedMode: true,"
        )
        #expect(retryWith.contains("transcriptionPipeline.transcribeWithDetails("), "\(retryWith)")
        let retryWithCall = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptActionHandler.swift",
            from: "let result = try await transcriptionPipeline.transcribeWithDetails(",
            to: ")"
        )
        #expect(retryWithCall.contains("jobKind: .fileJob"), "\(retryWithCall)")

        let controllerCall = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/Transcription/Support/TranscriptionRetryController.swift",
            from: "try await manager.transcribeWithDetails(",
            to: "))"
        )
        #expect(controllerCall.contains("jobKind: .fileJob"), "\(controllerCall)")

        let pendingRetryCall = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift",
            from: "try await transcriptionMgr.transcribeWithDetails(",
            to: ")"
        )
        #expect(!pendingRetryCall.contains("jobKind:"), "the pending retry is a dictation-kind run: \(pendingRetryCall)")

        let entry = try ProductionSource.slice(
            of: Self.pipelinePath,
            from: "func transcribeWithDetails(",
            to: "setJobState(.transcribing(provider: \"Starting...\", progress: 0.0), for: jobKind)"
        )
        #expect(entry.contains("jobKind: TranscriptionJobKind = .dictation"), "\(entry)")
        #expect(entry.contains("if let previous = runningTasks[jobKind] {"), "\(entry)")
        #expect(entry.contains("if !hasRunningJob(otherThan: jobKind) {"), "the engine-wide cancel must spare the other kind")
    }

    // MARK: - Path 4: a superseded successful retry leaves no Retry button

    /// The dialog's Retry button needs the pending path AND a dialog that is
    /// not loading, post-processing, retrying or showing a transcript.
    @Test func theRetryButtonNeedsThePathAndAnIdleDialog() throws {
        let condition = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Views/RecordingDialog.swift",
            from: "private var shouldShowPendingRetry: Bool {",
            to: "}"
        )
        for term in ["appState.pendingRetryAudioPath != nil", "!isLoading", "!isPostProcessing", "!isRetrying", "!showTranscription"] {
            #expect(condition.contains(term), "\(term) missing: \(condition)")
        }
    }

    /// A new recording, the flow that supersedes a retry and then shows the
    /// dialog, clears the pending path before it shows it.
    @Test func aStartClearsThePendingPathBeforeItShowsTheDialog() throws {
        let start = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+StartRecording.swift",
            from: "func handleStartRecording(mode: String) async {",
            to: "appState?.recordingState = .recording"
        )
        let clear = try #require(start.range(of: "appState?.pendingRetryAudioPath = nil"), "\(start)")
        let show = try #require(start.range(of: "appState?.showRecordingDialog = true"), "\(start)")
        #expect(clear.lowerBound < show.lowerBound)
    }

    /// A file import shows no recording dialog, and takes the state (bumping
    /// the generation) only from `.idle`, never from a retry's `.transcribing`.
    @Test func aFileImportShowsNoDialogAndTakesOnlyAnIdleState() throws {
        let path = "app/macos/hyperwhisper/Managers/Transcription/Flows/FileTranscriptionFlow.swift"
        let code = try ProductionSource.code(of: path)
        #expect(!code.contains("showRecordingDialog = true"))
        let gate = try #require(code.range(of: "if appState?.recordingState == .idle {"))
        let bump = try #require(code.range(of: "beginTranscriptionSession()"))
        #expect(gate.lowerBound < bump.lowerBound)
        #expect(!code[gate.upperBound..<bump.lowerBound].contains("}"), "the bump is outside the idle gate")
    }

    /// The superseded-success branch must NOT clear the path: a newer retry
    /// of the same file would lose its History-row write (AppState's didSet)
    /// and save a second row. The supersede return comes before the clear.
    @Test func aSupersededSuccessLeavesThePathToTheNewerFlow() throws {
        let body = try ProductionSource.slice(
            of: "app/macos/hyperwhisper/Managers/AudioRecording/RecordingFlow/RecordingTranscriptionFlow+ErrorHandling.swift",
            from: "let transcriptionResult = try await transcriptionMgr.transcribeWithDetails(",
            to: "} catch {"
        )
        let superseded = try #require(body.range(of: "guard !isPendingRetrySuperseded(identity) else { return }"), "\(body)")
        let clear = try #require(body.range(of: "appState.pendingRetryAudioPath = nil"), "\(body)")
        #expect(superseded.lowerBound < clear.lowerBound)
    }
}
