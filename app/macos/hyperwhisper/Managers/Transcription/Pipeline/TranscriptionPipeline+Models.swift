//
//  TranscriptionPipeline+Models.swift
//  hyperwhisper
//
//  Model mapping and lifecycle helpers.
//

import Foundation

/// Map a string model identifier to the `WhisperModel` enum.
/// Accepts legacy and variant identifiers (e.g. "ggml-small", "small.en", "large-v3-turbo").
func mapModelIdToWhisperModel(_ id: String) -> WhisperModel? {
    // Try exact match first (new model IDs from the API).
    if let exactMatch = WhisperModel(rawValue: id) {
        return exactMatch
    }

    // Be flexible: support identifiers like "ggml-small", "small.en", "medium-v3", etc.
    let lower = id.lowercased()

    // Check for English variants first (more specific).
    if lower.contains("tiny") && lower.contains(".en") { return .tinyEn }
    if lower.contains("base") && lower.contains(".en") { return .baseEn }
    if lower.contains("small") && lower.contains(".en") { return .smallEn }
    if lower.contains("medium") && lower.contains(".en") { return .mediumEn }

    // Check for large model variants.
    if lower.contains("large-v3_turbo") || lower.contains("large-v3-turbo") { return .largeV3Turbo }
    if lower.contains("large-v3") { return .largeV3 }
    if lower.contains("large-v2") || lower.contains("large_v2") { return .largeV2 }

    // Check for base multilingual models.
    if lower.contains("tiny") { return .tiny }
    if lower.contains("base") { return .base }
    if lower.contains("small") { return .small }
    if lower.contains("medium") { return .medium }

    // Fallback for any other large references (default to latest).
    if lower.contains("large") { return .largeV3 }

    return nil
}

extension TranscriptionPipeline {

    /// Prepare a model for use (preload it immediately when mode is selected).
    /// This ensures the model is ready before transcription starts.
    @MainActor
    func prepareModel(for mode: Mode?) async {
        await modelCoordinator.prepareModel(
            for: mode,
            currentState: state,
            cancelTranscription: { [weak self] in
                self?.cancelTranscription()
            }
        )
    }

    /// Prepare the local LLM runtime for the active mode.
    @MainActor
    func prepareLocalRuntime(for mode: Mode?) async {
        await modelCoordinator.prepareLocalRuntime(for: mode)
    }

    /// Reload the runtime for a persisted mode identifier.
    @MainActor
    func refreshLocalRuntime(forModeId modeId: String?) async {
        await modelCoordinator.refreshLocalRuntime(forModeId: modeId)
    }

    /// Delete a downloaded model to free up space.
    func deleteModel(_ model: WhisperModel) throws {
        try modelCoordinator.deleteModel(model)

        // Sync with local properties.
        availableModels = modelCoordinator.availableModels
        selectedModel = modelCoordinator.selectedModel
    }

    /// Get the total size of downloaded models.
    func getModelsSize() -> Int64 {
        return modelCoordinator.getModelsSize()
    }

    /// Preload a model and warm it up (if desired).
    @MainActor
    func preloadModel(_ model: WhisperModel) async {
        await modelCoordinator.preloadModel(model)
    }

    /// Rescan available local models (exposed for startup sync).
    @MainActor
    func rescanAvailableLocalModels() {
        modelCoordinator.rescanAvailableLocalModels()

        // Sync with local properties.
        availableModels = modelCoordinator.availableModels
        selectedModel = modelCoordinator.selectedModel
    }
}
