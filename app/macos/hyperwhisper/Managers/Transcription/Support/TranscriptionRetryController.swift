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

/// Why a History Retry refused to start.
///
/// Its own type, not a `TranscriptionError` case: a refusal is decided here,
/// before any provider runs, so it never reaches the Local API error mapping,
/// Sentry classification or anything else that switches over
/// `TranscriptionError`. The caller only shows `localizedDescription`.
enum TranscriptionRetryError: LocalizedError, Equatable {
    /// The mode that made the recording no longer exists (it was deleted, or
    /// the row never named one). Issue #1440: with no mode, the router picks
    /// HyperWhisper Cloud, so a retry would upload audio an on-device mode
    /// recorded, without asking. Ray (2026-10-08): Retry stops, sends nothing,
    /// and points the user at "Retry with..." to pick another mode.
    case originalModeDeleted
    /// The retry's mode was live when the retry started but was deleted
    /// before the router read it (#1620). Thrown by the pipeline when a
    /// retry passes `refuseDeletedMode`, so nothing is sent.
    case modeDeletedDuringRetry

    var errorDescription: String? {
        switch self {
        case .originalModeDeleted:
            return "transcripts.retry.error.modeDeleted".localized
        case .modeDeletedDuringRetry:
            return "recording.retry.modeDeleted".localized
        }
    }
}

/// The modes a user can pick to retry a recording with: History's
/// "Retry with..." menu and the recording dialog's pending-file mode picker
/// (#1617) both list exactly these, so the two never disagree.
enum RetryModeChoices {
    /// Every live mode while online. Offline, the modes that need the internet
    /// (a Cloud transcription model or cloud post-processing) are left out.
    /// A deleted mode is never a choice (#1620).
    static func available(from modes: [Mode], isOnline: Bool) -> [Mode] {
        let live = modes.filter(isLive)
        guard !isOnline else { return live }
        return live.filter { mode in
            let isCloudModel = (mode.model ?? "").lowercased() == "cloud"
            let processingMode = PostProcessingMode(rawValue: mode.postProcessingMode) ?? .off
            let hasCloudPostProcessing = processingMode.requiresInternet
            return !isCloudModel && !hasCloudPostProcessing
        }
    }

    /// False for a mode deleted since it was fetched. Once the delete is
    /// saved the object leaves its context and reads every attribute as nil,
    /// so a nil model would route a retry to HyperWhisper Cloud (#1440, #1620).
    static func isLive(_ mode: Mode) -> Bool {
        !mode.isDeleted && mode.managedObjectContext != nil
    }
}

/// Handles retry logic for failed transcriptions
@MainActor
class TranscriptionRetryController {

    // MARK: - Private Properties

    /// Weak reference to the transcription manager for executing retries
    /// Using weak reference to avoid retain cycles since manager owns this handler
    private weak var transcriptionPipeline: TranscriptionPipeline?

    /// Settings manager for checking VAD enabled state
    private weak var settingsManager: SettingsManager?

    /// Store for the mode lookup and the row's saves. `.shared` in the app;
    /// tests pass an in-memory store.
    private let persistence: PersistenceController

    /// VAD processing service for silence trimming and M4A conversion
    private let vadProcessingService = VADProcessingService()

    /// The VAD output paths in-flight retries write or still transcribe, with
    /// how many runs hold each. Every retry of the same recording writes the
    /// same names, so a cancelled run's cleanup must skip the files another
    /// running retry still uses (#1743). Static: History and the recording
    /// dialog each reach a controller of their own pipeline.
    private static var vadOutputsInFlight: [String: Int] = [:]

    // MARK: - Initialization

    /// Initialize retry handler with references to required managers
    /// - Parameters:
    ///   - transcriptionPipeline: The manager that will execute transcription
    ///   - settingsManager: Settings manager for VAD configuration
    ///   - persistence: Store for the mode lookup and saves (tests only)
    init(
        transcriptionPipeline: TranscriptionPipeline?,
        settingsManager: SettingsManager? = nil,
        persistence: PersistenceController = .shared
    ) {
        self.transcriptionPipeline = transcriptionPipeline
        self.settingsManager = settingsManager
        self.persistence = persistence
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
    /// 2. Extract mode from transcript (either from relationship or name);
    ///    refuse, leaving the row untouched, when it no longer exists
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
            mode = await persistence.resolveTranscriptionModeInBackground(
                id: "",
                fallbackName: modeName,
                allowDefaultFallback: false
            )
        }

        // REFUSAL (issue #1440): no mode means the router would pick
        // HyperWhisper Cloud, so audio an on-device mode recorded would be
        // uploaded without asking. Stop here, before the row is touched: no
        // status flip, no retryCount bump, `text` and `failedReason` keep
        // the original failure. The user picks a mode with "Retry with...".
        guard let mode else {
            AppLogger.transcription.warning("Retry refused: the transcript's mode no longer exists; no request sent")
            throw TranscriptionRetryError.originalModeDeleted
        }

        // The row's delete cancelled this retry before it began (#1743): leave
        // the row as it was, not flipped to "processing".
        try Task.checkCancellation()

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
        persistence.save()

