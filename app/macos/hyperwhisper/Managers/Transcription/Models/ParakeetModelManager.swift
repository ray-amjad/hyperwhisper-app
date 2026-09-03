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

        /// Resolve supported persisted aliases to the exact identifiers used
        /// by the model library. Keep this list explicit: coercing an unknown
        /// identifier to V3 would make a typo select a real model.
        static func canonicalModelId(for modelId: String) -> String? {
            switch modelId.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() {
            case v2ModelId:
                return v2ModelId
            case v3ModelId, "parakeet-tdt-v3-multilingual":
                return v3ModelId
            default:
                return nil
            }
        }

        /// Local API requests historically default an omitted Parakeet model
        /// to V3. Preserve unknown explicit values so the provider router can
        /// reject them instead of silently changing the requested model.
        static func modelIdForSelection(_ modelId: String?) -> String {
            guard let trimmed = modelId?.trimmingCharacters(in: .whitespacesAndNewlines),
                  !trimmed.isEmpty else {
                return v3ModelId
            }
            return canonicalModelId(for: trimmed) ?? trimmed
        }
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
        guard let canonicalModelId = Constants.canonicalModelId(for: modelId) else {
            return false
        }
        return downloads.isDownloading(canonicalModelId)
    }

    // VERSION DETECTION HELPER:
    // Determines AsrModelVersion based on model name string
    private func version(for modelName: String) -> AsrModelVersion? {
        guard let canonicalModelId = Constants.canonicalModelId(for: modelName) else {
            return nil
        }
        return canonicalModelId == Constants.v2ModelId ? .v2 : .v3
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

        // PUBLISH ONLY ON AN ACTUAL CHANGE:
        // `@Published` fires on every assignment, equal value or not. Assigning
        // unconditionally here closes a feedback cycle that never settles:
        // the root view's `.onReceive($availableModels)` calls
        // `refreshParakeetReadiness` -> `prepareModel(for:)` -> `refreshState()`,
        // which publishes again. `.removeDuplicates()` on that subscription
        // cannot break it, because `prepareModel` moves `modelReadyState` from
        // `.loading` to `.ready`, re-evaluating the root body and rebuilding the
        // operator chain with no memory of the previous value.
        // See ParakeetRefreshStateRepublishTests.
        if availableModels != models {
            availableModels = models
        }
    }

    // START DOWNLOAD:
    // Retains the download as a cancellable `Task` via `DownloadController`.
    // Each version downloads independently, keyed by modelId.
    @MainActor
    func startDownload(_ modelId: String) {
        guard let canonicalModelId = Constants.canonicalModelId(for: modelId) else {
            logger.error("Refusing to download unknown Parakeet model \(modelId, privacy: .public)")
            errorMessage = "Unknown Parakeet model: \(modelId)"
            return
        }
        // Issue #312: seed the stage on the same frame as the 0.01 progress seed.
        // The card renders the moment `downloading` publishes, and without a stage
        // there it would print "Downloading... 1%" over a determinate bar until
        // FluidAudio's first callback lands — the reported symptom, on every
        // download's first frame.
        downloads.start(canonicalModelId, initialStage: .preparing) { [weak self] controller in
            await self?.runDownload(canonicalModelId, controller)
        }
    }

    /// Cancel an in-flight download. FluidAudio honours cooperative `Task`
    /// cancellation, so cancelling the retained task tears the transfer down;
    /// `runDownload(_:_:)` then unwinds silently.
    @MainActor
    func cancelDownload(_ modelId: String) {
        guard let canonicalModelId = Constants.canonicalModelId(for: modelId) else {
            logger.warning("Ignoring cancellation for unknown Parakeet model \(modelId, privacy: .public)")
            return
        }
        logger.info("Cancelling Parakeet download \(canonicalModelId, privacy: .public)")
        downloads.cancel(canonicalModelId)
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
        guard let modelVersion = version(for: modelId) else {
            logger.error("Refusing to run download for unknown Parakeet model \(modelId, privacy: .public)")
            errorMessage = "Unknown Parakeet model: \(modelId)"
            return
        }
        logger.info("Starting download for Parakeet \(String(describing: modelVersion))")

        // FluidAudio reports one repository-wide, byte-weighted fraction for the whole
        // transfer and then a compile tick per component; the aggregator maps that onto
        // a single monotonic 0→1 fraction plus a coarse stage.
        let componentCount = modelVersion.hasFusedEncoder ? 3 : 4   // mirrors AsrModels.download's spec list
        let aggregator = ModelDownloadProgressAggregator(componentCount: componentCount)

        do {
            _ = try await AsrModels.download(
                version: modelVersion,
                progressHandler: { update in
                    // FluidAudio calls this on an unspecified queue — hop to the main
                    // actor before touching the aggregator or the controller.
                    Task { @MainActor in
                        let published = aggregator.aggregate(update)
                        controller.report(modelId, fraction: published.fraction, stage: published.stage)
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
        guard let modelVersion = version(for: modelId) else {
            logger.error("Refusing to delete unknown Parakeet model \(modelId, privacy: .public)")
            errorMessage = "Unknown Parakeet model: \(modelId)"
            return
        }
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

/// Collapses FluidAudio's download callbacks into a single monotonic 0→1 fraction plus a
/// coarse `ModelDownloadStage`.
///
/// `AsrModels.download` calls `DownloadUtils.loadModels` once per component
/// (Preprocessor/Encoder/Decoder/Joint), passing the same handler each time. Only the
/// *first* of those calls transfers anything: `loadModels` checks the cache for the whole
/// repository, so components 2…n find every file already on disk and emit their
/// cache-hit/compile/done ticks in milliseconds. Dividing the transfer by the component
/// count therefore squashed a four-minute download into the first 1/n of the progress bar
/// (issue #312).
///
/// Instead we take FluidAudio's own repository-wide, byte-weighted download fraction —
/// which occupies 0…0.5 of *its* range — and give it 0…0.9 of ours, reserving the last 0.1
/// for the per-component compile passes. Created fresh per download (no persisted state).
///
/// Both published values move forwards only, and they do so for the same reason: the
/// handler arrives on an unspecified queue and `runDownload` hops each callback onto the
/// main actor in its own unstructured `Task`, so a late straggler must not be able to rewind
/// the card. The one thing that *does* rewind them is a genuine restart — see `.listing`.
@MainActor
final class ModelDownloadProgressAggregator {

    /// Share of the published bar given to the transfer; the rest is the compile passes.
    private static let downloadSpan = 0.9

    private let componentCount: Int
    private var completedComponents = 0
    private var lastFraction = 0.0
    /// High-water mark for the stage, the exact counterpart of `lastFraction`.
    ///
    /// Without it the published stage runs `.processing → .preparing → .processing` once
    /// per cached component: `loadModels` re-checks the cache for the whole repository on
    /// every component, so components 2…n each emit the cache-hit sentinel
    /// `.downloading(completedFiles: 0, totalFiles: 0)` (`DownloadUtils.swift:296-298`)
    /// *after* component 1 has already compiled.
    private var lastStage: ModelDownloadStage = .preparing

    init(componentCount: Int) { self.componentCount = max(componentCount, 1) }

    func aggregate(
        _ update: DownloadUtils.DownloadProgress
    ) -> (fraction: Double, stage: ModelDownloadStage) {
        var fraction = lastFraction
        // No default: every branch assigns, so a FluidAudio bump that adds a phase
        // fails to compile here instead of silently reporting the wrong stage.
        let candidate: ModelDownloadStage

        switch update.phase {
        case .listing:
            // `downloadRepo` emits `.listing` exactly once, at its top, before it has
            // transferred a byte (`DownloadUtils.swift:495`). Seeing one therefore means a
            // repository transfer is starting from zero — and the second one means
            // `loadModels` caught a load failure, deleted the whole repo directory and
            // re-ran with this same handler (`DownloadUtils.swift:173-213`). Rewind both
            // high-water marks, or the retry's several hundred megabytes of real network
            // activity are swallowed by the guards and the bar sits still for minutes.
            //
            // `completedComponents` is deliberately *not* rewound: the per-component
            // `loadModels` calls `AsrModels.download` already finished have returned and
            // will not compile again, so the compile tail has to resume where it stopped
            // or it can never reach 1.0.
            lastFraction = 0.0
            lastStage = .preparing
            fraction = 0.0
            candidate = .preparing

        case .downloading(let completedFiles, let totalFiles):
            // FluidAudio's transfer occupies 0…0.5 of its own range and is already
            // byte-weighted across every file in the repository. Rescale to 0…1, then
            // onto our download span.
            let transferred = min(max(update.fractionCompleted * 2.0, 0.0), 1.0)
            fraction = transferred * Self.downloadSpan
            // A cached component emits `.downloading(completedFiles: 0, totalFiles: 0)`,
            // which is not a real file counter — report that as preparing. Once anything
            // has compiled, `advance(to:)` below discards it entirely.
            if totalFiles > 0 {
                candidate = .downloading(completedFiles: completedFiles, totalFiles: totalFiles)
            } else {
                candidate = .preparing
            }

        case .compiling:
            // `loadModels` ends each component with a `.compiling` update at 1.0.
            if update.fractionCompleted >= 1.0 {
                completedComponents = min(completedComponents + 1, componentCount)
            }
            if completedComponents >= componentCount {
                fraction = 1.0
            } else {
                fraction = Self.downloadSpan
                    + (1.0 - Self.downloadSpan) * Double(completedComponents) / Double(componentCount)
            }
            candidate = .processing
        }

        // Monotonic guard: the published fraction never moves backwards, whatever order
        // the callbacks arrive in after the hop to the main actor. The trailing `min`
        // is defensive — `componentCount` mirrors `AsrModels.download`'s spec list by
        // hand, so a future FluidAudio that adds a fifth component must not be able to
        // push the bar past a full one.
        fraction = min(max(fraction, lastFraction), 1.0)
        lastFraction = fraction
        let stage = Self.advance(from: lastStage, to: candidate)
        lastStage = stage
        return (fraction, stage)
    }

    /// The stage half of the monotonic guard: `preparing` → `downloading` → `processing`,
    /// one way only, and the file counter inside `downloading` climbs only.
    ///
    /// Returning the *current* stage rather than the candidate is what keeps the card on
    /// one stable state for the whole compile tail instead of flipping it back to
    /// "Preparing download…" once per cached component.
    private static func advance(
        from current: ModelDownloadStage,
        to candidate: ModelDownloadStage
    ) -> ModelDownloadStage {
        if rank(candidate) < rank(current) {
            return current
        }
        // Same rank: a straggler must not walk "file 19 of 22" back to "file 5 of 22".
        if case .downloading(let newFiles, let newTotal) = candidate,
            case .downloading(let oldFiles, let oldTotal) = current,
            newTotal == oldTotal, newFiles < oldFiles
        {
            return current
        }
        return candidate
    }

    /// Ordering for `advance(from:to:)`. No `default:`, so a new `ModelDownloadStage`
    /// has to be placed in the order explicitly rather than inheriting one.
    private static func rank(_ stage: ModelDownloadStage) -> Int {
        switch stage {
        case .preparing: return 0
        case .downloading: return 1
        case .processing: return 2
        }
    }
}
