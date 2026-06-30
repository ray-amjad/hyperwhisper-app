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
    
    /// Retry a failed transcription
    /// - Parameter transcript: The transcript to retry
    /// - Returns: Success status
    @discardableResult
    func retryTranscription(_ transcript: Transcript) async -> Bool {
        // VALIDATION PHASE:
        // Check if we can retry this transcript
        guard canRetry(transcript) else {
            await MainActor.run {
                self.lastError = "transcripts.error.cannotRetry".localized
            }
            return false
        }
        
        // Check if already retrying
        guard let transcriptId = transcript.id,
              !retryingTranscripts.contains(transcriptId) else {
            return false
        }
        
        // UI STATE UPDATE:
        // Mark as retrying to show progress indicator
        guard let transcriptId = transcript.id else {
            lastError = "transcripts.error.invalidId".localized
            return false
        }
        retryingTranscripts.insert(transcriptId)
        lastError = nil
        
        do {
            // RETRY EXECUTION:
            // Use TranscriptionPipeline's retry logic
            let result = try await transcriptionPipeline.retryTranscription(for: transcript)
            
            // SUCCESS HANDLING:
            // Remove from retrying set
            if let transcriptId = transcript.id {
                retryingTranscripts.remove(transcriptId)
            }
            
            // Show success feedback (optional)
            AppLogger.ui.info("Successfully retried transcription")
            
            return true
        } catch {
            // ERROR HANDLING:
            // Remove from retrying set and show error
            if let transcriptId = transcript.id {
                retryingTranscripts.remove(transcriptId)
            }
            
            let errorMessage = error.localizedDescription
            lastError = errorMessage
            
            // Show error alert
            await showRetryErrorAlert(errorMessage)
            
            AppLogger.ui.error("Retry failed: \(errorMessage, privacy: .public)")
            
            return false
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
            await MainActor.run {
                self.lastError = "transcripts.error.audioNotFound".localized
            }
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
            await MainActor.run {
                self.lastError = "transcripts.error.fileCopyFailed".localized
            }
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
        Task {
            do {
                let result = try await transcriptionPipeline.transcribeWithDetails(
                    audioURL: newAudioURL,
                    mode: mode
                )
                
                // Update the transcript with the result
                await persistenceController.updateTranscriptWithTranscription(
                    newTranscript,
                    transcribedText: result.rawText,
                    postProcessedText: result.wasPostProcessed ? result.text : nil,
                    transcriptionProvider: result.provider,
                    postProcessingProvider: result.postProcessingProvider
                )
                
                AppLogger.ui.info("Successfully retried transcription with new mode: \(mode.name ?? "Unknown")")
                
            } catch {
                AppLogger.ui.error("Retry with new mode failed: \(error.localizedDescription)")
                
                // Update transcript to failed state
                await MainActor.run {
                    newTranscript.setValue("failed", forKey: "status")
                    newTranscript.setValue(error.localizedDescription, forKey: "failedReason")
                    persistenceController.save()
                }
            }
        }
        
        return true
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
        // Delete through PersistenceController
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
