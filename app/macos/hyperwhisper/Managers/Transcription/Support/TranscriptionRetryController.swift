//
//  TranscriptionRetryController.swift
//  hyperwhisper
//
//  TRANSCRIPTION RETRY HANDLER
//  This class handles retry logic for failed transcriptions stored in Core Data.
//
//  Key Features:
//  - Validates audio file availability before retry
//  - Updates transcript status throughout retry process
//  - Tracks retry count and timestamp
//  - Handles both successful and failed retries
//  - VAD silence trimming for long audio files (>= 30 seconds)
//
//  Architecture Notes:
//  - Extracted from TranscriptionPipeline to separate retry concerns
//  - Integrates with Core Data for transcript persistence
//  - Uses weak reference to avoid retain cycles with TranscriptionPipeline
//  - VAD processing mirrors RecordingTranscriptionFlow's implementation
//

import Foundation

/// Handles retry logic for failed transcriptions
@MainActor
class TranscriptionRetryController {

    // MARK: - Private Properties

    /// Weak reference to the transcription manager for executing retries
    /// Using weak reference to avoid retain cycles since manager owns this handler
    private weak var transcriptionPipeline: TranscriptionPipeline?

    /// Settings manager for checking VAD enabled state
    private weak var settingsManager: SettingsManager?

    /// VAD processing service for silence trimming and M4A conversion
    private let vadProcessingService = VADProcessingService()

    // MARK: - Initialization

    /// Initialize retry handler with references to required managers
    /// - Parameters:
    ///   - transcriptionPipeline: The manager that will execute transcription
    ///   - settingsManager: Settings manager for VAD configuration
    init(transcriptionPipeline: TranscriptionPipeline?, settingsManager: SettingsManager? = nil) {
        self.transcriptionPipeline = transcriptionPipeline
        self.settingsManager = settingsManager
    }

    /// Update settings manager reference
    /// Called when the TranscriptionPipeline's settingsManager changes
    func setSettingsManager(_ settings: SettingsManager?) {
        self.settingsManager = settings
    }

    // MARK: - Public Methods

