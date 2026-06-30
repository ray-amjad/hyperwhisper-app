//
//  WhisperModelManager.swift
//  hyperwhisper
//
//  Manages whisper.cpp model downloads and storage
//  Models are stored as .bin files from Hugging Face
//

import Foundation
import Combine
import os
import AppKit  // For NSApplication notifications
import Atomics

/// Represents a whisper.cpp model
struct WhisperCppModel: Identifiable, Equatable {
    let id = UUID()
    let name: String           // e.g., "tiny", "base.en"
    let displayName: String     // e.g., "Tiny", "Base (English)"
    let filename: String        // e.g., "ggml-tiny.bin"
    let size: String           // e.g., "39 MB"
    let sizeInBytes: Int64     // Actual size in bytes
    let isEnglishOnly: Bool    // True for .en models
    let url: URL?              // Local URL if downloaded
    
    /// Download URL on Hugging Face
    var downloadURL: String {
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/\(filename)"
    }
    
    /// Check if this model is downloaded
    var isDownloaded: Bool {
        url != nil
    }
}

/// Manages whisper.cpp models
class WhisperModelManager: NSObject, ObservableObject {
    // MARK: - Published Properties
    
    /// Available models that can be downloaded
    @Published var availableModels: [WhisperCppModel] = []
    
    /// Downloaded models ready for use
    @Published var downloadedModels: [WhisperCppModel] = []
    
    /// Download progress by model name
    @Published var downloadProgress: [String: Double] = [:]
    
    /// Currently downloading models
    @Published var downloadingModels: Set<String> = []
    
    /// Error messages
    @Published var errorMessage: String?
    
    // MARK: - Properties
    
    /// Notification observers
    private var notificationObservers: [NSObjectProtocol] = []
    
