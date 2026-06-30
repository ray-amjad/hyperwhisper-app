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
        static let sizeDescription = "~1.3 GB"
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

    var isModelInstalled: Bool {
        isDownloaded
    }

    func isModelInstalled(_ modelId: String) -> Bool {
        modelId == Constants.modelId && isDownloaded
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
            _ = try await Qwen3AsrModels.download(
                variant: .f32,
                progressHandler: { progress in
                    Task { @MainActor in
                        // Qwen3's download runs no compile phase, so FluidAudio's fraction tops out
                        // at 0.5 (the download half of its 0–0.5 / 0.5–1.0 contract). Double it to
                        // fill the ring; report() clamps to ≤ 1.0.
                        controller.report(Constants.modelId, fraction: progress.fractionCompleted * 2.0)
                    }
                }
            )
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
