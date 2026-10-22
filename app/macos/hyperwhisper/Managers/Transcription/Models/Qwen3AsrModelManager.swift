import Foundation
import Combine
import AppKit
import os
import FluidAudio

@MainActor
final class Qwen3AsrModelManager: ObservableObject {

    enum Constants {
        static let modelId = "qwen3-asr-0.6b"
        static let displayName = "Qwen3 ASR"
        // Matches `downloadBytes` below; `ModelSizeLabelTests` pins the two together.
        static let sizeDescription = "~1.9 GB"

        /// Bytes the trimmed f32 download fetches: the sum of every file under
        /// `f32/` in `FluidInference/qwen3-asr-0.6b-coreml` that
        /// `shouldSkipRemotePath` keeps (measured 2026-10-07). The untrimmed
        /// `Qwen3AsrModels.download` pulled 4.19 GB.
        static let downloadBytes: Int64 = 1_880_834_670

        /// Precision folder in the Hugging Face repo, and the last path
        /// component of `Qwen3AsrModels.defaultCacheDirectory(variant: .f32)`.
        static let remoteSubdirectory = "f32"

        /// The entries directly under `f32/` that FluidAudio 0.15.2 reads:
        /// `Qwen3AsrModels.load` opens the two `.mlmodelc` bundles, the
        /// embeddings and `vocab.json`, and `modelsExist` probes the same set.
        /// `metadata.json` is 2 KB and `downloadRepo` always fetched it.
        static let requiredRemoteEntries: Set<String> = [
            ModelNames.Qwen3ASR.audioEncoderFile,
            ModelNames.Qwen3ASR.decoderStatefulFile,
            ModelNames.Qwen3ASR.embeddingsFile,
            "vocab.json",
            "metadata.json",
        ]

        /// Skip predicate for `DownloadUtils.downloadSubdirectory`. The repo also
        /// ships an `.mlpackage` source beside each `.mlmodelc` and the older
        /// `qwen3_asr_audio_encoder.mlmodelc`, 2.31 GB in all. `load` takes a
        /// `.mlmodelc` before it ever looks for an `.mlpackage`, and nothing
        /// reads the old encoder. `downloadRepo` keeps every `.json` and `.bin`
        /// file in the folder, and each of those bundles holds a `weight.bin`,
        /// so the untrimmed download pulled all of them.
        static func shouldSkipRemotePath(_ path: String) -> Bool {
            let components = path.split(separator: "/", omittingEmptySubsequences: true)
            guard components.first.map(String.init) == remoteSubdirectory else { return true }
            guard components.count >= 2 else { return false }
            return !requiredRemoteEntries.contains(String(components[1]))
        }
    }

    @Published private(set) var isDownloaded: Bool = false
    @Published var errorMessage: String?

    // Owns the retained-Task + progress + cancel machinery. Qwen3 has a single
    // model, so the controller is keyed by the one `Constants.modelId`.
    let downloads = DownloadController<String>()

    // BACKWARD-COMPATIBLE FORWARDERS:
    // `qwen3AsrRows()` reads these; keep them stable atop the controller.
    var isDownloading: Bool { downloads.isDownloading }
    var downloadProgress: Double? { downloads.progress[Constants.modelId] }

    /// Optional hook called when the downloaded model is deleted so the
    /// `Qwen3AsrProvider`'s in-memory `Runtime` cache can be invalidated.
    /// Without this hook, a `transcribe` after delete + re-download (which may
    /// land a different variant on disk) would return the stale in-memory
    /// manager loaded from the now-deleted directory.
    /// Set from `TranscriptionPipeline.setQwen3AsrModelManager(_:)`.
    var onModelInvalidated: (() async -> Void)?

    private var observation: NSObjectProtocol?
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "Qwen3AsrModelManager")

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

    @MainActor
    func refreshState() {
        guard #available(macOS 15.0, *) else {
            isDownloaded = false
            return
        }
        let f32Exists = Qwen3AsrModels.modelsExist(at: Qwen3AsrModels.defaultCacheDirectory(variant: .f32))
        let int8Exists = Qwen3AsrModels.modelsExist(at: Qwen3AsrModels.defaultCacheDirectory(variant: .int8))
        let newValue = f32Exists || int8Exists
        if isDownloaded != newValue { isDownloaded = newValue }
        logger.debug("Qwen3 ASR f32=\(f32Exists) int8=\(int8Exists)")
    }

    // START DOWNLOAD:
    // Retains the download as a cancellable `Task` via `DownloadController`.
    @MainActor
    func startDownload() {
        guard #available(macOS 15.0, *) else { return }
        downloads.start(Constants.modelId) { [weak self] controller in
            await self?.runDownload(controller)
        }
    }

    /// Cancel an in-flight download via cooperative `Task` cancellation.
    @MainActor
    func cancelDownload() {
        logger.info("Cancelling Qwen3 ASR download")
        downloads.cancel(Constants.modelId)
    }

    @MainActor
    private func runDownload(_ controller: DownloadController<String>) async {
        guard #available(macOS 15.0, *) else { return }
        errorMessage = nil
        logger.info("Starting Qwen3 ASR f32 download")

        do {
            // `Qwen3AsrModels.download` takes no file filter, so call the
            // downloader under it with one. Files land at
            // `<repo folder>/f32/...`, the same place `download` put them.
            let repoDirectory = Qwen3AsrModels.defaultCacheDirectory(variant: .f32)
                .deletingLastPathComponent()
            try await DownloadUtils.downloadSubdirectory(
                .qwen3Asr,
                subdirectory: Constants.remoteSubdirectory,
                to: repoDirectory,
                progressHandler: { progress in
                    Task { @MainActor in
                        // `downloadSubdirectory` sweeps 0→1 by completed file count
                        // (no 0–0.5 download half as in `downloadRepo`).
                        controller.report(Constants.modelId, fraction: progress.fractionCompleted)
                    }
                },
                shouldSkip: { Constants.shouldSkipRemotePath($0) }
            )
            guard Qwen3AsrModels.modelsExist(at: Qwen3AsrModels.defaultCacheDirectory(variant: .f32)) else {
                throw Qwen3AsrError.modelNotFound(Constants.remoteSubdirectory)
            }
            logger.info("Qwen3 ASR downloaded successfully")
        } catch is CancellationError {
            logger.info("Qwen3 ASR download cancelled")
        } catch let urlError as URLError where urlError.code == .cancelled {
            logger.info("Qwen3 ASR download cancelled")
        } catch {
            logger.error("Failed to download Qwen3 ASR: \(error.localizedDescription, privacy: .public)")
            errorMessage = error.localizedDescription
        }

        refreshState()
    }

    @MainActor
    func deleteModel() {
        guard #available(macOS 15.0, *) else { return }
        let f32Dir = Qwen3AsrModels.defaultCacheDirectory(variant: .f32)
        let int8Dir = Qwen3AsrModels.defaultCacheDirectory(variant: .int8)

        for directory in [f32Dir, int8Dir] {
            do {
                if FileManager.default.fileExists(atPath: directory.path) {
                    try FileManager.default.removeItem(at: directory)
                    logger.info("Removed Qwen3 ASR at \(directory.path, privacy: .public)")
                }
            } catch {
                logger.error("Failed to delete Qwen3 ASR: \(error.localizedDescription, privacy: .public)")
                errorMessage = error.localizedDescription
            }
        }
        refreshState()
        // Drop the in-memory cached manager so the next transcription re-reads
        // from (now-empty / re-downloaded) disk instead of serving the stale
        // manager loaded from a deleted directory.
        if let hook = onModelInvalidated {
            Task { await hook() }
        }
    }
}
