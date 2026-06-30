//
//  FileTranscriptionFlow.swift
//  hyperwhisper
//
//  Created for file import transcription feature
//

import Foundation
import AppKit
import AVFoundation
import UniformTypeIdentifiers
import SwiftUI

/// Manages the import and transcription of external audio files
///
/// **Purpose:**
/// Handles the complete workflow for transcribing audio files selected by the user:
/// - Opens file picker with supported audio formats
/// - Validates file size against provider limits
/// - Copies file to recordings folder
/// - Creates processing transcript entry
/// - Applies VAD silence trimming (for files >= 30 seconds when enabled)
/// - Shows progress popup during transcription
/// - Triggers transcription via TranscriptionPipeline
/// - Updates transcript and navigates to History on completion
///
/// **Progress Popup:**
/// A floating progress popup is shown during file transcription with stages:
/// - Preparing (0-15%): File copy, video extraction, VAD
/// - Transcribing (15-85%): API call (animated progress)
/// - Finishing (85-100%): Post-processing, saving
///
/// **File Size Validation:**
/// Each cloud provider has different file size limits:
/// - Local providers (LibWhisper, Parakeet): No limit
/// - HyperWhisper Cloud / Deepgram: 2 GB
/// - OpenAI / Groq: 25 MB
/// - AssemblyAI: 2.2 GB
/// - ElevenLabs: 3 GB
/// - Mistral: 100 MB
///
/// **VAD Processing:**
/// When VAD is enabled in settings and the imported file is >= 30 seconds,
/// silence trimming is applied before transcription. This mirrors the behavior
/// of live recordings in RecordingTranscriptionFlow.
///
/// **Thread Safety:**
/// All public methods run on main actor for UI consistency.
@MainActor
class FileTranscriptionFlow {

    // MARK: - Dependencies

    private weak var transcriptionPipeline: TranscriptionPipeline?
    private weak var settingsManager: SettingsManager?
    private weak var appState: AppState?
    private weak var licenseManager: LicenseManager?

    /// VAD processing service for silence trimming and M4A conversion
    private let vadProcessingService = VADProcessingService()

    // MARK: - Progress Tracking

    /// Observable progress state for the file transcription popup
    let progressState = FileTranscriptionProgress()

    /// Current transcription task (for cancellation support)
    private var currentTranscriptionTask: Task<Void, Error>?

    /// Path to the copied file (for cleanup on cancellation)
    private var currentCopiedFilePath: String?

    /// Callback to open the main window, provided by the caller (SwiftUI view)
    ///
    /// **Purpose:**
    /// Since FileTranscriptionFlow doesn't have access to SwiftUI's `openWindow(id:)`,
    /// the caller provides this callback to handle proper window creation/reuse.
    /// This ensures the main window is properly opened even when it doesn't exist yet
    /// (e.g., when app is launched minimized to menu bar only).
    private var onOpenMainWindow: (() -> Void)?

    // MARK: - Supported Audio Types

    /// Supported audio and video file types for import
    ///
    /// **Audio Files:**
    /// Most transcription providers accept: mp3, mp4, mpeg, mpga, m4a, wav, webm
    /// Audio files are sent directly to transcription providers.
    ///
    /// **Video Files (MP4, MOV):**
    /// Video files are supported by extracting the audio track locally before
    /// transcription. This allows transcribing video content using any provider,
    /// including local Whisper models.
    static let supportedAudioTypes: [UTType] = [
        // Audio formats
        .audio,           // Umbrella type for all audio
        .wav,             // Waveform Audio File Format
        .mp3,             // MPEG Audio Layer III
        .mpeg4Audio,      // M4A / AAC
        .aiff,            // Audio Interchange File Format
        UTType(filenameExtension: "webm") ?? .audio,  // WebM audio
        UTType(filenameExtension: "ogg") ?? .audio,   // Ogg Vorbis
        UTType(filenameExtension: "flac") ?? .audio,  // Free Lossless Audio Codec
        // Video formats (audio will be extracted)
        .mpeg4Movie,      // MP4 video
        .quickTimeMovie   // MOV video (QuickTime)
    ]

    // MARK: - Initialization

