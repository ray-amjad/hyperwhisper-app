import Foundation
import Combine
import AppKit
import os
import FluidAudio

// NEMOTRON 3.5 ASR STREAMING MULTILINGUAL:
//
// NVIDIA's Nemotron 3.5 ASR Streaming Multilingual 0.6B, packaged by FluidInference as
// two on-disk variants in a single HuggingFace repo:
//
//   ~/Library/Application Support/FluidAudio/Models/nemotron-multilingual/
//       latin/2240ms/         ← en/es/fr/it/pt/de (smaller joint, faster)
//       multilingual/2240ms/  ← full 13k-token vocab, ~40 languages incl. zh/ja/ko/ar
//
// FluidAudio v0.15.2 picks the variant implicitly from the language code passed to
// `setLanguage(_:)` (or `downloadVariant(languageCode:)`). On the HyperWhisper side we
// model it as two distinct entries in the Model Library so users explicitly trade
// speed (latin) vs. coverage (multilingual).
//
// Chunk size: 2240ms — FluidAudio's recommended default (highest RTFx, lowest overhead).
// Downloaded check: existence of `metadata.json` at `<variantDir>/`. Matches FluidAudio's
// own cache-reuse probe in `StreamingNemotronMultilingualAsrManager+Shared.downloadVariant`.

@available(macOS 14.0, *)
struct NemotronModel: Identifiable, Equatable {
    let id: String
    let name: String
    let displayName: String
    let size: String
    let notes: String
    let supportedLanguages: [String: String]
    let variant: NemotronModelManager.Variant
    let isNew: Bool
    var isDownloaded: Bool
    var localURL: URL?

    var isMultilingual: Bool {
        supportedLanguages.count > 1
    }
}

@available(macOS 14.0, *)
@MainActor
final class NemotronModelManager: ObservableObject {

    enum Variant: String, CaseIterable, Sendable {
        case latin
        case multilingual

        // Sub-directory name inside the cache repo. Mirrors FluidAudio's
        // `languageDirectory(for:)` return values — keep these strings in sync.
        var folderName: String { rawValue }

        // Language code we hand to FluidAudio's `downloadVariant(languageCode:)` to
        // make it route to this variant's on-disk folder. Latin needs a Latin-script
        // hint ("en"); multilingual uses "auto".
        var downloadLanguageHint: String {
            switch self {
            case .latin: return "en"
            case .multilingual: return "auto"
            }
        }
    }

    // MODEL CONSTANTS:
    // Public model identifiers used throughout the app (modes, router, library).
    enum Constants {
        static let latinModelId = "nemotron-asr-3.5-latin"
        static let latinDisplayName = "Nemotron 3.5 (Latin)"
        static let latinSize = "~350 MB"
        static let latinNotes = "NVIDIA's Nemotron 3.5 ASR Streaming, Latin-script tuned (English, Spanish, French, Italian, Portuguese, German). Smaller and faster than the multilingual variant."

        static let multilingualModelId = "nemotron-asr-3.5-multilingual"
        static let multilingualDisplayName = "Nemotron 3.5 (Multilingual)"
        static let multilingualSize = "~1.3 GB"
        static let multilingualNotes = "NVIDIA's Nemotron 3.5 ASR Streaming, full vocabulary covering ~40 languages including Chinese, Japanese, Korean, and Arabic. Higher coverage, slightly slower than the Latin variant."

        // Auto-retry tuning. FluidAudio's `downloadSubdirectory` reports only per-file
        // progress (no per-byte liveness), so we can't safely detect a mid-file stall from
        // the app side — instead we retry when the transfer actually THROWS (the platform
        // request timeout eventually fires on a silent CDN, e.g. the 2026-06-12 incident).
        static let maxDownloadAttempts = 3
        static let retryBackoffSeconds: [UInt64] = [2, 5, 10]

        // FluidAudio HuggingFace repo folder name + per-variant subdirectory.
        // Source of truth: `Repo.nemotronMultilingual.folderName` and
        // `StreamingNemotronMultilingualAsrManager.downloadVariant(...)`.
        static let repoFolderName = "nemotron-multilingual"

