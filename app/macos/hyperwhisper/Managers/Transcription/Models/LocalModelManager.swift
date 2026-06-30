//
//  LocalModelManager.swift
//  hyperwhisper
//
//  Manages on-device language model downloads stored in Application Support.
//  Handles progress tracking, checksum verification, and deletion for local post-processing models.
//

import Foundation
import Combine
import os

/// Represents a downloadable local model definition combined with runtime state.
struct LocalModel: Identifiable, Equatable {
    let id: String
    let displayName: String
    let filename: String
    let sizeDescription: String
    let sizeInBytes: Int64
    let sha256: String
    let notes: String
    let downloadURL: URL
    let isRecommended: Bool
    var localURL: URL?

    var isDownloaded: Bool {
        localURL != nil
    }
}

/// Tracks checksum verification status for a downloaded model.
enum LocalModelChecksumState: Equatable {
    case pending
    case verifying
    case valid
    case invalid(expected: String, actual: String)
}

/// Central manager for local LLM downloads. Handles progress, checksum verification, and deletion.
@MainActor
class LocalModelManager: NSObject, ObservableObject {
    // MARK: Published State
    @Published private(set) var availableModels: [LocalModel] = []
    @Published private(set) var downloadedModels: [LocalModel] = []
    @Published private(set) var downloadProgress: [String: Double] = [:]
    @Published private(set) var downloadingModels: Set<String> = []
    @Published private(set) var checksumStates: [String: LocalModelChecksumState] = [:]
    private var lastLoggedProgress: [String: Int] = [:]
    @Published private(set) var errorMessage: String?

    // MARK: Internal
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalModelManager")
    private static let staticLogger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalModelManager")

    private lazy var session: URLSession = {
        let configuration = URLSessionConfiguration.default
        configuration.waitsForConnectivity = true
        configuration.timeoutIntervalForRequest = 60 * 60
        configuration.timeoutIntervalForResource = 60 * 60 * 12
        return URLSession(configuration: configuration, delegate: self, delegateQueue: nil)
    }()

    private var downloadTasks: [String: URLSessionDownloadTask] = [:]
    private var taskModelIds: [Int: String] = [:]

    // MARK: Paths
    // `nonisolated` so the non-MainActor mode-repair pass can resolve model file
    // paths to check which weights are present on disk.
    nonisolated static let modelsDirectory: URL = {
        let support = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return support
            .appendingPathComponent("hyperwhisper", isDirectory: true)
            .appendingPathComponent("models", isDirectory: true)
    }()

    // MARK: Catalog
    private struct CatalogItem {
        let id: String
        let displayName: String
        let filename: String
        let sizeDescription: String
        let sizeInBytes: Int64
        let sha256: String
        let notes: String
        let downloadURL: URL
        let isRecommended: Bool
    }

