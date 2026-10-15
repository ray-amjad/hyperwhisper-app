//
//  TranscriptActionHandler.swift
//  hyperwhisper
//
//  TRANSCRIPT ACTION HANDLER
//  Centralized handler for all transcript-related actions (retry, delete, copy, etc.)
//  This ensures consistent behavior between detail view buttons and context menu actions.
//
//  Architecture:
//  - Single source of truth for transcript operations
//  - Manages operation state (loading, error states)
//  - Coordinates with TranscriptionPipeline for retry logic
//  - Handles user confirmations and error alerts
//

import SwiftUI
import CoreData
import AppKit

/// Centralized handler for transcript actions
@MainActor
class TranscriptActionHandler: ObservableObject {
    
    // MARK: - Published Properties
    
    /// Currently retrying transcripts (by ID) to show progress indicators
    @Published var retryingTranscripts: Set<UUID> = []
    
    /// Currently deleting transcripts (by ID) to disable UI during deletion
    @Published var deletingTranscripts: Set<UUID> = []
    
    /// Error messages for display
    @Published var lastError: String?

    // MARK: - Running Retry Tasks

    /// One running retry: how to cancel it, and a token naming this run.
    private struct RunningRetry {
        let token: UUID
        let cancel: () -> Void
    }

    /// The running retry transcriptions of each row, by the row's ID: the new
    /// row of a "Retry with" (#1458), or the failed row a plain Retry reuses
    /// (#1743). A list, because History and the recording dialog each have a
    /// handler that can retry the same row at once.
    ///
    /// Deleting the row must cancel its transcription, or the job runs on for
    /// minutes with nothing to write to. Static, because History keeps its
    /// handler in `@State` (closing and reopening the window makes a new
    /// handler, and its delete must still find the task), and the recording
    /// dialog's Retry runs on a handler of its own.
    private static var retryTasks: [UUID: [RunningRetry]] = [:]

    /// Remember a running retry transcription for this row. `token` names
    /// this run, so finishing it untracks only this run, not another of the row.
    static func trackRetryTask<Success: Sendable, Failure: Error>(_ task: Task<Success, Failure>, for transcriptId: UUID, token: UUID) {
        retryTasks[transcriptId, default: []].append(RunningRetry(token: token, cancel: { task.cancel() }))
    }

    /// Forget a finished retry transcription; other runs of the row stay tracked.
    static func untrackRetryTask(for transcriptId: UUID, token: UUID) {
        guard var running = retryTasks[transcriptId] else { return }
        running.removeAll { $0.token == token }
        retryTasks[transcriptId] = running.isEmpty ? nil : running
    }

    /// True while a retry transcription for this row is tracked.
    static func hasRetryTask(for transcriptId: UUID) -> Bool {
        retryTasks[transcriptId] != nil
    }

    /// Cancel every running retry transcription of this row, if any.
    /// Cancelling the task stops the engine too (`TranscriptionPipeline.value(of:)`, #1507).
    /// It cancels only this row's own runs: a newer dictation that took the
    /// pipeline over runs in a task of its own and is left alone (#1425).
    static func cancelRetryTask(for transcriptId: UUID) {
        guard let running = retryTasks.removeValue(forKey: transcriptId) else { return }
        running.forEach { $0.cancel() }
        AppLogger.ui.info("Cancelled the retry transcription of a deleted transcript")
    }

    // MARK: - Dependencies
    
    private let transcriptionPipeline: TranscriptionPipeline
    private let persistenceController: PersistenceController
    
    // MARK: - Initialization
    
    init(transcriptionPipeline: TranscriptionPipeline, 
         persistenceController: PersistenceController = .shared) {
        self.transcriptionPipeline = transcriptionPipeline
        self.persistenceController = persistenceController
    }
    
    // MARK: - Retry Action

    /// How a plain Retry ended.
    enum RetryOutcome: Equatable {
        case succeeded
        /// It failed or could not start; `lastError` says why.
        case failed
        /// Deleting the row stopped it (#1743): a quiet cancel, not an error,
        /// and `lastError` stays nil.
        case stoppedByDelete
    }

    /// Retry a failed transcription
    /// - Parameter transcript: The transcript to retry
    /// - Returns: Success status
    @discardableResult
    func retryTranscription(_ transcript: Transcript) async -> Bool {
        await retryTranscriptionOutcome(transcript) == .succeeded
    }