        // Chunk size tier (ms). 2240 is FluidAudio's recommended default — the source
        // also exposes 560/1120/4480, but only 2240 ships from `downloadAndPreloadShared`'s
        // default `chunkMs` and gives the highest RTFx on Apple Silicon.
        static let chunkMs: Int = 2240

        // Required file inside a downloaded variant — matches FluidAudio's own
        // cache-reuse probe in downloadVariant.
        static let metadataFileName = "metadata.json"
    }

    // LATIN VARIANT — vocab-pruned, fast path:
    // Source of truth: FluidAudio `StreamingNemotronMultilingualAsrManager.languageDirectory(for:)`,
    // which routes only `en|es|fr|it|pt|de` prefixes to the latin folder.
    nonisolated static let latinLanguages: [String: String] = [
        "en": "English",
        "es": "Spanish",
        "fr": "French",
        "it": "Italian",
        "pt": "Portuguese",
        "de": "German"
    ]

    // MULTILINGUAL VARIANT — full 13k-token vocab, ~40 languages:
    // The docs cite "~40 languages (en, es, de, fr, it, pt, ar, ja, ko, zh-CN, ru, hi, vi, …)".
    // The model's prompt_dictionary (metadata.json) is the runtime source of truth; this
    // set is a curated picker list. Codes not in the dictionary will fall back to the
    // model's `default_prompt_id` (auto) at runtime — no crash.
    nonisolated static let multilingualLanguages: [String: String] = [
        "en": "English",
        "es": "Spanish",
        "fr": "French",
        "de": "German",
        "it": "Italian",
        "pt": "Portuguese",
        "nl": "Dutch",
        "sv": "Swedish",
        "da": "Danish",
        "no": "Norwegian",
        "fi": "Finnish",
        "pl": "Polish",
        "cs": "Czech",
        "ro": "Romanian",
        "hu": "Hungarian",
        "el": "Greek",
        "uk": "Ukrainian",
        "ru": "Russian",
        "tr": "Turkish",
        "ar": "Arabic",
        "he": "Hebrew",
        "fa": "Persian",
        "hi": "Hindi",
        "id": "Indonesian",
        "ms": "Malay",
        "th": "Thai",
        "vi": "Vietnamese",
        "ja": "Japanese",
        "ko": "Korean",
        "zh": "Chinese"
    ]

    @Published private(set) var availableModels: [NemotronModel] = []

    // Per-model download tracking (latin + multilingual download independently).
    @Published private(set) var downloadingModels: Set<String> = []

    // Per-model download progress 0…1 — the raw `fractionCompleted` from FluidAudio's
    // `downloadVariant` (→ `DownloadUtils.downloadSubdirectory`), which sweeps 0→1 as
    // `(filesDone / totalFiles)`. Drives the Model Library ring so it fills instead of
    // spinning indeterminately. NOTE: this is per-FILE progress, not per-byte — the variant's
    // large weight files dominate wall-clock, so the ring can pause for a while on one file.
    @Published private(set) var downloadProgress: [String: Double] = [:]

    /// Retained per-model download tasks. Holding the handle is what makes a download
    /// cancellable — FluidAudio exposes no cancel API, but its `try await session.download`
    /// honours Swift cooperative cancellation, so cancelling this `Task` aborts the
    /// in-flight transfer.
    private var downloadTasks: [String: Task<Void, Never>] = [:]

    /// FIFO of model ids requested while another variant was already downloading. Drained
    /// one at a time so the two variants don't split bandwidth (and double the stall surface).
    private var downloadQueue: [String] = []

    /// Model ids sitting in `downloadQueue`, surfaced so the row can show a "Queued…" state.
    @Published private(set) var queuedModels: Set<String> = []

    /// Monotonic per-model attempt token. FluidAudio's progress callbacks hop to the main
    /// actor via detached tasks that can outlive their attempt; each captures the token live
    /// at fire time and no-ops if it no longer matches — so a late callback can't resurrect a
    /// finished row or rewind the ring across a retry/cancel/teardown. Bumped per attempt and
    /// on teardown via `invalidateProgressCallbacks(_:)`.
    private var attemptGeneration: [String: Int] = [:]