    private static let catalog: [CatalogItem] = [
        CatalogItem(
            id: "gemma-4-E2B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E2B (Recommended)",
            filename: "gemma-4-E2B-it-Q4_K_M.gguf",
            sizeDescription: "3.1 GB",
            sizeInBytes: 3_100_000_000,
            sha256: "9378bc471710229ef165709b62e34bfb62231420ddaf6d729e727305b5b8672d",
            notes: "April 2026. Google. Fast and accurate, great all-rounder.",
            // Pinned to the revision the sha256 above was taken from (mutable `main` would break verification).
            downloadURL: URL(string: "https://huggingface.co/unsloth/gemma-4-E2B-it-GGUF/resolve/ecc8b33b2c50598815e4b0f7cea6088e3ae7adb8/gemma-4-E2B-it-Q4_K_M.gguf")!,
            isRecommended: true
        ),
        CatalogItem(
            id: "gemma-4-E4B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 E4B",
            filename: "gemma-4-E4B-it-Q4_K_M.gguf",
            sizeDescription: "5 GB",
            sizeInBytes: 5_000_000_000,
            sha256: "519b9793ed6ce0ff530f1b7c96e848e08e49e7af4d57bb97f76215963a54146d",
            notes: "April 2026. Google. Higher quality with more detail.",
            downloadURL: URL(string: "https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/resolve/e1d90e5fb9f61d8dc71ef016580784a054e5c787/gemma-4-E4B-it-Q4_K_M.gguf")!,
            isRecommended: false
        ),
        CatalogItem(
            id: "gemma-4-12b-it-Q4_K_M.gguf",
            displayName: "Gemma 4 12B",
            filename: "gemma-4-12b-it-Q4_K_M.gguf",
            sizeDescription: "7.1 GB",
            sizeInBytes: 7_120_000_000,
            sha256: "43fec98c5102b1c446b4ddd0a9439f1db3a2e1f2e0b8cd143ce1ea619a9403d6",
            notes: "June 2026. Google. Mid-size dense model, balances quality and speed on 16 GB Macs.",
            downloadURL: URL(string: "https://huggingface.co/unsloth/gemma-4-12b-it-GGUF/resolve/3249fa54d5efa384afc552cc6700ad091efd5c39/gemma-4-12b-it-Q4_K_M.gguf")!,
            isRecommended: false
        ),
        CatalogItem(
            id: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            displayName: "Gemma 4 26B MoE",
            filename: "gemma-4-26B-A4B-it-UD-Q4_K_M.gguf",
            sizeDescription: "16.9 GB",
            sizeInBytes: 16_900_000_000,
            sha256: "34c746b1d50ab813e29cd46c4796e3f43c741901a582f93a67b55b9fc9687b35",
            notes: "April 2026. Google. 26B Mixture-of-Experts with 4B active parameters.",
            downloadURL: URL(string: "https://huggingface.co/unsloth/gemma-4-26B-A4B-it-GGUF/resolve/3bb10d594514ef4edb7f3a65d41a7e4eb8c5767a/gemma-4-26B-A4B-it-UD-Q4_K_M.gguf")!,
            isRecommended: false
        ),
        CatalogItem(
            id: "gemma-4-31B-it-Q4_K_M.gguf",
            displayName: "Gemma 4 31B Dense",
            filename: "gemma-4-31B-it-Q4_K_M.gguf",
            sizeDescription: "18.3 GB",
            sizeInBytes: 18_300_000_000,
            sha256: "9fdf3dc8b0384830b4402d151388c140bd8eb2abf8d60588d8224231198254a1",
            notes: "April 2026. Google. Highest quality, requires 18+ GB RAM.",
            downloadURL: URL(string: "https://huggingface.co/unsloth/gemma-4-31B-it-GGUF/resolve/8906b3db2e669a0b1d6293c315d3f9fbf934a86d/gemma-4-31B-it-Q4_K_M.gguf")!,
            isRecommended: false
        )
    ]

    /// Ids of every model the app knows how to download. `nonisolated` so the
    /// non-MainActor mode-repair pass in `PersistenceController` can tell a
    /// genuinely dangling model reference (turn post-processing off) apart from a
    /// known model that simply isn't downloaded yet (offer to download it).
    nonisolated static let catalogModelIds: Set<String> = Set(catalog.map { $0.id })

    // MARK: Initialization
    override init() {
        super.init()
        createModelsDirectoryIfNeeded()
        refreshCatalog()
    }

    // MARK: Public API

    /// Refresh available and downloaded model lists by checking the models directory.
    func refreshCatalog() {
        let directory = Self.modelsDirectory
        logger.info("Refreshing local model catalog at: \(directory.path, privacy: .public)")

        var updated: [LocalModel] = []
        for item in Self.catalog {
            let url = directory.appendingPathComponent(item.filename)
            var model = LocalModel(
                id: item.id,
                displayName: item.displayName,
                filename: item.filename,
                sizeDescription: item.sizeDescription,
                sizeInBytes: item.sizeInBytes,
                sha256: item.sha256,
                notes: item.notes,
                downloadURL: item.downloadURL,
                isRecommended: item.isRecommended,
                localURL: nil
            )
            let fileExists = FileManager.default.fileExists(atPath: url.path)

            if fileExists {
                model.localURL = url
                if checksumStates[model.id] == nil {
                    checksumStates[model.id] = .pending
                }
            } else {
                checksumStates.removeValue(forKey: model.id)
            }
            updated.append(model)
        }

        availableModels = updated
        downloadedModels = updated.filter { $0.isDownloaded }
        logger.info("Local model catalog: \(self.availableModels.count) available, \(self.downloadedModels.count) downloaded")
    }

