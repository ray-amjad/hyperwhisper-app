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

    /// Cancel current transcription and reset state.
    func cancelTranscription() {
        currentTask?.cancel()
        currentTask = nil
        if isTranscribing {
            state = .idle
        }
        localProvider.cancelTranscription()
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
