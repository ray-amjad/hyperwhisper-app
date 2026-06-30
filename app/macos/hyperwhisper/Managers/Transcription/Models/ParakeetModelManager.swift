import Foundation
import Combine
import AppKit
import os
import FluidAudio

// PARAKEET MODEL:
// Represents a downloadable Parakeet ASR model with version-specific properties
// Supports both V2 (English-only, highest recall) and V3 (Multilingual, 25 European languages)
@available(macOS 13.0, *)
struct ParakeetModel: Identifiable, Equatable {
    let id: String                              // Unique identifier (same as model name)
    let name: String                            // e.g., "parakeet-tdt-0.6b-v2"
    let displayName: String                     // e.g., "Parakeet V2 (English)"
    let size: String                            // e.g., "474 MB"
    let notes: String                           // Description of the model
    let supportedLanguages: [String: String]    // Language code -> display name mapping
    var isDownloaded: Bool
    var localURL: URL?

    // MULTILINGUAL CHECK:
    // V2 is English-only (1 language), V3 supports 25 European languages
    var isMultilingual: Bool {
        supportedLanguages.count > 1
    }

    // VERSION DETECTION:
    // Determines AsrModelVersion from model name string
    // V2 models contain "v2" in name, all others default to V3
    var version: AsrModelVersion {
        name.lowercased().contains("v2") ? .v2 : .v3
    }
}

// PARAKEET MODEL MANAGER:
// Manages downloading, deleting, and tracking state of Parakeet ASR models
// Supports multiple versions (V2/V3) with independent download/delete operations
@available(macOS 13.0, *)
@MainActor
final class ParakeetModelManager: ObservableObject {

    // MODEL CONSTANTS:
    // Defines the available Parakeet model versions and their metadata
    enum Constants {
        // V2 Model (English-only, highest recall)
        static let v2ModelId = "parakeet-tdt-0.6b-v2"
        static let v2DisplayName = "Parakeet V2 (English)"
        static let v2SizeDescription = "474 MB"
        static let v2Notes = "NVIDIA's Parakeet V2 optimized for fast English-only transcription with highest recall."
        static let v2Languages: [String: String] = ["en": "English"]

        // V3 Model (Multilingual, 25 European languages)
        static let v3ModelId = "parakeet-tdt-0.6b-v3"
        static let v3DisplayName = "Parakeet V3 (Multilingual)"
        static let v3SizeDescription = "494 MB"
        static let v3Notes = "Multilingual Parakeet transcription model supporting 25 European languages, optimized for ANE."
        static let v3Languages: [String: String] = [
            "en": "English", "de": "German", "fr": "French", "es": "Spanish",
            "it": "Italian", "pt": "Portuguese", "nl": "Dutch", "pl": "Polish",
            "ru": "Russian", "uk": "Ukrainian", "cs": "Czech", "sk": "Slovak",
            "hu": "Hungarian", "ro": "Romanian", "bg": "Bulgarian", "hr": "Croatian",
            "sl": "Slovenian", "sr": "Serbian", "da": "Danish", "sv": "Swedish",
            "no": "Norwegian", "fi": "Finnish", "et": "Estonian", "lv": "Latvian",
            "lt": "Lithuanian"
        ]

        // Backward compatibility: default to V3
        static let modelId = v3ModelId
    }

    @Published private(set) var availableModels: [ParakeetModel] = []

    // PER-MODEL DOWNLOAD STATE:
    // Owns the retained-Task + per-model progress + cancel machinery. Keyed by
    // modelId so V2 and V3 download independently/simultaneously. See
    // `DownloadController` for the seed/clamp/straggler-guard core.
    let downloads = DownloadController<String>()

    @Published var errorMessage: String?

    /// Optional hook called when a downloaded version is deleted so the
    /// `ParakeetProvider`'s in-memory `Runtime` cache can be invalidated for
    /// that version. Without this hook, a transcribe after delete +
    /// re-download would keep serving the stale in-memory `AsrManager`.
    /// Set from `TranscriptionPipeline.setParakeetModelManager(_:)`.
    var onVersionInvalidated: ((AsrModelVersion) async -> Void)?

