//
//  TranscriptionPipeline+Operations.swift
//  hyperwhisper
//
//  Configuration and retry helpers.
//

import Foundation

extension TranscriptionPipeline {

    // MARK: - Configuration

    /// Refresh API configuration (call when settings change).
    func refreshConfiguration() {
        providerCoordinator.refreshConfiguration(openAIAPIKey: openAIAPIKey)
        setupAIPostProcessor()
    }

    // MARK: - Cancellation / Retry

    /// Cancel every running transcription, of both kinds, and reset state.
    /// Model preparation uses this: it is about to swap the engine itself.
    func cancelTranscription() {
        for kind in TranscriptionJobKind.allCases {
            cancelTranscription(kind)
        }
        localProvider?.cancelTranscription()
    }

    /// Cancel the running transcription of one kind only (#1425): the
    /// recording dialog and the cancel shortcut stop a dictation and leave a
    /// file job running. The engine-wide Whisper cancel runs only when no
    /// other kind is running; the task cancel reaches the engine anyway.
    func cancelTranscription(_ kind: TranscriptionJobKind) {
        runningTasks[kind]?.cancel()
        runningTasks[kind] = nil
        if case .transcribing = jobStates[kind] {
            setJobState(.idle, for: kind)
        }
        if !hasRunningJob(otherThan: kind) {
            localProvider?.cancelTranscription()
        }
    }

    /// Retry a failed transcription using the stored audio file.
    @MainActor
    func retryTranscription(for transcript: Transcript) async throws -> TranscriptionResult {
        return try await retryHandler.retryTranscription(for: transcript)
    }

    /// Check if a transcript can be retried.
    func canRetryTranscript(_ transcript: Transcript) -> Bool {
        return retryHandler.canRetryTranscript(transcript)
    }
}