    /// Models directory - simplified path
    static let modelsDirectory: URL = {
        let appSupport = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask).first!
        return appSupport
            .appendingPathComponent("hyperwhisper")  // lowercase
            .appendingPathComponent("models")
    }()
    
    /// Logger
    private let logger = Logger(subsystem: "com.hyperwhisper.app", category: "WhisperModelManager")
    
    /// URL session for downloads
    private lazy var session: URLSession = {
        let config = URLSessionConfiguration.default
        config.waitsForConnectivity = true
        config.timeoutIntervalForRequest = 60 * 60
        config.timeoutIntervalForResource = 60 * 60 * 12
        return URLSession(configuration: config, delegate: self, delegateQueue: nil)
    }()
    
    /// Serial queue that confines all access to the download-tracking dictionaries below.
    /// URLSession delegate callbacks run on a background queue (delegateQueue: nil), while
    /// downloadFile/cancelDownload run on the main thread, so every read/write of these
    /// non-Sendable dictionaries MUST go through this queue to avoid a data race.
    private let stateQueue = DispatchQueue(label: "com.hyperwhisper.WhisperModelManager.state")

    /// Active download tasks
    private var downloadTasks: [String: URLSessionDownloadTask] = [:]

    /// Download completions by task identifier
    private var downloadCompletions: [Int: (URL?, Error?) -> Void] = [:]

    /// Model names by task identifier
    private var taskModelNames: [Int: String] = [:]
    private var lastReportedProgress: [String: Int] = [:]
    
    // MARK: - Model Definitions
    
    /// Map WhisperModel enum values to filenames
    /// This provides a single source of truth for model names
    private static let modelFilenameMap: [String: String] = [
        // Models paired by size (multilingual + English-only)
        "tiny": "ggml-tiny.bin",
        "tiny.en": "ggml-tiny.en.bin",
        "base": "ggml-base.bin",
        "base.en": "ggml-base.en.bin",
        "small": "ggml-small.bin",
        "small.en": "ggml-small.en.bin",
        "medium": "ggml-medium.bin",
        "medium.en": "ggml-medium.en.bin",
        "large-v2": "ggml-large-v2.bin",
        "large-v3": "ggml-large-v3.bin",
        "large-v3_turbo": "ggml-large-v3-turbo.bin"  // Note: enum uses underscore
    ]
    
    /// SHA256 checksums for model verification
    /// These are the official checksums from whisper.cpp repository
    /// Calculated with: shasum -a 256 filename.bin
    private static let modelChecksums: [String: String] = [
        "ggml-tiny.bin": "be07e048e1e599ad46341c8d2a135645097a538221678b7acdd1b1919c6e1b21",
        "ggml-tiny.en.bin": "921e4cf8686fdd993dcd081a5da5b6c365bfde1162e72b08d75ac75289920b1f",
        "ggml-base.bin": "60ed5bc3dd14eea856493d334349b405782ddcaf0028d4b5df4088345fba2efe",
        "ggml-base.en.bin": "a03779c86df3323075f5e796cb2ce5029f00ec8869eee3fdfb897afe36c6d002",
        "ggml-small.bin": "1be3a9b2063867b937e64e2ec7483364a79917e157fa98c5d94b5c1fffea987b",
        "ggml-small.en.bin": "c6138d6d58ecc8322097e0f987c32f1be8bb0a18532a3f88f734d1bbf9c41e5d",
        "ggml-medium.bin": "6c14d5adee5f86394037b4e4e8b59f1673b6cee10e3cf0b11bbdbee79c156208",
        "ggml-medium.en.bin": "cc37e93478338ec7700281a7ac30a10128929eb8f427dda2e865faa8f6da4356",
        "ggml-large-v2.bin": "9a423fe4d40c82774b6af34115b8b935f34152246eb19e80e376071d3f999487",
        "ggml-large-v3.bin": "64d182b440b98d5203c4f9bd541544d84c605196c4f7b845dfa11fb23594d1e2",
        "ggml-large-v3-turbo.bin": "1fc70f774d38eb169993ac391eea357ef47c88757ef72ee5943879b7e8e2bc69"
    ]
    
    /// All available whisper.cpp models
    /// Generated from WhisperModel enum to ensure consistency
    static let allModels: [WhisperCppModel] = {
        var models: [WhisperCppModel] = []
        
        // Define model sizes (matching what whisper.cpp provides)
        let modelSizes: [String: (String, Int64)] = [
            "tiny": ("39 MB", 39_000_000),
            "tiny.en": ("39 MB", 39_000_000),
            "base": ("142 MB", 142_000_000),
            "base.en": ("142 MB", 142_000_000),
            "small": ("466 MB", 466_000_000),
            "small.en": ("466 MB", 466_000_000),
            "medium": ("1.5 GB", 1_500_000_000),
            "medium.en": ("1.5 GB", 1_500_000_000),
            "large-v2": ("2.9 GB", 2_900_000_000),
            "large-v3": ("3.1 GB", 3_100_000_000),
            "large-v3_turbo": ("809 MB", 809_000_000)
        ]
        
        // Create models from the enum
        for model in WhisperModel.allCases {
            let rawValue = model.rawValue
            guard let filename = modelFilenameMap[rawValue] else { continue }
            
            let isEnglishOnly = rawValue.hasSuffix(".en")
            let displayName = model.name  // Uses the formatted name from enum
            let (sizeStr, sizeBytes) = modelSizes[rawValue] ?? ("Unknown", 0)
            
            models.append(WhisperCppModel(
                name: rawValue,  // Use the enum's raw value as canonical name
                displayName: displayName,
                filename: filename,
                size: sizeStr,
                sizeInBytes: sizeBytes,
                isEnglishOnly: isEnglishOnly,
                url: nil
            ))
        }
        
        return models
    }()
    
    // MARK: - Initialization
    
    override init() {
        super.init()
        createModelsDirectoryIfNeeded()
        loadAvailableModels()
        
        // Scan models once on startup
        scanDownloadedModels()
        
        // Set up observer for when app becomes active
        setupFocusObserver()
        
        logger.info("🚀 WhisperModelManager initialized")
    }
    
    deinit {
        // Clean up notification observers
        for observer in notificationObservers {
            NotificationCenter.default.removeObserver(observer)
        }
    }
    
    // MARK: - Directory Management
    
    /// Create models directory if it doesn't exist
    private func createModelsDirectoryIfNeeded() {
        // Create models directory
        do {
            try FileManager.default.createDirectory(
                at: Self.modelsDirectory,
                withIntermediateDirectories: true,
                attributes: nil
            )
            logger.info("📁 Models directory ready: \(Self.modelsDirectory.path)")
        } catch {
            logger.error("Failed to create models directory: \(error)")
        }
    }
    
    /// Load available models
    private func loadAvailableModels() {
        availableModels = Self.allModels
    }
    
    /// Setup observer for app focus changes
    private func setupFocusObserver() {
        // Listen for app becoming active (gaining focus)
        let observer = NotificationCenter.default.addObserver(
            forName: NSApplication.didBecomeActiveNotification,
            object: nil,
            queue: .main
        ) { [weak self] _ in
            // PERFORMANCE FIX: Debounce model scanning during focus thrashing
            // When the recording dialog opens as a floating window, focus events fire rapidly.
            // Skip scan if we've scanned recently (within 2 seconds)
            guard let self = self else { return }

            let now = Date()
            if let lastScan = self.lastScanTime, now.timeIntervalSince(lastScan) < 2.0 {
                self.logger.debug("📱 Skipping model scan - last scan was \(now.timeIntervalSince(lastScan))s ago")
                return
            }

            self.lastScanTime = now
            self.logger.info("📱 App became active, scanning for models")
            self.scanDownloadedModels()
        }
        notificationObservers.append(observer)
    }

    /// Track last scan time for debouncing
    private var lastScanTime: Date?
    
    /// Scan for downloaded models
    func scanDownloadedModels() {
        logger.info("🔍 Scanning for models in: \(Self.modelsDirectory.path)")
        
        do {
            let files = try FileManager.default.contentsOfDirectory(
                at: Self.modelsDirectory,
                includingPropertiesForKeys: [.fileSizeKey],
                options: .skipsHiddenFiles
            )
            
            logger.info("📁 Found \(files.count) total files in models directory")
            
            var downloaded: [WhisperCppModel] = []
            
            for file in files where file.pathExtension == "bin" {
                let filename = file.lastPathComponent
                logger.debug("🔍 Checking file: \(filename)")
                
                // Find matching model definition
                if let model = Self.allModels.first(where: { $0.filename == filename }) {
                    logger.debug("✅ Matched \(filename) to model: \(model.name)")
                    
                    // Create model with local URL
                    let downloadedModel = WhisperCppModel(
                        name: model.name,
                        displayName: model.displayName,
                        filename: model.filename,
                        size: model.size,
                        sizeInBytes: model.sizeInBytes,
                        isEnglishOnly: model.isEnglishOnly,
                        url: file
                    )
                    downloaded.append(downloadedModel)
                } else {
                    logger.warning("⚠️ No model definition found for file: \(filename)")
                    logger.debug("   Available model filenames: \(Self.allModels.map { $0.filename })")
                }
            }
            
            // PERFORMANCE FIX: Only update if models actually changed
            // This prevents unnecessary SwiftUI rerenders when focus thrashing fires
            // multiple didBecomeActiveNotification events
            let currentModelNames = Set(self.downloadedModels.map { $0.name })
            let newModelNames = Set(downloaded.map { $0.name })

            if currentModelNames != newModelNames {
                Task { @MainActor in
                    self.downloadedModels = downloaded
                    self.objectWillChange.send() // Force UI update
                }
                logger.info("📦 Model list changed! Found \(downloaded.count) downloaded models: \(downloaded.map { $0.name }.joined(separator: ", "))")
            } else {
                logger.debug("📦 Model list unchanged (\(downloaded.count) models)")
            }
            
            // Log each model's path for debugging
            for model in downloaded {
                if let url = model.url {
                    logger.debug("  - \(model.name): \(url.path)")
                }
            }
            
        } catch {
            logger.error("Failed to scan downloaded models: \(error)")
        }
    }
    
    // MARK: - Model Downloads
    
    /// Download a model
    @MainActor
    func downloadModel(_ model: WhisperCppModel) async {
        guard !downloadingModels.contains(model.name) else {
            logger.warning("⚠️ Already downloading \(model.name)")
            return
        }

        errorMessage = nil
        logger.info("🔽 Starting download for \(model.displayName) from \(model.downloadURL)")
        downloadingModels.insert(model.name)
        downloadProgress[model.name] = 0
        objectWillChange.send()

        do {
            let url = URL(string: model.downloadURL)!
            let destinationURL = Self.modelsDirectory.appendingPathComponent(model.filename)
            
            // Download with progress tracking
            let localURL = try await downloadFile(from: url, to: destinationURL, modelName: model.name)
            
            // Verify file exists
            if FileManager.default.fileExists(atPath: localURL.path) {
                logger.info("✅ Successfully downloaded \(model.displayName) to \(localURL.path)")
                
                // Scan immediately after successful download
                scanDownloadedModels()
            } else {
                logger.error("❌ File not found after download: \(localURL.path)")
            }

        } catch {
            if isCancellationError(error) {
                logger.info("🛑 Cancelled download for \(model.displayName)")
            } else {
                logger.error("❌ Failed to download \(model.displayName): \(error)")
                errorMessage = "Failed to download \(model.displayName): \(error.localizedDescription)"
            }
        }

        downloadingModels.remove(model.name)
        downloadProgress.removeValue(forKey: model.name)
        lastReportedProgress.removeValue(forKey: model.name)
        logger.info("🏁 Download task completed for \(model.name)")
    }

    private func isCancellationError(_ error: Error) -> Bool {
        if let urlError = error as? URLError, urlError.code == .cancelled {
            return true
        }

        let nsError = error as NSError
        if nsError.domain == NSURLErrorDomain && nsError.code == NSURLErrorCancelled {
            return true
        }

        return error is CancellationError
    }
    
    /// Download file with progress tracking
    /// - Returns: The local URL where the file was downloaded
    private func downloadFile(from url: URL, to destinationURL: URL, modelName: String) async throws -> URL {
        return try await withCheckedThrowingContinuation { continuation in
            // Create download task WITHOUT completion handler to enable delegate callbacks
            let task = session.downloadTask(with: url)

            // ATOMIC GUARD FOR CONTINUATION SAFETY:
            // Provides defense-in-depth against double-resume. While the URLSession delegate
            // pattern should only call completion once, this atomic guard ensures safety
            // even in edge cases (e.g., rapid cancellation during completion).
            let finished = ManagedAtomic(false)

            let completion: (URL?, Error?) -> Void = { localURL, error in
                // Atomically check if we're the first to finish - only resume if so
                guard finished.exchange(true, ordering: .acquiring) == false else { return }

                if let error = error {
                    continuation.resume(throwing: error)
                } else if let localURL = localURL {
                    continuation.resume(returning: localURL)
                } else {
                    continuation.resume(throwing: URLError(.badServerResponse))
                }
            }

            // Store completion handler and model name for this task.
            // Confined to stateQueue so it can't race with delegate callbacks.
            let taskIdentifier = task.taskIdentifier
            stateQueue.sync {
                downloadCompletions[taskIdentifier] = completion
                taskModelNames[taskIdentifier] = modelName
                downloadTasks[modelName] = task
            }

            // Start the download
            task.resume()
            logger.info("📥 Started download task #\(task.taskIdentifier) for \(modelName)")
        }
    }
    
    /// Cancel a download
    func cancelDownload(_ modelName: String) {
        // Read+remove the task under stateQueue, then cancel outside the lock.
        let task = stateQueue.sync { () -> URLSessionDownloadTask? in
            let task = downloadTasks[modelName]
            downloadTasks.removeValue(forKey: modelName)
            return task
        }
        task?.cancel()
        downloadingModels.remove(modelName)
        downloadProgress.removeValue(forKey: modelName)
        lastReportedProgress.removeValue(forKey: modelName)
    }

    private func removeTracking(forTaskIdentifier taskIdentifier: Int, modelName: String) {
        stateQueue.sync {
            downloadCompletions.removeValue(forKey: taskIdentifier)
            taskModelNames.removeValue(forKey: taskIdentifier)

            if downloadTasks[modelName]?.taskIdentifier == taskIdentifier {
                downloadTasks.removeValue(forKey: modelName)
            }
        }
    }
    
    /// Delete a downloaded model
    @MainActor
    func deleteModel(_ model: WhisperCppModel) async {
        guard let url = model.url else { return }
        
        do {
            try FileManager.default.removeItem(at: url)
            logger.info("Deleted model: \(model.displayName)")
            
            // Update the list directly without filesystem scan
            downloadedModels.removeAll { $0.name == model.name }
            objectWillChange.send()
        } catch {
            logger.error("Failed to delete model: \(error)")
            errorMessage = "Failed to delete \(model.displayName): \(error.localizedDescription)"
        }
    }
    
    /// Get path for a model by name
    func getModelPath(for modelName: String) -> String? {
        downloadedModels.first { $0.name == modelName }?.url?.path
    }
    
    // MARK: - Checksum Verification

    /// Calculate SHA256 checksum for a file
    /// - Parameter url: URL of the file to checksum
    /// - Returns: Hex string of the SHA256 hash, or nil if calculation fails
    private func calculateSHA256(for url: URL) -> String? {
        // CHECKSUM COMPUTATION:
        // Delegated to ChecksumVerifier utility to avoid code duplication
        // across model managers
        do {
            return try ChecksumVerifier.sha256(of: url)
        } catch {
            logger.error("Failed to calculate checksum for \(url.path): \(error.localizedDescription)")
            return nil
        }
    }
}