    private var observation: NSObjectProtocol?
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "ParakeetModelManager")

    init() {
        refreshState()

        observation = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            Task { @MainActor in
                self?.refreshState()
            }
        }
    }

    deinit {
        if let observation {
            NotificationCenter.default.removeObserver(observation)
        }
    }

    // BACKWARD COMPATIBILITY:
    // Returns true if downloading any model (used by existing code)
    var isDownloading: Bool {
        downloads.isDownloading
    }

    // PER-MODEL DOWNLOAD CHECK:
    // Returns true if the specific model is currently downloading
    func isDownloading(_ modelId: String) -> Bool {
        downloads.isDownloading(modelId)
    }

    // ANY MODEL INSTALLED:
    // Returns true if any Parakeet version is downloaded
    var isModelInstalled: Bool {
        availableModels.contains { $0.isDownloaded }
    }

    // SPECIFIC MODEL INSTALLED:
    // Returns true if the specified model ID is downloaded
    func isModelInstalled(_ modelId: String) -> Bool {
        availableModels.first { $0.id == modelId }?.isDownloaded ?? false
    }

    // VERSION DETECTION HELPER:
    // Determines AsrModelVersion based on model name string
    private func version(for modelName: String) -> AsrModelVersion {
        modelName.lowercased().contains("v2") ? .v2 : .v3
    }

    // CACHE DIRECTORY HELPER:
    // Returns version-specific cache directory from FluidAudio
    private func cacheDirectory(for modelVersion: AsrModelVersion) -> URL {
        AsrModels.defaultCacheDirectory(for: modelVersion)
    }

    // REFRESH STATE:
    // Checks both V2 and V3 cache directories and updates model availability
    // Called on init, app activation, and after download/delete operations
    @MainActor
    func refreshState() {
        var models: [ParakeetModel] = []

        // STEP 1: Check V2 model status
        let v2Directory = cacheDirectory(for: .v2)
        let v2Exists = AsrModels.modelsExist(at: v2Directory)
        logger.debug("Parakeet V2 exists: \(v2Exists) at \(v2Directory.path)")

        models.append(ParakeetModel(
            id: Constants.v2ModelId,
            name: Constants.v2ModelId,
            displayName: Constants.v2DisplayName,
            size: Constants.v2SizeDescription,
            notes: Constants.v2Notes,
            supportedLanguages: Constants.v2Languages,
            isDownloaded: v2Exists,
            localURL: v2Exists ? v2Directory : nil
        ))

        // STEP 2: Check V3 model status
        let v3Directory = cacheDirectory(for: .v3)
        let v3Exists = AsrModels.modelsExist(at: v3Directory)
        logger.debug("Parakeet V3 exists: \(v3Exists) at \(v3Directory.path)")

        models.append(ParakeetModel(
            id: Constants.v3ModelId,
            name: Constants.v3ModelId,
            displayName: Constants.v3DisplayName,
            size: Constants.v3SizeDescription,
            notes: Constants.v3Notes,
            supportedLanguages: Constants.v3Languages,
            isDownloaded: v3Exists,
            localURL: v3Exists ? v3Directory : nil
        ))

        availableModels = models
    }

    // START DOWNLOAD:
    // Retains the download as a cancellable `Task` via `DownloadController`.
    // Each version downloads independently, keyed by modelId.
    @MainActor
    func startDownload(_ modelId: String) {
        downloads.start(modelId) { [weak self] controller in
            await self?.runDownload(modelId, controller)
        }
    }

    /// Cancel an in-flight download. FluidAudio honours cooperative `Task`
    /// cancellation, so cancelling the retained task tears the transfer down;
    /// `runDownload(_:_:)` then unwinds silently.
    @MainActor
    func cancelDownload(_ modelId: String) {
        logger.info("Cancelling Parakeet download \(modelId, privacy: .public)")
        downloads.cancel(modelId)
    }

    // DOWNLOAD SPECIFIC MODEL:
    // Downloads the specified Parakeet version using FluidAudio's version-aware
    // API. Uses `download` rather than `downloadAndLoad`: `download` fetches and
    // CoreML-compiles each component (Preprocessor/Encoder/Decoder/Joint) once to
    // verify it, honouring cancellation *between* components; the swap drops the
    // second, full `load()` pass `downloadAndLoad` would run. `ParakeetProvider`
    // lazy-loads (and compiles) at first transcribe, gated by on-disk
    // `AsrModels.modelsExist`, so that download-time compile is verify-only.
    @MainActor
    private func runDownload(_ modelId: String, _ controller: DownloadController<String>) async {
        errorMessage = nil
        let modelVersion = version(for: modelId)
        logger.info("Starting download for Parakeet \(String(describing: modelVersion))")

        // `AsrModels.download` sweeps each component's raw fraction 0→1 in turn,
        // so collapse them into one monotonic 0→1 fraction before publishing.
        let componentCount = modelVersion.hasFusedEncoder ? 3 : 4   // mirrors AsrModels.download's spec list
        let aggregator = ComponentProgressAggregator(componentCount: componentCount)

        do {
            // Aggregate FluidAudio's per-component sweeps into the published ring value.
            _ = try await AsrModels.download(
                version: modelVersion,
                progressHandler: { update in
                    Task { @MainActor in
                        controller.report(modelId, fraction: aggregator.aggregate(update.fractionCompleted))
                    }
                }
            )
            logger.info("Parakeet \(String(describing: modelVersion)) downloaded successfully")
        } catch is CancellationError {
            logger.info("Parakeet \(String(describing: modelVersion)) download cancelled")
        } catch let urlError as URLError where urlError.code == .cancelled {
            logger.info("Parakeet \(String(describing: modelVersion)) download cancelled")
        } catch {
            logger.error("Failed to download Parakeet \(String(describing: modelVersion)): \(error.localizedDescription, privacy: .public)")
            errorMessage = error.localizedDescription
        }

        refreshState()
    }

    // BACKWARD COMPATIBLE DOWNLOAD:
    // Maintains existing API - downloads V3 by default
    @MainActor
    func download() {
        startDownload(Constants.v3ModelId)
    }

    // DELETE SPECIFIC MODEL:
    // Removes the specified Parakeet version's cache directory
    // Does not affect other versions
    @MainActor
    func deleteModel(_ modelId: String) {
        let modelVersion = version(for: modelId)
        let directory = cacheDirectory(for: modelVersion)

        do {
            if FileManager.default.fileExists(atPath: directory.path) {
                try FileManager.default.removeItem(at: directory)
                logger.info("Removed Parakeet \(String(describing: modelVersion)) at \(directory.path, privacy: .public)")
            }
        } catch {
            logger.error("Failed to delete Parakeet \(String(describing: modelVersion)): \(error.localizedDescription, privacy: .public)")
            errorMessage = error.localizedDescription
        }
        refreshState()
        // Drop the in-memory cached manager for this version so the next
        // transcription re-loads from (now-empty / re-downloaded) disk instead
        // of serving the stale weights.
        if let hook = onVersionInvalidated {
            Task { await hook(modelVersion) }
        }
    }

    // BACKWARD COMPATIBLE DELETE:
    // Maintains existing API - deletes V3 by default
    @MainActor
    func deleteModel() {
        deleteModel(Constants.v3ModelId)
    }

    enum Utils {
        // Default to V3 for backward compatibility
        static var modelsDirectory: URL {
            AsrModels.defaultCacheDirectory(for: .v3)
        }

        // Version-specific directory accessor
        static func modelsDirectory(for version: AsrModelVersion) -> URL {
            AsrModels.defaultCacheDirectory(for: version)
        }
    }
}

/// Collapses FluidAudio's per-component download progress into a single monotonic 0→1
/// fraction. `AsrModels.download` fetches + CoreML-compiles each component
/// (Preprocessor/Encoder/Decoder/Joint) in turn, restarting its handler at 0 per component;
/// published raw, that fills the Model Library ring 3–4× per install. A backward jump in the
/// raw fraction marks the next component. Created fresh per download (no persisted state).
@MainActor
final class ComponentProgressAggregator {
    private let componentCount: Int
    private var completed = 0
    private var lastRaw = 0.0
    init(componentCount: Int) { self.componentCount = max(componentCount, 1) }

    func aggregate(_ raw: Double) -> Double {
        if raw + 0.1 < lastRaw { completed = min(completed + 1, componentCount - 1) } // reset → next component
        lastRaw = raw
        return (Double(completed) + min(max(raw, 0), 1)) / Double(componentCount)
    }
}