    /// Creates a new FileTranscriptionFlow with required dependencies
    ///
    /// - Parameters:
    ///   - transcriptionPipeline: Manager for running transcription
    ///   - settingsManager: Manager for app settings
    ///   - appState: Shared app state for navigation and UI updates
    ///   - licenseManager: Manager for license/usage tracking
    ///   - onOpenMainWindow: Callback to open main window (required for proper window creation)
    init(
        transcriptionPipeline: TranscriptionPipeline?,
        settingsManager: SettingsManager?,
        appState: AppState?,
        licenseManager: LicenseManager?,
        onOpenMainWindow: (() -> Void)? = nil
    ) {
        self.transcriptionPipeline = transcriptionPipeline
        self.settingsManager = settingsManager
        self.appState = appState
        self.licenseManager = licenseManager
        self.onOpenMainWindow = onOpenMainWindow
    }

    // MARK: - Public API

    /// Cancel the current file transcription
    ///
    /// **What This Does:**
    /// 1. Sets the cancelled flag on progress state
    /// 2. Cancels the running transcription task
    /// 3. Dismisses the progress popup
    /// 4. Cleans up copied files if they exist
    /// 5. Resets app state
    ///
    /// **Thread Safety:**
    /// Safe to call from any context; runs on MainActor.
    func cancelTranscription() {
        AppLogger.transcription.info("❌ File transcription cancelled by user")

        // Mark as cancelled
        progressState.cancel()

        // Cancel the running task
        currentTranscriptionTask?.cancel()
        currentTranscriptionTask = nil

        // Dismiss the progress popup
        FileTranscriptionPopupManager.shared.dismiss()

        // Clean up copied file if it exists
        cleanupCopiedFile(reason: "cancellation")

        // Reset progress state
        progressState.reset()

        // Reset app state
        appState?.recordingState = .idle
    }

    /// Opens file picker and starts transcription for the selected file with the given mode
    ///
    /// **Flow:**
    /// 1. Show NSOpenPanel for audio file selection
    /// 2. Show progress popup
    /// 3. Validate file size against provider limits
    /// 4. Copy file to recordings folder
    /// 5. Get audio duration
    /// 6. Create "processing" transcript entry
    /// 7. Start transcription
    /// 8. Update transcript with results
    /// 9. Navigate to History view
    ///
    /// - Parameter mode: The transcription mode to use
    func openFilePickerAndTranscribe(for mode: Mode) {
        AppLogger.transcription.info("📂 Opening file picker for mode: \(mode.name ?? "unnamed")")

        // STEP 1: Show file picker
        let panel = NSOpenPanel()
        panel.title = "select.audio.file".localized
        panel.allowedContentTypes = Self.supportedAudioTypes
        panel.allowsMultipleSelection = false
        panel.canChooseDirectories = false
        panel.canChooseFiles = true

        // Run panel (blocking on main thread for menu interaction)
        guard panel.runModal() == .OK, let selectedURL = panel.url else {
            AppLogger.transcription.info("📂 File picker cancelled")
            return
        }

        AppLogger.transcription.info("📂 Selected file: \(selectedURL.lastPathComponent)")

        // STEP 2-8: Process the file
        // Store the task in currentTranscriptionTask so user-initiated cancellation
        // via cancelTranscription() can actually stop the running transcription.
        // Without this, the cancel button would dismiss the popup but the
        // transcription would continue running in the background.
        currentTranscriptionTask = Task {
            await processSelectedFile(selectedURL, mode: mode)
        }
    }

    // MARK: - Private Methods