// MARK: - URLSessionDownloadDelegate

extension WhisperModelManager: URLSessionDownloadDelegate {
    
    /// Called periodically to report download progress
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask, 
                    didWriteData bytesWritten: Int64, totalBytesWritten: Int64, 
                    totalBytesExpectedToWrite: Int64) {
        
        let taskIdentifier = downloadTask.taskIdentifier
        guard let modelName = stateQueue.sync(execute: { taskModelNames[taskIdentifier] }) else { return }

        // DOWNLOAD PROGRESS SAFETY: Handle unknown/zero content length
        // When server doesn't provide Content-Length, totalBytesExpectedToWrite is NSURLSessionTransferSizeUnknown (-1)
        let progress: Double
        if totalBytesExpectedToWrite > 0 {
            // Normal case: we know the total size
            progress = Double(totalBytesWritten) / Double(totalBytesExpectedToWrite)
        } else {
            // Indeterminate download: show bytes downloaded but no percentage
            // Use a fake progress value that indicates "unknown but downloading"
            // Could also use totalBytesWritten to show size downloaded so far
            progress = -Double(totalBytesWritten) // Negative indicates indeterminate
            logger.debug("📥 Download progress for \(modelName): \(totalBytesWritten) bytes (size unknown)")
        }
        
        // Throttle UI updates: only update when integer percentage changes
        let currentPercentage = progress >= 0 ? Int(progress * 100) : Int(progress)
        Task { @MainActor in
            let previousPercentage = self.lastReportedProgress[modelName] ?? -1
            guard currentPercentage != previousPercentage else { return }
            self.downloadProgress[modelName] = progress
            self.lastReportedProgress[modelName] = currentPercentage
        }
    }
    
    /// Called when download completes
    func urlSession(_ session: URLSession, downloadTask: URLSessionDownloadTask,
                    didFinishDownloadingTo location: URL) {

        let taskIdentifier = downloadTask.taskIdentifier
        // Read tracking state under stateQueue. The checksum/move below must stay
        // synchronous because URLSession deletes `location` as soon as this returns.
        let (modelName, completion) = stateQueue.sync {
            (taskModelNames[taskIdentifier], downloadCompletions[taskIdentifier])
        }
        guard let modelName,
              let model = Self.allModels.first(where: { $0.name == modelName }) else {
            logger.error("❌ No model name found for task \(taskIdentifier)")
            return
        }

        let destinationURL = Self.modelsDirectory.appendingPathComponent(model.filename)

        do {
            // CHECKSUM VERIFICATION: Verify the downloaded model before moving
            if let expectedChecksum = Self.modelChecksums[model.filename] {
                logger.info("🔐 Verifying checksum for \(model.filename)...")

                if let actualChecksum = calculateSHA256(for: location) {
                    if actualChecksum.lowercased() == expectedChecksum.lowercased() {
                        logger.info("✅ Checksum verified for \(model.filename)")
                    } else {
                        logger.error("❌ Checksum mismatch for \(model.filename)")
                        logger.error("   Expected: \(expectedChecksum)")
                        logger.error("   Actual:   \(actualChecksum)")

                        // Delete corrupted download
                        try? FileManager.default.removeItem(at: location)

                        // Call completion with error
                        let error = NSError(domain: "WhisperModelManager",
                                          code: 1001,
                                          userInfo: [NSLocalizedDescriptionKey: "models.error.checksumFailed".localized])
                        completion?(nil, error)

                        // Clean up and return early
                        removeTracking(forTaskIdentifier: taskIdentifier, modelName: modelName)
                        return
                    }
                } else {
                    logger.warning("⚠️ Could not calculate checksum for \(model.filename), proceeding anyway")
                }
            } else {
                logger.warning("⚠️ No checksum available for \(model.filename), skipping verification")
            }

            // Remove existing file if present
            if FileManager.default.fileExists(atPath: destinationURL.path) {
                try FileManager.default.removeItem(at: destinationURL)
            }

            // Move downloaded file to destination
            try FileManager.default.moveItem(at: location, to: destinationURL)
            logger.info("✅ Downloaded \(modelName) to \(destinationURL.path)")

            // Call completion handler with success
            completion?(destinationURL, nil)

        } catch {
            logger.error("❌ Failed to move downloaded file: \(error)")
            // Call completion handler with error
            completion?(nil, error)
        }

        // Clean up tracking dictionaries
        removeTracking(forTaskIdentifier: taskIdentifier, modelName: modelName)
    }
    
    /// Called when task completes (with or without error)
    func urlSession(_ session: URLSession, task: URLSessionTask, didCompleteWithError error: Error?) {
        guard let downloadTask = task as? URLSessionDownloadTask else { return }
        let taskIdentifier = downloadTask.taskIdentifier
        let (modelName, completion) = stateQueue.sync {
            (taskModelNames[taskIdentifier], downloadCompletions[taskIdentifier])
        }
        guard let modelName else { return }

        if let error = error {
            logger.error("❌ Download failed for \(modelName): \(error)")

            // Call completion handler with error
            completion?(nil, error)
        }

        // Clean up
        removeTracking(forTaskIdentifier: taskIdentifier, modelName: modelName)
    }
}
