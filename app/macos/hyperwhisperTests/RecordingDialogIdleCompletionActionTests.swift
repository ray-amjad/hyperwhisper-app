//
//  RecordingDialogIdleCompletionActionTests.swift
//  hyperwhisperTests
//

import Testing
@testable import HyperWhisper

struct RecordingDialogIdleCompletionActionTests {
    @Test func closesWhenIdleCompletesWithEmptyTranscriptionWhileLoading() {
        let action = RecordingDialogIdleCompletionAction.resolve(
            wasLoadingOrPostProcessing: true,
            lastTranscription: ""
        )

        #expect(action == .close)
    }

    @Test func ignoresIdleWhenDialogWasNotWaitingForTranscription() {
        let action = RecordingDialogIdleCompletionAction.resolve(
            wasLoadingOrPostProcessing: false,
            lastTranscription: ""
        )

        #expect(action == .none)
    }

    @Test func showsTranscriptWhenIdleCompletesWithText() {
        let action = RecordingDialogIdleCompletionAction.resolve(
            wasLoadingOrPostProcessing: true,
            lastTranscription: "hello"
        )

        #expect(action == .showTranscription("hello"))
    }

    @Test func showsErrorWhenIdleCompletesWithErrorText() {
        let action = RecordingDialogIdleCompletionAction.resolve(
            wasLoadingOrPostProcessing: true,
            lastTranscription: "Error: transcription failed"
        )

        #expect(action == .showError("Error: transcription failed"))
    }
}