    /// Retry a failed transcription using the stored audio file
    /// RETRY FLOW:
    /// 1. Validate audio file exists on disk
    /// 2. Extract mode from transcript (either from relationship or name)
    /// 3. Update transcript to "processing" status
    /// 4. Increment retry count and update timestamp
    /// 5. Apply VAD silence trimming if enabled and duration >= 30s
    /// 6. Execute transcription
    /// 7. Save trimmed audio path to Core Data (if VAD was used)
    /// 8. Update transcript with result (success or failure)
    ///
    /// - Parameter transcript: The failed transcript to retry
    /// - Returns: The transcription result if successful
    /// - Throws: TranscriptionError if audio file missing or transcription fails
    func retryTranscription(for transcript: Transcript) async throws -> TranscriptionResult {
        // VALIDATION STEP 1: Check audio file availability
        // Without the audio file, we cannot retry the transcription
        guard let audioPath = transcript.audioFilePath,
              FileManager.default.fileExists(atPath: audioPath) else {
            AppLogger.transcription.error("Cannot retry transcription: audio file not found at \(transcript.audioFilePath ?? "nil")")
            throw TranscriptionError.audioFileNotFound
        }

        let audioURL = URL(fileURLWithPath: audioPath)

        // VALIDATION STEP 2: Get the mode from Core Data relationship or fallback to name
        // The mode contains important settings like model, language, and post-processing config
        var mode: Mode?
        if let modeRelation = transcript.modeRelationship {
            // Preferred: Use the Core Data relationship
            mode = modeRelation
        } else if let modeName = transcript.mode {
            // Fallback: Find mode by name if relationship is missing.
            // Keep the SQL off the main context; retry can be triggered while
            // recording UI is transitioning. Do not fall back to the default
            // mode here; a renamed/deleted legacy mode should remain unresolved.
            mode = await PersistenceController.shared.resolveTranscriptionModeInBackground(
                id: "",
                fallbackName: modeName,
                allowDefaultFallback: false
            )
        }

        // UPDATE STEP 1: Mark transcript as processing
        // This immediately shows the user that we're retrying the transcription
        transcript.setValue("processing", forKey: "status")
        transcript.setValue(nil, forKey: "failedReason") // Clear previous error

        // UPDATE STEP 2: Track retry metadata
        // Increment retry count and record timestamp for analytics/debugging
        let currentRetryCount = transcript.value(forKey: "retryCount") as? Int16 ?? 0
        transcript.setValue(currentRetryCount + 1, forKey: "retryCount")
        transcript.setValue(Date(), forKey: "lastRetryDate")

        // Save changes to Core Data before starting transcription
        // This ensures UI updates immediately even if transcription takes time
        PersistenceController.shared.save()

        // VAD SILENCE TRIMMING (Optional)
        // Uses VADProcessingService to analyze audio and trim leading/trailing silence.
        // Only applies to recordings >= 30 seconds when VAD is enabled in settings.
        let recordingDuration = transcript.duration
        let vadResult = await vadProcessingService.processAudioForTranscription(
            audioURL: audioURL,
            duration: recordingDuration,
            vadEnabled: settingsManager?.enableVAD ?? false,
            context: "Retry"
        )
        let finalAudioURL = vadResult.finalAudioURL
        let trimResult = vadResult.trimResult

        do {
            // TRANSCRIPTION STEP: Execute the actual transcription
            // Guard against nil manager (should never happen, but defensive)
            guard let manager = transcriptionPipeline else {
                throw TranscriptionError.providerNotAvailable(provider: nil, reason: "Transcription manager not available")
            }

            // Perform transcription with the final audio URL (may be VAD-trimmed)
            // This maintains consistency with the original transcription attempt
            let result = try await manager.transcribeWithDetails(
                audioURL: finalAudioURL,
                mode: mode,
                recordingSession: transcript.recordingSession
            )

            // SAVE TRIMMED AUDIO PATH:
            // If VAD created a valid trimmed file, store the path in Core Data.
            // This allows users to toggle between original and trimmed audio in history view.
            if vadResult.wasProcessed, let result = trimResult {
                PersistenceController.shared.setTrimmedAudioPath(transcript, trimmedPath: result.outputURL.path)
                AppLogger.transcription.debug("📝 [Retry] Saved trimmed audio path to transcript")
            }

            // SUCCESS HANDLING: Update transcript with successful result
            // This marks the transcript as completed and stores the text
            PersistenceController.shared.updateTranscriptWithTranscription(
                transcript,
                transcribedText: result.rawText,
                postProcessedText: result.wasPostProcessed ? result.text : nil,
                transcriptionProvider: result.provider,
                postProcessingProvider: result.postProcessingProvider
            )

            AppLogger.transcription.info("✅ Retry successful for transcript \(transcript.id?.uuidString ?? "unknown")")

            return result
        } catch {
            // FAILURE HANDLING: Update transcript with error information
            // This helps users understand why the retry failed
            transcript.setValue("failed", forKey: "status")
            transcript.setValue(error.localizedDescription, forKey: "failedReason")
            transcript.text = "Retry failed: \(error.localizedDescription)"

            // Save the failed state
            PersistenceController.shared.save()

            AppLogger.transcription.error("❌ Retry failed for transcript: \(error.localizedDescription)")

            // Re-throw so caller can handle the error
            throw error
        }
    }

    /// Check if a transcript can be retried
    /// ELIGIBILITY CRITERIA:
    /// - Audio file must still exist on disk
    /// - If file was deleted, retry is impossible
    ///
    /// - Parameter transcript: The transcript to check
    /// - Returns: true if audio file exists and retry is possible
    func canRetryTranscript(_ transcript: Transcript) -> Bool {
        // Check if audio file path exists and file is on disk
        guard let audioPath = transcript.audioFilePath else {
            return false
        }

        return FileManager.default.fileExists(atPath: audioPath)
    }
}