    /// Retry a failed transcription, telling a failure apart from a stop by
    /// the row's delete (the recording dialog shows no error for the latter).
    func retryTranscriptionOutcome(_ transcript: Transcript) async -> RetryOutcome {
        // VALIDATION PHASE:
        // Check if we can retry this transcript
        guard canRetry(transcript) else {
            // Say so: the History context menu has no other place to show it (#1455).
            let message = "transcripts.error.cannotRetry".localized
            lastError = message
            await showRetryErrorAlert(message)
            return .failed
        }
        
        // Check if already retrying
        guard let transcriptId = transcript.id,
              !retryingTranscripts.contains(transcriptId) else {
            return .failed
        }
        
        // UI STATE UPDATE:
        // Mark as retrying to show progress indicator
        guard let transcriptId = transcript.id else {
            lastError = "transcripts.error.invalidId".localized
            return .failed
        }
        retryingTranscripts.insert(transcriptId)
        lastError = nil

        // RETRY EXECUTION:
        // Use TranscriptionPipeline's retry logic, in a task kept by the row's
        // ID so deleting the row cancels it (#1743)
        let pipeline = transcriptionPipeline
        let retryTask = Task {
            try await pipeline.retryTranscription(for: transcript)
        }
        let retryToken = UUID()
        Self.trackRetryTask(retryTask, for: transcriptId, token: retryToken)
        defer { Self.untrackRetryTask(for: transcriptId, token: retryToken) }

        do {
            _ = try await retryTask.value

            // SUCCESS HANDLING:
            // Remove from retrying set
            retryingTranscripts.remove(transcriptId)

            // Show success feedback (optional)
            AppLogger.ui.info("Successfully retried transcription")
            
            return .succeeded
        } catch {
            // ERROR HANDLING:
            // Remove from retrying set and show error
            retryingTranscripts.remove(transcriptId)

            // A delete cancelled this retry (#1743): the row is gone, so no alert.
            if transcript.retryWasStoppedByDelete(taskCancelled: retryTask.isCancelled) {
                AppLogger.ui.info("Retry stopped: its transcript was deleted")
                return .stoppedByDelete
            }

            let errorMessage = error.localizedDescription
            lastError = errorMessage
            
            // Show error alert
            await showRetryErrorAlert(errorMessage)
            
            AppLogger.ui.error("Retry failed: \(errorMessage, privacy: .public)")
            
            return .failed
        }
    }
    
    /// Retry a transcription with a specific mode (creates a new transcript)
    /// - Parameters:
    ///   - transcript: The original transcript to retry
    ///   - mode: The new mode to use for transcription
    /// - Returns: Success status
    @discardableResult
    func retryTranscription(_ transcript: Transcript, with mode: Mode) async -> Bool {
        // VALIDATION:
        // Check if audio file exists
        guard let originalAudioPath = transcript.audioFilePath,
              FileManager.default.fileExists(atPath: originalAudioPath) else {
            // Say so: nothing in History reads lastError, so a bare return
            // swallowed the click (#1455).
            let message = "transcripts.error.cannotRetry".localized
            lastError = message
            await showRetryErrorAlert(message)
            return false
        }
        
        // PREPARATION:
        // 1. Create a duplicate of the audio file
        // 2. Create a new transcript entry
        
        let originalURL = URL(fileURLWithPath: originalAudioPath)
        let fileExtension = originalURL.pathExtension
        let newFileName = UUID().uuidString + "." + fileExtension
        let newAudioURL = originalURL.deletingLastPathComponent().appendingPathComponent(newFileName)
        
        do {
            try FileManager.default.copyItem(at: originalURL, to: newAudioURL)
            AppLogger.ui.info("Duplicated audio file for retry: \(newAudioURL.lastPathComponent)")
        } catch {
            AppLogger.ui.error("Failed to duplicate audio file: \(error.localizedDescription)")
            let message = error.localizedDescription
            lastError = message
            await showRetryErrorAlert(message)
            return false
        }
        
        // Create new transcript in processing state
        let newTranscript = persistenceController.createProcessingTranscript(
            duration: transcript.duration,
            mode: mode.name,
            audioFilePath: newAudioURL.path
        )
        
        // EXECUTION:
        // Run transcription with the new mode
        let newTranscriptId = newTranscript.id
        let retryToken = UUID()
        let task = Task {
            defer {
                if let newTranscriptId {
                    Self.untrackRetryTask(for: newTranscriptId, token: retryToken)
                }
            }
            do {
                let result = try await transcriptionPipeline.transcribeWithDetails(
                    audioURL: newAudioURL,
                    mode: mode
                )

                // The row was deleted while the engine finished: nothing to write to.
                guard !newTranscript.retryWasStoppedByDelete() else {
                    return
                }

                // No speech fails the row with that reason, not blank (#1429).
                try result.throwIfNoSpeech()

                // Update the transcript with the result
                await persistenceController.updateTranscriptWithTranscription(
                    newTranscript,
                    transcribedText: result.rawText,
                    postProcessedText: result.historyPostProcessedText,
                    transcriptionProvider: result.provider,
                    postProcessingProvider: result.postProcessingProvider
                )
                
                AppLogger.ui.info("Successfully retried transcription with new mode · modeId=\(mode.id?.uuidString ?? "nil", privacy: .public) · preset=\(PresetType.reportingValue(for: mode), privacy: .public)")
                
            } catch {
                // A delete cancelled this job (#1458): the row is gone, so leave it be.
                if newTranscript.retryWasStoppedByDelete() {
                    AppLogger.ui.info("Retry with new mode stopped: its transcript was deleted")
                    return
                }

                AppLogger.ui.error("Retry with new mode failed: \(error.localizedDescription)")

                // Update transcript to failed state
                await Self.markRetryWithFailed(newTranscript.objectID, after: error, persistence: persistenceController)
            }
        }
        if let newTranscriptId {
            Self.trackRetryTask(task, for: newTranscriptId, token: retryToken)
        }

        return true
    }