    /// Processes the selected audio file through the transcription pipeline
    ///
    /// **Progress Popup Flow:**
    /// This method shows a floating progress popup that tracks transcription stages:
    /// - Preparing (0-15%): File validation, copy, video extraction, VAD
    /// - Transcribing (15-85%): API call with slow animated progress
    /// - Finishing (85-100%): Post-processing, saving results
    ///
    /// **Cancellation:**
    /// User can cancel at any time via the popup's cancel button.
    /// Cancellation is checked between stages and during the transcription call.
    ///
    /// - Parameters:
    ///   - fileURL: URL of the selected audio file
    ///   - mode: The transcription mode to use
    private func processSelectedFile(_ fileURL: URL, mode: Mode) async {
        // STEP 1: Show progress popup
        progressState.beginTranscription(
            fileName: fileURL.lastPathComponent,
            modeName: mode.name ?? "Default"
        )
        FileTranscriptionPopupManager.shared.show(
            progress: progressState,
            onCancel: { [weak self] in
                self?.cancelTranscription()
            }
        )

        do {
            // STEP 2a: Validate file size against provider limits (0-5%)
            progressState.animateProgress(to: 0.03, duration: 0.2)
            try validateFileSize(fileURL, for: mode)

            // Check for cancellation
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 2b: Validate file format is supported by provider
            // This catches unsupported formats early with a helpful error message
            // rather than letting the API return a cryptic error after upload
            progressState.animateProgress(to: 0.05, duration: 0.2)
            try validateFileFormat(fileURL, for: mode)

            // Check for cancellation
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 3: Copy file to recordings folder (5-8%)
            progressState.animateProgress(to: 0.08, duration: 0.3)
            let copiedURL = try copyFileToRecordingsFolder(fileURL)
            currentCopiedFilePath = copiedURL.path

            // Check for cancellation
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 3b: VIDEO AUDIO EXTRACTION (if needed) (8-12%)
            // For video files (MP4, MOV), we extract the audio track locally before
            // transcription. This ensures compatibility with all providers including
            // local Whisper models that only accept audio files.
            //
            // Flow:
            // - Video file → Extract audio → M4A file → Transcription
            // - Audio file → Use directly → Transcription
            var audioURL = copiedURL
            if isVideoFileType(copiedURL) {
                AppLogger.transcription.info("🎬 [FileImport] Detected video file, extracting audio...")
                progressState.animateProgress(to: 0.10, duration: 0.5)

                // Create output path for extracted audio (same name but .m4a extension)
                let extractedAudioURL = copiedURL.deletingPathExtension().appendingPathExtension("m4a")

                do {
                    // Extract audio from video using AudioFileConverter
                    // This reuses the existing AVAssetReader/Writer pipeline
                    let audioConverter = AudioFileConverter()
                    let extractionResult = try await audioConverter.extractAudioFromVideo(
                        from: copiedURL,
                        to: extractedAudioURL
                    )

                    AppLogger.transcription.info("🎬 [FileImport] Audio extracted: \(String(format: "%.1f", extractionResult.duration))s, \(Int(extractionResult.sampleRate))Hz, \(extractionResult.channels)ch")

                    // Use extracted audio for transcription
                    audioURL = extractedAudioURL
                    progressState.animateProgress(to: 0.12, duration: 0.2)
                } catch AudioError.noAudioTrack {
                    // Video has no audio track - show user-friendly error
                    throw FileTranscriptionError.videoHasNoAudio
                }
                // Other errors (e.g., exportFailed) will bubble up naturally
            }

            // Check for cancellation
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 4: Get audio duration (12-13%)
            progressState.animateProgress(to: 0.13, duration: 0.2)
            let duration = try await getAudioDuration(audioURL)

            // STEP 4b: VAD SILENCE TRIMMING (Optional) (13-15%)
            // Uses VADProcessingService to analyze audio and trim leading/trailing silence.
            // Only applies to files >= 30 seconds when VAD is enabled in settings.
            // Note: For video files, audioURL points to the extracted M4A, not the original video.
            progressState.animateProgress(to: 0.15, duration: 0.3)
            let vadResult = await vadProcessingService.processAudioForTranscription(
                audioURL: audioURL,
                duration: duration,
                vadEnabled: settingsManager?.enableVAD ?? false,
                context: "FileImport"
            )
            let finalAudioURL = vadResult.finalAudioURL
            let trimResult = vadResult.trimResult

            // Check for cancellation
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 5: Create "processing" transcript entry
            // This makes the entry appear immediately in HistoryView with "Processing..." text
            let processingTranscript = PersistenceController.shared.createProcessingTranscript(
                duration: duration,
                mode: mode.name,
                audioFilePath: copiedURL.path
            )
            // The history row now owns the copied file for playback/retry.
            currentCopiedFilePath = nil

            // STEP 5a: Save trimmed audio path if VAD was used
            // This allows users to toggle between original and trimmed audio in history view.
            if vadResult.wasProcessed, let result = trimResult {
                PersistenceController.shared.setTrimmedAudioPath(processingTranscript, trimmedPath: result.outputURL.path)
                AppLogger.transcription.debug("📝 [FileImport] Saved trimmed audio path to transcript")
            }

            AppLogger.transcription.info("📝 Created processing transcript for file: \(copiedURL.lastPathComponent)")

            // STEP 6: Start transcription (15-85%)
            // Update progress to transcribing stage with slow animation
            progressState.updateStage(.transcribing)
            // Start a slow animation that will be cut short when transcription completes
            // Duration estimates: 60s covers most transcriptions, will jump to completion when done
            progressState.animateProgress(to: 0.85, duration: 60.0)

            guard let transcriptionPipeline = transcriptionPipeline else {
                AppLogger.transcription.error("❌ TranscriptionPipeline not available")
                updateTranscriptWithError(processingTranscript, error: "Transcription manager unavailable")
                FileTranscriptionPopupManager.shared.dismiss()
                progressState.reset()
                return
            }

            // Update app state to show transcribing status
            appState?.recordingState = .transcribing

            // Use finalAudioURL which may be VAD-trimmed if VAD was enabled
            let result = try await transcriptionPipeline.transcribeWithDetails(
                audioURL: finalAudioURL,
                mode: mode,
                recordingSession: nil,
                applicationContext: nil
            )

            // Check for cancellation after transcription
            guard !progressState.isCancelled else { throw CancellationError() }

            // STEP 7: Finishing stage (85-100%)
            progressState.updateStage(.finishing)
            progressState.animateProgress(to: 0.95, duration: 0.3)

            // Update transcript with results
            PersistenceController.shared.updateTranscriptWithTranscription(
                processingTranscript,
                transcribedText: result.rawText,
                postProcessedText: result.wasPostProcessed ? result.text : nil,
                transcriptionProvider: result.provider,
                postProcessingProvider: result.postProcessingProvider
            )

            // No local usage recording — local transcription is unlimited (open source).

            // Complete progress animation
            progressState.animateProgress(to: 1.0, duration: 0.2)

            AppLogger.transcription.info("✅ File transcription completed: \(copiedURL.lastPathComponent)")

            // STEP 8: Brief delay for visual feedback, then dismiss and navigate
            try await Task.sleep(nanoseconds: 500_000_000) // 0.5s

            FileTranscriptionPopupManager.shared.dismiss()
            progressState.reset()
            currentCopiedFilePath = nil
            currentTranscriptionTask = nil

            // Navigate to History view
            appState?.selectedNavigationItem = .history
            openMainWindowWithHistory()

            // Reset state after completion
            appState?.recordingState = .idle

        } catch is CancellationError {
            // User cancelled - cleanup was already done by cancelTranscription()
            AppLogger.transcription.info("📂 File transcription cancelled")
            currentTranscriptionTask = nil
        } catch let error as FileTranscriptionError {
            // Handle known errors with appropriate UI
            FileTranscriptionPopupManager.shared.dismiss()
            progressState.reset()
            cleanupCopiedFile(reason: "import error")
            currentTranscriptionTask = nil
            appState?.recordingState = .idle
            handleError(error)
        } catch {
            // Handle unexpected errors
            FileTranscriptionPopupManager.shared.dismiss()
            progressState.reset()
            cleanupCopiedFile(reason: "unexpected error")
            currentTranscriptionTask = nil
            AppLogger.transcription.error("❌ File transcription failed: \(error.localizedDescription)")
            showErrorAlert(
                title: "transcribe.file.error.title".localized,
                message: error.localizedDescription
            )
            appState?.recordingState = .idle
        }
    }

