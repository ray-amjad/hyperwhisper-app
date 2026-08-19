//
//  TranscriptionPipeline+Operations.swift
//  hyperwhisper
//
//  Vocabulary, configuration, and retry helpers.
//

import Foundation

extension TranscriptionPipeline {

    // MARK: - Vocabulary

    /// Remove a word from custom vocabulary.
    func removeFromVocabulary(_ word: String) {
        let items = PersistenceController.shared.fetchAllVocabularyItems()
        if let item = items.first(where: { $0.word == word }) {
            PersistenceController.shared.deleteVocabularyItem(item)
        }
    }

    /// Remove a vocabulary item by ID.
    func removeFromVocabulary(byId id: UUID) {
        PersistenceController.shared.deleteVocabularyItem(byId: id)
    }

    // MARK: - Configuration

    /// Store the OpenAI API key and update provider configuration.
    func setAPIKey(_ key: String) {
        openAIAPIKey = key
        providerCoordinator.setupCloudProvider(with: key)
        useOpenAITranscription = !key.isEmpty

        AppLogger.network.info("API Key updated - Length: \(key.count, privacy: .public), OpenAI enabled: \(self.useOpenAITranscription, privacy: .public)")
    }

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