    /// Marks a failed "Retry with" row failed, and replaces its "Processing
    /// transcription..." placeholder with a failure text (#1457), through the
    /// same serial-writer helper, so the list and detail pane stop reading as
    /// running.
    ///
    /// Keeps the "Transcription failed: …" text a file import used to write.
    /// The empty-text rule of #1427 covers file imports only.
    static func markRetryWithFailed(_ transcriptID: NSManagedObjectID, after error: Error, persistence: PersistenceController) async {
        let reason = error is CancellationError ? "cancelled" : error.localizedDescription
        let text = error is CancellationError ? "Transcription cancelled" : "Transcription failed: \(reason)"
        await persistence.markTranscriptFailedInBackground(
            transcriptID: transcriptID,
            failedReason: reason,
            errorText: text
        )
    }
    
    // MARK: - Delete Actions
    
    /// Delete a single transcript with confirmation
    /// - Parameters:
    ///   - transcript: The transcript to delete
    ///   - skipConfirmation: Skip the confirmation dialog (for programmatic deletion)
    /// - Returns: Success status
    @discardableResult
    func deleteTranscript(_ transcript: Transcript, skipConfirmation: Bool = false) async -> Bool {
        // CONFIRMATION PHASE:
        // Show confirmation dialog unless skipped
        if !skipConfirmation {
            let confirmed = await showDeleteConfirmation(for: [transcript])
            guard confirmed else { return false }
        }
        
        // Check if already deleting
        guard let transcriptId = transcript.id,
              !deletingTranscripts.contains(transcriptId) else {
            return false
        }
        
        // UI STATE UPDATE:
        // Mark as deleting to disable UI
        guard let transcriptId = transcript.id else {
            lastError = "transcripts.error.invalidId".localized
            return false
        }
        deletingTranscripts.insert(transcriptId)
        lastError = nil
        
        // DELETION EXECUTION:
        // PersistenceController's delete also stops a Retry or "Retry with"
        // still transcribing this row (#1458, #1743)
        persistenceController.deleteTranscript(transcript)
        
        // Clean up state
        if let transcriptId = transcript.id {
            deletingTranscripts.remove(transcriptId)
        }
        
        AppLogger.ui.info("Deleted transcript")
        
        return true
    }
    
    /// Delete multiple transcripts with confirmation
    /// - Parameters:
    ///   - transcripts: The transcripts to delete
    ///   - skipConfirmation: Skip the confirmation dialog (for programmatic deletion)
    /// - Returns: Number of successfully deleted transcripts
    @discardableResult
    func deleteTranscripts(_ transcripts: Set<Transcript>, skipConfirmation: Bool = false) async -> Int {
        // VALIDATION:
        // Return early if no transcripts to delete
        guard !transcripts.isEmpty else { return 0 }
        
        // CONFIRMATION PHASE:
        // Show confirmation dialog unless skipped
        if !skipConfirmation {
            let confirmed = await showDeleteConfirmation(for: Array(transcripts))
            guard confirmed else { return 0 }
        }
        
        // BATCH DELETION:
        // Track successfully deleted count
        var deletedCount = 0
        
        // Filter out any already being deleted
        let transcriptsToDelete = transcripts.filter { transcript in
            guard let id = transcript.id else { return false }
            return !deletingTranscripts.contains(id)
        }
        
        // Mark all as deleting
        for transcript in transcriptsToDelete {
            if let id = transcript.id {
                deletingTranscripts.insert(id)
            }
        }
        
        // Delete each transcript
        for transcript in transcriptsToDelete {
            persistenceController.deleteTranscript(transcript)
            deletedCount += 1
            
            // Clean up state for this transcript
            if let id = transcript.id {
                deletingTranscripts.remove(id)
            }
        }
        
        AppLogger.ui.info("Deleted \(deletedCount) transcript(s)")
        
        return deletedCount
    }
    