    /// Validates that the file size doesn't exceed the provider's limit
    ///
    /// - Parameters:
    ///   - fileURL: URL of the file to validate
    ///   - mode: The transcription mode (determines provider)
    /// - Throws: FileTranscriptionError.fileTooLarge if file exceeds limit
    private func validateFileSize(_ fileURL: URL, for mode: Mode) throws {
        // Get file size
        let attributes = try FileManager.default.attributesOfItem(atPath: fileURL.path)
        guard let fileSize = attributes[.size] as? Int64 else {
            throw FileTranscriptionError.cannotReadFile
        }

        // Determine if using cloud provider
        let isCloudMode = mode.model?.lowercased() == "cloud"

        if isCloudMode {
            // Cloud mode: check provider-specific limit
            guard let providerRaw = mode.cloudProvider,
                  let provider = CloudProvider(rawValue: providerRaw) else {
                // If no provider specified, assume default (OpenAI with 25MB limit)
                let defaultLimit: Int64 = 25 * 1024 * 1024
                if fileSize > defaultLimit {
                    throw FileTranscriptionError.fileTooLarge(
                        fileSize: fileSize,
                        limit: defaultLimit,
                        providerName: "Cloud Provider"
                    )
                }
                return
            }

            let limit = provider.maxFileSizeBytes
            if fileSize > limit {
                throw FileTranscriptionError.fileTooLarge(
                    fileSize: fileSize,
                    limit: limit,
                    providerName: provider.displayName
                )
            }
        }
        // Local providers have no file size limit - validation passes automatically

        let fileSizeMB = Double(fileSize) / (1024 * 1024)
        AppLogger.transcription.info("📊 File size: \(String(format: "%.2f", fileSizeMB)) MB - validation passed")
    }