    /// Kick off a download for the given model.
    func downloadModel(_ modelId: String, forceRedownload: Bool = false) {
        guard let model = availableModels.first(where: { $0.id == modelId }) else {
            logger.error("Cannot download - model not found in catalog: \(modelId, privacy: .public)")
            return
        }

        if downloadingModels.contains(modelId) {
            logger.debug("Ignoring duplicate download for \(modelId, privacy: .public)")
            return
        }

        let destinationURL = Self.modelsDirectory.appendingPathComponent(model.filename)
        if FileManager.default.fileExists(atPath: destinationURL.path) {
            guard forceRedownload else {
                logger.info("Model already exists on disk: \(model.filename, privacy: .public)")
                return
            }
            do {
                try FileManager.default.removeItem(at: destinationURL)
            } catch {
                logger.error("Failed to remove existing model before redownload: \(error, privacy: .public)")
                errorMessage = "Could not remove existing model for re-download"
                return
            }
        }

        do {
            try FileManager.default.createDirectory(at: Self.modelsDirectory, withIntermediateDirectories: true)
        } catch {
            logger.error("Failed to create models directory before download: \(error, privacy: .public)")
            errorMessage = "Could not create models directory"
            return
        }

        // Pre-flight free-space check: the file is downloaded to a temp area and
        // then moved into place, so require headroom for both copies plus a small
        // margin before starting a multi-GB download that would otherwise fill the
        // boot volume and only surface as a URLSession error after disk exhaustion.
        let requiredBytes = Int64(Double(model.sizeInBytes) * 1.15)
        if let values = try? Self.modelsDirectory.resourceValues(forKeys: [.volumeAvailableCapacityForImportantUsageKey]),
           let freeBytes = values.volumeAvailableCapacityForImportantUsage,
           freeBytes < requiredBytes {
            let neededString = ByteCountFormatter.string(fromByteCount: requiredBytes - freeBytes, countStyle: .file)
            logger.error("Insufficient disk space for \(model.filename, privacy: .public): need \(requiredBytes) bytes, \(freeBytes) free")
            errorMessage = "Not enough disk space to download \(model.displayName) (\(model.sizeDescription)). Free up about \(neededString) and try again."
            return
        }

        let task = session.downloadTask(with: model.downloadURL)
        downloadTasks[modelId] = task
        taskModelIds[task.taskIdentifier] = modelId
        downloadingModels.insert(modelId)
        downloadProgress[modelId] = 0
        checksumStates[modelId] = .pending
        errorMessage = nil
        logger.info("Starting download: \(model.filename, privacy: .public)")
        task.resume()
    }

    /// Cancel an in-progress download.
    func cancelDownload(_ modelId: String) {
        guard let task = downloadTasks[modelId] else { return }
        task.cancel()
        downloadTasks[modelId] = nil
        downloadingModels.remove(modelId)
        downloadProgress[modelId] = nil
        taskModelIds[task.taskIdentifier] = nil
        checksumStates[modelId] = .pending
        logger.info("Cancelled download: \(modelId, privacy: .public)")
    }

    /// Remove a downloaded model from disk.
    func deleteModel(_ modelId: String) {
        guard let model = availableModels.first(where: { $0.id == modelId }),
              let url = model.localURL else { return }
        do {
            try FileManager.default.removeItem(at: url)
            logger.info("Removed local model: \(model.filename, privacy: .public)")
            checksumStates.removeValue(forKey: modelId)
            refreshCatalog()
        } catch {
            logger.error("Failed to delete local model: \(error.localizedDescription, privacy: .public)")
            errorMessage = "Failed to delete model \(model.displayName)."
        }
    }

    /// Force checksum validation for a model already on disk.
    func validateChecksum(for modelId: String) {
        guard let model = availableModels.first(where: { $0.id == modelId }),
              let url = model.localURL else { return }
        checksumStates[modelId] = .verifying
        Task.detached { [weak self] in
            guard let self else { return }
            let result = self.computeChecksum(for: url)
            await MainActor.run {
                switch result {
                case .success(let digest):
                    self.updateChecksumState(for: modelId, expected: model.sha256, actual: digest)
                case .failure(let error):
                    self.logger.error("Checksum validation failed: \(error.localizedDescription, privacy: .public)")
                    self.errorMessage = "Could not verify checksum for \(model.displayName)."
                    self.checksumStates[modelId] = .invalid(expected: model.sha256, actual: "error")
                }
            }
        }
    }

    // MARK: Helpers
    private func createModelsDirectoryIfNeeded() {
        do {
            if !FileManager.default.fileExists(atPath: Self.modelsDirectory.path) {
                try FileManager.default.createDirectory(
                    at: Self.modelsDirectory,
                    withIntermediateDirectories: true,
                    attributes: nil
                )
                logger.info("Created local models directory: \(Self.modelsDirectory.path, privacy: .public)")
            }
        } catch {
            logger.error("Failed to create local models directory: \(error, privacy: .public)")
        }
    }