    // MARK: - Copy Action
    
    /// Copy transcript text to clipboard
    /// - Parameter transcript: The transcript to copy text from
    func copyTranscriptText(_ transcript: Transcript) {
        // COPY LOGIC:
        // Get the appropriate text (processed or raw)
        let textToCopy = getDisplayText(for: transcript)

        // A failed import has no text (#1427); copying "" would only empty the
        // user's clipboard. Covers the History Cmd+C and context-menu Copy.
        guard !textToCopy.isEmpty else {
            AppLogger.ui.info("Skipped copy: transcript has no text")
            return
        }

        // Copy to clipboard using AccessibilityHelper
        AccessibilityHelper.shared.copyToClipboard(textToCopy)
        
        AppLogger.ui.info("Copied transcript text to clipboard")
    }
    
    // MARK: - Helper Methods
    
    /// Check if a transcript can be retried
    func canRetry(_ transcript: Transcript) -> Bool {
        // RETRY ELIGIBILITY:
        // 1. Must be a failed transcript
        // 2. Must have an audio file that exists
        
        // Check failure status
        let status = transcript.value(forKey: "status") as? String
        let hasFailedReason = (transcript.value(forKey: "failedReason") as? String)?.isEmpty == false
        
        let isFailed = status == "failed" || hasFailedReason ||
                       transcript.text?.starts(with: "Transcription failed:") == true ||
                       transcript.text?.starts(with: "Retry failed:") == true
        
        guard isFailed else { return false }
        
        // Check audio file exists
        guard let audioPath = transcript.audioFilePath else { return false }
        return FileManager.default.fileExists(atPath: audioPath)
    }
    
    /// Check if a transcript is currently being retried
    func isRetrying(_ transcript: Transcript) -> Bool {
        guard let transcriptId = transcript.id else { return false }
        return retryingTranscripts.contains(transcriptId)
    }
    
    /// Check if a transcript is currently being deleted
    func isDeleting(_ transcript: Transcript) -> Bool {
        guard let transcriptId = transcript.id else { return false }
        return deletingTranscripts.contains(transcriptId)
    }
    
    /// Get the display text for a transcript (respecting raw/processed preference)
    private func getDisplayText(for transcript: Transcript) -> String {
        // DISPLAY TEXT LOGIC:
        // Priority order:
        // 1. postProcessedText (if available)
        // 2. text field (always present)
        
        if let postProcessed = transcript.value(forKey: "postProcessedText") as? String,
           !postProcessed.isEmpty {
            return postProcessed
        }
        
        return transcript.text ?? ""
    }
    
    // MARK: - User Dialogs
    
    /// Show delete confirmation dialog for one or more transcripts
    private func showDeleteConfirmation(for transcripts: [Transcript]) async -> Bool {
        // IMPORTANT: We're already on @MainActor, so no need for DispatchQueue.main
        // Using DispatchQueue.main.async here was causing a deadlock
        let alert = NSAlert()
        
        // DIALOG CONTENT:
        // Adapt message based on single vs multiple selection
        if transcripts.count == 1 {
            // Single transcript deletion
            alert.messageText = "transcripts.delete.single.title".localized
            alert.informativeText = "transcripts.delete.single.message".localized
        } else {
            // Multiple transcripts deletion
            alert.messageText = "transcripts.delete.multiple.title".localized(arguments: transcripts.count)
            alert.informativeText = "transcripts.delete.multiple.message".localized(arguments: transcripts.count)
        }
        
        alert.alertStyle = .warning
        alert.addButton(withTitle: transcripts.count == 1 ? "common.delete".localized : "transcripts.delete.multiple.confirm".localized)
        alert.addButton(withTitle: "common.cancel".localized)
        
        // Make delete button red
        if let deleteButton = alert.buttons.first {
            deleteButton.hasDestructiveAction = true
        }
        
        // Run the modal and return the result
        // Since we're already on @MainActor, this is safe
        let response = alert.runModal()
        return response == .alertFirstButtonReturn
    }
    
    /// Show retry error alert
    private func showRetryErrorAlert(_ error: String) async {
        // IMPORTANT: We're already on @MainActor, so no need for DispatchQueue.main
        let alert = NSAlert()
        alert.messageText = "transcripts.retry.error.title".localized
        alert.informativeText = error
        alert.alertStyle = .warning
        alert.addButton(withTitle: "common.ok".localized)
        alert.runModal()
    }
}
