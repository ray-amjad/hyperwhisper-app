//
//  TranscriptionPipeline+PostProcessing.swift
//  hyperwhisper
//
//  AI post-processing setup and callbacks.
//

import Foundation

extension TranscriptionPipeline {

    /// Set up AI post-processor with callbacks.
    @MainActor
    func setupAIPostProcessor() {
        guard let settings = settingsManager else {
            aiPostProcessor = nil
            aiPostProcessorSettingsReference = nil
            return
        }

        // Reuse existing processor if it's already bound to this settings manager.
        if aiPostProcessor != nil,
           aiPostProcessorSettingsReference === settings {
            // Re-wire weak model managers in case they were set after initial creation
            if aiPostProcessor?.localModelManager == nil, let localModelManager = modelCoordinator.localModelManager {
                aiPostProcessor?.localModelManager = localModelManager
            }
            return
        }

        let processor = AIPostProcessor(settingsManager: settings)

        // Wire license manager for HyperWhisper Cloud standalone post-processing.
        processor.licenseManager = licenseManager

        // Wire custom post-processing manager for custom endpoint routing.
        processor.customPostProcessingManager = customPostProcessingManager

        processor.llamaServerController = modelCoordinator.llamaServerController
        if let localModelManager = modelCoordinator.localModelManager {
            processor.localModelManager = localModelManager
        }

        // Wire the same runtime references onto the provider router so the
        // pre-flight health check can probe llama-server before transcription
        // completes.
        providerCoordinator.llamaServerController = modelCoordinator.llamaServerController
        if providerCoordinator.localModelManager == nil,
           let localModelManager = modelCoordinator.localModelManager {
            providerCoordinator.localModelManager = localModelManager
        }

        // Wire callbacks to AppState for streaming updates and errors.
        processor.onStreamingStateChange = { [weak self] isStreaming in
            self?.appState?.isStreaming = isStreaming
        }
        processor.onStreamingTextUpdate = { [weak self] text in
            self?.appState?.streamingText = text
        }
        processor.onPostProcessingError = { [weak self] error in
            self?.appState?.showInlineError(error)
        }

        aiPostProcessor = processor
        aiPostProcessorSettingsReference = settings
    }
}
