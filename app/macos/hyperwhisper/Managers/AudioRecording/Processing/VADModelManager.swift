//
//  VADModelManager.swift
//  hyperwhisper
//
//  Manages the Silero VAD (Voice Activity Detection) model.
//  The model is bundled with the app and used to detect speech segments
//  in audio before transcription.
//
//  VAD INTEGRATION OVERVIEW:
//  ========================
//  HyperWhisper uses whisper.cpp's standalone VAD API (not the integrated VAD
//  in whisper_full). This allows VAD processing BEFORE sending audio to any
//  transcription provider (local or cloud), ensuring consistent behavior.
//
//  The Silero VAD model (ggml-silero-v5.1.2.bin, ~864KB) is bundled in Resources/
//  and loaded at runtime when VAD is enabled.
//

import Foundation
import OSLog

// MARK: - VADModelManager

/// Manages the Silero VAD model bundled with the app.
///
/// SINGLETON PATTERN:
/// Uses a shared instance to avoid loading the model multiple times.
/// The model path is cached after first lookup.
///
/// MODEL DETAILS:
/// - Format: GGML binary format (compatible with whisper.cpp)
/// - Size: ~864KB
/// - Source: Silero VAD v5.1.2
/// - Location: Bundle Resources/ggml-silero-v5.1.2.bin
class VADModelManager {

    // MARK: - Singleton

    /// Shared instance for app-wide access
    static let shared = VADModelManager()

    // MARK: - Properties

    /// Logger for debugging
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "VADModelManager")

    /// Cached model path to avoid repeated bundle lookups
    private var cachedModelPath: String?

    // MARK: - Initialization

    /// Private initializer to enforce singleton pattern
    private init() {}

    // MARK: - Public Methods

    /// Get the path to the VAD model file bundled with the app.
    ///
    /// MODEL LOOKUP FLOW:
    /// 1. Return cached path if available
    /// 2. Look for model in app bundle Resources
    /// 3. Cache the path for future calls
    /// 4. Return nil if model not found (logs error)
    ///
    /// - Returns: Path to the VAD model, or nil if not found
    func getModelPath() async -> String? {
        // Return cached path if available
        if let cached = cachedModelPath {
            return cached
        }

        // Look for model in bundle
        // MODEL FILE: ggml-silero-v5.1.2.bin
        // Must be added to the Xcode project's "Copy Bundle Resources" build phase
        guard let modelURL = Bundle.main.url(forResource: "ggml-silero-v5.1.2", withExtension: "bin") else {
            logger.error("VAD model not found in bundle resources. Ensure ggml-silero-v5.1.2.bin is added to the app bundle.")
            return nil
        }

        // Cache and return
        let path = modelURL.path
        cachedModelPath = path
        logger.info("VAD model found at: \(path)")

        return path
    }

    /// Check if the VAD model is available in the app bundle.
    ///
    /// USAGE: Call this before enabling VAD to ensure the model is available.
    /// If returns false, VAD should be disabled gracefully.
    ///
    /// - Returns: true if the model file exists in the bundle
    func isModelAvailable() -> Bool {
        return Bundle.main.url(forResource: "ggml-silero-v5.1.2", withExtension: "bin") != nil
    }

    /// Clear the cached model path.
    ///
    /// Useful for testing or if the bundle is updated at runtime (unlikely).
    func clearCache() {
        cachedModelPath = nil
    }
}