    @MainActor
    private func invalidateProgressCallbacks(_ modelId: String) {
        attemptGeneration[modelId, default: 0] += 1
    }

    /// Model IDs whose on-disk install passed the metadata-only probe but failed
    /// to load (corrupt or partial download). The Library row surfaces these as
    /// "Re-download" so the user can recover without guessing. Cleared on
    /// `refreshState()` when the on-disk install is fixed (file removed +
    /// re-downloaded) or simply when the model is no longer marked installed.
    @Published private(set) var brokenVariants: Set<String> = []

    @Published var errorMessage: String?

    /// Optional hook called when a downloaded variant is deleted so the
    /// `NemotronProvider`'s in-memory `Runtime` cache can be invalidated for
    /// that variant. Without this hook, a `transcribe` after delete +
    /// re-download would return the stale in-memory bundle.
    /// Set from `TranscriptionPipeline.setNemotronModelManager(_:)`.
    var onVariantInvalidated: ((Variant) async -> Void)?

    private var observation: NSObjectProtocol?
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "NemotronModelManager")

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

    var isDownloading: Bool {
        !downloadingModels.isEmpty
    }

    func isDownloading(_ modelId: String) -> Bool {
        downloadingModels.contains(modelId)
    }

    var isModelInstalled: Bool {
        availableModels.contains { $0.isDownloaded }
    }

    func isModelInstalled(_ modelId: String) -> Bool {
        availableModels.first { $0.id == modelId }?.isDownloaded ?? false
    }

    /// Whether the variant's metadata says "installed" but a load attempt failed
    /// (probably a partial / corrupt install). Drives the Library row's
    /// "Re-download" prompt.
    func isVariantBroken(_ modelId: String) -> Bool {
        brokenVariants.contains(modelId)
    }

    /// Flag a variant as broken — called by the provider when `preloadShared`
    /// fails on a variant the probe considered installed.
    @MainActor
    func markVariantBroken(_ modelId: String) {
        guard Self.variant(forModelId: modelId) != nil else { return }
        brokenVariants.insert(modelId)
    }

    /// Clear a variant from the broken set — called when the file census flips
    /// (download finished, files removed).
    @MainActor
    func clearVariantBroken(_ modelId: String) {
        brokenVariants.remove(modelId)
    }

    // VARIANT FROM MODEL ID:
    // Maps Constants.latinModelId / Constants.multilingualModelId to the on-disk variant.
    // Returns nil for any unrelated model id (e.g. a Parakeet/Qwen3 id).
    //
    // Marked nonisolated because the @MainActor class isolation otherwise pins these
    // pure helpers to the main actor, which would forbid use from the (background)
    // provider / streaming session / router paths.
    nonisolated static func variant(forModelId modelId: String) -> Variant? {
        switch modelId {
        case Constants.latinModelId: return .latin
        case Constants.multilingualModelId: return .multilingual
        default: return nil
        }
    }

    // LANGUAGE FILTER:
    // Public API used by `LanguageSelectionView` to narrow the language dropdown
    // when a Nemotron variant is selected as the mode's model. Returns nil for any
    // non-Nemotron model id so the picker falls through to its existing list.
    nonisolated static func supportedLanguages(forModelId modelId: String) -> [String: String]? {
        switch variant(forModelId: modelId) {
        case .latin: return latinLanguages
        case .multilingual: return multilingualLanguages
        case .none: return nil
        }
    }

    // CACHE DIRECTORY:
    // Computes the path FluidAudio v0.15.2 itself uses inside
    // `StreamingNemotronMultilingualAsrManager.downloadVariant(...)`. Keep in lockstep
    // with that function — if FluidAudio ever exposes a public helper, swap to it.
    nonisolated static func cacheDirectory(for variant: Variant) -> URL {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first
            ?? URL(fileURLWithPath: NSHomeDirectory()).appendingPathComponent("Library/Application Support", isDirectory: true)
        return support
            .appendingPathComponent("FluidAudio", isDirectory: true)
            .appendingPathComponent("Models", isDirectory: true)
            .appendingPathComponent(Constants.repoFolderName, isDirectory: true)
            .appendingPathComponent(variant.folderName, isDirectory: true)
            .appendingPathComponent("\(Constants.chunkMs)ms", isDirectory: true)
    }

    // Mirror FluidAudio's own cache-reuse probe (`metadata.json` only) instead of
    // replicating the full file-layout contract. The strict 5-file probe is
    // brittle: FluidAudio is free to add/rename artifacts and a partial install
    // we still detected as "installed" surfaces as an actionable
    // `markVariantBroken(_:)` flip on the first failed load (see
    // `NemotronProvider.prepareIfNeeded`). The user gets a clear "Re-download"
    // row instead of being silently locked out by a file census drift.
    nonisolated static func variantIsDownloaded(_ variant: Variant) -> Bool {
        let dir = cacheDirectory(for: variant)
        let fm = FileManager.default
        return fm.fileExists(atPath: dir.appendingPathComponent(Constants.metadataFileName).path)
    }

    /// Public façade — same liveness rules as the (nonisolated) `variantIsDownloaded`
    /// helper, addressed by model id. Used by every "is the model installed?"
    /// call site so the rules stay in lockstep across the app.
    nonisolated static func isVariantInstalled(_ modelId: String) -> Bool {
        guard let variant = variant(forModelId: modelId) else { return false }
        return variantIsDownloaded(variant)
    }

    @MainActor
    func refreshState() {
        var models: [NemotronModel] = []

        let latinDir = Self.cacheDirectory(for: .latin)
        let latinDownloaded = Self.variantIsDownloaded(.latin)
        logger.debug("Nemotron latin downloaded=\(latinDownloaded) at \(latinDir.path)")
        models.append(NemotronModel(
            id: Constants.latinModelId,
            name: Constants.latinModelId,
            displayName: Constants.latinDisplayName,
            size: Constants.latinSize,
            notes: Constants.latinNotes,
            supportedLanguages: Self.latinLanguages,
            variant: .latin,
            isNew: true,
            isDownloaded: latinDownloaded,
            localURL: latinDownloaded ? latinDir : nil
        ))

        let multilingualDir = Self.cacheDirectory(for: .multilingual)
        let multilingualDownloaded = Self.variantIsDownloaded(.multilingual)
        logger.debug("Nemotron multilingual downloaded=\(multilingualDownloaded) at \(multilingualDir.path)")
        models.append(NemotronModel(
            id: Constants.multilingualModelId,
            name: Constants.multilingualModelId,
            displayName: Constants.multilingualDisplayName,
            size: Constants.multilingualSize,
            notes: Constants.multilingualNotes,
            supportedLanguages: Self.multilingualLanguages,
            variant: .multilingual,
            isNew: true,
            isDownloaded: multilingualDownloaded,
            localURL: multilingualDownloaded ? multilingualDir : nil
        ))

        availableModels = models

        // Drop broken flags whose on-disk install is no longer detected: either
        // the user deleted the model (so there's nothing to re-download from) or
        // the install state matches expectations again (e.g. just downloaded).
        // We intentionally do NOT auto-clear when isDownloaded == true and the
        // variant is still broken — only a successful load (or an explicit
        // delete + redownload, which lands here via isDownloaded toggling) clears it.
        let installedIds = Set(models.filter { $0.isDownloaded }.map { $0.id })
        brokenVariants = brokenVariants.intersection(installedIds)
    }

    /// Start (or queue) a variant download. Non-async so the View can call it directly
    /// and the retained `Task` becomes the cancel handle. If another variant is already
    /// downloading, this one is queued behind it (one transfer at a time).
    @MainActor
    func startDownload(_ modelId: String) {
        guard Self.variant(forModelId: modelId) != nil else {
            logger.error("startDownload() called with non-Nemotron modelId: \(modelId, privacy: .public)")
            return
        }
        // Already downloading or already queued — ignore the duplicate tap.
        guard downloadTasks[modelId] == nil, !queuedModels.contains(modelId) else { return }

        // Serialize: if another variant is mid-download, queue this one behind it so the
        // two don't split bandwidth (and double the stall surface).
        if !downloadTasks.isEmpty {
            errorMessage = nil
            downloadQueue.append(modelId)
            queuedModels.insert(modelId)
            downloadingModels.insert(modelId)        // row shows a pending ring + cancel (x)
            downloadProgress[modelId] = 0.01
            logger.info("Queued Nemotron download \(modelId, privacy: .public) behind an active download")
            return
        }
        beginDownload(modelId)
    }

    /// Cancel an active or queued download. Active downloads are torn down via cooperative
    /// `Task` cancellation (FluidAudio's `try await session.download` honours it); queued
    /// ones are simply dropped before they start.
    @MainActor
    func cancelDownload(_ modelId: String) {
        // Queued but not yet started: just drop it from the queue.
        if queuedModels.contains(modelId) {
            downloadQueue.removeAll { $0 == modelId }
            queuedModels.remove(modelId)
            downloadingModels.remove(modelId)
            downloadProgress.removeValue(forKey: modelId)
            logger.info("Removed queued Nemotron download \(modelId, privacy: .public)")
            return
        }
        // Active: cancel the retained task. `download(_:)` unwinds silently (cancellation
        // is not surfaced as an error) and `downloadFinished` then drains the queue.
        if let task = downloadTasks[modelId] {
            logger.info("Cancelling Nemotron download \(modelId, privacy: .public)")
            task.cancel()
        }
    }

    @MainActor
    private func beginDownload(_ modelId: String) {
        queuedModels.remove(modelId)
        downloadTasks[modelId] = Task { [weak self] in
            await self?.download(modelId)
            await self?.downloadFinished(modelId)
        }
    }

    /// Called once a download task fully unwinds (success, error, or cancel). Clears the
    /// retained handle and starts the next queued variant, if any.
    @MainActor
    private func downloadFinished(_ modelId: String) {
        downloadTasks.removeValue(forKey: modelId)
        guard !downloadQueue.isEmpty else { return }
        let next = downloadQueue.removeFirst()
        beginDownload(next)
    }

    /// Per-attempt outcome the retry loop branches on. Cancellation is detected separately
    /// via the outer task's `Task.isCancelled`, not encoded here.
    /// The actual download, with bounded auto-retry on transport errors. Private — callers go
    /// through `startDownload(_:)` so the task is retained and cancellable.
    ///
    /// We do NOT run an app-side stall watchdog: FluidAudio's `downloadSubdirectory` reports
    /// only per-file progress (`sharedSession.download(for:)`, no per-byte callbacks), so a
    /// healthy multi-hundred-MB weight file is indistinguishable from a stall while it's in
    /// flight — a fraction/time watchdog would cancel good downloads. Instead we let the
    /// transfer's own request timeout surface as a thrown error and retry that.
    @MainActor
    private func download(_ modelId: String) async {
        guard let variant = Self.variant(forModelId: modelId) else {
            logger.error("download() called with non-Nemotron modelId: \(modelId, privacy: .public)")
            return
        }
        errorMessage = nil
        downloadingModels.insert(modelId)
        queuedModels.remove(modelId)
        // Seed at 0.01 so the ring renders immediately while we wait for the first
        // real progress callback from FluidAudio (network handshake can take a beat).
        downloadProgress[modelId] = 0.01

        logger.info("Starting download for Nemotron \(variant.rawValue, privacy: .public) (\(Constants.chunkMs)ms)")

        var succeeded = false
        var lastError: Error?

        for attempt in 0..<Constants.maxDownloadAttempts {
            if Task.isCancelled { break }
            do {
                try await runDownloadAttempt(modelId: modelId, variant: variant)
                succeeded = true
                break
            } catch is CancellationError {
                break   // user cancelled → silent
            } catch let urlError as URLError where urlError.code == .cancelled {
                break   // user cancelled → silent
            } catch {
                if Task.isCancelled { break }
                lastError = error
                logger.warning("Nemotron \(variant.rawValue, privacy: .public) attempt \(attempt + 1)/\(Constants.maxDownloadAttempts) failed: \(error.localizedDescription, privacy: .public)")
                // Retire this attempt's callbacks so a straggler can't reinsert stale progress.
                invalidateProgressCallbacks(modelId)
                if attempt < Constants.maxDownloadAttempts - 1 {
                    let backoff = Constants.retryBackoffSeconds[min(attempt, Constants.retryBackoffSeconds.count - 1)]
                    try? await Task.sleep(nanoseconds: backoff * 1_000_000_000)
                }
            }
        }

        if Task.isCancelled {
            logger.info("Nemotron \(variant.rawValue, privacy: .public) download cancelled")
        } else if succeeded {
            logger.info("Nemotron \(variant.rawValue, privacy: .public) downloaded successfully")
            // Fresh files on disk — any prior "broken" flag is stale now.
            brokenVariants.remove(modelId)
        } else {
            let message = lastError?.localizedDescription ?? "Download failed — check your connection and try again."
            logger.error("Nemotron \(variant.rawValue, privacy: .public) download failed after \(Constants.maxDownloadAttempts) attempts: \(message, privacy: .public)")
            errorMessage = message
        }

        // Retire any in-flight progress callbacks before we wipe state, so a straggler
        // can't re-insert a ghost ring after teardown.
        invalidateProgressCallbacks(modelId)
        downloadingModels.remove(modelId)
        downloadProgress.removeValue(forKey: modelId)
        refreshState()
    }

    /// One `downloadVariant` invocation. Throws on transport failure (caller retries with
    /// backoff) and on cancellation (caller treats as a silent user-cancel). Updates the
    /// ring/caption from FluidAudio's per-file progress.
    @MainActor
    private func runDownloadAttempt(modelId: String, variant: Variant) async throws {
        // Stamp a fresh callback generation so stragglers from a prior attempt are ignored.
        attemptGeneration[modelId, default: 0] += 1
        let generation = attemptGeneration[modelId] ?? 0

        // `downloadVariant` only downloads (no model preload); the Provider path preloads
        // later. FluidAudio invokes `progressHandler` off a background task, so we bounce each
        // update back to the MainActor.
        _ = try await StreamingNemotronMultilingualAsrManager.downloadVariant(
            languageCode: variant.downloadLanguageHint,
            chunkMs: Constants.chunkMs,
            to: nil,
            progressHandler: { [weak self] snapshot in
                // `downloadSubdirectory` reports per-file progress sweeping 0→1; floor at 0.01
                // so the ring never visually empties. The ring is the only UI surface — no
                // caption — so we just mirror the fraction.
                let fraction = min(max(snapshot.fractionCompleted, 0.01), 1.0)
                Task { @MainActor [weak self] in
                    guard let self else { return }
                    // Ignore stragglers from a retired attempt (retry / cancel / teardown).
                    guard self.attemptGeneration[modelId] == generation else { return }
                    self.downloadProgress[modelId] = fraction
                }
            }
        )
    }

    @MainActor
    func deleteModel(_ modelId: String) {
        guard let variant = Self.variant(forModelId: modelId) else {
            logger.error("deleteModel() called with non-Nemotron modelId: \(modelId, privacy: .public)")
            return
        }
        let directory = Self.cacheDirectory(for: variant)
        do {
            if FileManager.default.fileExists(atPath: directory.path) {
                try FileManager.default.removeItem(at: directory)
                logger.info("Removed Nemotron \(variant.rawValue, privacy: .public) at \(directory.path, privacy: .public)")
            }
        } catch {
            logger.error("Failed to delete Nemotron \(variant.rawValue, privacy: .public): \(error.localizedDescription, privacy: .public)")
            errorMessage = error.localizedDescription
        }
        refreshState()
        // Drop the in-memory cached bundle for this variant so the next session
        // re-reads from (now-empty / re-downloaded) disk instead of returning
        // the stale shared models.
        if let hook = onVariantInvalidated {
            Task { await hook(variant) }
        }
    }
}