    /// Validates that the file format is supported by the provider
    ///
    /// **Purpose:**
    /// Different cloud providers support different audio formats. This validation
    /// catches unsupported formats early with a helpful error message, rather than
    /// letting the API return a cryptic error after the file is uploaded.
    ///
    /// **Provider Format Support:**
    /// - OpenAI/Groq: mp3, mp4, mpeg, mpga, m4a, wav, webm
    /// - Deepgram/HyperWhisper Cloud: Broad support including flac, ogg, aac, opus
    /// - Local providers (LibWhisper/Parakeet): Any format AVFoundation can decode
    ///
    /// **Behavior:**
    /// - Local modes skip validation (AVFoundation handles format conversion)
    /// - Cloud modes validate against provider's supported formats
    /// - If no provider is specified, validation is skipped
    ///
    /// - Parameters:
    ///   - fileURL: URL of the file to validate
    ///   - mode: The transcription mode (determines provider)
    /// - Throws: FileTranscriptionError.unsupportedFormat if format not supported
    private func validateFileFormat(_ fileURL: URL, for mode: Mode) throws {
        let fileExtension = fileURL.pathExtension.lowercased()

        // STEP 1: Check if this is a cloud mode
        // Local providers (LibWhisper, Parakeet) support any format AVFoundation can decode
        let isCloudMode = mode.model?.lowercased() == "cloud"
        guard isCloudMode else {
            AppLogger.transcription.info("📄 Format validation skipped (local mode accepts all formats)")
            return
        }

        // STEP 2: Get the cloud provider
        // If no provider specified, skip validation (will use default provider settings)
        guard let providerRaw = mode.cloudProvider,
              let provider = CloudProvider(rawValue: providerRaw) else {
            AppLogger.transcription.info("📄 Format validation skipped (no provider specified)")
            return
        }

        // STEP 3: Check if file extension is in the provider's supported formats
        let supportedFormats = provider.supportedAudioExtensions
        if !supportedFormats.contains(fileExtension) {
            AppLogger.transcription.warning("⚠️ Unsupported format: .\(fileExtension) for \(provider.displayName)")
            throw FileTranscriptionError.unsupportedFormat(
                format: fileExtension,
                providerName: provider.displayName,
                supportedFormats: Array(supportedFormats).sorted()
            )
        }

        AppLogger.transcription.info("📄 Format .\(fileExtension) is supported by \(provider.displayName)")
    }