    private func finishDownload(for modelId: String, tempURL: URL) {
        guard let model = availableModels.first(where: { $0.id == modelId }) else {
            logger.error("Model not found in catalog for ID: \(modelId, privacy: .public)")
            cleanupAfterDownload(modelId: modelId)
            return
        }

        let destinationURL = Self.modelsDirectory.appendingPathComponent(model.filename)

        do {
            try FileManager.default.createDirectory(at: Self.modelsDirectory, withIntermediateDirectories: true)
        } catch {
            logger.error("Failed to create models directory: \(error, privacy: .public)")
            errorMessage = "Could not create models directory: \(error.localizedDescription)"
            cleanupAfterDownload(modelId: modelId)
            return
        }

        if FileManager.default.fileExists(atPath: destinationURL.path) {
            do {
                try FileManager.default.removeItem(at: destinationURL)
            } catch {
                logger.error("Failed to remove existing file: \(error, privacy: .public)")
                errorMessage = "Could not replace existing model: \(error.localizedDescription)"
                cleanupAfterDownload(modelId: modelId)
                return
            }
        }

        do {
            try FileManager.default.moveItem(at: tempURL, to: destinationURL)
            logger.info("Successfully saved local model: \(destinationURL.path, privacy: .public)")
        } catch {
            logger.error("Failed to move model file: \(error, privacy: .public)")
            errorMessage = "Could not save model \(model.displayName): \(error.localizedDescription)"
            if FileManager.default.fileExists(atPath: tempURL.path) {
                try? FileManager.default.removeItem(at: tempURL)
            }
            cleanupAfterDownload(modelId: modelId)
            return
        }

        checksumStates[modelId] = .verifying
        Task.detached { [weak self] in
            guard let self else { return }
            let result = self.computeChecksum(for: destinationURL)
            await MainActor.run {
                switch result {
                case .success(let digest):
                    self.updateChecksumState(for: modelId, expected: model.sha256, actual: digest)
                    self.refreshCatalog()
                case .failure(let error):
                    self.logger.error("Checksum computation failed: \(error.localizedDescription, privacy: .public)")
                    self.errorMessage = "Checksum failed for \(model.displayName)."
                    self.checksumStates[modelId] = .invalid(expected: model.sha256, actual: "error")
                    try? FileManager.default.removeItem(at: destinationURL)
                    self.refreshCatalog()
                }
                self.cleanupAfterDownload(modelId: modelId)
            }
        }
    }

    private func cleanupAfterDownload(modelId: String) {
        downloadTasks[modelId] = nil
        downloadingModels.remove(modelId)
        downloadProgress[modelId] = nil
        lastLoggedProgress[modelId] = nil
        if let taskId = taskModelIds.first(where: { $0.value == modelId })?.key {
            taskModelIds[taskId] = nil
        }
    }

    private func updateChecksumState(for modelId: String, expected: String, actual: String) {
        let trimmedExpected = expected.trimmingCharacters(in: .whitespacesAndNewlines)

        guard !trimmedExpected.isEmpty else {
            // No expected checksum is a catalog configuration bug — never report it as a
            // verified download. Keep the file (it may be fine) but surface the failure.
            checksumStates[modelId] = .invalid(expected: "<not configured>", actual: actual)
            logger.warning("No expected checksum configured for \(modelId, privacy: .public); cannot verify download. actual=\(actual, privacy: .public)")
            return
        }

        if trimmedExpected.lowercased() == actual.lowercased() {
            checksumStates[modelId] = .valid
            logger.info("Checksum valid for \(modelId, privacy: .public)")
        } else {
            checksumStates[modelId] = .invalid(expected: trimmedExpected, actual: actual)
            errorMessage = "Checksum mismatch for \(modelId)."
            logger.error("Checksum mismatch for \(modelId, privacy: .public) expected=\(trimmedExpected, privacy: .public) actual=\(actual, privacy: .public)")
            if let model = availableModels.first(where: { $0.id == modelId }), let url = model.localURL {
                try? FileManager.default.removeItem(at: url)
                refreshCatalog()
            }
        }
    }

    nonisolated private func computeChecksum(for url: URL) -> Result<String, Error> {
        do {
            let checksum = try ChecksumVerifier.sha256(of: url)
            return .success(checksum)
        } catch {
            return .failure(error)
        }
    }
}