        // VAD SILENCE TRIMMING (Optional)
        // Uses VADProcessingService to analyze audio and trim leading/trailing silence.
        // Only applies to recordings >= 30 seconds when VAD is enabled in settings.
        let recordingDuration = transcript.duration
        // Claimed before VAD writes them, held until this run is over.
        let claimedVADOutputs = Self.vadOutputPaths(for: audioURL)
        Self.claimVADOutputs(claimedVADOutputs)
        var holdsVADOutputs = true
        defer {
            if holdsVADOutputs { Self.releaseVADOutputs(claimedVADOutputs) }
        }
        let vadResult = await vadProcessingService.processAudioForTranscription(
            audioURL: audioURL,
            duration: recordingDuration,
            vadEnabled: settingsManager?.enableVAD ?? false,
            context: .retry
        )
        let finalAudioURL = vadResult.finalAudioURL
        let trimResult = vadResult.trimResult

        do {
            // The row was deleted while VAD ran (#1743): do not start the engine.
            try Task.checkCancellation()

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
                recordingSession: transcript.recordingSession,
                refuseDeletedMode: true
            )

            // The row was deleted while the engine finished: nothing to write to.
            if transcript.retryWasStoppedByDelete() {
                throw CancellationError()
            }

            // A retry that hears no speech keeps the row failed, with the
            // "no speech" reason, instead of a blank completed row (#1429).
            try result.throwIfNoSpeech()

            // SAVE TRIMMED AUDIO PATH:
            // If VAD created a valid trimmed file, store the path in Core Data.
            // This allows users to toggle between original and trimmed audio in history view.
            if vadResult.wasProcessed, let result = trimResult {
                if await persistence.setTrimmedAudioPath(transcript, trimmedPath: result.outputURL.path) {
                    AppLogger.transcription.debug("📝 [Retry] Saved trimmed audio path to transcript")
                }
            }

            // The row was deleted while that save ran: still nothing to write to.
            if transcript.retryWasStoppedByDelete() {
                throw CancellationError()
            }

            // SUCCESS HANDLING: Update transcript with successful result
            // This marks the transcript as completed and stores the text
            persistence.updateTranscriptWithTranscription(
                transcript,
                transcribedText: result.rawText,
                postProcessedText: result.historyPostProcessedText,
                transcriptionProvider: result.provider,
                postProcessingProvider: result.postProcessingProvider
            )

            AppLogger.transcription.info("✅ Retry successful for transcript \(transcript.id?.uuidString ?? "unknown")")

            return result
        } catch {
            // A delete cancelled this retry (#1743): the row is gone, so leave it be.
            // The delete swept only the files the row recorded, so remove the
            // VAD output this run wrote, or it stays on disk (#1513), unless
            // another row recorded from the same audio still owns it.
            if transcript.retryWasStoppedByDelete() {
                Self.releaseVADOutputs(claimedVADOutputs)
                holdsVADOutputs = false
                let leftovers = PersistenceController.audioPathsNoTranscriptOwns(
                    Self.vadOutputsNoRunUses(Self.filesMadeByRetry(vadResult)),
                    in: persistence.container.viewContext
                )
                _ = await FileDeletion.deleteFiles(at: leftovers)
                AppLogger.transcription.info("Retry stopped: its transcript was deleted")
                throw error
            }

            // FAILURE HANDLING: Update transcript with error information
            // This helps users understand why the retry failed
            transcript.setValue("failed", forKey: "status")
            transcript.setValue(error.localizedDescription, forKey: "failedReason")
            transcript.text = "Retry failed: \(error.localizedDescription)"

            // Save the failed state
            persistence.save()

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

    /// The names VAD gives its output for this recording: the trimmed WAV and
    /// its M4A upload copy.
    static func vadOutputPaths(for audioURL: URL) -> [String] {
        let trimmed = SilenceTrimmer().generateOutputURL(for: audioURL)
        return [trimmed.path, TranscriptAudioFiles.uploadCopyURL(forTrimmedWAV: trimmed).path]
    }

    static func claimVADOutputs(_ paths: [String]) {
        for path in paths {
            vadOutputsInFlight[path, default: 0] += 1
        }
    }

    static func releaseVADOutputs(_ paths: [String]) {
        for path in paths {
            guard let count = vadOutputsInFlight[path] else { continue }
            vadOutputsInFlight[path] = count > 1 ? count - 1 : nil
        }
    }

    /// Of `paths`, the ones no in-flight retry holds.
    static func vadOutputsNoRunUses(_ paths: [String]) -> [String] {
        paths.filter { vadOutputsInFlight[$0] == nil }
    }

    /// The files a retry's VAD step wrote: the trimmed WAV and, for a large
    /// one, its M4A upload copy. Never the recording the retry started from.
    static func filesMadeByRetry(_ vadResult: VADProcessingResult) -> [String] {
        let originalPath = vadResult.originalAudioURL.path
        var paths: [String] = []
        for url in [vadResult.trimResult?.outputURL, vadResult.finalAudioURL] {
            guard let path = url?.path, path != originalPath, !paths.contains(path) else { continue }
            paths.append(path)
        }
        return paths
    }
}

extension Transcript {
    /// True once deleting this row stopped its retry: the delete cancelled the
    /// retry's task (`TranscriptActionHandler.cancelRetryTask`) or already
    /// removed the row. The one rule for both kinds of retry (#1458, #1743).
    ///
    /// `taskCancelled` defaults to the calling task. A caller that awaits the
    /// retry from outside its task passes that task's `isCancelled`. A newer
    /// dictation that took the pipeline over (#1425) cancels only the engine
    /// run, not the retry task, so its row still records the failure.
    func retryWasStoppedByDelete(taskCancelled: Bool = Task.isCancelled) -> Bool {
        taskCancelled || isDeleted || managedObjectContext == nil
    }
}