    /// Copies the selected file to the recordings folder with a unique name
    ///
    /// - Parameter sourceURL: URL of the source file
    /// - Returns: URL of the copied file in the recordings folder
    /// - Throws: FileTranscriptionError if copy fails
    private func copyFileToRecordingsFolder(_ sourceURL: URL) throws -> URL {
        let recordingsDirectory = getRecordingsDirectory()

        // Ensure recordings directory exists
        try FileManager.default.createDirectory(
            at: recordingsDirectory,
            withIntermediateDirectories: true,
            attributes: nil
        )

        // Generate unique filename: imported_<timestamp>_<original_name>.<ext>
        let timestamp = Int(Date().timeIntervalSince1970)
        let originalName = sourceURL.deletingPathExtension().lastPathComponent
        let ext = sourceURL.pathExtension
        let newFileName = "imported_\(timestamp)_\(originalName).\(ext)"
        let destinationURL = recordingsDirectory.appendingPathComponent(newFileName)

        // Copy file
        try FileManager.default.copyItem(at: sourceURL, to: destinationURL)

        AppLogger.transcription.info("📁 Copied file to: \(destinationURL.lastPathComponent)")
        return destinationURL
    }

    /// Gets the recordings directory from settings or default location
    ///
    /// - Returns: URL of the recordings directory
    private func getRecordingsDirectory() -> URL {
        if let path = settingsManager?.recordingsFolder, !path.isEmpty {
            return URL(fileURLWithPath: path, isDirectory: true)
        }

        return FileManager.default.urls(for: .documentDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("Recordings", isDirectory: true)
    }

    private func cleanupCopiedFile(reason: String) {
        guard let path = currentCopiedFilePath else { return }

        try? FileManager.default.removeItem(atPath: path)
        currentCopiedFilePath = nil
        AppLogger.transcription.debug("🗑️ Cleaned up copied file after \(reason): \(path)")
    }

    /// Gets the duration of an audio file
    ///
    /// - Parameter url: URL of the audio file
    /// - Returns: Duration in seconds
    /// - Throws: Error if duration cannot be determined
    private func getAudioDuration(_ url: URL) async throws -> TimeInterval {
        let asset = AVURLAsset(url: url)
        let duration = try await asset.load(.duration)

        // Reject invalid/indefinite durations so a non-finite value never escapes
        // this helper. For some files (fragmented/streamable MP4s, indefinite tracks)
        // AVFoundation cannot resolve the duration and CMTimeGetSeconds returns NaN
        // without throwing. That NaN would later trap at Int(duration.rounded()) and
        // corrupt persisted duration via max(_, NaN), so we surface a clean error here.
        guard duration.isValid, !duration.flags.contains(.indefinite) else {
            AppLogger.transcription.error("❌ Audio duration is invalid/indefinite: \(url.lastPathComponent)")
            throw FileTranscriptionError.cannotReadFile
        }

        let seconds = CMTimeGetSeconds(duration)
        guard seconds.isFinite, seconds >= 0 else {
            AppLogger.transcription.error("❌ Audio duration is not finite or is negative: \(url.lastPathComponent)")
            throw FileTranscriptionError.cannotReadFile
        }
        return seconds
    }

    /// Updates a transcript with an error message
    ///
    /// - Parameters:
    ///   - transcript: The transcript to update
    ///   - error: Error message
    private func updateTranscriptWithError(_ transcript: Transcript, error: String) {
        PersistenceController.shared.updateTranscriptWithTranscription(
            transcript,
            transcribedText: "Error: \(error)",
            postProcessedText: nil,
            transcriptionProvider: nil,
            postProcessingProvider: nil
        )
    }

    /// Handles file transcription errors with appropriate UI
    ///
    /// **Purpose:**
    /// Displays user-friendly error alerts for known file transcription errors.
    /// Each error type has a specific title and message with actionable guidance.
    ///
    /// **Error Handling:**
    /// - fileTooLarge: Shows file size vs limit and provider name
    /// - unsupportedFormat: Lists supported formats for the provider
    /// - cannotReadFile: Generic file access error
    /// - copyFailed: Recording folder access error
    ///
    /// - Parameter error: The error to handle
    private func handleError(_ error: FileTranscriptionError) {
        appState?.recordingState = .idle

        switch error {
        case .fileTooLarge(let fileSize, let limit, let providerName):
            let fileSizeStr = formatFileSize(fileSize)
            let limitStr = formatFileSize(limit)
            showErrorAlert(
                title: "transcribe.file.error.size.title".localized,
                message: String(
                    format: "transcribe.file.error.size.message".localized,
                    fileSizeStr,
                    limitStr,
                    providerName
                )
            )

        case .unsupportedFormat(let format, let providerName, let supportedFormats):
            // Format the supported extensions list: .mp3, .wav, .m4a, etc.
            let formatsStr = supportedFormats.map { ".\($0)" }.joined(separator: ", ")
            showErrorAlert(
                title: "transcribe.file.error.format.title".localized,
                message: String(
                    format: "transcribe.file.error.format.message".localized,
                    format.uppercased(),
                    providerName,
                    formatsStr
                )
            )

        case .cannotReadFile:
            showErrorAlert(
                title: "transcribe.file.error.title".localized,
                message: "transcribe.file.error.read".localized
            )

        case .copyFailed:
            showErrorAlert(
                title: "transcribe.file.error.title".localized,
                message: "transcribe.file.error.copy".localized
            )

        case .videoHasNoAudio:
            showErrorAlert(
                title: "transcribe.file.error.video.title".localized,
                message: "transcribe.file.error.video.noAudio".localized
            )
        }
    }

    /// Formats a file size in bytes to a human-readable string
    ///
    /// - Parameter bytes: File size in bytes
    /// - Returns: Formatted string (e.g., "25 MB", "1.5 GB")
    private func formatFileSize(_ bytes: Int64) -> String {
        if bytes >= 1024 * 1024 * 1024 {
            let gb = Double(bytes) / (1024.0 * 1024.0 * 1024.0)
            return String(format: "%.1f GB", gb)
        } else {
            let mb = bytes / (1024 * 1024)
            return "\(mb) MB"
        }
    }

    /// Checks if a URL points to a video file type that requires audio extraction
    ///
    /// **Purpose:**
    /// Determines whether an imported file is a video container (MP4, MOV) that
    /// needs audio extraction before transcription, versus an audio file that
    /// can be sent directly to the transcription provider.
    ///
    /// **Supported Video Types:**
    /// - MP4 (.mp4, .m4v) - MPEG-4 Part 14 container
    /// - MOV (.mov) - QuickTime container
    ///
    /// - Parameter url: URL of the file to check
    /// - Returns: `true` if file is a video type requiring audio extraction
    private func isVideoFileType(_ url: URL) -> Bool {
        let videoExtensions = ["mp4", "mov", "m4v"]
        return videoExtensions.contains(url.pathExtension.lowercased())
    }

    /// Shows an error alert to the user
    ///
    /// - Parameters:
    ///   - title: Alert title
    ///   - message: Alert message
    private func showErrorAlert(title: String, message: String) {
        let alert = NSAlert()
        alert.messageText = title
        alert.informativeText = message
        alert.alertStyle = .warning
        alert.addButton(withTitle: "common.ok".localized)
        alert.runModal()
    }

    /// Opens the main window and navigates to History view
    ///
    /// **Purpose:**
    /// Ensures the main window is visible and showing the History view when file
    /// transcription starts. This provides immediate feedback to the user.
    ///
    /// **Window Creation:**
    /// - If a main window already exists, it's brought to front
    /// - If no window exists (e.g., app launched minimized), uses the `onOpenMainWindow`
    ///   callback to create one via SwiftUI's `openWindow(id:)`
    ///
    /// **Why a callback?**
    /// This manager doesn't have access to SwiftUI's Environment, so it can't call
    /// `openWindow(id:)` directly. The callback is provided by the caller (a SwiftUI view)
    /// which does have access to the environment.
    private func openMainWindowWithHistory() {
        let methodStart = CFAbsoluteTimeGetCurrent()
        AppLogger.transcription.debug("📋 [FileTranscription] openMainWindowWithHistory() started")

        // Activate the app so it comes to foreground
        NSApp.activate(ignoringOtherApps: true)
        let activateElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
        AppLogger.transcription.debug("📋 [FileTranscription] NSApp.activate completed in \(String(format: "%.2f", activateElapsed))ms")

        // STEP 1: Check the stored window reference first (most reliable)
        // This avoids timing issues where the window exists but hasn't had its identifier set yet
        if let mainWindow = MainWindowStore.window {
            let findElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
            AppLogger.transcription.debug("📋 [FileTranscription] Found main window via MainWindowStore in \(String(format: "%.2f", findElapsed))ms, bringing to front...")
            mainWindow.makeKeyAndOrderFront(nil)
            let totalElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
            AppLogger.transcription.debug("📋 [FileTranscription] openMainWindowWithHistory() completed (reused stored window) in \(String(format: "%.2f", totalElapsed))ms")
            return
        }

        // STEP 2: Fallback - search by identifier (for edge cases where WindowConfigurator hasn't run)
        let windowCount = NSApplication.shared.windows.count
        AppLogger.transcription.debug("📋 [FileTranscription] MainWindowStore.window is nil, searching \(windowCount) windows by identifier...")

        if let mainWindow = NSApplication.shared.windows.first(where: { window in
            window.identifier == .hyperwhisperMainWindow
        }) {
            let findElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
            AppLogger.transcription.debug("📋 [FileTranscription] Found existing main window by identifier in \(String(format: "%.2f", findElapsed))ms, bringing to front...")
            mainWindow.makeKeyAndOrderFront(nil)
            let totalElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
            AppLogger.transcription.debug("📋 [FileTranscription] openMainWindowWithHistory() completed (reused window by identifier) in \(String(format: "%.2f", totalElapsed))ms")
            return
        }

        let findElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
        AppLogger.transcription.debug("📋 [FileTranscription] No existing main window found after \(String(format: "%.2f", findElapsed))ms")

        // STEP 3: No existing window found - use callback to create one
        // This handles the case when app is launched minimized (menu bar only)
        // and no main window exists yet
        if let openWindow = onOpenMainWindow {
            AppLogger.transcription.debug("📋 [FileTranscription] Calling onOpenMainWindow callback...")
            openWindow()
            let totalElapsed = (CFAbsoluteTimeGetCurrent() - methodStart) * 1000
            AppLogger.transcription.debug("📋 [FileTranscription] openMainWindowWithHistory() completed (created window) in \(String(format: "%.2f", totalElapsed))ms")
        } else {
            // Fallback: If no callback provided, log a warning
            // The window might be created later when user interacts with the app
            AppLogger.transcription.warning("⚠️ No main window found and no callback to create one")
        }
    }
}

// MARK: - Error Types

/// Errors that can occur during file transcription
///
/// **Purpose:**
/// Provides structured error handling for the file transcription workflow with
/// user-friendly error messages that include actionable guidance.
///
/// **Error Cases:**
/// - `fileTooLarge`: File exceeds provider's size limit (e.g., OpenAI's 25 MB limit)
/// - `unsupportedFormat`: File format not supported by the selected provider
/// - `cannotReadFile`: Unable to access or read the selected file
/// - `copyFailed`: Failed to copy file to the recordings folder
/// - `videoHasNoAudio`: Video file contains no audio track to extract
enum FileTranscriptionError: Error, LocalizedError {
    /// File size exceeds the provider's maximum allowed size
    case fileTooLarge(fileSize: Int64, limit: Int64, providerName: String)

    /// Audio format is not supported by the selected cloud provider
    /// - Parameters:
    ///   - format: The file extension that was attempted (e.g., "flac")
    ///   - providerName: Display name of the provider (e.g., "OpenAI")
    ///   - supportedFormats: List of formats the provider accepts
    case unsupportedFormat(format: String, providerName: String, supportedFormats: [String])

    /// Cannot read or access the selected file
    case cannotReadFile

    /// Failed to copy file to recordings folder
    case copyFailed

    /// Video file does not contain an audio track
    /// This occurs when importing a video file (MP4, MOV) that has no audio,
    /// such as a screen recording without microphone input.
    case videoHasNoAudio

    var errorDescription: String? {
        switch self {
        case .fileTooLarge(_, _, let providerName):
            return "File exceeds \(providerName) size limit"
        case .unsupportedFormat(let format, let providerName, _):
            return "\(providerName) does not support .\(format) files"
        case .cannotReadFile:
            return "Cannot read file"
        case .copyFailed:
            return "Failed to copy file"
        case .videoHasNoAudio:
            return "Video has no audio track"
        }
    }
}