// MARK: URLSessionDownloadDelegate
extension LocalModelManager: URLSessionDownloadDelegate {
    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didWriteData bytesWritten: Int64, totalBytesWritten: Int64, totalBytesExpectedToWrite: Int64) {
        Task { @MainActor in
            guard let modelId = self.taskModelIds[downloadTask.taskIdentifier] else { return }
            let denominator: Double
            if totalBytesExpectedToWrite > 0 {
                denominator = Double(totalBytesExpectedToWrite)
            } else if let size = self.availableModels.first(where: { $0.id == modelId })?.sizeInBytes, size > 0 {
                denominator = Double(size)
            } else {
                denominator = Double(totalBytesWritten)
            }
            let progress = denominator > 0 ? Double(totalBytesWritten) / denominator : 0
            let currentPercentage = Int(progress * 100)
            let previousProgress = self.lastLoggedProgress[modelId] ?? -1

            // Throttle UI updates: only update when integer percentage changes
            guard currentPercentage != previousProgress else { return }
            self.downloadProgress[modelId] = min(max(progress, 0), 1)
            self.lastLoggedProgress[modelId] = currentPercentage

            // Log progress at 25%, 50%, 75%, and 100%
            if currentPercentage >= 25 && previousProgress < 25 {
                self.logger.info("Download progress 25% for \(modelId, privacy: .public)")
            } else if currentPercentage >= 50 && previousProgress < 50 {
                self.logger.info("Download progress 50% for \(modelId, privacy: .public)")
            } else if currentPercentage >= 75 && previousProgress < 75 {
                self.logger.info("Download progress 75% for \(modelId, privacy: .public)")
            } else if currentPercentage >= 100 && previousProgress < 100 {
                self.logger.info("Download complete 100% for \(modelId, privacy: .public)")
            }
        }
    }

    nonisolated func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, didFinishDownloadingTo location: URL) {
        let callbackLogger = Logger(subsystem: "com.hyperwhisper.app", category: "LocalModelManager")
        let persistentTempURL = FileManager.default.temporaryDirectory
            .appendingPathComponent("hyperwhisper_llm_download_\(UUID().uuidString)")
            .appendingPathExtension("tmp")

        do {
            try FileManager.default.moveItem(at: location, to: persistentTempURL)
            callbackLogger.info("Moved download to temp location: \(persistentTempURL.lastPathComponent, privacy: .public)")

            Task { @MainActor in
                guard let modelId = self.taskModelIds[downloadTask.taskIdentifier] else {
                    self.logger.error("Model ID not found for completed download task")
                    try? FileManager.default.removeItem(at: persistentTempURL)
                    return
                }
                self.finishDownload(for: modelId, tempURL: persistentTempURL)
            }
        } catch {
            callbackLogger.error("Failed to move downloaded file: \(error, privacy: .public)")
            Task { @MainActor in
                guard let modelId = self.taskModelIds[downloadTask.taskIdentifier] else { return }
                self.errorMessage = "Failed to save downloaded model: \(error.localizedDescription)"
                self.cleanupAfterDownload(modelId: modelId)
            }
        }
    }

    nonisolated func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        Task { @MainActor in
            guard let modelId = self.taskModelIds[task.taskIdentifier] else { return }
            if let error = error as NSError?, error.code != NSURLErrorCancelled {
                self.logger.error("Download failed: \(modelId, privacy: .public) - \(error.localizedDescription, privacy: .public)")

                if error.domain == NSURLErrorDomain {
                    switch error.code {
                    case NSURLErrorNotConnectedToInternet:
                        self.errorMessage = "No internet connection"
                    case NSURLErrorTimedOut:
                        self.errorMessage = "Download timed out for \(modelId)"
                    case NSURLErrorCannotFindHost, NSURLErrorCannotConnectToHost:
                        self.errorMessage = "Cannot connect to download server"
                    case NSURLErrorNetworkConnectionLost:
                        self.errorMessage = "Network connection lost during download"
                    default:
                        self.errorMessage = "Download failed for \(modelId): \(error.localizedDescription)"
                    }
                } else {
                    self.errorMessage = "Download failed for \(modelId): \(error.localizedDescription)"
                }

                SentryService.capture(
                    error: error,
                    message: "Local model download failed",
                    extras: [
                        "model_id": modelId,
                        "error_domain": error.domain,
                        "error_code": error.code,
                        "user_message": self.errorMessage ?? ""
                    ],
                    tags: [
                        "component": "LocalModelManager",
                        "operation": "download"
                    ],
                    fingerprint: ["local-model-download-failed", error.domain, String(error.code)]
                )
            }
            self.cleanupAfterDownload(modelId: modelId)
        }
    }
}
